using Syncfusion.Maui.Sliders;

namespace DroneMonitor.Platforms.Android
{
    public class AndroidGamepadHandler : GamepadHandler
    {
        public AndroidGamepadHandler(Dictionary<Slider, byte> sentValues, Slider[] sliderArray, Label messageLabel)
            : base(sentValues, sliderArray, messageLabel)
        {
        }
        public override void Init()
        {
            Start();
        }
        public override void Start()
        {
        }

        public override void Dispose()
        {
        }

        public void OnStickChanged(float x, float y)
        {
            U5 = new List<float> { U5[0], 0, 0, 0, 0 }; // U5をリセット

            // スティックの値を取得して制御
            ControlRollPitch(x, y);
            ControlYaw(x, y);

            //Servoのオフセットを追加
            for (int i = 0; i < sliders.Length; i++)
            {
                sliders[i].Value = i == 0 ? U5[i] : U5[i] + 50;
            }
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