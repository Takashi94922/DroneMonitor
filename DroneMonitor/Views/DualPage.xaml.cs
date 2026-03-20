using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using DroneMonitor;
using DroneMonitor.Views;

namespace DroneMonitor.Views
{
    public partial class DualPage : ContentPage
    {
        HomeView? _homeView;
        ControlDataView? _controlView;

        public DualPage()
        {
            InitializeComponent();

            // HomeView と ControlDataView を直接ホストする（ContentView ベース）
            _homeView = new HomeView();
            _controlView = new ControlDataView();

            // 直接コンテナにセット
            HomeContainer.Content = _homeView;
            ControlContainer.Content = _controlView;
        }

        // AppShell から BleService を渡せるようにラッパーを用意
        public void SetBleService(BleService svc)
        {
            _homeView?.SetBleService(svc);
            _controlView?.SetBleService(svc);
        }

        // 必要なら切断操作も委譲
        public async Task DisconnectBle()
        {
            if (_homeView != null)
            {
                await _homeView.DisconnectBle();
            }
            _controlView?.Cleanup();
        }
    }
}