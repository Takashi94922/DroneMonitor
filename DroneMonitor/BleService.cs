using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Abstractions.Extensions;
using System.Diagnostics;

public class BleService
{
    public IAdapter Adapter { get; }
    public IBluetoothLE Ble { get; }
    public IDevice? Device { get; private set; }
    public IService? Service { get; private set; }
    public bool IsConnected => Device != null && Adapter.ConnectedDevices.Contains(Device);

    // 制御方法。変更時は ControlStateChanged で各画面に知らせる
    private byte _isControlBySelf = 0;
    public byte IsControlBySelf
    {
        get => _isControlBySelf;
        set
        {
            if (_isControlBySelf == value) return;
            _isControlBySelf = value;
            ControlStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public event EventHandler? ControlStateChanged;

    // GATT操作は同時に1つしか実行できない（Androidでは重なると busy で失敗する）ので、
    // 書き込みと通知の開始・停止はこのロックで直列化する
    private readonly SemaphoreSlim _gattLock = new(1, 1);

    // ハートビート。ドローン側は一定時間（1秒）コマンドが届かないと緊急動作に入るので、それより短い周期で送る
    private const byte CMD_HEARTBEAT = 18;
    private const int HEARTBEAT_INTERVAL_MS = 250;
    private CancellationTokenSource? _heartbeatCts;

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
    private readonly Guid CHAR_UUID_LoopStats_Telem = To128BitUuid(0xFF07);

    public event EventHandler<byte[]>? NotificationReceived;
    public event EventHandler<IDevice>? UnexpectedDisconnected;

    public BleService()
    {
        Ble = CrossBluetoothLE.Current;
        Adapter = CrossBluetoothLE.Current.Adapter;

        Adapter.DeviceDisconnected += OnDeviceDisconnected;
        Adapter.DeviceConnectionLost += OnDeviceDisconnected;
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
            //DeviceAddresses = new[] { "4c:11:ae:eb:91:86" }
            // (必要ならDeviceName, ManufacturerDataFiltersなども指定可能)
        };

        var tcs = new TaskCompletionSource<IDevice>();

        // ★ ハンドラを1つだけ登録
        EventHandler<DeviceEventArgs>? handler = null;
        handler = (s, e) =>
        {
            if (e.Device.Name?.Contains(deviceName) == true)
            {
                tcs.TrySetResult(e.Device);
            }
        };
        Adapter.DeviceDiscovered += handler;

        try
        {
            var scanningTask = Adapter.StartScanningForDevicesAsync(filter, cts.Token);
            Device = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task ? tcs.Task.Result : null;
            await Adapter.StopScanningForDevicesAsync();

            if (Device == null)
                return false;

            await scanningTask;
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
                CHAR_UUID_Command,
                CHAR_UUID_LoopStats_Telem
            };
            var keys = new[]
            {
                "Xhat_Telem",
                "PRY_Telem",
                "contU_TelemWrite",
                "ContGain_Upd",
                "Command",
                "LoopStats_Telem" // 古いファームウェアには無いので null なら登録されない
            };

            var tasks = uuids.Select(u => Service.GetCharacteristicAsync(u));
            var chars = await Task.WhenAll(tasks);

            for (int i = 0; i < uuids.Length; i++)
            {
                if (chars[i] != null)
                {
                    // 例: uuids と同じ順番で "Xhat_Telem" などのキーを用意しておく
                    Characteristics[keys[i]] = chars[i];
                }
            }

            StartHeartbeat();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Adapter.DeviceDiscovered -= handler;
        }
    }
    private void OnDeviceDisconnected(object? sender, DeviceEventArgs e)
    {
        if (Device != null && e.Device.Id == Device.Id)
        {
            StopHeartbeat();
            Device = null;
            Service = null;
            Characteristics.Clear();
            UnexpectedDisconnected?.Invoke(this, e.Device);
        }
    }

    // キーで指定したCharacteristicに書き込む。他のGATT操作と重ならないよう直列化する
    public async Task<bool> WriteAsync(string key, byte[] data)
    {
        if (!Characteristics.TryGetValue(key, out var characteristic))
            return false;

        await _gattLock.WaitAsync();
        try
        {
            await characteristic.WriteAsync(data);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"BLE書き込み失敗 {key}: {ex.Message}");
            return false;
        }
        finally
        {
            _gattLock.Release();
        }
    }

    // 通知開始・停止もキーで指定できるように
    public async Task<bool> StartNotificationAsync(string key)
    {
        if (!Characteristics.TryGetValue(key, out var characteristic))
            return false;

        await _gattLock.WaitAsync();
        try
        {
            characteristic.ValueUpdated -= OnValueUpdated; // 二重登録防止
            characteristic.ValueUpdated += OnValueUpdated;
            await characteristic.StartUpdatesAsync();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"通知開始失敗 {key}: {ex.Message}");
            return false;
        }
        finally
        {
            _gattLock.Release();
        }
    }

    public async Task<bool> StopNotificationAsync(string key)
    {
        if (!Characteristics.TryGetValue(key, out var characteristic))
            return false;

        await _gattLock.WaitAsync();
        try
        {
            characteristic.ValueUpdated -= OnValueUpdated;
            await characteristic.StopUpdatesAsync();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"通知停止失敗 {key}: {ex.Message}");
            return false;
        }
        finally
        {
            _gattLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        StopHeartbeat();
        if (IsConnected)
        {
            // ① 通知をすべて停止してイベント解除
            foreach (var kv in Characteristics)
            {
                var c = kv.Value;
                try
                {
                    c.ValueUpdated -= OnValueUpdated;

                    if (c.CanUpdate)
                        await c.StopUpdatesAsync();
                }
                catch
                {
                    // 切断中は GATT エラーが出ることがあるので握りつぶす
                }
            }

            // ② デバイス切断
            try
            {
                    await Adapter.DisconnectDeviceAsync(Device);
            }
            catch
            {
                // Android では切断中に例外が出ることがあるので無視
            }

        }
    }

    private void StartHeartbeat()
    {
        StopHeartbeat();
        _heartbeatCts = new CancellationTokenSource();
        _ = HeartbeatLoopAsync(_heartbeatCts.Token);
    }

    private void StopHeartbeat()
    {
        _heartbeatCts?.Cancel();
        _heartbeatCts = null;
    }

    // 接続中は HEARTBEAT_INTERVAL_MS ごとに生存通知を送る（書き込みキューを通すので他の送信とは重ならない）
    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        byte seq = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HEARTBEAT_INTERVAL_MS, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (!IsConnected) continue;
            await WriteAsync("Command", new byte[] { CMD_HEARTBEAT, seq++ });
        }
    }

    private void OnValueUpdated(object? sender, CharacteristicUpdatedEventArgs e)
    {
        NotificationReceived?.Invoke(e.Characteristic, e.Characteristic.Value);
    }
}