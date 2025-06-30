using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Utilities;
using Microsoft.Maui.Controls;
using Plugin.BLE.Abstractions.Contracts;

namespace DroneMonitor.Views;

public partial class ControlDataPage : ContentPage
{
    private BleService? _bleService;

    public ControlDataPage()
    {
        InitializeComponent();
    }
    public void StartNotificationAsync()
    {
        // 通知開始の成否をログ出力
        _bleService.StartNotificationAsync("Xhat_Telem").ContinueWith(t =>
            Debug.WriteLine($"Xhat_Telem通知開始: {t.Result}"));
        _bleService.StartNotificationAsync("contU_TelemWrite").ContinueWith(t =>
            Debug.WriteLine($"contU_TelemWrite通知開始: {t.Result}"));
        _bleService.StartNotificationAsync("Command").ContinueWith(t =>
            Debug.WriteLine($"Command通知開始: {t.Result}"));
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
        else
        {
            // senderがBleService自身の場合（Invoke(this, data)のような実装）
            // dataが一致するCharacteristicを探す
            foreach (var pair in _bleService.Characteristics)
            {
                // ここではValueUpdatedイベントのsenderがCharacteristicであることが前提
                // それ以外の場合はキーを特定できないので空文字
            }
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

                ctrlValueLabel.Text = string.Join(", ", floats.Select(f => f.ToString("F2")));
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
        if(count == 5 * 6)
        {
            //KCUpdateButton.BackgroundColor = Colors.Blue;
            KCUpdateButton.Text = $"送信";
            KCUpdateButton.IsEnabled = true;
        }
        else
        {
            KCUpdateButton.IsEnabled = false;
            KCUpdateButton.Text = $"入力済み：{count}個";
        }
        
    }
    public void OnSendClicked(object sender, EventArgs e)
    {
        string input = messageEntry.Text;
        List<float> KCvalueArray = new();
        if (_bleService != null && _bleService.IsConnected)
        {
            if (!string.IsNullOrWhiteSpace(input))
            {
                // ここで送信処理や表示ロジックを追加！
                Debug.WriteLine($"送信内容: ");
                input.SplitByComma().ToList().ForEach(x =>
                {
                    if (float.TryParse(x, out float value))
                    {
                        Debug.Write($"{value:F2},");
                        KCvalueArray.Add(value);
                    }
                    else
                    {
                        Debug.WriteLine($"無効な入力: {x}");
                    }
                });
                Debug.WriteLine($"end");
                if (KCvalueArray.Count != 30)
                {
                    DisplayAlert("入力エラー", "30つの値をカンマ区切りで入力してください。", "OK");
                    Debug.WriteLine("入力エラー: 30つの値をカンマ区切りで入力してください。");
                    return;
                }
                // 送信する値をfloatに変換して処理
                if (_bleService.Characteristics.TryGetValue("ContGain_Upd", out var characteristic))
                {
                    // 送信データをバイト配列に変換
                    characteristic.WriteAsync(KCvalueArray.SelectMany(BitConverter.GetBytes).ToArray()).ContinueWith(t =>
                        Debug.WriteLine($"ContGain_Upd送信結果: {t.Result}"));
                }
            }
        }

    }
    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (_bleService != null)
        {
            // 必要な通知キーを指定して停止
            _bleService.StopNotificationAsync("Xhat_Telem");
            _bleService.StopNotificationAsync("contU_TelemWrite");
            // 他にも通知を止めたいCharacteristicがあればここで追加
            _bleService.NotificationReceived -= OnNotificationReceived;
        }
    }
}