using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using KatanaAirWinHost.Ble;
using KatanaAirWinHost.Bridge;
using KatanaAirWinHost.Model;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace KatanaAirWinHost.Cli;

/// <summary>Diagnostic commands: --ble-scan, --ble-pair, --ble-diag, --list-midi, --list-winrt-midi. BLE-MIDI transport is documented in HOST-INTERFACES.md.</summary>
internal static class Diagnostics
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        ModelProfile model = ModelProfiles.Find(options.ModelId ?? ModelProfiles.Default.Id) ?? ModelProfiles.Default;
        model = model with { BleNamePrefix = options.BleNamePrefix ?? model.BleNamePrefix };
        switch (options.Mode)
        {
            case CliOptions.ModeScan:
                return await BleScanAsync(options, model);
            case CliOptions.ModePair:
                return await BlePairAsync(options, model);
            case CliOptions.ModeDiag:
                return await BleDiagAsync(model);
            case CliOptions.ModeListWinMmMidi:
                return ListWinMmMidi();
            case CliOptions.ModeListWinRtMidi:
                return ListWinRtMidi();
            default:
                Console.Error.WriteLine($"unknown diagnostic command: {options.Mode}");
                return 2;
        }
    }

    // ------------------------------------------------------------------ --ble-scan

    private static async Task<int> BleScanAsync(CliOptions options, ModelProfile model)
    {
        // A connected device stops advertising; --addr skips the scan.
        ulong knownAddress = ParseAddress(options.Address);

        var found = new ConcurrentDictionary<ulong, (string Name, short Rssi)>();

        if (knownAddress == 0)
        {
            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active,
            };

            watcher.Received += (_, e) =>
            {
                string name = e.Advertisement.LocalName;
                if (string.IsNullOrEmpty(name))
                {
                    // The name may also be carried in advertising data sections: 0x08 shortened, 0x09 complete.
                    foreach (var section in e.Advertisement.DataSections)
                    {
                        if (section.DataType != 0x09 && section.DataType != 0x08) continue;
                        try
                        {
                            var bytes = new byte[section.Data.Length];
                            using var reader = DataReader.FromBuffer(section.Data);
                            reader.ReadBytes(bytes);
                            name = Encoding.UTF8.GetString(bytes).Trim('\0');
                        }
                        catch
                        {
                            // An unparsable advertising packet is ignored.
                        }
                    }
                }

                if (!string.IsNullOrEmpty(name))
                    found[e.BluetoothAddress] = (name, e.RawSignalStrengthInDBm);
            };

            Console.WriteLine("scanning 8s ...");
            watcher.Start();
            await Task.Delay(8000);
            watcher.Stop();

            Console.WriteLine($"found {found.Count} advertising devices:");
            foreach (var item in found.OrderByDescending(kv => kv.Value.Rssi))
                Console.WriteLine($"  {item.Value.Name,-28} {item.Value.Rssi,4} dBm  addr={item.Key:X12}");
        }

        ulong address;
        string label;

        if (knownAddress != 0)
        {
            Console.WriteLine($"using known address {knownAddress:X12} (skipping scan result match)");
            address = knownAddress;
            label = $"{model.BleDeviceName} (by addr)";
        }
        else
        {
            var hit = found.FirstOrDefault(kv =>
                kv.Value.Name.StartsWith(model.BleNamePrefix, StringComparison.OrdinalIgnoreCase));

            if (hit.Value.Name is null)
            {
                Console.WriteLine();
                Console.WriteLine($"No {model.DisplayName} is advertising. --ble-scan --addr FBB7D06DC373 skips the scan.");
                return 2;
            }

            address = hit.Key;
            label = hit.Value.Name;
        }

        Console.WriteLine();
        Console.WriteLine($"connecting to {label} ({address:X12}) by address ...");

        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null)
        {
            Console.WriteLine("FromBluetoothAddressAsync -> null");
            return 1;
        }
        Console.WriteLine($"  status={device.ConnectionStatus}");

        var received = new List<string>();
        void OnMidi(byte[] midi)
        {
            lock (received) received.Add(Convert.ToHexString(midi));
        }

        try
        {
            var conn = await BleMidiConnection.ConnectAsync(device, device.DeviceId, OnMidi, reuseDevice: true, model);
            await PrintLinkCheckAsync(conn, received);
            conn.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  connect FAILED: {ex.Message}");

            // Unpaired links cannot connect; pairing runs while the device still advertises,
            // then the connection is retried.
            await TryPairAndRetryAsync(device, address, OnMidi, received, model);
        }

        Console.WriteLine("done");
        return 0;
    }

    /// <summary>Reads one characteristic value and sends one Device Inquiry, printing both to the console.</summary>
    private static async Task PrintLinkCheckAsync(BleMidiConnection conn, List<string> received)
    {
        Console.WriteLine($"  gatt ok, notify={conn.NotificationsEnabled}");
        Console.WriteLine($"  {await conn.ReadAsync()}");

        const string inquiry = "F07E000601F7";
        Console.WriteLine($"  TX: {inquiry}");
        Console.WriteLine($"  tx: {(await conn.SendAsync(ProtocolCodec.HexToBytes(inquiry), withResponse: false) ? "ok" : "FAILED")}");

        await Task.Delay(3000);

        lock (received)
        {
            Console.WriteLine($"  RX ({received.Count}):");
            foreach (string hex in received) Console.WriteLine($"    {hex}");
        }
    }

    private static async Task TryPairAndRetryAsync(
        BluetoothLEDevice device,
        ulong address,
        Action<byte[]> onMidi,
        List<string> received,
        ModelProfile model)
    {
        var pairing = device.DeviceInformation?.Pairing;
        if (pairing is null) return;

        Console.WriteLine($"  pairing: IsPaired={pairing.IsPaired} CanPair={pairing.CanPair}");
        if (pairing.IsPaired || !pairing.CanPair) return;

        try
        {
            var result = await pairing.PairAsync();
            Console.WriteLine($"  pair: {result.Status}");

            if (result.Status != DevicePairingResultStatus.Paired &&
                result.Status != DevicePairingResultStatus.AlreadyPaired)
                return;

            Console.WriteLine("  reconnecting after pairing ...");
            await Task.Delay(1500);

            var reopened = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (reopened is null) return;

            var conn = await BleMidiConnection.ConnectAsync(reopened, reopened.DeviceId, onMidi, reuseDevice: true, model);
            await PrintLinkCheckAsync(conn, received);
            conn.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  pair threw: 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ --ble-pair

    private static async Task<int> BlePairAsync(CliOptions options, ModelProfile model)
    {
        // Active scanning sends SCAN_REQ and ends the speaker's pairing window almost immediately;
        // passive scanning is used instead. --addr touches the device only once.
        ulong address = ParseAddress(options.Address);

        if (address == 0)
        {
            Console.WriteLine("passive scan 4s ...");

            var found = new ConcurrentDictionary<ulong, string>();
            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Passive,
            };

            watcher.Received += (_, e) =>
            {
                string name = e.Advertisement.LocalName;
                if (!string.IsNullOrEmpty(name) &&
                    name.StartsWith(model.BleNamePrefix, StringComparison.OrdinalIgnoreCase))
                    found[e.BluetoothAddress] = name;
            };

            watcher.Start();
            await Task.Delay(4000);
            watcher.Stop();

            var hit = found.FirstOrDefault();
            if (hit.Value is null)
            {
                Console.WriteLine("Nothing found. --ble-pair --addr FBB7D06DC373 skips the scan.");
                return 2;
            }

            address = hit.Key;
            Console.WriteLine($"found {hit.Value} addr={address:X12}");
        }
        else
        {
            Console.WriteLine($"using address {address:X12}");
        }

        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null)
        {
            Console.WriteLine("FromBluetoothAddressAsync null");
            return 2;
        }

        var pairing = device.DeviceInformation?.Pairing;
        if (pairing is null)
        {
            Console.WriteLine("no Pairing info");
            return 2;
        }

        Console.WriteLine($"IsPaired={pairing.IsPaired} CanPair={pairing.CanPair}");
        if (pairing.IsPaired)
        {
            Console.WriteLine("already paired");
            return 0;
        }

        if (!pairing.CanPair)
            Console.WriteLine("CanPair=false; custom pairing is attempted (some devices set this only during the pairing exchange).");

        var custom = pairing.Custom;
        custom.PairingRequested += (_, e) =>
        {
            Console.WriteLine($"  PairingRequested: kind={e.PairingKind}");
            try
            {
                e.Accept();
                Console.WriteLine("  -> accepted");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  -> accept failed: {ex.Message}");
            }
        };

        const DevicePairingKinds kinds =
            DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ProvidePin | DevicePairingKinds.DisplayPin;

        var levels = new[]
        {
            DevicePairingProtectionLevel.EncryptionAndAuthentication,
            DevicePairingProtectionLevel.Encryption,
            DevicePairingProtectionLevel.Default,
            DevicePairingProtectionLevel.None,
        };

        foreach (var level in levels)
        {
            try
            {
                Console.WriteLine($"custom PairAsync({level}) ...");
                var result = await custom.PairAsync(kinds, level);
                Console.WriteLine($"  status = {result.Status}");

                if (result.Status == DevicePairingResultStatus.Paired ||
                    result.Status == DevicePairingResultStatus.AlreadyPaired)
                {
                    Console.WriteLine("PAIRED OK");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  threw: 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        Console.WriteLine("all pairing attempts failed");
        return 1;
    }

    // ------------------------------------------------------------------ --ble-diag

    private static async Task<int> BleDiagAsync(ModelProfile model)
    {
        var stopwatch = Stopwatch.StartNew();

        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var devices = await DeviceInformation.FindAllAsync(selector);
        Console.WriteLine($"paired BLE devices: {devices.Count} ({stopwatch.ElapsedMilliseconds} ms)");

        foreach (var info in devices)
        {
            Console.WriteLine();
            Console.WriteLine($"* {info.Name}");
            Console.WriteLine($"  id     : {info.Id}");

            try
            {
                stopwatch.Restart();
                var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (device is null)
                {
                    Console.WriteLine("  open   : FAILED (null)");
                    continue;
                }

                Console.WriteLine($"  open   : ok ({stopwatch.ElapsedMilliseconds} ms)");
                Console.WriteLine($"  conn   : {device.ConnectionStatus}");
                Console.WriteLine($"  name   : {device.Name}");
                Console.WriteLine($"  addr   : {device.BluetoothAddress:X12}");

                stopwatch.Restart();
                var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                Console.WriteLine($"  gatt   : {result.Status} ({stopwatch.ElapsedMilliseconds} ms), {result.Services.Count} services");

                foreach (var service in result.Services)
                {
                    Console.WriteLine($"      svc {service.Uuid}");

                    if (service.Uuid != model.MidiServiceUuid) continue;

                    var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    Console.WriteLine($"        >> BLE-MIDI service, chars={characteristics.Status} {characteristics.Characteristics.Count}");

                    foreach (var characteristic in characteristics.Characteristics)
                        Console.WriteLine($"           chr {characteristic.Uuid} props={characteristic.CharacteristicProperties}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR  : 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("done");
        return 0;
    }

    // ------------------------------------------------------------------ MIDI port list

    private static int ListWinMmMidi()
    {
        var inputs = MidiEnum.Inputs();
        var outputs = MidiEnum.Outputs();

        Console.WriteLine($"MIDI IN  ({inputs.Count})");
        foreach (var device in inputs) Console.WriteLine($"  [{device.Index}] {device.Name}");

        Console.WriteLine($"MIDI OUT ({outputs.Count})");
        foreach (var device in outputs) Console.WriteLine($"  [{device.Index}] {device.Name}");

        return 0;
    }

    private static int ListWinRtMidi()
    {
        // Enumerating WinRT MIDI lets the Windows Bluetooth stack claim the amp; a direct GATT
        // connection returns Unreachable until the host restarts.
        Console.WriteLine("WinRT MIDI enumeration may let the Windows Bluetooth stack claim the amp;");
        Console.WriteLine("      a direct GATT connection returns Unreachable until the host restarts.");
        Console.WriteLine();

        var inputs = WinRtMidi.Inputs();
        Console.WriteLine($"WinRT MIDI IN ({inputs.Count})");
        foreach (var device in inputs)
            Console.WriteLine($"  {device.Name}\n     {device.Id}");

        var outputs = WinRtMidi.Outputs();
        Console.WriteLine($"WinRT MIDI OUT ({outputs.Count})");
        foreach (var device in outputs)
            Console.WriteLine($"  {device.Name}\n     {device.Id}");

        return 0;
    }

    // ------------------------------------------------------------------ helpers

    private static ulong ParseAddress(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return 0;
        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address)
            ? address
            : 0;
    }
}
