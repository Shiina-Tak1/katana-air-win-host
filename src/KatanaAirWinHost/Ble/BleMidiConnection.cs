using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using KatanaAirWinHost.Host;
using KatanaAirWinHost.Model;

namespace KatanaAirWinHost.Ble;

/// <summary>
/// GATT connection to a BLE-MIDI characteristic. MaintainConnection is set to true; retries are
/// limited to 2 (about 7.7 s per GATT timeout) on a new BluetoothLEDevice; writes use
/// WriteWithoutResponse. An unencrypted CCCD write returns 0x8065000F.
/// </summary>
internal sealed class BleMidiConnection : IDisposable
{
    private readonly BluetoothLEDevice _device;
    private readonly GattCharacteristic _characteristic;
    private readonly GattSession? _session;
    private readonly Action<byte[]> _onData;
    private readonly bool _pairingRequired;

    /// <summary>Persists SysEx state across notifications.</summary>
    private readonly BleMidiUnframer _unframer = new();

    public string DeviceName { get; }
    public bool NotificationsEnabled { get; }

    /// <summary>True when a 0x8065000F indicates the device is not paired with Windows.</summary>
    public bool PairingRequired => _pairingRequired;

    private BleMidiConnection(BluetoothLEDevice device, GattCharacteristic ch, string name, Action<byte[]> onData, bool notify, GattSession? session, bool pairingRequired)
    {
        _device = device;
        _characteristic = ch;
        _session = session;
        DeviceName = name;
        _onData = onData;
        NotificationsEnabled = notify;
        _pairingRequired = pairingRequired;
        _characteristic.ValueChanged += OnValueChanged;
    }

    public static Task<BleMidiConnection> ConnectAsync(BluetoothLEDevice device, Action<byte[]> onData, ModelProfile model)
        => ConnectAsync(device, device.DeviceId, onData, reuseDevice: true, model);

    /// <summary>
    /// Connects with up to 2 attempts, the second on a reopened BluetoothLEDevice.
    /// Requires GetAwaiter().GetResult() on the caller's own thread.
    /// </summary>
    public static async Task<BleMidiConnection> ConnectAsync(BluetoothLEDevice device, string deviceId, Action<byte[]> onData, bool reuseDevice, ModelProfile model)
    {
        GattDeviceServicesResult? services = null;
        BluetoothLEDevice current = device;
        string name = device.Name ?? "";
        bool pairingRequired = false;
        Exception? lastError = null;

        GattSession? session = null;
        try
        {
            var bleId = BluetoothDeviceId.FromId(deviceId);
            session = GattSession.FromDeviceIdAsync(bleId).AsTask().GetAwaiter().GetResult();
            if (session is not null)
            {
                session.MaintainConnection = true;
                Console.Error.WriteLine($"[ble] GattSession ok, MaxPduSize={session.MaxPduSize}, CanMaintain={session.CanMaintainConnection}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ble] GattSession failed: 0x{ex.HResult:X8} {ex.Message}");
        }

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                services = await current.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                if (services.Status == GattCommunicationStatus.Success) break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (ex.HResult == PairingGuide.NotPairedHResult) pairingRequired = true;
                Console.Error.WriteLine($"[ble] attempt {attempt} threw 0x{ex.HResult:X8}: {ex.Message}");
            }

            if (attempt == 2) break;

            // The second attempt uses a reopened device.
            try { current.Dispose(); } catch { }
            await Task.Delay(900);
            try
            {
                var reopened = await BluetoothLEDevice.FromIdAsync(deviceId);
                if (reopened is not null)
                {
                    current = reopened;
                    name = reopened.Name ?? name;
                }
            }
            catch { }
        }

        if (services is null || services.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"GATT services failed: {services?.Status}", lastError);

        var service = services.Services.FirstOrDefault(s => s.Uuid == model.MidiServiceUuid)
                      ?? throw new InvalidOperationException("BLE-MIDI service not found");

        // Request access explicitly; the service may be held by Windows' Bluetooth-MIDI stack.
        try
        {
            var access = await service.RequestAccessAsync();
            Console.Error.WriteLine($"[ble] RequestAccessAsync -> {access}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ble] RequestAccessAsync threw: 0x{ex.HResult:X8} {ex.Message}");
        }

        // Characteristics are re-fetched on a reopened device after a failed read.
        GattCharacteristicsResult? chars = null;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (chars.Status == GattCommunicationStatus.Success) break;
            Console.Error.WriteLine($"[ble] chars attempt {attempt}: {chars.Status}");
            if (attempt == 2) break;

            await Task.Delay(400);
            try
            {
                var reopened = await BluetoothLEDevice.FromIdAsync(deviceId);
                if (reopened is not null)
                {
                    var s2 = await reopened.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                    if (s2.Status == GattCommunicationStatus.Success)
                    {
                        var svc2 = s2.Services.FirstOrDefault(s => s.Uuid == model.MidiServiceUuid);
                        if (svc2 is not null)
                        {
                            try { current.Dispose(); } catch { }
                            current = reopened;
                            service = svc2;
                        }
                    }
                }
            }
            catch { }
        }

        if (chars is null || chars.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"GATT characteristics failed: {chars?.Status}", lastError);

        var ch = chars.Characteristics.FirstOrDefault(c => c.Uuid == model.MidiCharacteristicUuid)
                 ?? throw new InvalidOperationException("BLE-MIDI characteristic not found");

        bool notify = false;
        try
        {
            var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            notify = status == GattCommunicationStatus.Success;
            if (!notify) Console.Error.WriteLine($"[ble] notify enable failed: {status}");
        }
        catch (Exception ex)
        {
            if (ex.HResult == PairingGuide.NotPairedHResult) pairingRequired = true;
            Console.Error.WriteLine($"[ble] notify enable threw: 0x{ex.HResult:X8} {ex.Message}");
        }

        return new BleMidiConnection(current, ch, name, onData, notify, session, pairingRequired);
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var buffer = args.CharacteristicValue;
            if (buffer is null || buffer.Length == 0) return;

            var raw = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer))
                reader.ReadBytes(raw);

            // One unframer per connection; SysEx state persists across notifications.
            var midi = _unframer.Process(raw);
            if (midi.Length > 0) _onData(midi);
        }
        catch
        {
            // Exceptions stay inside the WinRT event callback.
        }
    }

    /// <summary>Reads the characteristic value once.</summary>
    public async Task<string> ReadAsync()
    {
        var result = await _characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
            return $"read status={result.Status}";

        var buffer = result.Value;
        var raw = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
            reader.ReadBytes(raw);
        return $"read ok: {Convert.ToHexString(raw)}";
    }

    /// <summary>Sends a MIDI message wrapped in a BLE-MIDI frame.</summary>
    public async Task<bool> SendAsync(byte[] midi, bool withResponse = false)
    {
        var frame = BleMidiFraming.Frame(midi);
        var writer = new DataWriter();
        writer.WriteBytes(frame);
        var buffer = writer.DetachBuffer();

        var option = withResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        var result = await _characteristic.WriteValueWithResultAsync(buffer, option);
        if (result.Status != GattCommunicationStatus.Success)
            Console.Error.WriteLine($"[ble] write status: {result.Status} (protocolError={result.ProtocolError})");
        return result.Status == GattCommunicationStatus.Success;
    }

    public void Dispose()
    {
        try { _characteristic.ValueChanged -= OnValueChanged; } catch { }
        try { _session?.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}
