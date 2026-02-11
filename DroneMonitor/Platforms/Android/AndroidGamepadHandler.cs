

namespace DroneMonitor.Platforms.Android
{
    public class AndroidGamepadHandler : GamepadHandler
    {
        public int DroneType { get; set; } = 0; // 0:垂直, 1:X字

        public AndroidGamepadHandler(Slider[] sliderArray, Label messageLabel)
            : base(sliderArray, messageLabel)
        {
        }
        public override void Init()
        {
        }
        public override void Start()
        {
        }

        public override void Dispose()
        {
        }

        public void OnStickChanged(float x, float y)
        {
            IsControlByPad = true; // ゲームパッド制御を有効にする
            U5 = new List<float> { U5[0], 0, 0, 0, 0 }; // U5をリセット

            // スティックの値を取得して制御
            ControlRollPitch(x, y);
            //ControlYaw(x, y);

            //Servoのオフセットを追加
            for (int i = 1; i < sliders.Length; i++)
            {
                sliders[i].Value = U5[i] + 50;
            }
        }

        private void ControlRollPitch(float rollStick, float pitchStick)
        {
            const float DEAD_ZONE = 0.0f;
            const float ROLL_SENS = 30f;
            const float PITCH_SENS = 30f;

            float dx = Math.Abs(rollStick) < DEAD_ZONE ? 0 : rollStick;
            float dy = Math.Abs(pitchStick) < DEAD_ZONE ? 0 : pitchStick;

            if(DroneType == 0)
            {
                U5[1] = +dx * ROLL_SENS + dy * PITCH_SENS;
                U5[2] = -dx * ROLL_SENS + dy * PITCH_SENS;
                U5[3] = -dx * ROLL_SENS - dy * PITCH_SENS;
                U5[4] = +dx * ROLL_SENS - dy * PITCH_SENS;
            }
            else if(DroneType == 1)
            {
                //padの象限によって制御対象を変える
                float c45 = (float)(Math.Cos(Math.PI / 4));
                U5[1] = c45 * dx + c45 * dy < 0 ? 0 : (c45 * dx + c45 * dy) * 50;
                U5[2] = c45 * dx - c45 * dy < 0 ? 0 : (c45 * dx - c45 * dy) * 50;
                U5[3] = -c45 * dx - c45 * dy < 0 ? 0 : (-c45 * dx - c45 * dy) * 50;
                U5[4] = -c45 * dx + c45 * dy < 0 ? 0 : (-c45 * dx + c45 * dy) * 50;
            }
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