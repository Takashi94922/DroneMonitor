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

        private readonly Dictionary<Stepper, byte> _lastSentPIDValues = new();
        private readonly Dictionary<Stepper, byte> _newInputPIDValues = new();
        private readonly Stepper[] _steppers;

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

            _steppers = new[]
            {
                pitchStepper,
                rollStepper,
                yawStepper
            };
            
            foreach (var stepper in _steppers)
            {
                _lastSentPIDValues[stepper] = (byte)stepper.Value;
                _newInputPIDValues[stepper] = (byte)stepper.Value;
            }

            // 操作ボタンを Stepper に変更したため、Stepper のイベントを登録
            throttleStepper.ValueChanged += OnThrottleStepperChanged;
            pitchStepper.ValueChanged += OnPRYStepperChanged;
            rollStepper.ValueChanged += OnPRYStepperChanged;
            yawStepper.ValueChanged += OnPRYStepperChanged;

            _sendControlUTimer = Application.Current.Dispatcher.CreateTimer();
            _sendControlUTimer.Interval = TimeSpan.FromMilliseconds(16);
            _sendControlUTimer.Tick += SendSliderValueAsync;

            //ドローンタイプ選択用ラジオボタン
            TypeVert.CheckedChanged += OnTargetCheckedChanged;
            TypeXpider.CheckedChanged += OnTargetCheckedChanged;

#if WINDOWS
            _gamepadHandler = new WindowsGamepadHandler(_sliders, msgPad);
#endif
#if ANDROID
            _gamepadHandler = new AndroidGamepadHandler(_sliders, msgPad);
            // Android では JoystickView を使う場合、AndroidGamepadHandler を使う
            joystickView.SetGamepadHandler(_gamepadHandler);
#endif  
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
        }

        async public void SetBleService(BleService bleService)
        {
            _bleService = bleService;
            // イベントの重複登録を防ぐため一度解除
            _bleService.NotificationReceived += OnNotificationReceived;

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

            // Stepper イベント解除
            throttleStepper.ValueChanged -= OnThrottleStepperChanged;
            pitchStepper.ValueChanged -= OnPRYStepperChanged;
            rollStepper.ValueChanged -= OnPRYStepperChanged;
            yawStepper.ValueChanged -= OnPRYStepperChanged;
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

        // Stepper ハンドラ: Stepper の変更で対応する Slider を更新（既存の送信処理を流用）
        private void OnThrottleStepperChanged(object? sender, ValueChangedEventArgs e)
        {
            var val = (byte)Math.Round(e.NewValue);
            throttleValueLabel.Text = val.ToString() + "%";
            // スライダーに反映すると OnSliderChanged が呼ばれて送信準備が整う
            throttleSeekBar.Value = val;
        }

        private void OnPRYStepperChanged(object? sender, ValueChangedEventArgs e)
        {
            var val = (byte)Math.Round(e.NewValue);
            if (sender == pitchStepper)
            {
                pitchValueLabel.Text = val.ToString() + "°";
            }
            else if (sender == rollStepper)
            {
                rollValueLabel.Text = val.ToString() + "°";
            }
            else if (sender == yawStepper)
            {
                yawValueLabel.Text = val.ToString() + "°";
            }

        }

        private async void SendSliderValueAsync(object sender, object e)
        {
            if (_bleService == null || !_bleService.IsConnected) return;
            _bleService.Characteristics.TryGetValue("Command", out var c);
            if (c == null) return;

            var defaultBuf = new byte[]{0x00, 50, 50, 50, 50};
            var buf = (byte[])defaultBuf.Clone();

            //変更なしの場合-1、変更があった場合はそのスライダーのインデックスを保持
            var changedIndex = -1;

            //操舵があるか調べる
            for (int i = 1; i < _sliders.Length; i++)
            {
                var slider = _sliders[i];
                byte newVal = _newInputValues[slider];
                byte oldVal = _lastSentValues[slider];
                buf[i] = oldVal; // とりあえず古い値をセット

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
                buf[0] = 0x0A; // 一斉送信のコマンド
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
                buf = new byte[] { (byte)changedIndex, buf[changedIndex] };
                c.WriteAsync(buf);
                _lastSentValues[_sliders[changedIndex]] = _newInputValues[_sliders[changedIndex]];
            }

            //スロットル操作の時
            if(_lastSentValues[throttleSeekBar] != _newInputValues[throttleSeekBar] )
            {
                buf = new byte[]{ 0x00, _newInputValues[throttleSeekBar] };
                await c.WriteAsync(buf);
                _lastSentValues[throttleSeekBar] = _newInputValues[throttleSeekBar];
            }

            // PID操作のtargetと値を送信　radに変換するのを忘れないこと
            for (int i = 0; i < _steppers.Length; i++)
            {
                var stepper = _steppers[i];
                byte newVal = _newInputPIDValues[stepper];
                byte oldVal = _lastSentPIDValues[stepper];
                
                //値に変更があるか調べる
                if (newVal != oldVal)
                {
                    buf = new byte[] { (byte)(0x15 + i), (byte)((float)newVal * 0.0174533f)}; // 0x10, 0x11, 0x12 をターゲットIDとする
                    await c.WriteAsync(buf);
                    _lastSentPIDValues[stepper] = newVal;
                }
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            if (_bleService != null)
            {
                _sendControlUTimer?.Stop();
                 _gamepadHandler?.Dispose();  // ゲームパッド停止

                _bleService.NotificationReceived -= OnNotificationReceived;

                // Stepper イベント解除（重複防止）
                throttleStepper.ValueChanged -= OnThrottleStepperChanged;
                pitchStepper.ValueChanged -= OnPRYStepperChanged;
                rollStepper.ValueChanged -= OnPRYStepperChanged;
                yawStepper.ValueChanged -= OnPRYStepperChanged;
            }
        }

        // メソッド: CheckedChanged ハンドラ
        private void OnTargetCheckedChanged(object? sender, CheckedChangedEventArgs e)
        {
            // e.Value==true のときにチェックされた側を処理
            if (!e.Value || _gamepadHandler == null) return;

            if (sender == TypeVert)
            {
                _gamepadHandler.DroneType = 0; // Vert を選択
            }
            else if (sender == TypeXpider)
            {
                _gamepadHandler.DroneType = 1; // X を選択
            }
        }

    }

}