namespace DroneMonitor.Views
{
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();

            // 状態バーの TRIP は操縦画面のスロットル処理で実行する
            statusBar.TripRequested += async (s, e) => await homeView.TripAsync();
            homeView.ThrottleChanged += (s, percent) => statusBar.SetThrottle(percent);

            SelectTab(flightTabButton);
        }

        // AppShell から BleService を渡す
        public void SetBleService(BleService svc)
        {
            statusBar.SetBleService(svc);
            homeView.SetBleService(svc);
            gainView.SetBleService(svc);
            controlView.SetBleService(svc);
        }

        private void OnTabClicked(object? sender, EventArgs e)
        {
            if (sender is Button button) SelectTab(button);
        }

        // 左側の表示を切り替え、選択中のタブボタンを強調する
        private void SelectTab(Button selected)
        {
            homeView.IsVisible = selected == flightTabButton;
            gainView.IsVisible = selected == gainsTabButton;

            foreach (var button in new[] { flightTabButton, gainsTabButton })
            {
                bool isSelected = button == selected;
                button.BackgroundColor = isSelected ? Colors.Blue : Colors.LightGray;
                button.TextColor = isSelected ? Colors.White : Colors.Black;
                button.FontAttributes = isSelected ? FontAttributes.Bold : FontAttributes.None;
            }
        }
    }
}
