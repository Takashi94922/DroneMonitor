using Plugin.BLE.Abstractions.Contracts;
using Microsoft.Maui.ApplicationModel;
using System.Diagnostics;
using System.Globalization;

namespace DroneMonitor.Views;

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

    // ゲインの読み戻し。Command の通知で [21, 種類, float...] が返る（種類 0=PID の9個、1〜5=KC の各行6個）
    private const byte CMD_GAIN_READ = 21;
    private const int GAIN_READ_TIMEOUT_MS = 3000;
    private const int GainReadParts = 1 + KcRows;
    private readonly HashSet<int> _gainReadReceived = new();
    private bool _gainReading;

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
            FillEntries(_pidEntries, 0, _pidEntries.Length, data);
        else if (kind >= 1 && kind <= KcRows && data.Length >= 2 + sizeof(float) * KcCols)
            FillEntries(_kcEntries, (kind - 1) * KcCols, KcCols, data);
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
                var buf = new byte[6];
                buf[0] = (byte)(11 + index / PidTerms); // 11:Pitch 12:Roll 13:Yaw
                buf[1] = (byte)(index % PidTerms);      // 0:P 1:I 2:D
                Array.Copy(BitConverter.GetBytes(value), 0, buf, 2, 4);
                if (!await _bleService!.WriteAsync("Command", buf)) break;
                MarkSent(_pidEntries[index]);
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
