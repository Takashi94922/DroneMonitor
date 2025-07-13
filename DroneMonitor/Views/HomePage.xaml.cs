#if ANDROID
using DroneMonitor.Platforms.Android;
#elif WINDOWS
using DroneMonitor.Platforms.Windows;
#endif
using Microsoft.Maui.Controls;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Diagnostics;
using System.Windows.Input;

namespace DroneMonitor.Views
{
    public partial class HomePage : ContentPage
    {
        private BleService? _bleService;
        private readonly Dictionary<Slider, byte> _lastSentValues = new();
        private readonly Slider[] _sliders;
        private IDispatcherTimer? _sendControlUTimer;

        // 変化量しきい値（= 送信するために必要な最小 Δ）
        private const byte SEND_THRESHOLD = 1;
#if WINDOWS
        private WindowsGamepadHandler? _gamepadHandler;
#endif

        public HomePage()
        {        
            InitializeComponent();

            _sliders = new[]
            {
                throttleSeekBar,
                S1SeekBar,
                S2SeekBar,
                S3SeekBar,
                S4SeekBar
            };

            foreach (var slider in _sliders)
            {
                slider.ValueChanged += OnSliderChanged;
                _lastSentValues[slider] = (byte)slider.Value;
            }

            throttlePlusBtn.Clicked += OnPlusClicked;
            throttleMinusBtn.Clicked += OnMinusClicked;

            _sendControlUTimer = Application.Current.Dispatcher.CreateTimer();
            _sendControlUTimer.Interval = TimeSpan.FromMilliseconds(100);
            _sendControlUTimer.Tick += SendSliderValueAsync;
        }
        protected override void OnAppearing()
        {
            base.OnAppearing();

#if WINDOWS
            _gamepadHandler = new WindowsGamepadHandler(
                throttleSeekBar,
                _lastSentValues,
                _sliders,
                msgPad  // ← 状態表示用の Label
            );
#endif
        }

        async public void SetBleService(BleService bleService)
        {
            _bleService = bleService;
            // イベントの重複登録を防ぐため一度解除
            _bleService.NotificationReceived -= OnNotificationReceived;
            _bleService.NotificationReceived += OnNotificationReceived;
            if (_bleService != null && _bleService.IsConnected)
            {
                _bleService.StartNotificationAsync("Command");
            }

            _sendControlUTimer.Start();

            // 各スライダーのイベント登録と、初期値 50 をセット
            foreach (var s in _sliders)
            {
                if (s == throttleSeekBar)
                {
                    s.Value = 0;
                    _lastSentValues[s] = 0;
                }
                else
                {
                    s.ValueChanged += OnSliderChanged;
                    _lastSentValues[s] = 50;   // ← ここで初期化
                    s.Value = 50;              // UIも50スタートにしたい場合
                }
            }
        }
        async public Task DisconnectBle()
        {
            _bleService.NotificationReceived -= OnNotificationReceived;
            _sendControlUTimer?.Stop();
#if WINDOWS
             _gamepadHandler?.Dispose(); // ← 新しい Dispose メソッドでゲームパッド処理を停止
#endif

            try
            {
                // 必要な通知キーを指定して停止
                //await _bleService.StopNotificationAsync("Command");
            }catch (Exception ex)
            {
                Debug.WriteLine($"通知停止エラー: {ex.Message}");
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
            byte newValue = (byte)Math.Round(e.NewValue);

            byte lastValue = _lastSentValues[slider];
            byte delta = (byte)Math.Abs(newValue - lastValue);

            // しきい値を超えたら送信
            if (delta >= SEND_THRESHOLD)
            {
                _lastSentValues[slider] = newValue;
            }

            // UI 表示は即時更新
            msgWindow.Text = $"{slider.ClassId}: {newValue:0.00}";
        }

        private async void SendSliderValueAsync(object sender, object e)
        {
            if (_bleService == null || !_bleService.IsConnected) return;

            // コマンドID + 各スライダーの byte 値
            var buf = new byte[1 + _sliders.Length];
            buf[0] = 0x0A;  // コマンドID

            for (int i = 0; i < _sliders.Length; i++)
            {
                var s = _sliders[i];
                // ディクショナリに値がなければ 50
                buf[1 + i] = _lastSentValues.TryGetValue(s, out var v) ? v : (byte)50;
            }

            if (_bleService.Characteristics.TryGetValue("Command", out var c))
            {
                await c.WriteAsync(buf);
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

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            if (_bleService != null)
            {
                _sendControlUTimer?.Stop();
#if WINDOWS
    _gamepadHandler?.Dispose();  // ゲームパッド停止
#endif

                _bleService.NotificationReceived -= OnNotificationReceived;
                // 必要な通知キーを指定して停止
                _bleService.StopNotificationAsync("Command");
                // 他にも通知を止めたいCharacteristicがあればここで追加
            }
        }
    }

}