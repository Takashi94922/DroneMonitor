namespace DroneMonitor.Views
{
    public partial class HomePage : ContentPage
    {
        public HomePage()
        {
            InitializeComponent();
        }

        // DualPage / AppShell から呼ばれる互換ラッパー
        public void SetBleService(BleService svc)
        {
            homeViewHost?.SetBleService(svc);
        }

        public Task DisconnectBle()
        {
            return homeViewHost?.DisconnectBle() ?? Task.CompletedTask;
        }

        // 明示的なクリーンアップが必要なら呼ぶ
        public void Cleanup()
        {
            homeViewHost?.Cleanup();
        }
    }
}