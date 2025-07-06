using Microsoft.Maui.Controls;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Diagnostics;
#if WINDOWS
using Windows.Gaming.Input;
using Windows.Networking.Sockets;
#endif

namespace DroneMonitor.Views
{
    public partial class HomePage : ContentPage
    {
        private BleService? _bleService;
        private bool _isControlByPad = false;
        // 各スライダーの最後に送信した値を保持するマップ
        private readonly Dictionary<Slider, byte> _lastSentValues
            = new Dictionary<Slider, byte>();
        private readonly Slider[] _sliders;
        private IDispatcherTimer? _sendControlUTimer;
        List<float> U5 = new List<float>
            {
                0, 0, 0, 0, 0
            };


        // 変化量しきい値（= 送信するために必要な最小 Δ）
        private const byte SEND_THRESHOLD = 1;
#if WINDOWS
        private GamepadButtons _lastButtons = GamepadButtons.None;
        private IDispatcherTimer _gamepadTimer;
        private Gamepad _gamepad;
        private DateTime _lastUpdate = DateTime.Now;
#endif

        public HomePage()
        {
            InitializeComponent();
            //Seekbarのイベントハンドラを登録
            // ここで配列にまとめておく
            _sliders = new[]
                    {
                throttleSeekBar,
                S1SeekBar,
                S2SeekBar,
                S3SeekBar,
                S4SeekBar
            };

            throttleSeekBar.ValueChanged += OnSliderChanged;
            S1SeekBar.ValueChanged += OnSliderChanged;
            S2SeekBar.ValueChanged += OnSliderChanged;
            S3SeekBar.ValueChanged += OnSliderChanged;
            S4SeekBar.ValueChanged += OnSliderChanged;
            // ボタンのイベントハンドラを登録
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
            Gamepad.GamepadAdded += OnGamepadAdded;
            Gamepad.GamepadRemoved += OnGamepadRemoved;

            // 念のため今存在するものを取得
            if (Gamepad.Gamepads.Any())
            {
                _gamepad = Gamepad.Gamepads.First();
                StartPolling();
            }
#endif
        }
#if WINDOWS
        private void OnGamepadAdded(object sender, Gamepad e)
        {
            Debug.WriteLine("🎮 Gamepad Connected");
            _gamepad = e;
            StartPolling();
        }

        private void OnGamepadRemoved(object sender, Gamepad e)
        {
            if (_gamepad == e)
            {
                Debug.WriteLine("🎮 Gamepad Disconnected");
                _gamepad = null;
                _gamepadTimer?.Stop();
            }
        }

        private void StartPolling()
        {
            if (_gamepadTimer != null && _gamepadTimer.IsRunning)
                return;

            _gamepadTimer = Application.Current.Dispatcher.CreateTimer();
            _gamepadTimer.Interval = TimeSpan.FromMilliseconds(16);
            _gamepadTimer.Tick += OnGamepadPoll;
            _gamepadTimer.Start();

            Debug.WriteLine("⏱ Gamepad polling started");
        }

        private void OnGamepadPoll(object sender, object e)
        {
            if (_gamepad == null)
            {
                _gamepad = Gamepad.Gamepads.FirstOrDefault();
                if (_gamepad == null) return;
            }

            var reading = _gamepad.GetCurrentReading();
            //Debug.WriteLine($"🎮 Gamepad Reading: {reading.Buttons} | Left: ({reading.LeftThumbstickX}, {reading.LeftThumbstickY})");
            if (!_lastButtons.HasFlag(GamepadButtons.A) && reading.Buttons.HasFlag(GamepadButtons.A))
            {
                _isControlByPad = !_isControlByPad; // トグル
                msgPad.Text = $"GamePad : {_isControlByPad}";
                Debug.WriteLine($"操作ボタンが押れました{_isControlByPad}");
            }

            _lastButtons = reading.Buttons;

            if (!_isControlByPad) return;

            U5 = new List<float>
            {
                0, 0, 0, 0, 0
            };
            //Left Stick for rolling and pitching
            ControlRollPitch((float)reading.LeftThumbstickX, (float)reading.LeftThumbstickY);
            //Right Stick for throttle
            ControlThrottle((float)reading.RightThumbstickY);
            //L/R triggers for yaw
            ControlYaw((float)reading.LeftTrigger, (float)reading.RightTrigger);

            for (int i = 0; i < _sliders.Length; i++)
            {
                if (_sliders[i] == throttleSeekBar)
                {
                    // スロットルは 0 から始まる
                    _sliders[i].Value = U5[i];
                }
                else
                {
                    _sliders[i].Value = U5[i] + 50;
                }
            }
        }

        private void ControlRollPitch(float rollStick, float pitchStick)
        {
            const float DEAD_ZONE = 0.15f;      // デッドゾーンの閾値
            const float ROLL_SENSITIVITY = 30; // ロール感度
            const float PITCH_SENSITIVITY = 30;// ピッチ感度

            // デッドゾーン処理
            float dx = Math.Abs(rollStick) < DEAD_ZONE ? 0 : rollStick;
            float dy = Math.Abs(pitchStick) < DEAD_ZONE ? 0 : pitchStick;

            // 各モーター／サーボ出力を計算
            // U5[1]：Front-Left, U5[2]：Front-Right
            // U5[3]：Back-Right,  U5[4]：Back-Left という想定
            U5[1] = + dx * ROLL_SENSITIVITY + dy * PITCH_SENSITIVITY;
            U5[2] = - dx * ROLL_SENSITIVITY + dy * PITCH_SENSITIVITY;
            U5[3] = - dx * ROLL_SENSITIVITY - dy * PITCH_SENSITIVITY;
            U5[4] = + dx * ROLL_SENSITIVITY - dy * PITCH_SENSITIVITY;
        }
        private void ControlThrottle(float throttleStick)
        {
            const float DEAD_ZONE = 0.15f;      // デッドゾーンの閾値
            const float THR_SENSITIVITY = 100; // Throttle

            // デッドゾーン処理
            float dx = Math.Abs(throttleStick) < DEAD_ZONE ? 0 : throttleStick;

            U5[0] = (float)Math.Clamp(dx * THR_SENSITIVITY, throttleSeekBar.Minimum, throttleSeekBar.Maximum);
        }
        private void ControlYaw(float YawPls, float YawMins)
        {
            const float DEAD_ZONE = 1;      // デッドゾーンの閾値
            const float YAW_SENSITIVITY = 10; // YAW
            // デッドゾーン処理
            float dx = Math.Abs(YawPls) < DEAD_ZONE ? 0 : YawPls;
            float dy = Math.Abs(YawMins) < DEAD_ZONE ? 0 : YawMins;

            U5[1] += +(YawPls - YawMins) * YAW_SENSITIVITY;
            U5[2] += +(YawPls - YawMins) * YAW_SENSITIVITY;
            U5[3] += +(YawPls - YawMins) * YAW_SENSITIVITY;
            U5[4] += +(YawPls - YawMins) * YAW_SENSITIVITY;
        }
#endif

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
#if WINDOWS
            _gamepadTimer?.Start();
#endif


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
            _gamepadTimer?.Stop();
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
                _gamepadTimer?.Stop();
#endif
                _isControlByPad = false;
                _bleService.NotificationReceived -= OnNotificationReceived;
                // 必要な通知キーを指定して停止
                _bleService.StopNotificationAsync("Command");
                // 他にも通知を止めたいCharacteristicがあればここで追加
            }
        }
    }
}