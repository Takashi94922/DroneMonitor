using Microsoft.Maui.Controls;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Diagnostics;

namespace DroneMonitor.Views
{
    public partial class HomePage : ContentPage
    {
        private BleService? _bleService;

        public HomePage()
        {
            InitializeComponent();
            //Seekbarのイベントハンドラを登録
            throttleSeekBar.ValueChanged += OnSliderChanged;
            S1SeekBar.ValueChanged += OnSliderChanged;
            S2SeekBar.ValueChanged += OnSliderChanged;
            S3SeekBar.ValueChanged += OnSliderChanged;
            S4SeekBar.ValueChanged += OnSliderChanged;
            // ボタンのイベントハンドラを登録
            throttlePlusBtn.Clicked += OnPlusClicked;
            throttleMinusBtn.Clicked += OnMinusClicked;

        }

        async public void SetBleService(BleService bleService)
        {
            _bleService = bleService;
            // イベントの重複登録を防ぐため一度解除
            _bleService.NotificationReceived -= OnNotificationReceived;
            _bleService.NotificationReceived += OnNotificationReceived;
            // 必要なら通知開始
            _bleService.StartNotificationAsync("Command");
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

            if (!_bleService.IsConnected || _bleService == null)
            {
                Debug.WriteLine("BLE未接続またはサービス未設定");
                return;
            }

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
            else
            {
                Debug.WriteLine("Command characteristic not found.");
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

        protected override async void OnDisappearing()
        {
            base.OnDisappearing();
            if (_bleService != null)
            {
                // 必要な通知キーを指定して停止
                _bleService.StopNotificationAsync("Command");
                // 他にも通知を止めたいCharacteristicがあればここで追加
                _bleService.NotificationReceived -= OnNotificationReceived;
            }
        }
    }
}