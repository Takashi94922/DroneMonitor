using System;
using DroneMonitor.Views;

namespace DroneMonitor
{
    public partial class AppShell : Shell
    {
        private readonly BleService _bleService = new();
        private bool _isConnected = false;

        public AppShell()
        {
            InitializeComponent();
            SetBleButtonState(false);

            // 初期表示時にHomePageへBleServiceを渡す
            SetBleServiceToCurrentPage();

            // タブ切り替え時にも渡す
            this.Navigated += (s, e) => SetBleServiceToCurrentPage();
        }

        private void SetBleServiceToCurrentPage()
        {
            // ShellのCurrentPageは実際には現在表示中のContentPageインスタンス
            if (CurrentPage is HomePage home)
            {
                home.SetBleService(_bleService);
            }
            else if (CurrentPage is ControlDataPage notify)
            {
                notify.SetBleService(_bleService);
            }
        }
        private async Task DiconnectBleServiceToCurrentPage()
        {
            // ShellのCurrentPageは実際には現在表示中のContentPageインスタンス
            if (CurrentPage is HomePage home)
            {
                await home.DisconnectBle();
            }
        }

        private async void OnBleConnectClicked(object sender, EventArgs e)
        {
            var btn = (Button)sender;
            if (!_isConnected)
            {
                bool connected = await _bleService.ConnectToDeviceAsync("ESP_DRONE");
                if (connected)
                {
                    _isConnected = true;
                    SetBleButtonState(_isConnected);
                    
                     // ShellのCurrentPageは実際には現在表示中のContentPageインスタンス
                    SetBleServiceToCurrentPage();
                }
                else
                {
                    // 接続失敗時の処理
                    await DisplayAlert("エラー", "接続できませんでした", "OK");
                    SetBleButtonState(false);
                }
            }
            else
            {
                await DiconnectBleServiceToCurrentPage();
                await _bleService.DisconnectAsync();
                _isConnected = false;
                SetBleButtonState(_isConnected);
            }
        }

        private void SetBleButtonState(bool connected)
        {
            // AppShell.xamlのボタン名と一致させてください
            if (this.FindByName<Button>("bleConnectBtn") is Button btn)
            {
                btn.Text = connected ? "切断" : "BLE接続";
                btn.BackgroundColor = connected ? Colors.Red : Colors.Blue;
            }
        }
    }
}
