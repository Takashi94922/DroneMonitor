namespace DroneMonitor.Platforms.Android
{
    public partial class JoystickView : ContentView
    {
        private AndroidGamepadHandler? _androidHandler;
        public JoystickDrawable JoystickDrawable { get; } = new JoystickDrawable();

        public JoystickView()
        {
            InitializeComponent();
            BindingContext = this;

            var activity = Platform.CurrentActivity;
            var nativeView = Platform.CurrentActivity?.Window?.DecorView?.RootView;

            JoystickCanvas.StartInteraction += OnStartInteraction;
            JoystickCanvas.DragInteraction += OnDragInteraction;
            JoystickCanvas.EndInteraction += OnEndInteraction;
        }

        public void SetGamepadHandler(AndroidGamepadHandler handler)
        {
            _androidHandler = handler;
        }
        
        // ↓ この 3 つを必ずこのまま（アクセス修飾子は public でも private でも OK）
        public void OnStartInteraction(object sender, TouchEventArgs e)
          => UpdateStickPosition(e);

        public void OnDragInteraction(object sender, TouchEventArgs e)
          => UpdateStickPosition(e);

        public void OnEndInteraction(object sender, TouchEventArgs e)
        {
            JoystickDrawable.Center = new PointF(80, 80);
            JoystickCanvas.Invalidate();
            ApplyVirtualStick(0, 0);
        }

        void UpdateStickPosition(TouchEventArgs e)
        {
            // e.Touches[0] で最初のタッチ座標を取る
            var pt = e.Touches.FirstOrDefault();
            if (pt == null) return;

            var center = new PointF(80, 80);
            var dx = pt.X - center.X;
            var dy = pt.Y - center.Y;
            var radius = 70f;
            var len = MathF.Min(MathF.Sqrt(dx * dx + dy * dy), radius);
            var ang = MathF.Atan2(dy, dx);

            // ノブの絶対位置を更新
            JoystickDrawable.Center = new PointF(
                center.X + MathF.Cos(ang) * len,
                center.Y + MathF.Sin(ang) * len
            );
            JoystickCanvas.Invalidate();

            // -1..+1 正規化して U5 に反映
            ApplyVirtualStick(dx / radius, -dy / radius);
        }

        void ApplyVirtualStick(float stickX, float stickY)
        {
            if(_androidHandler != null)_androidHandler.OnStickChanged(stickX, stickY);
        }
    }
}