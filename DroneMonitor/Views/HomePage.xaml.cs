using Microsoft.Maui.Controls;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Diagnostics;

namespace DroneMonitor.Views
{
    public partial class HomePage : ContentPage
    {
        private readonly BleService _bleService;
        private bool _isConnected = false;

        // デフォルトコンストラクタを追加
        public HomePage() : this(new BleService())
        {
        }

        public HomePage(BleService bleService)
        {
            InitializeComponent();
            _bleService = bleService;

            // BLE通知受信時のイベントハンドラ登録
            _bleService.NotificationReceived += OnNotificationReceived;

            // 既存のUIイベントハンドラ
            S1SeekBar.ValueChanged += OnSliderChanged!;
            throttleSeekBar.ValueChanged += OnSliderChanged!;
            S2SeekBar.ValueChanged += OnSliderChanged!;
            S3SeekBar.ValueChanged += OnSliderChanged!;
            S4SeekBar.ValueChanged += OnSliderChanged!;
            throttlePlusBtn.Clicked += OnPlusClicked!;
            throttleMinusBtn.Clicked += OnMinusClicked!;

            // ボタンの初期ラベル
            bleConnectBtn.Text = "Disconnected";
        }

        // BLE接続/切断ボタン
        private async void OnConnectButtonClicked(object sender, EventArgs e)
        {
            if (!_isConnected)
            {
                bool connected = await _bleService.ConnectToDeviceAsync("ESP_DRONE");
                if (connected)
                {
                    await _bleService.StartNotificationAsync("CHAR_UUID_contU_TelemWrit");
                    await _bleService.StartNotificationAsync("CHAR_UUID_Command");
                    _isConnected = true;
                    bleConnectBtn.Text = "Connected";
                    bleConnectBtn.BackgroundColor = Colors.Blue;
                }
                else
                {
                    await DisplayAlert("エラー", "接続できませんでした", "OK");
                }
            }
            else
            {
                await _bleService.DisconnectAsync();
                _isConnected = false;
                bleConnectBtn.Text = "Disconnected";
                bleConnectBtn.BackgroundColor = Colors.Red;
            }
        }

        // BLE通知受信時の処理
        private void OnNotificationReceived(object? sender, byte[] data)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    string str = System.Text.Encoding.UTF8.GetString(data);
                    msgWindow.Text = str;
                }
                catch
                {
                    msgWindow.Text = BitConverter.ToString(data);
                }
            });
        }

        // 既存のUIイベントハンドラ
        private async void OnSliderChanged(object? sender, ValueChangedEventArgs e)
        {
            var slider = (Slider)sender!;
            msgWindow.Text = $"{slider.AutomationId ?? "Slider"}: {e.NewValue:0.00}";

            if (!_isConnected) return;

            byte[] data;
            // スライダー名で送信データを分岐し、float値をbyte配列に変換
            float value = (float)e.NewValue;
            byte[] floatBytes = BitConverter.GetBytes(value);

            if (slider == throttleSeekBar)
            {
                data = new byte[1 + floatBytes.Length];
                data[0] = 0x00;
                Buffer.BlockCopy(floatBytes, 0, data, 1, floatBytes.Length);
            }
            else if (slider == S1SeekBar)
            {
                data = new byte[1 + floatBytes.Length];
                data[0] = 0x01;
                Buffer.BlockCopy(floatBytes, 0, data, 1, floatBytes.Length);
            }
            else if (slider == S2SeekBar)
            {
                data = new byte[1 + floatBytes.Length];
                data[0] = 0x02;
                Buffer.BlockCopy(floatBytes, 0, data, 1, floatBytes.Length);
            }
            else if (slider == S3SeekBar)
            {
                data = new byte[1 + floatBytes.Length];
                data[0] = 0x03;
                Buffer.BlockCopy(floatBytes, 0, data, 1, floatBytes.Length);
            }
            else if (slider == S4SeekBar)
            {
                data = new byte[1 + floatBytes.Length];
                data[0] = 0x04;
                Buffer.BlockCopy(floatBytes, 0, data, 1, floatBytes.Length);
            }
            else
            {
                return;
            }

            // CommandキーでCharacteristic取得
            if (_bleService.Characteristics.TryGetValue("Command", out var characteristic))
            {
                await characteristic.WriteAsync(data);
            }
        }

        private void OnPlusClicked(object? sender, EventArgs e)
        {
            throttleSeekBar.Value = Math.Min(throttleSeekBar.Value + 10, throttleSeekBar.Maximum);
        }

        private void OnMinusClicked(object? sender, EventArgs e)
        {
            throttleSeekBar.Value = Math.Max(throttleSeekBar.Value - 10, 0);
        }
    }
}