using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Maui.Controls;
using Plugin.BLE.Abstractions.Contracts;

namespace DroneMonitor.Views;

public partial class NotificationPage : ContentPage
{
    private BleService? _bleService;

    public NotificationPage()
    {
        InitializeComponent();
    }

    public void SetBleService(BleService bleService)
    {
        _bleService = bleService;
        _bleService.NotificationReceived -= OnNotificationReceived;
        _bleService.NotificationReceived += OnNotificationReceived;

        // 通知開始の成否をログ出力
        _bleService.StartNotificationAsync("Xhat_Telem").ContinueWith(t =>
            Debug.WriteLine($"Xhat_Telem通知開始: {t.Result}"));
        _bleService.StartNotificationAsync("contU_TelemWrite").ContinueWith(t =>
            Debug.WriteLine($"contU_TelemWrite通知開始: {t.Result}"));
    }

    private void OnNotificationReceived(object? sender, byte[] data)
    {
        Debug.WriteLine("OnNotificationReceived発火");
        if (_bleService == null)
        {
            Debug.WriteLine("BLE 未接続");
            return;
        }

        // senderの型をログ出力
        Debug.WriteLine($"sender type: {sender?.GetType().FullName}");

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

        Debug.WriteLine($"受信: key={key}, data={BitConverter.ToString(data)}");
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