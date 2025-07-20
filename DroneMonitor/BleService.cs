using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Abstractions.Extensions;

public class BleService
{
    public IAdapter Adapter { get; }
    public IBluetoothLE Ble { get; }
    public IDevice? Device { get; private set; }
    public IService? Service { get; private set; }
    public bool IsConnected => Device != null && Adapter.ConnectedDevices.Contains(Device);
    public byte IsControlBySelf { get; set; } = 0; // 制御方法

    // 複数Characteristicを保持するDictionaryを追加
    public Dictionary<string, ICharacteristic> Characteristics { get; } = new();

    // サービス・キャラクタリスティックUUIDを16bitから128bitに変換
    private static Guid To128BitUuid(ushort uuid16) =>
        new Guid($"0000{uuid16:X4}-0000-1000-8000-00805F9B34FB");

    private readonly Guid SERVICE_UUID = To128BitUuid(0x00FF);
    private readonly Guid CHAR_UUID_Xhat_Telem = To128BitUuid(0xFF01);
    private readonly Guid CHAR_UUID_PRY_Telem = To128BitUuid(0xFF02);
    private readonly Guid CHAR_UUID_contU_TelemWrite = To128BitUuid(0xFF03);
    private readonly Guid CHAR_UUID_ContGain_Upd = To128BitUuid(0xFF04);
    private readonly Guid CHAR_UUID_Command = To128BitUuid(0xFF05);

    public event EventHandler<byte[]>? NotificationReceived;

    public BleService()
    {
        Ble = CrossBluetoothLE.Current;
        Adapter = CrossBluetoothLE.Current.Adapter;
    }
    async Task<bool> RequestBlePermissionsAsync()
    {
        // 位置情報権限をチェック＆リクエスト
        var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();

        return status == PermissionStatus.Granted;

    }
    public async Task<bool> ConnectToDeviceAsync(string deviceName, CancellationToken? token = null)
    {
        Device = null;
        Service = null;
        Characteristics.Clear();
        await RequestBlePermissionsAsync();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        
        // スキャン時に使うフィルタ設定を作成
        var filter = new ScanFilterOptions
        {
            // サービス UUID で絞り込み
            //ServiceUuids = new[] { SERVICE_UUID },
            DeviceAddresses = new[] { "4c:11:ae:eb:91:86" }
            // (必要ならDeviceName, ManufacturerDataFiltersなども指定可能)
        };

        Adapter.DeviceDiscovered += OnDeviceDiscovered;

        try
        {
            var tcs = new TaskCompletionSource<IDevice>();
            Adapter.DeviceDiscovered += (s, e) =>
            {
                if (e.Device.Name?.Contains(deviceName) == true)
                    tcs.TrySetResult(e.Device);
            };

            await Adapter.StopScanningForDevicesAsync();
            await Adapter.StartScanningForDevicesAsync(filter, cts.Token);
            Device = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task ? tcs.Task.Result : null;
            await Adapter.StopScanningForDevicesAsync();
            Adapter.DeviceDiscovered -= OnDeviceDiscovered;

            if (Device == null)
                return false;

            await Adapter.ConnectToDeviceAsync(Device);
#if ANDROID

            await Device.RequestMtuAsync(500);
#endif
            Service = await Device.GetServiceAsync(SERVICE_UUID);
            var uuids = new[]
            {
                CHAR_UUID_Xhat_Telem,
                CHAR_UUID_PRY_Telem,
                CHAR_UUID_contU_TelemWrite,
                CHAR_UUID_ContGain_Upd,
                CHAR_UUID_Command
            };
            var keys = new[]
            {
                "Xhat_Telem",
                "PRY_Telem",
                "contU_TelemWrite",
                "ContGain_Upd",
                "Command"
            };

            var tasks = uuids.Select(u => Service.GetCharacteristicAsync(u));
            var chars = await Task.WhenAll(tasks);

            for (int i = 0; i < uuids.Length; i++)
            {
                var c = chars[i];
                if (c != null)
                {
                    // 例: uuids と同じ順番で "Xhat_Telem" などのキーを用意しておく
                    var key = keys[i];
                    Characteristics[key] = c;
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            await Adapter.StopScanningForDevicesAsync();

            Adapter.DeviceDiscovered -= OnDeviceDiscovered;
        }

        void OnDeviceDiscovered(object? sender, DeviceEventArgs e)
        {
            if (e.Device.Name != null && e.Device.Name.Contains(deviceName))
            {
                Device = e.Device;
            }
        }
    }

    // 通知開始・停止もキーで指定できるように
    public async Task<bool> StartNotificationAsync(string key)
    {
        if (!Characteristics.TryGetValue(key, out var characteristic))
            return false;

        characteristic.ValueUpdated += OnValueUpdated;
        await characteristic.StartUpdatesAsync();
        return true;
    }

    public async Task<bool> StopNotificationAsync(string key)
    {
        if (!Characteristics.TryGetValue(key, out var characteristic))
            return false;

        characteristic.ValueUpdated -= OnValueUpdated;
        await characteristic.StopUpdatesAsync();
        return true;
    }

    public async Task DisconnectAsync()
    {
        if (IsConnected)
        {
            await Adapter.DisconnectDeviceAsync(Device);
        }
        Device = null;
        Service = null;
        Characteristics.Clear();
    }

    private void OnValueUpdated(object? sender, CharacteristicUpdatedEventArgs e)
    {
        NotificationReceived?.Invoke(e.Characteristic, e.Characteristic.Value);
    }
}