using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using DroneMonitor;
using DroneMonitor.Views;

namespace DroneMonitor.Views
{
    public partial class DualPage : ContentPage
    {
        HomePage? _homePage;
        ControlDataPage? _controlPage;

        public DualPage()
        {
            InitializeComponent();

            // 既存のページをインスタンス化し、Content をコンテナに移す（簡易対応）
            _homePage = new HomePage();
            _controlPage = new ControlDataPage();

            if (_homePage.Content != null)
            {
                HomeContainer.Content = _homePage.Content;
                // 元ページの Content を切り離しておく（重複表示防止）
                _homePage.Content = null;
            }

            if (_controlPage.Content != null)
            {
                ControlContainer.Content = _controlPage.Content;
                // 追加: 移動したコンテンツのバインディング先を元のページに戻す
                ControlContainer.Content.BindingContext = _controlPage;
                _controlPage.Content = null;
            }
        }

        // AppShell から BleService を渡せるようにラッパーを用意
        public void SetBleService(BleService svc)
        {
            _homePage?.SetBleService(svc);
            _controlPage?.SetBleService(svc);
        }

        // 必要なら切断操作も委譲
        public async Task DisconnectBle()
        {
            if (_homePage != null)
            {
                // HomePage に DisconnectBle があれば呼ぶ（存在する前提）
                await _homePage.DisconnectBle();
            }
            // ControlDataPage に切断処理があれば追加する
        }
    }
}