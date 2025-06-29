using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

public class BleService
{
    public IAdapter Adapter { get; }
    public IBluetoothLE Ble { get; }
    public IDevice? Device { get; private set; }
    public IService? Service { get; private set; }
    public bool IsConnected => Device != null && Adapter.ConnectedDevices.Contains(Device);

    // 複数Characteristicを保持するDictionaryを追加
    public Dictionary<string, ICharacteristic> Characteristics { get; } = new();

    // esp32 MAC 4c:11:ae:eb:91:86

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

    public async Task<bool> ConnectToDeviceAsync(string deviceName, CancellationToken? token = null)
    {
        Device = null;
        Service = null;
        Characteristics.Clear();

        Adapter.DeviceDiscovered += OnDeviceDiscovered;

        try
        {
            await Adapter.StartScanningForDevicesAsync();
            int wait = 0;
            while (Device == null && wait < 100 && (token == null || !token.Value.IsCancellationRequested))
            {
                await Task.Delay(100);
                wait++;
            }
            await Adapter.StopScanningForDevicesAsync();
            Adapter.DeviceDiscovered -= OnDeviceDiscovered;

            if (Device == null)
                return false;

            await Adapter.ConnectToDeviceAsync(Device);

            // --- MTU拡大リクエスト ---
            try
            {
                // Plugin.BLE の IDevice.RequestMtuAsync を利用
                // Android/iOS のみ有効。500 バイトをリクエスト
                if (Device != null)
                {
                    await Device.RequestMtuAsync(500);
                }
            }
            catch
            {
                // MTU拡大失敗時は無視
            }
            // -----------------------

            Service = await Device.GetServiceAsync(SERVICE_UUID);
            if (Service == null)
                return false;

            // 必要なCharacteristicをすべて取得してDictionaryに格納
            await AddCharacteristic("Xhat_Telem", CHAR_UUID_Xhat_Telem);
            await AddCharacteristic("PRY_Telem", CHAR_UUID_PRY_Telem);
            await AddCharacteristic("contU_TelemWrite", CHAR_UUID_contU_TelemWrite);
            await AddCharacteristic("ContGain_Upd", CHAR_UUID_ContGain_Upd);
            await AddCharacteristic("Command", CHAR_UUID_Command);

            // 1つでも取得できなければ失敗
            if (Characteristics.Count == 0)
                return false;

            return true;
        }
        catch
        {
            Adapter.DeviceDiscovered -= OnDeviceDiscovered;
            return false;
        }

        async Task AddCharacteristic(string key, Guid uuid)
        {
            var characteristic = await Service.GetCharacteristicAsync(uuid);
            if (characteristic != null)
                Characteristics[key] = characteristic;
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
        if (Device != null && Adapter.ConnectedDevices.Contains(Device))
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