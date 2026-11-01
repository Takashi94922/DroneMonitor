using Plugin.BLE.Abstractions.Contracts;
using System.Diagnostics;
using Microsoft.Maui.ApplicationModel;

namespace DroneMonitor.Views;

// 制御モードの切り替えとテレメトリ表示
public partial class ControlDataView : ContentView
{
    private BleService? _bleService;
    private float pitch, roll, yaw;
    private Estimate estimate = new();

    // ジャイロ校正コマンド。結果は Command の通知で [19, status, bias x/y/z(float, deg/s)] が返る
    private const byte CMD_GYRO_CALIB = 19;
    private const int GYRO_CALIB_TIMEOUT_MS = 5000;
    private bool _gyroCalibrating;

    // 制御モード（ファームウェアの ControlMethod）: 0=None, 1=MIMO(KC), 2=PID
    private readonly Dictionary<Button, byte> _modeButtons;

    // 表の数値セル（XAMLで同じ形のLabelを並べる代わりにコードで生成する）
    private readonly Label[] _accelLabels = new Label[3];
    private readonly Label[] _gyroLabels = new Label[3];
    private readonly Label[] _magLabels = new Label[3];
    private readonly Label[] _ctrlLabels = new Label[5];

    public ControlDataView()
    {
        InitializeComponent();
        _modeButtons = new()
        {
            [modeNoneButton] = 0,
            [modeMimoButton] = 1,
            [modePidButton] = 2,
        };
        UpdateModeButtons();

        for (int i = 0; i < 3; i++)
        {
            _accelLabels[i] = AddValueCell(sensorGrid, 2, i + 1, LayoutOptions.End);
            _gyroLabels[i] = AddValueCell(sensorGrid, 3, i + 1, LayoutOptions.End);
            _magLabels[i] = AddValueCell(sensorGrid, 4, i + 1, LayoutOptions.End);
        }
        for (int i = 0; i < 5; i++)
        {
            _ctrlLabels[i] = AddValueCell(ctrlGrid, 2, i, LayoutOptions.Center);
        }
    }

    // センサ値の枠を表示するか（Gains タブでは姿勢と制御入力だけ見せる）
    public bool ShowSensors
    {
        get => sensorBorder.IsVisible;
        set => sensorBorder.IsVisible = value;
    }

    private static Label AddValueCell(Grid grid, int row, int column, LayoutOptions align)
    {
        var label = new Label { Text = "-", FontSize = 16, HorizontalOptions = align };
        grid.Add(label, column, row);
        return label;
    }

    public async void StartNotificationAsync()
    {
        if (_bleService == null) return;

        // StartNotificationAsync は BleService 側でキーごとに通知開始する実装なので await してログ出力
        var ok1 = await _bleService.StartNotificationAsync("Sensor_Telem");
        Debug.WriteLine($"Sensor_Telem通知開始: {ok1}");
        var ok2 = await _bleService.StartNotificationAsync("contU_TelemWrite");
        Debug.WriteLine($"contU_TelemWrite通知開始: {ok2}");
        var ok3 = await _bleService.StartNotificationAsync("PRY_Telem");
        Debug.WriteLine($"PRY_Telem通知開始: {ok3}");
        // 古いファームウェアには無いので false でも問題ない（表示は StatusBarView が担当）
        var ok4 = await _bleService.StartNotificationAsync("LoopStats_Telem");
        Debug.WriteLine($"LoopStats_Telem通知開始: {ok4}");
        // ドローンからの応答（ジャイロ校正の結果など）
        var ok5 = await _bleService.StartNotificationAsync("Command");
        Debug.WriteLine($"Command通知開始: {ok5}");
    }

    public void SetBleService(BleService bleService)
    {
        // 以前の購読解除
        if (_bleService != null)
        {
            _bleService.NotificationReceived -= OnNotificationReceived;
            _bleService.ControlStateChanged -= OnStateChanged;
            _bleService.ConnectionStateChanged -= OnStateChanged;
            _bleService.DroneStatusChanged -= OnDroneStatusChanged;
        }

        _bleService = bleService;
        _bleService.NotificationReceived += OnNotificationReceived;
        _bleService.ControlStateChanged += OnStateChanged;
        _bleService.ConnectionStateChanged += OnStateChanged;
        _bleService.DroneStatusChanged += OnDroneStatusChanged;

        if (_bleService.IsConnected)
        {
            StartNotificationAsync();
        }
        UpdateModeButtons();
    }

    // 現在のモードのボタンを強調し、未接続ならすべて無効にする
    private void UpdateModeButtons()
    {
        bool connected = _bleService?.IsConnected == true;
        byte current = _bleService?.IsControlBySelf ?? 0;
        foreach (var (button, mode) in _modeButtons)
        {
            bool selected = mode == current;
            button.IsEnabled = connected;
            button.BackgroundColor = selected ? (mode == 0 ? Colors.DimGray : Colors.Blue) : Colors.LightGray;
            button.TextColor = selected ? Colors.White : Colors.Black;
            button.FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
        }
        gyroCalibButton.IsEnabled = connected && !_gyroCalibrating;
        if (!connected) droneStateLabel.Text = "";
    }

    // ドローン側の状態を表示する（モードは BleService が IsControlBySelf に反映済み）
    private void OnDroneStatusChanged(object? sender, DroneStatus status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            const float r2d = 180.0f / MathF.PI;
            var text = $"機体側: スロットル {status.Throttle:F0}%　目標 Pitch {status.PitchTarget * r2d:F1}° / Roll {status.RollTarget * r2d:F1}° / Yaw {status.YawTarget * r2d:F1}°";
            if (status.Calibrating) text += "　ジャイロ校正中";
            if (status.Failsafe) text += "　緊急動作中";
            droneStateLabel.Text = text;
            droneStateLabel.TextColor = status.Failsafe ? Colors.Red : Colors.Gray;
        });
    }

    // 他の画面（緊急停止など）や接続状態の変化でボタン表示を合わせる
    private void OnStateChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(UpdateModeButtons);
    }

    private async void OnModeButtonClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button || !_modeButtons.TryGetValue(button, out var mode)) return;
        if (_bleService == null || !_bleService.IsConnected)
        {
            await Application.Current?.MainPage?.DisplayAlert("BLE接続エラー", "BLEが接続されていません。接続を確認してください。", "OK");
            return;
        }

        // 制御方法を送信し、届いたときだけ現在のモードを更新する
        if (await _bleService.WriteAsync("Command", new byte[] { 0x05, mode }))
        {
            _bleService.IsControlBySelf = mode;
        }
        UpdateModeButtons();
    }

    private async void OnGyroCalibClicked(object? sender, EventArgs e)
    {
        var page = Application.Current?.MainPage;
        if (_bleService == null || !_bleService.IsConnected)
        {
            if (page != null) await page.DisplayAlert("BLE接続エラー", "BLEが接続されていません。接続を確認してください。", "OK");
            return;
        }
        if (page != null && !await page.DisplayAlert("ジャイロ校正",
                "機体を静止させてから実行してください。\nスロットルが0のときだけ実行されます（約1秒、その間は制御が止まります）。",
                "実行", "キャンセル"))
        {
            return;
        }

        if (!await _bleService.WriteAsync("Command", new byte[] { CMD_GYRO_CALIB, 0 }))
        {
            gyroCalibLabel.Text = "校正コマンドを送れませんでした";
            return;
        }
        _gyroCalibrating = true;
        gyroCalibLabel.Text = "校正中…（機体を動かさないでください）";
        UpdateModeButtons();

        // 応答が来なければボタンを戻す（古いファームウェアなど）
        await Task.Delay(GYRO_CALIB_TIMEOUT_MS);
        if (_gyroCalibrating)
        {
            _gyroCalibrating = false;
            gyroCalibLabel.Text = "応答がありませんでした";
            UpdateModeButtons();
        }
    }

    // ジャイロ校正の結果を表示する
    private void ShowGyroCalibResult(byte[] data)
    {
        _gyroCalibrating = false;
        byte status = data[1];
        float bx = BitConverter.ToSingle(data, 2);
        float by = BitConverter.ToSingle(data, 6);
        float bz = BitConverter.ToSingle(data, 10);
        string bias = $"バイアス {bx:F2}, {by:F2}, {bz:F2} deg/s";
        gyroCalibLabel.Text = status switch
        {
            0 => $"校正完了: {bias}",
            1 => $"機体が動いていました（結果は使用中）: {bias}",
            2 => "スロットルが0でないため実行しませんでした",
            _ => $"不明な応答 ({status})",
        };
        gyroCalibLabel.TextColor = status == 0 ? Colors.Gray : Colors.OrangeRed;
        UpdateModeButtons();
    }

    private void OnNotificationReceived(object? sender, byte[] data)
    {
        if (_bleService == null) return;

        string key = "";
        if (sender is ICharacteristic characteristic)
        {
            key = _bleService.Characteristics.FirstOrDefault(x => x.Value == characteristic).Key ?? "";
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (key == "Sensor_Telem" && data.Length >= 24)
            {
                // 加速度・ジャイロ（6個）。新しいファームウェアは続けて地磁気（3個）も送る
                int count = Math.Min(data.Length / 4, 9);
                float[] floats = new float[count];
                for (int i = 0; i < count; i++)
                    floats[i] = BitConverter.ToSingle(data, i * 4);

                for (int i = 0; i < 3; i++)
                {
                    _accelLabels[i].Text = floats[i].ToString("F2");
                    _gyroLabels[i].Text = floats[i + 3].ToString("F2");
                    if (count >= 9) _magLabels[i].Text = floats[i + 6].ToString("F3");
                }
            }
            else if (key == "contU_TelemWrite" && data.Length >= 20)
            {
                float[] floats = new float[5];
                for (int i = 0; i < 5; i++)
                    floats[i] = BitConverter.ToSingle(data, i * 4);

                // u[0] は推力、u[1..4] はサーボ S1〜S4
                for (int i = 0; i < 5; i++)
                    _ctrlLabels[i].Text = floats[i].ToString("F1");
            }
            else if (key == "Command" && data.Length >= 14 && data[0] == CMD_GYRO_CALIB)
            {
                ShowGyroCalibResult(data);
            }
            else if (key == "PRY_Telem" && data.Length >= 12)
            {
                pitch = BitConverter.ToSingle(data, 0) * 180.0f / (float)Math.PI;
                roll = BitConverter.ToSingle(data, 4) * 180.0f / (float)Math.PI;
                yaw = BitConverter.ToSingle(data, 8) * 180.0f / (float)Math.PI;
                pitchValueLabel.Text = pitch.ToString("F1");
                rollValueLabel.Text = roll.ToString("F1");
                yawValueLabel.Text = yaw.ToString("F1");
            }
        });
    }

    // 明示的に呼べるクリーンアップ
    public void Cleanup()
    {
        if (_bleService != null)
        {
            _bleService.NotificationReceived -= OnNotificationReceived;
            _bleService.ControlStateChanged -= OnStateChanged;
            _bleService.ConnectionStateChanged -= OnStateChanged;
            _bleService.DroneStatusChanged -= OnDroneStatusChanged;
        }
    }
}
