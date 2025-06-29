using Microsoft.Maui.Controls;

namespace DroneMonitor.Views
{
    public partial class DashboardPage : ContentPage
    {
        private BleService? _bleService;

        public DashboardPage()
        {
            InitializeComponent();
        }

        public void SetBleService(BleService bleService)
        {
            _bleService = bleService;
            // 必要ならここでイベント登録や初期化
        }
    }
}