using System.Diagnostics;
using System.Net;
using Windows.Gaming.Input;

namespace DroneMonitor.Platforms.Windows
{
    public class WindowsGamepadHandler : GamepadHandler
    {
        private Gamepad? _gamepad;
        private GamepadButtons _lastButtons = GamepadButtons.None;
        private IDispatcherTimer? _gamepadTimer;
        public int DroneType { get; set; } = 1; // 0:垂直, 1:X字

        public WindowsGamepadHandler(Slider[] sliderArray, Label messageLabel)
            : base(sliderArray, messageLabel)
        {
        }
        public override void Init()
        {
            Gamepad.GamepadAdded += OnGamepadAdded;
            Gamepad.GamepadRemoved += OnGamepadRemoved;

            _gamepad = Gamepad.Gamepads.FirstOrDefault();
            Start();
        }
        public override void Start()
        {
            if (_gamepad != null) StartPolling();
        }

        public override void Dispose()
        {
            Debug.WriteLine("🎮 Gamepad poling stoped");
            _gamepadTimer?.Stop();
            Gamepad.GamepadAdded -= OnGamepadAdded;
            Gamepad.GamepadRemoved -= OnGamepadRemoved;
        }

        private void OnGamepadAdded(object? sender, Gamepad e)
        {
            Debug.WriteLine("🎮 Gamepad Connected");
            _gamepad = e;
            StartPolling();
        }

        private void OnGamepadRemoved(object? sender, Gamepad e)
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

        private void OnGamepadPoll(object? sender, object e)
        {
            if (_gamepad == null) return;

            var reading = _gamepad.GetCurrentReading();

            if (!_lastButtons.HasFlag(GamepadButtons.A) && reading.Buttons.HasFlag(GamepadButtons.A))
            {
                IsControlByPad = !IsControlByPad;
                Debug.WriteLine($"Aボタンで切り替え: {IsControlByPad}");
            }
            if (!_lastButtons.HasFlag(GamepadButtons.B) && reading.Buttons.HasFlag(GamepadButtons.B))
            {
                IsThrottleByPad = !IsThrottleByPad;
                Debug.WriteLine($"Bボタンで切り替え: {IsThrottleByPad}");
            }
            msgPad.Text = $"GamePad : {IsControlByPad}, ThrottlePad : {IsThrottleByPad}";

            // スロットルの制御
            if (IsThrottleByPad)
            {
                if (!_lastButtons.HasFlag(GamepadButtons.LeftShoulder) && reading.Buttons.HasFlag(GamepadButtons.LeftShoulder))
                {
                    // LTでスロットルを10%減らす
                    U5[0] = (float)Math.Max(U5[0] - 10, 0);
                    Debug.WriteLine("LTボタンが押されました");
                }
                else if (!_lastButtons.HasFlag(GamepadButtons.RightShoulder) && reading.Buttons.HasFlag(GamepadButtons.RightShoulder))
                {
                    // RTでスロットルを10%増やす
                    U5[0] = (float)Math.Min(U5[0] + 10, 100);
                    Debug.WriteLine("RTボタンが押されました");
                }
                // スロットルの右スティックY軸
                ControlThrottle((float)reading.RightThumbstickY);
                U5[0] = (float)Math.Clamp(U5[0], 0, 100);
                sliders[0].Value = U5[0]; // スライダーの値を更新
            }

            // Aボタンが押されている場合は制御
            if (IsControlByPad)
            {
               // U5 = new List<float> { U5[0], 0, 0, 0, 0 }; // U5をリセット

                // スティックの値を取得して制御
                ControlRollPitch((float)reading.LeftThumbstickX, (float)reading.LeftThumbstickY);
                ControlYaw((float)reading.LeftTrigger, (float)reading.RightTrigger);

                //Servoのオフセットを追加
                for (int i = 1; i < sliders.Length; i++)
                {
                    sliders[i].Value = U5[i] + 50f;
                }

            }

            _lastButtons = reading.Buttons;
        }

        private void ControlRollPitch(float rollStick, float pitchStick)
        {
            const float DEAD_ZONE = 0.01f;
            const float ROLL_SENS = 30f;
            const float PITCH_SENS = 30f;

            float dx = Math.Abs(rollStick) < DEAD_ZONE ? 0 : rollStick;
            float dy = Math.Abs(pitchStick) < DEAD_ZONE ? 0 : pitchStick;

            Array DrontypePitchMatrix = new float[2, 2, 4]
            {
                { { +1.0f, -1.0f, -1.0f, 1.0f }, { +1.0f, +1.0f, -1.0f, -1.0f } }, // 0:垂直
                { { +1.0f, -1.0f, 0f, 0f }, { +1.0f, 0f, 0f, -1.0f } }  // 1:X字
            };

            if (DroneType == 0)
            {
                for (int i = 1; i < 5; i++)
                {
                    U5[i] = (DrontypePitchMatrix.GetValue(0, 0, i - 1) is float val1 ? val1 * dx * ROLL_SENS : 0)
                          + (DrontypePitchMatrix.GetValue(0, 1, i - 1) is float val2 ? val2 * dy * PITCH_SENS : 0);
                }
            }
            else if(DroneType == 1) // X字
            {
                //padの象限によって制御対象を変える
                float c45 = (float)(Math.Cos(Math.PI / 4));
                Debug.WriteLine($"dx: {dx}, dy: {dy}, c45*dx+c45*dy: {c45 * dx + c45 * dy}");
                float Clamp50(float v) => Math.Clamp(v * 50, 0, 50);

                U5[1] = Clamp50(c45 * dx + c45 * dy);
                U5[2] = Clamp50(c45 * dx - c45 * dy);
                U5[3] = Clamp50(-c45 * dx - c45 * dy);
                U5[4] = Clamp50(-c45 * dx + c45 * dy);
            }
        }

        private void ControlYaw(float yawPls, float yawMins)
        {
            const float DEAD_ZONE = 1f;
            const float YAW_SENS = 10f;

            float dx = Math.Abs(yawPls) < DEAD_ZONE ? 0 : yawPls;
            float dy = Math.Abs(yawMins) < DEAD_ZONE ? 0 : yawMins;
            float delta = (dx - dy) * YAW_SENS;

            if(DroneType == 0) // 垂直
            {
                for (int i = 1; i < 5; i++) U5[i] += delta;
            }        
        }
        private void ControlThrottle(float throttleStick)
        {
            const float DEAD_ZONE = 0.15f;
            const float THR_SENS = 1f;

            float dx = Math.Abs(throttleStick) < DEAD_ZONE ? 0 : throttleStick;
            U5[0] = (float)Math.Clamp(U5[0] + dx * THR_SENS, 0, 100);
        }
    }
}