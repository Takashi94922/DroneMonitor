

namespace DroneMonitor
{
    public abstract class GamepadHandler
    {
        public bool IsControlByPad { get; protected set; } = false;
        public bool IsThrottleByPad { get; protected set; } = false;
        public List<float> U5 { get; protected set; } = new() { 0, 0, 0, 0, 0 };

        protected IDispatcherTimer? _gamepadTimer;
        protected readonly Dictionary<Slider, byte> lastSentValues;
        protected readonly Slider[] sliders;
        protected readonly Label msgPad;
        public GamepadHandler(
            Dictionary<Slider, byte> sentValues,
            Slider[] sliderArray,
            Label messageLabel)
        {
            // Initialize fields
            this.lastSentValues = sentValues;
            this.sliders = sliderArray;
            this.msgPad = messageLabel;
            Init();
        }
        public abstract void Start();
        public abstract void Dispose();
        public abstract void Init();
    }
}
