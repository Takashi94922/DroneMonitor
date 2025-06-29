using System;
using Microsoft.Maui.Controls;
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
            else if (CurrentPage is DashboardPage dash)
            {
                dash.SetBleService(_bleService);
            }
            else if (CurrentPage is NotificationPage notify)
            {
                notify.SetBleService(_bleService);
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
                    btn.Text = "切断";
                    btn.BackgroundColor = Colors.Blue;
                    
                     // ShellのCurrentPageは実際には現在表示中のContentPageインスタンス
                    SetBleServiceToCurrentPage();
                }
                else
                {
                    await DisplayAlert("エラー", "接続できませんでした", "OK");
                    SetBleButtonState(false);
                }
            }
            else
            {
                await _bleService.DisconnectAsync();
                _isConnected = false;
                btn.Text = "BLE接続";
                btn.BackgroundColor = Colors.Red;
                

            }
        }

        private void SetBleButtonState(bool connected)
        {
            // AppShell.xamlのボタン名と一致させてください
            if (this.FindByName<Button>("bleConnectBtn") is Button btn)
            {
                btn.Text = connected ? "切断" : "BLE接続";
                btn.BackgroundColor = connected ? Colors.Blue : Colors.Red;
            }
        }
    }
}
