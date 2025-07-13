using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Windows.Gaming.Input;
using Windows.Networking.Sockets;

namespace DroneMonitor.Platforms.Windows
{
    public class WindowsGamepadHandler
    {
        public bool IsControlByPad { get; private set; } = false;
        public bool IsThrottleByPad { get; private set; } = false;
        public List<float> U5 { get; private set; } = new() { 0, 0, 0, 0, 0 };

        private Gamepad? _gamepad;
        private GamepadButtons _lastButtons = GamepadButtons.None;
        private IDispatcherTimer? _gamepadTimer;
        private readonly Slider throttleSeekBar;
        private readonly Dictionary<Slider, byte> lastSentValues;
        private readonly Slider[] sliders;
        private readonly Label msgPad;

        public WindowsGamepadHandler(
            Slider throttleSlider,
            Dictionary<Slider, byte> sentValues,
            Slider[] sliderArray,
            Label messageLabel)
        {
            throttleSeekBar = throttleSlider;
            lastSentValues = sentValues;
            sliders = sliderArray;
            msgPad = messageLabel;

            Gamepad.GamepadAdded += OnGamepadAdded;
            Gamepad.GamepadRemoved += OnGamepadRemoved;

            _gamepad = Gamepad.Gamepads.FirstOrDefault();
            if (_gamepad != null) StartPolling();
        }
        public void Start()
        {
            if (_gamepad != null) StartPolling();
        }

        public void Dispose()
        {
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
                msgPad.Text = $"GamePad : {IsControlByPad}";
                Debug.WriteLine($"Aボタンで切り替え: {IsControlByPad}");
            }
            else if (!_lastButtons.HasFlag(GamepadButtons.B) && reading.Buttons.HasFlag(GamepadButtons.B))
            {
                IsThrottleByPad = !IsThrottleByPad;
                msgPad.Text = $"ThrottlePad : {IsThrottleByPad}";
                Debug.WriteLine($"Bボタンで切り替え: {IsThrottleByPad}");
            }

            // Aボタンが押されていない場合は制御を無効化
            if (!IsControlByPad) return;

            U5 = new List<float> { U5[0], 0, 0, 0, 0 }; // U5をリセット

            // スティックの値を取得して制御
            ControlRollPitch((float)reading.LeftThumbstickX, (float)reading.LeftThumbstickY);
            ControlYaw((float)reading.LeftTrigger, (float)reading.RightTrigger);

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
            }
            else
            {
                //制御無効な場合はスロットルをスライダーの値にする
                U5[0] = lastSentValues[throttleSeekBar];
            }

            //Servoのオフセットを追加
            for (int i = 0; i < sliders.Length; i++)
            {
                sliders[i].Value = i == 0 ? U5[i] : U5[i] + 50;
            }
            _lastButtons = reading.Buttons;
        }

        private void ControlRollPitch(float rollStick, float pitchStick)
        {
            const float DEAD_ZONE = 0.15f;
            const float ROLL_SENS = 30f;
            const float PITCH_SENS = 30f;

            float dx = Math.Abs(rollStick) < DEAD_ZONE ? 0 : rollStick;
            float dy = Math.Abs(pitchStick) < DEAD_ZONE ? 0 : pitchStick;

            U5[1] = +dx * ROLL_SENS + dy * PITCH_SENS;
            U5[2] = -dx * ROLL_SENS + dy * PITCH_SENS;
            U5[3] = -dx * ROLL_SENS - dy * PITCH_SENS;
            U5[4] = +dx * ROLL_SENS - dy * PITCH_SENS;
        }

        private void ControlYaw(float yawPls, float yawMins)
        {
            const float DEAD_ZONE = 1f;
            const float YAW_SENS = 10f;

            float dx = Math.Abs(yawPls) < DEAD_ZONE ? 0 : yawPls;
            float dy = Math.Abs(yawMins) < DEAD_ZONE ? 0 : yawMins;
            float delta = (dx - dy) * YAW_SENS;

            for (int i = 1; i < 5; i++) U5[i] += delta;
        
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