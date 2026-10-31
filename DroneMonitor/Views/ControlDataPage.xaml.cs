namespace DroneMonitor.Views
{
    public partial class ControlDataPage : ContentPage
    {
        public ControlDataPage()
        {
            InitializeComponent();
        }

        // AppShell から呼ばれる互換ラッパー
        public void SetBleService(BleService svc)
        {
            controlViewHost?.SetBleService(svc);
        }

        // 明示的なクリーンアップが必要なら呼ぶ
        public void Cleanup()
        {
            controlViewHost?.Cleanup();
        }
    }
}
