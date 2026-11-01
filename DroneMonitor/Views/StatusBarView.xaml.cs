using Plugin.BLE.Abstractions.Contracts;
using Microsoft.Maui.ApplicationModel;

namespace DroneMonitor.Views;

// 画面上部の状態バー。BleService のイベントを受けて表示だけを更新する
public partial class StatusBarView : ContentView
{
    private BleService? _bleService;
    private uint _lastOverrun = 0; // 取りこぼしが増えたかの判定用

    // スロットルの指令値と、それを変えた時刻（変えた直後は機体側の状態がまだ届いていないので比べない）
    private byte _commandedThrottle = 0;
    private DateTime _throttleChangedAt = DateTime.MinValue;
    private static readonly TimeSpan ThrottleCompareDelay = TimeSpan.FromSeconds(0.5);

    private static readonly string[] ControlModeNames = { "None", "MIMO", "PID" };

    // TRIP ボタンが押されたときに通知する（実際の送信処理は HomeView が持つ）
    public event EventHandler? TripRequested;

    public StatusBarView()
    {
        InitializeComponent();
    }

    private void OnTripClicked(object? sender, EventArgs e)
    {
        TripRequested?.Invoke(this, EventArgs.Empty);
    }

    // 台の試験の表示。null なら消す
    public void SetRigMode(string? axisName)
    {
        rigModeLabel.IsVisible = axisName != null;
        rigModeLabel.Text = axisName != null ? $"台の試験: {axisName}" : "";
    }

    // 操縦画面のスロットル指令値を表示する
    public void SetThrottle(byte percent)
    {
        throttleLabel.Text = $"{percent}%";
        _commandedThrottle = percent;
        _throttleChangedAt = DateTime.Now;
        UpdateDroneStatus();
    }

    public void SetBleService(BleService bleService)
    {
        if (_bleService != null)
        {
            _bleService.ConnectionStateChanged -= OnStateChanged;
            _bleService.ControlStateChanged -= OnStateChanged;
            _bleService.NotificationReceived -= OnNotificationReceived;
            _bleService.DroneStatusChanged -= OnDroneStatusChanged;
        }

        _bleService = bleService;
        _bleService.ConnectionStateChanged += OnStateChanged;
        _bleService.ControlStateChanged += OnStateChanged;
        _bleService.NotificationReceived += OnNotificationReceived;
        _bleService.DroneStatusChanged += OnDroneStatusChanged;
        UpdateState();
    }

    public void Cleanup()
    {
        if (_bleService == null) return;
        _bleService.ConnectionStateChanged -= OnStateChanged;
        _bleService.ControlStateChanged -= OnStateChanged;
        _bleService.NotificationReceived -= OnNotificationReceived;
        _bleService.DroneStatusChanged -= OnDroneStatusChanged;
    }

    private void OnDroneStatusChanged(object? sender, DroneStatus status)
    {
        MainThread.BeginInvokeOnMainThread(UpdateDroneStatus);
    }

    // 機体側の状態の表示。緊急動作中と機体側のスロットルを示し、指令値と違えば色を変える
    private void UpdateDroneStatus()
    {
        var status = _bleService?.LastDroneStatus;
        failsafeLabel.IsVisible = status?.Failsafe == true;

        if (status == null)
        {
            droneThrottleLabel.Text = "-";
            droneThrottleLabel.TextColor = Colors.Black;
            return;
        }
        bool settled = DateTime.Now - _throttleChangedAt > ThrottleCompareDelay;
        bool differs = settled && Math.Abs(status.Throttle - _commandedThrottle) >= 1.0f;
        droneThrottleLabel.Text = $"{status.Throttle:F0}%";
        droneThrottleLabel.TextColor = differs ? Colors.OrangeRed : Colors.Black;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(UpdateState);
    }

    private void UpdateState()
    {
        if (_bleService == null) return;

        switch (_bleService.State)
        {
            case BleConnectionState.Connected:
                connectionLabel.Text = $"接続中 {_bleService.Device?.Name}";
                connectionLabel.TextColor = Colors.Green;
                break;
            case BleConnectionState.Connecting:
                connectionLabel.Text = "スキャン中…";
                connectionLabel.TextColor = Colors.Orange;
                break;
            default:
                connectionLabel.Text = "未接続";
                connectionLabel.TextColor = Colors.Gray;
                loopStatsLabel.Text = "-";
                loopStatsLabel.TextColor = Colors.Black;
                _lastOverrun = 0;
                break;
        }

        byte mode = _bleService.IsControlBySelf;
        controlModeLabel.Text = mode < ControlModeNames.Length ? ControlModeNames[mode] : mode.ToString();
        controlModeLabel.TextColor = mode == 0 ? Colors.Black : Colors.Blue;

        UpdateDroneStatus();

        // 接続済みなのに負荷のキャラクタリスティックが無い＝古いファームウェア
        if (_bleService.State == BleConnectionState.Connected && !_bleService.Characteristics.ContainsKey("LoopStats_Telem"))
        {
            loopStatsLabel.Text = "未対応のファームウェア";
        }
    }

    private void OnNotificationReceived(object? sender, byte[] data)
    {
        if (_bleService == null || sender is not ICharacteristic characteristic) return;
        var key = _bleService.Characteristics.FirstOrDefault(x => x.Value == characteristic).Key;
        if (key != "LoopStats_Telem" || data.Length < 12) return;

        // ファームウェアの loop_stats_t: max_exec_us, overrun(累計), period_us の uint32 x3
        uint maxExecUs = BitConverter.ToUInt32(data, 0);
        uint overrun = BitConverter.ToUInt32(data, 4);
        uint periodUs = BitConverter.ToUInt32(data, 8);
        uint loadPercent = periodUs > 0 ? maxExecUs * 100 / periodUs : 0;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            loopStatsLabel.Text = $"{maxExecUs} / {periodUs} µs ({loadPercent}%)  取りこぼし {overrun}";
            // 取りこぼしが増えた、または周期の8割を超えたら赤で警告
            bool warn = overrun > _lastOverrun || loadPercent >= 80;
            loopStatsLabel.TextColor = warn ? Colors.Red : Colors.Black;
            _lastOverrun = overrun;
        });
    }
}
