using EmbedIO.Utilities;
using Plugin.BLE.Abstractions.Contracts;
using System;
using System.Diagnostics;
using System.Text;

namespace DroneMonitor.Views;

public partial class ControlDataPage : ContentPage
{
    private BleService? _bleService;
    private float pitch, roll, yaw;
    public List<string> OptionList { get; } = new() { "Pitch P", "Pitch I", "Pitch D", "Roll P", "Roll I", "Roll D", "Yaw P", "Yaw I", "Yaw D"};
    public List<float> PIDvalues { get; set; } = new() { 6.0f, 0, 3.0f, 10.0f, 0, 8.0f, 0, 0, 0 };

    public ControlDataPage()
    {
        InitializeComponent();
        BindingContext = this;
        setGainLabel(-1, -1);
    }
    public async void StartNotificationAsync()
    {
        // 通知開始の成否をログ出力
        await _bleService.StartNotificationAsync("Xhat_Telem").ContinueWith(t =>
            Debug.WriteLine($"Xhat_Telem通知開始: {t.Result}"));
        await _bleService.StartNotificationAsync("contU_TelemWrite").ContinueWith(t =>
            Debug.WriteLine($"contU_TelemWrite通知開始: {t.Result}"));
        /*
        await _bleService.StartNotificationAsync("Command").ContinueWith(t =>
            Debug.WriteLine($"Command通知開始: {t.Result}"));*/
        await _bleService.StartNotificationAsync("PRY_Telem").ContinueWith(t =>
            Debug.WriteLine($"PRY_Telem通知開始: {t.Result}"));
    }
    public void SetBleService(BleService bleService)
    {
        _bleService = bleService;
        _bleService.NotificationReceived -= OnNotificationReceived;
        _bleService.NotificationReceived += OnNotificationReceived;
        if (_bleService != null && _bleService.IsConnected)
        {
            StartNotificationAsync();
        }
        UpdateControlButtonState();
    }
    public void UpdateControlButtonState()
    {
        beginControl.IsEnabled = _bleService.IsConnected ? true : false;
        beginControl.Text = _bleService.IsControlBySelf ? "制御停止" : "制御開始";
        beginControl.BackgroundColor = _bleService.IsControlBySelf ? Colors.Red : Colors.Blue; 
    }

    private void OnNotificationReceived(object? sender, byte[] data)
    {
        //Debug.WriteLine("OnNotificationReceived発火");
        if (_bleService == null)
        {
            Debug.WriteLine("BLE 未接続");
            return;
        }

        // senderの型をログ出力
        //Debug.WriteLine($"sender type: {sender?.GetType().FullName}");

        // senderがICharacteristicでない場合、全Characteristicからdata一致で特定
        string key = "";
        if (sender is ICharacteristic characteristic)
        {
            key = _bleService.Characteristics.FirstOrDefault(x => x.Value == characteristic).Key ?? "";
        }

        //Debug.WriteLine($"受信: key={key}, data={BitConverter.ToString(data)}");
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (key == "Xhat_Telem" && data.Length >= 24)
            {
                float[] floats = new float[6];
                for (int i = 0; i < 6; i++)
                    floats[i] = BitConverter.ToSingle(data, i * 4);

                accelValueLabel.Text = $"{floats[0]:F2}, {floats[1]:F2}, {floats[2]:F2}";
                velocityValueLabel.Text = $"{floats[3]:F2}, {floats[4]:F2}, {floats[5]:F2}";
                positionValueLabel.Text = "";
            }
            else if (key == "contU_TelemWrite" && data.Length >= 20)
            {
                float[] floats = new float[5];
                for (int i = 0; i < 5; i++)
                    floats[i] = BitConverter.ToSingle(data, i * 4);

                ctrlValueLabel.Text = string.Join("\n", floats.Select(f => f.ToString("F2")));
            }
            else if (key == "PRY_Telem")
            {
                for (int i = 0; i < 3; i++)
                {
                    float value = BitConverter.ToSingle(data, i * 4);
                    switch (i)
                    {
                        case 0: pitch = value * 180.0f / (float)Math.PI; break;
                        case 1: roll = value * 180.0f / (float)Math.PI; break;
                        case 2: yaw = value * 180.0f / (float)Math.PI; break;
                    }
                }
                pryLabel.Text = $"PRY: {pitch:F2}, {roll:F2}, {yaw:F2}";
            }
        });
    }
    public async void OnControlButtonClicked(object sender, EventArgs e)
    {
        if (_bleService != null && _bleService.IsConnected)
        {
            if(sender is Button beginControl)
            {
                // ボタンのテキストを確認して適切な処理を実行
                if (!_bleService.IsControlBySelf)
                {
                    // 制御開始の処理をここに追加
                    Debug.WriteLine("制御開始ボタンがクリックされました");
                    if(_bleService.Characteristics.TryGetValue("Command", out var CharCommand)){ 
                        _bleService.IsControlBySelf = true; // 制御開始フラグを設定
                        await CharCommand.WriteAsync(new byte[] { 0x05, 0x00 }); // 制御開始コマンドを送信 0x00はダミーデータ
                    }
                }
                else if (beginControl.Text == "制御停止")
                {
                    // 制御停止の処理をここに追加
                    if(_bleService.Characteristics.TryGetValue("Command", out var CharCommand)){
                        _bleService.IsControlBySelf = false; // 制御停止フラグを設定
                        await CharCommand.WriteAsync(new byte[] { 0x06, 0x00 }); // 制御開始コマンドを送信0x00
                    }
                }
                UpdateControlButtonState();
            }
        }
        else
        {
            Debug.WriteLine("BLEサービスが接続されていません");
            DisplayAlert("BLE接続エラー", "BLEサービスが接続されていません。接続を確認してください。", "OK");   
        }
    }
    private void KCEntry_TextChanged(object sender, TextChangedEventArgs e)
    {
        string input = e.NewTextValue ?? "";
        // 末尾カンマ除外、空文字除外のための処理
        var count = input
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
        if(sender == KCEntry)
        {
            PIDUpdateButton.IsEnabled = (count == 5 * 6);
            PIDUpdateButton.Text = (count == 5 * 6) ? $"送信" : $"入力済み：{count}個";
        }
        else if(sender == PIDgainEntry)
        {
            PIDUpdateButton.IsEnabled = (count == 1);
            PIDUpdateButton.Text = (count == 1)? $"送信" : $"入力済み：{count}個";
        }        
    }
    
    public void OnSendClicked(object sender, EventArgs e)
    {
        int KCcount = 0;
        string input = "";
        string characteristicKey = "";
        if (sender == KCUpdateButton)
        {
            input = KCEntry.Text;
            characteristicKey = "ContGain_Upd";
            KCcount = 30;
        }
        else if (sender == PIDUpdateButton)
        {
            input = PIDgainEntry.Text;
            characteristicKey = "Command";
            KCcount = 1;
        }
        else return;

        List<float> valueArray = new();
        if (_bleService != null && _bleService.IsConnected && !string.IsNullOrWhiteSpace(input))
        {
            // ここで送信処理や表示ロジックを追加！
            input.SplitByComma().ToList().ForEach(x =>
            {
                if (float.TryParse(x, out float value))
                {
                    valueArray.Add(value);
                }
                else
                {
                    Debug.WriteLine($"無効な入力: {x}");
                    return;
                }
            });
            if (valueArray.Count != KCcount)
            {
                DisplayAlert("入力エラー", $"{KCcount}つの値をカンマ区切りで入力してください。", "OK");
                Debug.WriteLine($"入力エラー: {KCcount}30つの値をカンマ区切りで入力してください。");
                return;
            }
            // 送信する値をfloatに変換して処理
            if (_bleService.Characteristics.TryGetValue(characteristicKey, out var characteristic))
            {
                if(characteristicKey == "ContGain_Upd")
                {
                    // 送信データをバイト配列に変換
                    var valueBytes = valueArray.SelectMany(BitConverter.GetBytes).ToList();
                    characteristic.WriteAsync(valueBytes.ToArray()).ContinueWith(t =>
                        Debug.WriteLine($"送信結果: {t.Result}"));
                }
                else if(characteristicKey == "Command")
                {
                    // PIDゲイン更新のためのコマンド送信
                    var index = PIDPicker.SelectedIndex;

                    var valueBytes = new byte[6];
                    valueBytes[0] = (byte)(11 + index / 3); // コマンドID
                    valueBytes[1] = (byte)(index % 3); // 選択されたPIDゲインのインデックス
                    Array.Copy(BitConverter.GetBytes(valueArray[0]), 0, valueBytes, 2, 4); // PID値はfloat
                    Debug.WriteLine($"PIDゲイン送信: コマンドID={valueBytes[0]}, インデックス={valueBytes[1]}, 値={valueBytes[2]}");
                    characteristic.WriteAsync(valueBytes).ContinueWith(t =>
                        Debug.WriteLine($"PIDゲイン送信結果: {t.Result}"));

                    setGainLabel(index, valueArray[0]);
                }
            }
        }
    }

    private void setGainLabel(int index, float newGain)
    {
        if (index != -1)
        {
            PIDvalues[index] = newGain; // PID値を更新
        }

        //3要素ごとに改行する
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < PIDvalues.Count; i++)
        {
            sb.Append(PIDvalues[i].ToString("F2")); // 小数点以下2桁
            if ((i + 1) % 3 == 0)
                sb.AppendLine(); // グループの終わりで改行
            else
                sb.Append(", "); // グループ内はカンマ区切り
        }
        PIDLabel.Text = $"ゲイン: {sb.ToString()}";
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (_bleService != null)
        {
            _bleService.NotificationReceived -= OnNotificationReceived;
        }
    }
}