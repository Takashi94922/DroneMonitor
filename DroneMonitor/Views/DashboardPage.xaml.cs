using EmbedIO;
using EmbedIO.Files;
using EmbedIO.Utilities;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Plugin.BLE.Abstractions.Contracts;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.FileProviders;
using System.Security.Cryptography;

namespace DroneMonitor.Views
{
    public partial class DashboardPage : ContentPage
    {
        private BleService? _bleService;
        private float pitch, roll, yaw;
        private WebServer? _server;

        public DashboardPage()
        {
            InitializeComponent();
        }
        protected override void OnAppearing()
        {
            base.OnAppearing();
            if (_server == null) loadHtml();
        }

        async public void loadHtml()
        {
            // パス（string）を明示的に IFileProvider に変換
            var rootPath = System.IO.Path.Combine(FileSystem.AppDataDirectory, "WebRoot");
            Debug.WriteLine(FileSystem.AppDataDirectory);
            var server = new WebServer(o => o
                .WithUrlPrefix("http://localhost:12345/")
                .WithMode(HttpListenerMode.EmbedIO))
                .WithStaticFolder("/", rootPath, true)  // ← これが最適！
                .WithLocalSessionManager();

            _ = server.RunAsync();  // 非同期でサーバ起動
            await Task.Delay(500);
            webView.Source = "http://localhost:12345/modelview.html";
        }

        public void StartNotificationAsync()
        {
            // 通知開始の成否をログ出力
            _bleService.StartNotificationAsync("PRY_Telem").ContinueWith(t =>
                Debug.WriteLine($"PRY_Telem通知開始: {t.Result}"));
        }
        public void SetBleService(BleService bleService)
        {
            _bleService = bleService;
            _bleService.NotificationReceived -= OnNotificationReceived;
            _bleService.NotificationReceived += OnNotificationReceived;
            if (_bleService != null && _bleService.IsConnected)
            {
                StartNotificationAsync();
            }
        }

        private void OnNotificationReceived(object? sender, byte[] data)
        {
            if (_bleService == null)
            {
                Debug.WriteLine("BLE 未接続");
                return;
            }

            string key = "";
            if (sender is ICharacteristic characteristic)
            {
                key = _bleService.Characteristics.FirstOrDefault(x => x.Value == characteristic).Key ?? "";
            }
            else
            {
                // senderがBleService自身の場合（Invoke(this, data)のような実装）
                // dataが一致するCharacteristicを探す
                foreach (var pair in _bleService.Characteristics)
                {
                    // ここではValueUpdatedイベントのsenderがCharacteristicであることが前提
                    // それ以外の場合はキーを特定できないので空文字
                }
            }

            Debug.WriteLine($"受信: key={key}, data={BitConverter.ToString(data)}");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (key == "PRY_Telem" && data.Length >= 12)
                {
                    float[] floats = new float[3];
                    for (int i = 0; i < 3; i++)
                        floats[i] = BitConverter.ToSingle(data, i * 4);

                    pitch = floats[0];
                    roll = floats[1];
                    yaw = floats[2];
                    Debug.WriteLine($"{pitch:F2}, {roll:F2}, {yaw:F2}");
                    Rotetef(pitch, roll, yaw);
                }
            });
        }
        async Task Rotetef(float ptich, float roll , float yaw)
        {
            // viewer.html が完全に読み込まれたあとに呼ぶこと
            var js = $"setRotation({pitch}, {yaw}, {roll});";
            try
            {
                await webView.EvaluateJavaScriptAsync(js);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"JS呼び出し失敗: {ex}");
            }

        }
        protected override async void OnDisappearing()
        {
            base.OnDisappearing();
            if (_bleService != null)
            {
                // 必要な通知キーを指定して停止
                _bleService.StopNotificationAsync("PRY_Telem");
                // 他にも通知を止めたいCharacteristicがあればここで追加
                _bleService.NotificationReceived -= OnNotificationReceived;
            }
        }
    }
}