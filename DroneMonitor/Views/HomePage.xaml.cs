#if ANDROID
using DroneMonitor.Platforms.Android;
#elif WINDOWS
using DroneMonitor.Platforms.Windows;
#endif
using Plugin.BLE.Abstractions.Contracts;

namespace DroneMonitor.Views
{
    public partial class HomePage : ContentPage
    {
        private BleService? _bleService;
        private readonly Dictionary<Slider, byte> _lastSentValues = new();
        private readonly Dictionary<Slider, byte> _newInputValues = new();
        private readonly Slider[] _sliders;
        private IDispatcherTimer? _sendControlUTimer;

        // 変化量しきい値（= 送信するために必要な最小 Δ）
        private const byte SEND_THRESHOLD = 1;
#if WINDOWS
        private WindowsGamepadHandler? _gamepadHandler;
#elif ANDROID
        private AndroidGamepadHandler? _gamepadHandler;
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
                _newInputValues[slider] = (byte)slider.Value;
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
            _gamepadHandler = new WindowsGamepadHandler(_sliders, msgPad);
#endif
#if ANDROID
            _gamepadHandler = new AndroidGamepadHandler(_sliders, msgPad);
            // Android では JoystickView を使う場合、AndroidGamepadHandler を使う
            joystickView.SetGamepadHandler(_gamepadHandler);
#endif
        }

        async public void SetBleService(BleService bleService)
        {
            _bleService = bleService;
            // イベントの重複登録を防ぐため一度解除
            _bleService.NotificationReceived += OnNotificationReceived;

            // 各スライダーのイベント登録と、初期値0か 50 をセット
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
                    _lastSentValues[s] = 50;
                    s.Value = 50;
                }
            }
            if (_gamepadHandler != null)
            {
                _gamepadHandler.Start(); // ゲームパッドのポーリング開始
            }

            _sendControlUTimer.Start();
        }

        async public Task DisconnectBle()
        {
            _bleService.NotificationReceived -= OnNotificationReceived;
            _sendControlUTimer?.Stop();
            _gamepadHandler?.Dispose(); // ← 新しい Dispose メソッドでゲームパッド処理を停止
        }

        // BLE通知受信時の処理
        private void OnNotificationReceived(object? sender, byte[] data)
        {
            // senderがICharacteristicでない場合、全Characteristicからdata一致で特定
            string key = "";
            if (sender is ICharacteristic characteristic)
            {
                key = _bleService.Characteristics.FirstOrDefault(x => x.Value == characteristic).Key ?? "";
            }

            //Debug.WriteLine($"受信: key={key}, data={BitConverter.ToString(data)}");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (key == "Command" && data.Length >= 24)
                {
                    msgWindow.Text = $"{data}";
                }
            });
        }

        // 既存のUIイベントハンドラ
        private async void OnSliderChanged(object? sender, ValueChangedEventArgs e)
        {
            var slider = (Slider)sender!;
            var newValue = (byte)Math.Round(e.NewValue);

            byte delta = (byte)Math.Abs(newValue - _lastSentValues[slider]);
            // しきい値を超えたら送信
            if (delta >= SEND_THRESHOLD)
            {
                _newInputValues[slider] = newValue;
            }

            // UI 表示は即時更新
            msgWindow.Text = $"{slider.ClassId}: {newValue:0.00}";
        }

        private async void SendSliderValueAsync(object sender, object e)
        {
            if (_bleService == null || !_bleService.IsConnected) return;
            _bleService.Characteristics.TryGetValue("Command", out var c);
            if (c == null) return;

            var defaultBuf = new byte[]{0x00, 50, 50, 50, 50};
            var buf = (byte[])defaultBuf.Clone();

            var changedIndex = -1;

            //操舵があるか調べる
            for (int i = 1; i < _sliders.Length; i++)
            {
                var slider = _sliders[i];
                byte newVal = _newInputValues[slider];
                byte oldVal = _lastSentValues[slider];

                // 新しい値のとき、送信準備

                if (newVal != oldVal)
                {
                    buf[i] = newVal;
                    changedIndex = i;
                }
            }

            // PADコントロールの場合 は無操作でも0に戻す信号が必要
            if (_gamepadHandler.IsControlByPad && changedIndex != -1)
            {
                buf[0] = 0x0A; // コマンドID
                c.WriteAsync(buf);
                for (int i= 1; i < _sliders.Length; i++)
                {
                    var slider = _sliders[i];
                    // 新しい値を送信
                    _lastSentValues[slider] = _newInputValues[slider];
                }
            }
            //PAD操作ではないがクリックによってスライダー値が変更された場合
            else if (changedIndex != -1)
            {
                buf[0] = (byte)changedIndex; // コマンドID
                buf[1] = buf[changedIndex]; // スロットル値
                c.WriteAsync(buf);
                _lastSentValues[_sliders[changedIndex]] = _newInputValues[_sliders[changedIndex]];
            }

            //スロットル操作の時
            if(_lastSentValues[throttleSeekBar] != _newInputValues[throttleSeekBar] )
            {
                buf[0] = 0x00; // コマンドID
                //将来の実装のためにfloatに変換しておく
                var newValue = BitConverter.GetBytes((float)_newInputValues[throttleSeekBar]);
                System.Array.Copy(newValue, 0, buf, 1, newValue.Length);
                await c.WriteAsync(buf);
                _lastSentValues[throttleSeekBar] = _newInputValues[throttleSeekBar];
            }
        }

        private void OnPlusClicked(object? sender, EventArgs e)
        {
            throttleSeekBar.Value = Math.Min(throttleSeekBar.Value + 1, throttleSeekBar.Maximum);
        }

        private void OnMinusClicked(object? sender, EventArgs e)
        {
            throttleSeekBar.Value = Math.Max(throttleSeekBar.Value - 1, 0);
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            if (_bleService != null)
            {
                _sendControlUTimer?.Stop();
                 _gamepadHandler?.Dispose();  // ゲームパッド停止

                _bleService.NotificationReceived -= OnNotificationReceived;
            }
        }
    }

}