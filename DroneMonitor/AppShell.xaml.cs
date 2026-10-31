using DroneMonitor.Views;
using Plugin.BLE.Abstractions.Contracts;
using System;
using System.Diagnostics;
using System.Linq; // ← 追加

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

            // 初期表示時に表示中のページへBleServiceを渡す（既定タブは先頭の Flight）
            SetBleServiceToCurrentPage();

            // タブ切り替え時にも渡す
            this.Navigated += (s, e) => SetBleServiceToCurrentPage();
            _bleService.UnexpectedDisconnected += OnDisconnected;
        }

        private void SetBleServiceToCurrentPage()
        {
            // ShellのCurrentPageは実際には現在表示中のContentPageインスタンス
            if (CurrentPage is MainPage main)
            {
                main.SetBleService(_bleService);
            }
        }

        private async void OnBleConnectClicked(object sender, EventArgs e)
        {
            var btn = (Button)sender;
            if (!_isConnected)
            {
                // スキャン中は二重に押せないようにし、状態を表示する
                btn.IsEnabled = false;
                btn.Text = "スキャン中…";
                bool connected = await _bleService.ConnectToDeviceAsync("ESP_DRONE");
                btn.IsEnabled = true;
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
                await _bleService.DisconnectAsync();
                _isConnected = false;
                MainThread.BeginInvokeOnMainThread(() => SetBleButtonState(_isConnected));
            }
        }
        private void OnDisconnected(object? sender, IDevice device)
        {
            Debug.WriteLine("予期せぬ切断");
            _isConnected = false;
            MainThread.BeginInvokeOnMainThread(() => SetBleButtonState(false));
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
