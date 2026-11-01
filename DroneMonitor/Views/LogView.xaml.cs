using Plugin.BLE.Abstractions.Contracts;
using Microsoft.Maui.ApplicationModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DroneMonitor.Views;

// 制御周期の記録画面。記録開始のコマンドを送り、Log_Telem で届いた記録を CSV に保存する。
// 形式はファームウェアの flight_log.h と合わせる
public partial class LogView : ContentView
{
    private BleService? _bleService;

    private const byte CMD_LOG_START = 23;
    private const byte CMD_LOG_RESEND = 24;
    private const byte PKT_HEADER = 0, PKT_DATA = 1, PKT_END = 2;
    private const int SampleSize = 46;   // FlightLogSample のバイト数
    private const int SampleValues = 23; // int16 の個数
    private const int MaxSeconds = 10;
    private const int MaxResend = 5;
    private static readonly TimeSpan PacketTimeout = TimeSpan.FromSeconds(3);

    // スケール（flight_log.h の FLIGHT_LOG_SCALE_*）。列の並びは FlightLogSample と同じ
    private const double ScalePercent = 100.0, ScaleAngle = 10000.0, ScaleRate = 1000.0;
    private static readonly (string Name, double Scale)[] Columns =
    {
        ("throttle_pct", ScalePercent),
        ("target_pitch_rad", ScaleAngle), ("target_roll_rad", ScaleAngle), ("target_yaw_rad", ScaleAngle),
        ("pitch_rad", ScaleAngle), ("roll_rad", ScaleAngle), ("yaw_rad", ScaleAngle),
        ("rate_pitch_rads", ScaleRate), ("rate_roll_rads", ScaleRate), ("rate_yaw_rads", ScaleRate),
        ("pitch_P_pct", ScalePercent), ("pitch_I_pct", ScalePercent), ("pitch_D_pct", ScalePercent),
        ("roll_P_pct", ScalePercent), ("roll_I_pct", ScalePercent), ("roll_D_pct", ScalePercent),
        ("yaw_P_pct", ScalePercent), ("yaw_I_pct", ScalePercent), ("yaw_D_pct", ScalePercent),
        ("servo1_pct", ScalePercent), ("servo2_pct", ScalePercent), ("servo3_pct", ScalePercent), ("servo4_pct", ScalePercent),
    };
    private static readonly string[] ControlModeNames = { "None", "MIMO", "PID" };

    // 受信中の記録
    private bool _active;
    private bool _headerReceived;
    private int _periodUs;
    private int _sampleCount;
    private byte _controlMode;
    private readonly float[] _gains = new float[9];
    private readonly float[] _targets = new float[3];
    // スロットルによるゲインの切り替え（ヘッダの版2から。null なら古いファームウェアで不明）
    private (byte mask, float tRef, float tZero)? _sched;
    private byte[] _data = Array.Empty<byte>();
    private bool[] _received = Array.Empty<bool>();
    private int _receivedCount;
    private int _resendCount;
    private DateTime _lastPacketAt;
    private string _memo = "";
    private string _step = "none"; // CSV のコメント用
    private string _rigTag = "none", _rigName = "なし";

    public LogView()
    {
        InitializeComponent();
    }

    public void SetBleService(BleService bleService)
    {
        if (_bleService != null)
        {
            _bleService.NotificationReceived -= OnNotificationReceived;
        }
        _bleService = bleService;
        _bleService.NotificationReceived += OnNotificationReceived;
        if (_bleService.IsConnected)
        {
            _ = _bleService.StartNotificationAsync("Log_Telem");
        }
    }

    // 台の試験の状態（Gains タブで切り替える）。CSV のファイル名とコメントに入れる
    public void SetRigAxis(string tag, string name)
    {
        _rigTag = tag;
        _rigName = name;
        rigLabel.Text = name;
        rigLabel.TextColor = tag == "none" ? Colors.Black : Colors.DarkOrange;
    }

    private static string LogFolder =>
#if WINDOWS
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DroneLogs");
#else
        Path.Combine(FileSystem.AppDataDirectory, "DroneLogs");
#endif

    // ---- 記録の開始 ----

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        if (!int.TryParse(secondsEntry.Text?.Trim(), out int seconds) || seconds < 1 || seconds > MaxSeconds)
        {
            SetStatus($"記録時間は 1〜{MaxSeconds} 秒で入力してください", Colors.Red);
            return;
        }
        if (_bleService == null || !_bleService.IsConnected)
        {
            SetStatus("BLE が接続されていません", Colors.Red);
            return;
        }
        if (!_bleService.Characteristics.ContainsKey("Log_Telem"))
        {
            SetStatus("ファームウェアが記録に対応していません（Log_Telem がありません）", Colors.Red);
            return;
        }

        byte[] command = { CMD_LOG_START, (byte)seconds };
        _step = "none";
        if (stepCheck.IsChecked)
        {
            int axis = _rigTag switch { "pitch" => 0, "roll" => 1, _ => -1 };
            var ci = CultureInfo.InvariantCulture;
            if (axis < 0)
            {
                SetStatus("ステップの軸がありません。Gains タブの台の試験でピッチかロールを選んでください", Colors.Red);
                return;
            }
            if (!int.TryParse(stepAmpEntry.Text?.Trim(), NumberStyles.Integer, ci, out int amp) || amp == 0 || amp < -30 || amp > 30
                || !double.TryParse(stepOnEntry.Text?.Trim(), NumberStyles.Float, ci, out double on)
                || !double.TryParse(stepOffEntry.Text?.Trim(), NumberStyles.Float, ci, out double off)
                || on < 0 || off <= on || off > seconds)
            {
                SetStatus($"ステップの設定を確かめてください（振幅は ±1〜30°の整数、0 ≦ 開始 < 戻す ≦ 記録時間）", Colors.Red);
                return;
            }
            byte onDs = (byte)Math.Round(on * 10), offDs = (byte)Math.Round(off * 10);
            command = new byte[] { CMD_LOG_START, (byte)seconds, (byte)axis, unchecked((byte)(sbyte)amp), onDs, offDs };
            _step = $"{_rigTag} {amp:+0;-0}deg on={onDs / 10.0:0.0}s off={offDs / 10.0:0.0}s";
        }

        await _bleService.StartNotificationAsync("Log_Telem");
        ResetReceive();
        _memo = memoEntry.Text?.Trim() ?? "";
        _active = true;
        _lastPacketAt = DateTime.Now;
        startButton.IsEnabled = false;
        savedPathLabel.Text = "";
        SetStatus($"記録中…（{seconds} 秒）", Colors.Gray);

        if (!await _bleService.WriteAsync("Command", command))
        {
            Finish("記録開始のコマンドを送れませんでした", Colors.Red);
            return;
        }
        await WatchAsync(seconds);
    }

    private void ResetReceive()
    {
        _headerReceived = false;
        _sampleCount = 0;
        _data = Array.Empty<byte>();
        _received = Array.Empty<bool>();
        _receivedCount = 0;
        _resendCount = 0;
        progressBar.Progress = 0;
    }

    // 受信が止まったら、足りない分を送り直してもらう。何も届かなければ諦める
    private async Task WatchAsync(int seconds)
    {
        var deadline = DateTime.Now + TimeSpan.FromSeconds(seconds + 5);
        while (_active)
        {
            await Task.Delay(500);
            if (!_active) break;
            if (_bleService == null || !_bleService.IsConnected)
            {
                Finish("切断されました", Colors.Red);
                break;
            }
            if (!_headerReceived)
            {
                if (DateTime.Now > deadline) Finish("記録が届きませんでした", Colors.Red);
                continue;
            }
            if (DateTime.Now - _lastPacketAt > PacketTimeout)
            {
                await RequestMissingAsync();
            }
        }
    }

    private void Finish(string message, Color color)
    {
        _active = false;
        startButton.IsEnabled = true;
        SetStatus(message, color);
    }

    private void SetStatus(string message, Color color)
    {
        statusLabel.Text = message;
        statusLabel.TextColor = color;
    }

    // ---- 受信 ----

    private void OnNotificationReceived(object? sender, byte[] data)
    {
        if (_bleService == null || sender is not ICharacteristic characteristic) return;
        if (!_bleService.Characteristics.TryGetValue("Log_Telem", out var log) || characteristic != log) return;
        var copy = (byte[])data.Clone(); // 通知のバッファは使い回されることがあるので写しておく
        MainThread.BeginInvokeOnMainThread(() => HandlePacket(copy));
    }

    private async void HandlePacket(byte[] data)
    {
        if (!_active || data.Length < 1) return;
        _lastPacketAt = DateTime.Now;

        switch (data[0])
        {
            case PKT_HEADER:
                if (data.Length < 8 + 4 * 12) return;
                if (data[6] != SampleSize)
                {
                    Finish($"記録の形式が違います（サンプル {data[6]} バイト）。ファームウェアとモニターを合わせてください", Colors.Red);
                    return;
                }
                _periodUs = BitConverter.ToUInt16(data, 2);
                int count = BitConverter.ToUInt16(data, 4);
                _controlMode = data[7];
                for (int i = 0; i < 9; i++) _gains[i] = BitConverter.ToSingle(data, 8 + i * 4);
                for (int i = 0; i < 3; i++) _targets[i] = BitConverter.ToSingle(data, 44 + i * 4);
                _sched = data.Length >= 56 + 9
                    ? (data[56], BitConverter.ToSingle(data, 57), BitConverter.ToSingle(data, 61))
                    : null;
                if (!_headerReceived || count != _sampleCount)
                {
                    _sampleCount = count;
                    _data = new byte[count * SampleSize];
                    _received = new bool[count];
                    _receivedCount = 0;
                }
                _headerReceived = true;
                SetStatus($"受信中… 0/{_sampleCount}", Colors.Gray);
                break;

            case PKT_DATA:
                if (!_headerReceived || data.Length < 4) return;
                int n = data[1];
                int first = BitConverter.ToUInt16(data, 2);
                for (int i = 0; i < n && 4 + (i + 1) * SampleSize <= data.Length; i++)
                {
                    int index = first + i;
                    if (index >= _sampleCount) break;
                    Buffer.BlockCopy(data, 4 + i * SampleSize, _data, index * SampleSize, SampleSize);
                    if (!_received[index])
                    {
                        _received[index] = true;
                        _receivedCount++;
                    }
                }
                progressBar.Progress = _sampleCount > 0 ? (double)_receivedCount / _sampleCount : 0;
                SetStatus($"受信中… {_receivedCount}/{_sampleCount}", Colors.Gray);
                break;

            case PKT_END:
                if (!_headerReceived)
                {
                    await RequestResendAsync(0, 0); // ヘッダが落ちた。全部送り直してもらう
                    return;
                }
                if (_receivedCount == _sampleCount)
                    await SaveAsync();
                else
                    await RequestMissingAsync();
                break;
        }
    }

    // 最初に抜けているところから、続けて抜けている分をまとめて送り直してもらう
    private async Task RequestMissingAsync()
    {
        if (!_active) return;
        if (!_headerReceived)
        {
            await RequestResendAsync(0, 0);
            return;
        }
        int first = Array.IndexOf(_received, false);
        if (first < 0)
        {
            await SaveAsync();
            return;
        }
        int last = first;
        while (last + 1 < _sampleCount && !_received[last + 1]) last++;
        await RequestResendAsync(first, last - first + 1);
    }

    private async Task RequestResendAsync(int first, int count)
    {
        if (++_resendCount > MaxResend)
        {
            Finish($"送り直しても受信がそろいませんでした（{_receivedCount}/{_sampleCount}）", Colors.Red);
            return;
        }
        _lastPacketAt = DateTime.Now;
        var cmd = new byte[5];
        cmd[0] = CMD_LOG_RESEND;
        BitConverter.GetBytes((ushort)first).CopyTo(cmd, 1);
        BitConverter.GetBytes((ushort)count).CopyTo(cmd, 3);
        Debug.WriteLine($"記録の送り直し: {first} から {count} 個");
        await _bleService!.WriteAsync("Command", cmd);
    }

    // ---- 保存 ----

    private async Task SaveAsync()
    {
        _active = false;
        try
        {
            Directory.CreateDirectory(LogFolder);
            string name = $"{DateTime.Now:yyyyMMdd_HHmmss}" + (_rigTag == "none" ? "" : $"_rig-{_rigTag}") + ".csv";
            string path = Path.Combine(LogFolder, name);
            await File.WriteAllTextAsync(path, BuildCsv(), new UTF8Encoding(false));
            savedPathLabel.Text = path;
            Finish($"保存しました（{_sampleCount} サンプル、{_sampleCount * _periodUs / 1e6:0.##} 秒）", Colors.Green);
        }
        catch (Exception ex)
        {
            Finish($"保存に失敗しました: {ex.Message}", Colors.Red);
        }
    }

    private string BuildCsv()
    {
        var ci = CultureInfo.InvariantCulture;
        string F(float v) => v.ToString("0.######", ci);
        string mode = _controlMode < ControlModeNames.Length ? ControlModeNames[_controlMode] : _controlMode.ToString(ci);

        var sb = new StringBuilder();
        sb.AppendLine("# DroneMonitor flight log v1");
        sb.AppendLine($"# date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# period_us: {_periodUs}");
        sb.AppendLine($"# samples: {_sampleCount}");
        sb.AppendLine($"# control_mode: {_controlMode} ({mode})");
        sb.AppendLine($"# rig: {_rigTag}");
        sb.AppendLine($"# memo: {_memo.Replace('\n', ' ').Replace('\r', ' ')}");
        sb.AppendLine($"# step: {_step}");
        sb.AppendLine($"# pid_pitch: Kp={F(_gains[0])} Ki={F(_gains[1])} Kd={F(_gains[2])}");
        sb.AppendLine($"# pid_roll: Kp={F(_gains[3])} Ki={F(_gains[4])} Kd={F(_gains[5])}");
        sb.AppendLine($"# pid_yaw: Kp={F(_gains[6])} Ki={F(_gains[7])} Kd={F(_gains[8])}");
        sb.AppendLine($"# target_rad: pitch={F(_targets[0])} roll={F(_targets[1])} yaw={F(_targets[2])}");
        // ゲインの切り替えが有効なら、上の pid_* は基準スロットルでの値。実際のゲインは倍率を掛けたもの
        if (_sched is { } s)
        {
            var axes = new[] { (1, "pitch"), (2, "roll"), (4, "yaw") }.Where(a => (s.mask & a.Item1) != 0).Select(a => a.Item2).ToArray();
            sb.AppendLine(axes.Length == 0
                ? "# gain_sched: off"
                : $"# gain_sched: {string.Join("+", axes)} t_ref={F(s.tRef)} t_zero={F(s.tZero)}");
        }
        else
        {
            sb.AppendLine("# gain_sched: unknown");
        }

        sb.Append("t_s");
        foreach (var c in Columns) sb.Append(',').Append(c.Name);
        sb.AppendLine();

        for (int i = 0; i < _sampleCount; i++)
        {
            sb.Append((i * _periodUs / 1e6).ToString("0.######", ci));
            int offset = i * SampleSize;
            for (int k = 0; k < SampleValues; k++)
            {
                short raw = BitConverter.ToInt16(_data, offset + k * 2);
                sb.Append(',').Append((raw / Columns[k].Scale).ToString("0.######", ci));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private void OnOpenFolderClicked(object? sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
#if WINDOWS
            Process.Start(new ProcessStartInfo("explorer.exe", LogFolder) { UseShellExecute = true });
#else
            savedPathLabel.Text = LogFolder;
#endif
        }
        catch (Exception ex)
        {
            SetStatus($"フォルダを開けませんでした: {ex.Message}", Colors.Red);
        }
    }
}
