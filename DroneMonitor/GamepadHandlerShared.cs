

namespace DroneMonitor
{
    public abstract class GamepadHandler
    {
        public bool IsControlByPad { get; set; } = false;
        public bool IsThrottleByPad { get; set; } = false;
        public List<float> U5 { get; protected set; } = new() { 0, 0, 0, 0, 0 };
        public List<float> PID_tar { get; protected set; } = new() { 5, 0, 0 };

        protected IDispatcherTimer? _gamepadTimer;
        protected readonly Slider[] sliders;
        protected readonly Label msgPad;
        protected readonly Stepper[] steppers;
        public GamepadHandler(Slider[] sliderArray, Label messageLabel, Stepper[] stepperArray)
        {
            // Initialize fields
            this.sliders = sliderArray;
            this.msgPad = messageLabel;
            this.steppers = stepperArray;
            Init();
        }
        public abstract void Start();
        public abstract void Dispose();
        public abstract void Init();
    }
}
