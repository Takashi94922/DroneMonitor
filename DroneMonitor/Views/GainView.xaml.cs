using Plugin.BLE.Abstractions.Contracts;
using Microsoft.Maui.ApplicationModel;
using System.Diagnostics;
using System.Globalization;

namespace DroneMonitor.Views;

// 台の試験の状態。Tag は "none" / "pitch" / "roll"
public record RigMode(string Tag, string Name);

// ゲイン設定画面（PID ゲイン・KC ゲインを表で入力して送信）
public partial class GainView : ContentView
{
    private BleService? _bleService;

    // ファームウェア（Motion_control.h）の既定値。入力欄の薄字表示と「既定値で埋める」に使う
    private static readonly float[] PidDefaults =
    {
        7.2f, 0.0f, 0.54f,   // Pitch P, I, D
        9.6f, 0.0f, 2.0f,    // Roll  P, I, D
        0.0f, 0.0f, 0.0f,    // Yaw   P, I, D
    };
    private static readonly float[] KcDefaults =
    {
        0, 0, 0, 0, 0, 0,
        -2.5303691f, -6.3249978f, 4.4290784f, -44.534649f, -103.852f, 1.7407752f,
        2.5548081f, -6.1235034f, 0.0396076f, 42.480473f, -101.7184f, -0.0407247f,
        2.5303691f, 6.3249978f, -4.4290784f, 44.534649f, 103.852f, -1.7407752f,
        -2.5548081f, 6.1235034f, -0.0396076f, -42.480473f, 101.7184f, 0.0407247f,
    };

    private const int PidAxes = 3, PidTerms = 3;
    private const int KcRows = 5, KcCols = 6;

    // 入力欄と、最後に送信できた文字列（未送信の変更を黄色で示すため）
    private readonly Entry[] _pidEntries = new Entry[PidAxes * PidTerms];
    private readonly Entry[] _kcEntries = new Entry[KcRows * KcCols];
    private readonly Dictionary<Entry, string> _lastSentText = new();

    // ゲインの読み戻し。Command の通知で [21, 種類, ...] が返る
    // （種類 0=PID の float 9個、1〜5=KC の各行 float 6個、6=ゲインの切り替え [軸 u8, 基準 float, b0 float]）
    private const byte CMD_GAIN_READ = 21;
    private const byte GAIN_KIND_SCHED = 6;
    private const int GAIN_READ_TIMEOUT_MS = 3000;
    private const int GainReadParts = 1 + KcRows + 1;

    // スロットルによるゲインの切り替え [25, 軸 u8（bit0 pitch, bit1 roll）, 基準[%] float, b0[%] float]
    private const byte CMD_GAIN_SCHED = 25;
    private static readonly int[] SchedPreviewThrottles = { 40, 50, 55, 60 };
    private (byte mask, float tRef, float tZero)? _schedOnDrone; // 最後に読み戻したドローンの設定
    private readonly HashSet<int> _gainReadReceived = new();
    private bool _gainReading;

    // 台の試験。回す軸以外のゲインを0にする。切り替え前のゲインを控えておき、「なし」で送り直す
    private static readonly RigMode[] RigModes = { new("pitch", "ピッチ"), new("roll", "ロール") };
    private static readonly RigMode RigNone = new("none", "なし");
    public event EventHandler<RigMode>? RigModeChanged;
    private int _rigAxis = -1;          // -1: なし、0: ピッチ、1: ロール
    private float[]? _rigSaved;         // 切り替え前の PID ゲイン（pitch roll yaw の Kp Ki Kd）
    private bool _rigBusy;
    private TaskCompletionSource<float[]>? _pidReadTcs;
    private const int PID_READ_TIMEOUT_MS = 2000;

    private static readonly Color ChangedColor = Colors.LightYellow;
    private static readonly Color InvalidColor = Colors.MistyRose;

    public GainView()
    {
        InitializeComponent();

        for (int axis = 0; axis < PidAxes; axis++)
            for (int term = 0; term < PidTerms; term++)
                _pidEntries[axis * PidTerms + term] = AddEntryCell(pidGrid, axis + 1, term + 1, PidDefaults[axis * PidTerms + term]);

        for (int row = 0; row < KcRows; row++)
            for (int col = 0; col < KcCols; col++)
                _kcEntries[row * KcCols + col] = AddEntryCell(kcGrid, row + 1, col + 1, KcDefaults[row * KcCols + col]);

        UpdateRigButtons();
        UpdateSchedPreview();
    }

    public void SetBleService(BleService bleService)
    {
        if (_bleService != null)
        {
            _bleService.NotificationReceived -= OnNotificationReceived;
        }
        _bleService = bleService;
        _bleService.NotificationReceived += OnNotificationReceived;
    }

    private Entry AddEntryCell(Grid grid, int row, int column, float defaultValue)
    {
        var entry = new Entry
        {
            Placeholder = FormatValue(defaultValue),
            Keyboard = Keyboard.Numeric,
            HorizontalTextAlignment = TextAlignment.End,
        };
        entry.TextChanged += OnEntryTextChanged;
        _lastSentText[entry] = "";
        grid.Add(entry, column, row);
        return entry;
    }

    private static string FormatValue(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static bool TryParseValue(string? text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // 入力が変わったら色で状態を示す（読めない値＝赤、未送信の変更＝黄）
    private void OnEntryTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is Entry entry) UpdateEntryColor(entry);
    }

    private void UpdateEntryColor(Entry entry)
    {
        var text = entry.Text?.Trim() ?? "";
        if (text.Length > 0 && !TryParseValue(text, out _))
            entry.BackgroundColor = InvalidColor;
        else if (text != _lastSentText[entry])
            entry.BackgroundColor = ChangedColor;
        else
            entry.BackgroundColor = Colors.Transparent;
    }

    private void MarkSent(Entry entry)
    {
        _lastSentText[entry] = entry.Text?.Trim() ?? "";
        UpdateEntryColor(entry);
    }

    private async Task<bool> EnsureConnectedAsync()
    {
        if (_bleService != null && _bleService.IsConnected) return true;
        await Application.Current?.MainPage?.DisplayAlert("BLE接続エラー", "BLEが接続されていません。接続を確認してください。", "OK");
        return false;
    }

    // ---- ドローンからの読み込み ----

    private async void OnGainReadClicked(object? sender, EventArgs e)
    {
        if (!await EnsureConnectedAsync()) return;

        _gainReadReceived.Clear();
        _gainReading = true;
        gainReadButton.IsEnabled = false;
        gainReadStatusLabel.Text = "読み込み中…";
        gainReadStatusLabel.TextColor = Colors.Gray;

        if (!await _bleService!.WriteAsync("Command", new byte[] { CMD_GAIN_READ, 0 }))
        {
            FinishGainRead("読み込みコマンドを送れませんでした", Colors.Red);
            return;
        }

        // 応答がそろわなければボタンを戻す（古いファームウェアなど）
        await Task.Delay(GAIN_READ_TIMEOUT_MS);
        if (_gainReading)
            FinishGainRead($"応答がそろいませんでした（{_gainReadReceived.Count}/{GainReadParts}）", Colors.Red);
    }

    private void FinishGainRead(string message, Color color)
    {
        _gainReading = false;
        gainReadButton.IsEnabled = true;
        gainReadStatusLabel.Text = message;
        gainReadStatusLabel.TextColor = color;
    }

    private void OnNotificationReceived(object? sender, byte[] data)
    {
        if (_bleService == null || sender is not ICharacteristic characteristic) return;
        var key = _bleService.Characteristics.FirstOrDefault(x => x.Value == characteristic).Key;
        if (key != "Command" || data.Length < 2 || data[0] != CMD_GAIN_READ) return;
        MainThread.BeginInvokeOnMainThread(() => ApplyGainReadback(data));
    }

    private void ApplyGainReadback(byte[] data)
    {
        int kind = data[1];
        if (kind == 0 && data.Length >= 2 + sizeof(float) * _pidEntries.Length)
        {
            FillEntries(_pidEntries, 0, _pidEntries.Length, data);
            var values = new float[_pidEntries.Length];
            for (int i = 0; i < values.Length; i++) values[i] = BitConverter.ToSingle(data, 2 + i * sizeof(float));
            _pidReadTcs?.TrySetResult(values);
        }
        else if (kind >= 1 && kind <= KcRows && data.Length >= 2 + sizeof(float) * KcCols)
            FillEntries(_kcEntries, (kind - 1) * KcCols, KcCols, data);
        else if (kind == GAIN_KIND_SCHED && data.Length >= 3 + sizeof(float) * 2)
            ApplySchedReadback(data[2], BitConverter.ToSingle(data, 3), BitConverter.ToSingle(data, 7));
        else
            return;

        _gainReadReceived.Add(kind);
        if (_gainReading && _gainReadReceived.Count == GainReadParts)
            FinishGainRead("ドローンのゲインを読み込みました", Colors.Green);
    }

    // data[2..] の float を entries[first..] に入れる。ドローンと同じ値なので送信済みとして扱う
    private void FillEntries(Entry[] entries, int first, int count, byte[] data)
    {
        for (int i = 0; i < count; i++)
        {
            var entry = entries[first + i];
            entry.Text = FormatValue(BitConverter.ToSingle(data, 2 + i * sizeof(float)));
            MarkSent(entry);
        }
    }

    // ---- 台の試験 ----

    private async void OnRigClicked(object? sender, EventArgs e)
    {
        if (_rigBusy) return;
        int axis = sender == rigPitchButton ? 0 : sender == rigRollButton ? 1 : -1;
        if (axis == _rigAxis) return;
        if (!await EnsureConnectedAsync()) return;

        _rigBusy = true;
        UpdateRigButtons();
        try
        {
            // いまの試験を解除して、0にしていた軸のゲインを元に戻す
            if (_rigAxis >= 0 && _rigSaved != null)
            {
                SetRigStatus("元のゲインに戻しています…", Colors.Gray);
                for (int a = 0; a < PidAxes; a++)
                {
                    if (a == _rigAxis) continue;
                    for (int t = 0; t < PidTerms; t++)
                    {
                        if (!await SendPidValueAsync(a, t, _rigSaved[a * PidTerms + t]))
                        {
                            SetRigStatus("元に戻す途中で送信に失敗しました。もう一度押してください", Colors.Red);
                            return;
                        }
                    }
                }
                SetRigMode(-1);
            }
            if (axis < 0)
            {
                SetRigStatus("元のゲインに戻しました", Colors.Green);
                return;
            }

            // 元のゲインをドローンから読んで控え、回す軸以外を0にする
            SetRigStatus("ドローンのゲインを読み込んでいます…", Colors.Gray);
            var saved = await ReadPidFromDroneAsync();
            if (saved == null)
            {
                SetRigStatus("ドローンからゲインを読み込めませんでした", Colors.Red);
                return;
            }
            _rigSaved = saved;
            SetRigMode(axis); // 途中で失敗しても「なし」で戻せるよう、先に試験中にする
            for (int a = 0; a < PidAxes; a++)
            {
                if (a == axis) continue;
                for (int t = 0; t < PidTerms; t++)
                {
                    if (!await SendPidValueAsync(a, t, 0.0f))
                    {
                        SetRigStatus("0にする途中で送信に失敗しました。「なし」で元に戻してください", Colors.Red);
                        return;
                    }
                }
            }
            SetRigStatus($"{RigModes[axis].Name}以外の PID ゲインを0にしました", Colors.Green);
        }
        finally
        {
            _rigBusy = false;
            UpdateRigButtons();
        }
    }

    private void SetRigMode(int axis)
    {
        _rigAxis = axis;
        RigModeChanged?.Invoke(this, axis < 0 ? RigNone : RigModes[axis]);
    }

    private void SetRigStatus(string message, Color color)
    {
        rigStatusLabel.Text = message;
        rigStatusLabel.TextColor = color;
    }

    // 選択中の試験のボタンを強調する。切り替え中は押せなくする
    private void UpdateRigButtons()
    {
        var buttons = new[] { (rigNoneButton, -1), (rigPitchButton, 0), (rigRollButton, 1) };
        foreach (var (button, axis) in buttons)
        {
            bool selected = axis == _rigAxis;
            button.IsEnabled = !_rigBusy;
            button.BackgroundColor = selected ? (axis < 0 ? Colors.DimGray : Colors.DarkOrange) : Colors.LightGray;
            button.TextColor = selected ? Colors.White : Colors.Black;
            button.FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
        }
    }

    // ドローンの PID ゲイン（9個）を読む。応答がなければ null
    private async Task<float[]?> ReadPidFromDroneAsync()
    {
        _pidReadTcs = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!await _bleService!.WriteAsync("Command", new byte[] { CMD_GAIN_READ, 0 })) return null;
            var done = await Task.WhenAny(_pidReadTcs.Task, Task.Delay(PID_READ_TIMEOUT_MS));
            return done == _pidReadTcs.Task ? _pidReadTcs.Task.Result : null;
        }
        finally
        {
            _pidReadTcs = null;
        }
    }

    // PID ゲインを1項目送り、表の値も送った値にそろえる
    private async Task<bool> SendPidValueAsync(int axis, int term, float value)
    {
        var buf = new byte[6];
        buf[0] = (byte)(11 + axis); // 11:Pitch 12:Roll 13:Yaw
        buf[1] = (byte)term;        // 0:P 1:I 2:D
        Array.Copy(BitConverter.GetBytes(value), 0, buf, 2, 4);
        if (!await _bleService!.WriteAsync("Command", buf)) return false;
        var entry = _pidEntries[axis * PidTerms + term];
        entry.Text = FormatValue(value);
        MarkSent(entry);
        return true;
    }

    // ---- PID ----

    private void OnPidDefaultClicked(object? sender, EventArgs e)
    {
        for (int i = 0; i < _pidEntries.Length; i++)
            _pidEntries[i].Text = FormatValue(PidDefaults[i]);
    }

    // 入力済みかつ前回送信から変わった項目だけを、1項目ずつコマンド 11〜13 で送る
    private async void OnPidSendClicked(object? sender, EventArgs e)
    {
        // 送信前に全項目を検証し、読めない値があれば何も送らない
        var targets = new List<(int index, float value)>();
        for (int i = 0; i < _pidEntries.Length; i++)
        {
            var text = _pidEntries[i].Text?.Trim() ?? "";
            if (text.Length == 0 || text == _lastSentText[_pidEntries[i]]) continue;
            if (!TryParseValue(text, out var value))
            {
                pidStatusLabel.Text = "赤い項目を直してください";
                pidStatusLabel.TextColor = Colors.Red;
                return;
            }
            targets.Add((i, value));
        }
        if (targets.Count == 0)
        {
            pidStatusLabel.Text = "送信する変更はありません";
            pidStatusLabel.TextColor = Colors.Gray;
            return;
        }
        if (!await EnsureConnectedAsync()) return;

        pidSendButton.IsEnabled = false;
        int sent = 0;
        try
        {
            foreach (var (index, value) in targets)
            {
                if (!await SendPidValueAsync(index / PidTerms, index % PidTerms, value)) break;
                sent++;
            }
        }
        finally
        {
            pidSendButton.IsEnabled = true;
        }

        bool allOk = sent == targets.Count;
        pidStatusLabel.Text = allOk ? $"{sent} 項目を送信しました" : $"送信失敗（{sent}/{targets.Count} 項目まで送信）";
        pidStatusLabel.TextColor = allOk ? Colors.Green : Colors.Red;
        Debug.WriteLine($"PIDゲイン送信: {sent}/{targets.Count}");
    }

    // ---- スロットルによるゲインの切り替え ----

    private void ApplySchedReadback(byte mask, float tRef, float tZero)
    {
        _schedOnDrone = (mask, tRef, tZero);
        schedPitchCheck.IsChecked = (mask & 1) != 0;
        schedRollCheck.IsChecked = (mask & 2) != 0;
        schedRefEntry.Text = FormatValue(tRef);
        schedZeroEntry.Text = FormatValue(tZero);
        UpdateSchedPreview();
    }

    private byte SchedMask() => (byte)((schedPitchCheck.IsChecked ? 1 : 0) | (schedRollCheck.IsChecked ? 2 : 0));

    // 入力を読む。基準が b0 より 1% 以上大きくなければ読めないとする（ファームウェアも受け付けない）
    private bool TryReadSched(out float tRef, out float tZero) =>
        TryParseValue(schedRefEntry.Text?.Trim(), out tRef) & TryParseValue(schedZeroEntry.Text?.Trim(), out tZero)
        && tRef > tZero + 1 && tRef <= 100 && tZero >= 0;

    private void OnSchedInputChanged(object? sender, EventArgs e) => UpdateSchedPreview();

    // 倍率の見込み（ファームウェアの GainSchedule::scale と同じ式）と、ドローンの設定との違いを示す
    private void UpdateSchedPreview()
    {
        if (schedPreviewLabel == null) return; // InitializeComponent の途中
        if (!TryReadSched(out var tRef, out var tZero))
        {
            schedPreviewLabel.Text = "基準は b0 より大きく、100 以下にしてください";
            schedPreviewLabel.TextColor = Colors.Red;
            return;
        }
        byte mask = SchedMask();
        string scales = string.Join("、", SchedPreviewThrottles.Select(t =>
        {
            float s = t <= tRef ? 1f : Math.Max(0.2f, (tRef - tZero) / (t - tZero));
            return $"{t}% ×{s:0.00}";
        }));
        bool changed = _schedOnDrone is not { } d || d.mask != mask || d.tRef != tRef || d.tZero != tZero;
        schedPreviewLabel.Text = (mask == 0 ? "切り替えなし" : $"倍率: {scales}") + (changed ? "（ドローンの設定と違います）" : "");
        schedPreviewLabel.TextColor = changed ? Colors.DarkOrange : Colors.Gray;
    }

    private async void OnSchedSendClicked(object? sender, EventArgs e)
    {
        if (!TryReadSched(out var tRef, out var tZero))
        {
            schedStatusLabel.Text = "入力を直してください";
            schedStatusLabel.TextColor = Colors.Red;
            return;
        }
        if (!await EnsureConnectedAsync()) return;

        var buf = new byte[2 + sizeof(float) * 2];
        buf[0] = CMD_GAIN_SCHED;
        buf[1] = SchedMask();
        BitConverter.GetBytes(tRef).CopyTo(buf, 2);
        BitConverter.GetBytes(tZero).CopyTo(buf, 6);
        schedSendButton.IsEnabled = false;
        bool ok;
        try
        {
            ok = await _bleService!.WriteAsync("Command", buf);
        }
        finally
        {
            schedSendButton.IsEnabled = true;
        }
        // ドローンは受け取ると全ゲインを読み戻して送ってくる。届けば「ドローンの設定と違います」が消える
        schedStatusLabel.Text = ok ? "送信しました" : "送信失敗";
        schedStatusLabel.TextColor = ok ? Colors.Green : Colors.Red;
    }

    // ---- KC ----

    private void OnKcDefaultClicked(object? sender, EventArgs e)
    {
        for (int i = 0; i < _kcEntries.Length; i++)
            _kcEntries[i].Text = FormatValue(KcDefaults[i]);
    }

    // 設計ツール等からコピーした30個の数値（カンマ・空白・改行区切り）を行優先で入れる
    private async void OnKcPasteClicked(object? sender, EventArgs e)
    {
        var text = await Clipboard.Default.GetTextAsync() ?? "";
        var tokens = text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
        var values = new List<float>();
        foreach (var token in tokens)
        {
            if (!TryParseValue(token, out var v))
            {
                kcStatusLabel.Text = $"数値として読めません: {token}";
                kcStatusLabel.TextColor = Colors.Red;
                return;
            }
            values.Add(v);
        }
        if (values.Count != _kcEntries.Length)
        {
            kcStatusLabel.Text = $"{_kcEntries.Length} 個の数値が必要です（クリップボードは {values.Count} 個）";
            kcStatusLabel.TextColor = Colors.Red;
            return;
        }
        for (int i = 0; i < _kcEntries.Length; i++)
            _kcEntries[i].Text = FormatValue(values[i]);
        kcStatusLabel.Text = "貼り付けました（未送信）";
        kcStatusLabel.TextColor = Colors.Gray;
    }

    // 30個すべてそろっているときだけ、まとめて ContGain_Upd に送る
    private async void OnKcSendClicked(object? sender, EventArgs e)
    {
        var values = new float[_kcEntries.Length];
        for (int i = 0; i < _kcEntries.Length; i++)
        {
            if (!TryParseValue(_kcEntries[i].Text?.Trim(), out values[i]))
            {
                kcStatusLabel.Text = "30個すべてに数値を入力してください（空欄・赤い項目があります）";
                kcStatusLabel.TextColor = Colors.Red;
                return;
            }
        }
        if (!await EnsureConnectedAsync()) return;

        kcSendButton.IsEnabled = false;
        bool ok;
        try
        {
            var bytes = values.SelectMany(BitConverter.GetBytes).ToArray();
            ok = await _bleService!.WriteAsync("ContGain_Upd", bytes);
        }
        finally
        {
            kcSendButton.IsEnabled = true;
        }

        if (ok)
        {
            foreach (var entry in _kcEntries) MarkSent(entry);
        }
        kcStatusLabel.Text = ok ? "KC を送信しました" : "送信失敗";
        kcStatusLabel.TextColor = ok ? Colors.Green : Colors.Red;
        Debug.WriteLine($"KCゲイン送信: {ok}");
    }
}
