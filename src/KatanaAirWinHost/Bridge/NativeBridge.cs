using System.Collections.Concurrent;
using System.Text.Json;
using KatanaAirWinHost.Ble;
using KatanaAirWinHost.Model;
using Windows.Devices.Bluetooth;

namespace KatanaAirWinHost.Bridge;

/// <summary><c>$$</c> command dispatch.</summary>
internal sealed class NativeBridge : IDisposable
{
    private const int CommandLogLimit = 200;
    private readonly ModelProfile _model;

    /// <summary>$$ commands handled by the host; listed by /__debug and in HOST-INTERFACES.md.</summary>
    public static readonly string[] SupportedCommands =
    {
        "$$app_ready", "$$app_getevent", "$$app_startevent", "$$app_storage", "$$app_storage2",
        "$$app_control", "$$app_rotation", "$$app_setSleepable", "$$app_exit", "$$app_clipboard",
        "$$app_startScan", "$$app_toggleConnectBLEDevice",
        "$$app_importfile", "$$app_importmultiplefile", "$$app_exportfile", "$$app_exportmultiplefile",
        "$$midi_inendpoints", "$$midi_outendpoints", "$$midi_inconnect", "$$midi_outconnect",
        "$$midi_indisconnect", "$$midi_outdisconnect", "$$midi_send", "$$midi_panel",
        "$$fs_separator", "$$fs_path", "$$fs_volumes", "$$fs_stat", "$$fs_contents",
        "$$fs_mkdir", "$$fs_unlink", "$$fs_copy", "$$fs_move", "$$fs_unzip",
        "$$fs_readString", "$$fs_readData", "$$fs_writeString", "$$fs_writeData",
        "$$fs_appendString", "$$fs_appendData",
        "$$fs_savefilename", "$$fs_openfilename", "$$fs_openfilenamev2",
        "$$host_event",
        "$$btx_viewPositionConfig", "$$btx_show", "$$btx_hide", "$$btx_openPage", "$$btx_sendMsgToBTX",
        "$$security_kiv", "$$security_uuidgen", "$$security_aesdecrypt", "$$security_decipher",
        "$$util_text", "$$util_bytes",
    };

    private readonly FileBridge _files;
    private readonly AppFileBridge _appFiles;
    private readonly FileDialogBridge _dialogs;
    private readonly YtPlayerBridge _yt;
    private readonly BtxBridge _btx;
    private readonly EventQueue _events;
    private readonly StorageStore _storage;

    private readonly ConcurrentQueue<string> _commandLog = new();
    private readonly object _bleGate = new();
    private readonly object _sysexGate = new();
    private readonly List<byte> _sysex = new();

    /// <summary>Recent MIDI traffic; exposed by /__debug.</summary>
    private readonly ConcurrentQueue<string> _midiTrace = new();
    private const int MidiTraceLimit = 500;
    private int _midiTxCount;
    private int _midiRxCount;
    private int _sysexTruncated;

    private BleMidiConnection? _ble;
    private ulong _bleAddress;
    private string? _lastError;
    private int _scanBurstStarted;
    private int _exitRequested;
    private bool _midiNotConnectedWarned;
    private bool _disposed;

    /// <summary>Shutdown callback injected by AppHost; when null, $$app_exit only replies "v".</summary>
    public Action? ExitRequested { get; set; }

    public NativeBridge(
        FileBridge files,
        AppFileBridge appFiles,
        FileDialogBridge dialogs,
        YtPlayerBridge yt,
        BtxBridge btx,
        EventQueue events,
        StorageStore storage,
        string? addressOverride,
        ModelProfile model)
    {
        _files = files;
        _appFiles = appFiles;
        _dialogs = dialogs;
        _yt = yt;
        _btx = btx;
        _events = events;
        _storage = storage;
        _model = model;

        // Address precedence: --addr over ble.address in storage.json; --addr is not written back.
        _bleAddress = ParseAddress(addressOverride) ?? storage.GetBleAddress();
    }

    private static ulong? ParseAddress(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong address) && address != 0
            ? address
            : null;
    }

    // ------------------------------------------------------------------ dispatch

    public string Handle(string command, IReadOnlyList<string> args)
    {
        LogCall(command, args);

        switch (command)
        {
            // -------------------------------------------------------------- app
            case "$$app_ready":
                return ProtocolCodec.Enc(null);

            case "$$app_getevent":
                return ProtocolCodec.Enc(_events.Pop());

            case "$$app_startevent":
                // First host call at startup; starts the onScanned re-push.
                if (Interlocked.Exchange(ref _scanBurstStarted, 1) == 0)
                    ScheduleScanBurst();
                return ProtocolCodec.Enc(null);

            case "$$app_storage":
                if (args.Count > 0 && args[0].Length > 0)
                {
                    _storage.Set("", args[0]);
                    return ProtocolCodec.Enc(null);
                }
                return ProtocolCodec.Enc(_storage.Get(""));

            case "$$app_storage2":
                if (args.Count >= 2)
                {
                    if (args[1].Length == 0) _storage.Remove(args[0]);
                    else _storage.Set(args[0], args[1]);
                    return ProtocolCodec.Enc(null);
                }
                return ProtocolCodec.Enc(_storage.Get(Arg(args, 0)));

            case "$$app_control":
            case "$$app_rotation":
            case "$$app_setSleepable":
                return ProtocolCodec.Enc(null);

            case "$$app_exit":
                // Replies "v"; shutdown runs on a background thread after 250 ms.
                RequestExit();
                return ProtocolCodec.Enc(null);

            case "$$app_clipboard":
                return ProtocolCodec.Enc("");

            case "$$app_startScan":
                PushScanResults();
                return ProtocolCodec.Enc(null);

            case "$$app_toggleConnectBLEDevice":
                _events.Push("midi", "changed");
                return ProtocolCodec.Enc(null);

            // -------------------------------------------------------------- midi
            case "$$midi_inendpoints":
                return ProtocolCodec.Enc(EndpointsJson("IN"));

            case "$$midi_outendpoints":
                return ProtocolCodec.Enc(EndpointsJson("OUT"));

            case "$$midi_inconnect":
            case "$$midi_outconnect":
                EnsureConnected();
                return ProtocolCodec.Enc(null);

            case "$$midi_indisconnect":
            case "$$midi_outdisconnect":
                Disconnect();
                return ProtocolCodec.Enc(null);

            case "$$midi_send":
                SendMidi(Arg(args, 0));
                return ProtocolCodec.Enc(null);

            case "$$midi_panel":
                return ProtocolCodec.Enc(null);

            // -------------------------------------------------------------- fs
            case "$$fs_separator":
                return ProtocolCodec.Enc(FileBridge.Separator);

            case "$$fs_path":
                return ProtocolCodec.Enc(_files.PathFor(Arg(args, 0)));

            case "$$fs_volumes":
                return ProtocolCodec.Enc(FileBridge.VolumesJson());

            case "$$fs_stat":
                return ProtocolCodec.Enc(_files.StatJson(Arg(args, 0)));

            case "$$fs_contents":
                return ProtocolCodec.Enc(_files.ContentsJson(Arg(args, 0)));

            case "$$fs_mkdir":
                _files.Mkdir(Arg(args, 0));
                return ProtocolCodec.Enc(null);

            case "$$fs_unlink":
                _files.Unlink(Arg(args, 0));
                return ProtocolCodec.Enc(null);

            case "$$fs_copy":
                if (args.Count >= 2) _files.Copy(args[0], args[1]);
                return ProtocolCodec.Enc(null);

            case "$$fs_move":
                if (args.Count >= 2) _files.Move(args[0], args[1]);
                return ProtocolCodec.Enc(null);

            case "$$fs_unzip":
                if (args.Count >= 2) _files.Unzip(args[0], args[1]);
                return ProtocolCodec.Enc(null);

            case "$$fs_readString":
                return ProtocolCodec.Enc(_files.ReadString(Arg(args, 0)));

            case "$$fs_readData":
                return ProtocolCodec.Enc(_files.ReadHex(Arg(args, 0)));

            case "$$fs_writeString":
            case "$$fs_writeData":
                if (args.Count >= 2) _files.WriteString(args[0], args[1]);
                return ProtocolCodec.Enc(null);

            case "$$fs_appendString":
            case "$$fs_appendData":
                if (args.Count >= 2) _files.AppendString(args[0], args[1]);
                return ProtocolCodec.Enc(null);

            // Dialogs reply "v" immediately; results are pushed as events.
            case "$$fs_savefilename":
                _dialogs.ShowSave(Arg(args, 0), Arg(args, 1),
                    path => _events.Push("fs", "savefilename", path ?? ""));
                return ProtocolCodec.Enc(null);

            case "$$fs_openfilename":
                _dialogs.ShowOpen(FileDialogBridge.ParseFilterExtensions(Arg(args, 0)), multiple: false,
                    paths => _events.Push("fs", "openfilename", paths is { Length: > 0 } ? paths[0] : ""));
                return ProtocolCodec.Enc(null);

            case "$$fs_openfilenamev2":
                _dialogs.ShowOpen(FileDialogBridge.ParseFilterExtensions(Arg(args, 0)), multiple: true,
                    paths => _events.Push("fs", "openfilename",
                        paths is { Length: > 0 } ? JsonSerializer.Serialize(paths) : ""));
                return ProtocolCodec.Enc(null);

            // -------------------------------------------------------------- app file dialogs
            case "$$app_importfile":
                _appFiles.ImportFile(Arg(args, 0));
                return ProtocolCodec.Enc(null);

            case "$$app_importmultiplefile":
                _appFiles.ImportMultipleFile(Arg(args, 0));
                return ProtocolCodec.Enc(null);

            case "$$app_exportfile":
                _appFiles.ExportFile(Arg(args, 0));
                return ProtocolCodec.Enc(null);

            case "$$app_exportmultiplefile":
                _appFiles.ExportMultipleFile(Arg(args, 0));
                return ProtocolCodec.Enc(null);

            // -------------------------------------------------------------- YouTube
            case "$$host_event":
                return _yt.HandleHostEvent(Arg(args, 0));

            // -------------------------------------------------------------- security / util
            case "$$security_kiv":
                return ProtocolCodec.Enc("0123456789ABCDEF0123456789ABCDEF");

            case "$$security_uuidgen":
                return ProtocolCodec.Enc(Guid.NewGuid().ToString().ToUpperInvariant());

            case "$$security_aesdecrypt":
            case "$$security_decipher":
                return ProtocolCodec.Enc("");

            case "$$util_text":
                return ProtocolCodec.Enc(ProtocolCodec.HexToAscii(Arg(args, 0)));

            case "$$util_bytes":
                return ProtocolCodec.Enc(ProtocolCodec.AsciiToHex(Arg(args, 0)));

            default:
                // Page-side bridges are forwarded to Web/inject/tail.js.
                if (YtPlayerBridge.Handles(command))
                    return _yt.Handle(command, args);

                if (BtxBridge.Handles(command))
                    return _btx.Handle(command, args);

                return ProtocolCodec.Enc(null);
        }
    }

    // ------------------------------------------------------------------ MIDI endpoint enumeration

    private string EndpointsJson(string direction)
    {
        string name = $"{_model.BleDeviceName} ({(direction == "IN" ? "Bluetooth MIDI IN" : "Bluetooth MIDI OUT")})";
        return $"[{{\"MIDIDeviceNameKey\":{Q(name)},\"MIDIEndpointUIDKey\":{Q(_model.EndpointUid)}," +
               $"\"MIDIEndpointIndexKey\":{Q(_model.EndpointKey)},\"MIDIEntityNameKey\":{Q(name)}}}]";
    }

    // Event string: app\fonScanned\f<JSON string>; Web/inject/tail.js converts it to an array.
    private void PushScanResults() => _events.Push("app", "onScanned", EndpointsJson("OUT"));

    /// <summary>Pushes onScanned immediately and again after 500/1500/3000/5000 ms.</summary>
    private void ScheduleScanBurst()
    {
        PushScanResults();

        foreach (int delayMs in new[] { 500, 1500, 3000, 5000 })
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                    PushScanResults();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[scan] failed to re-push onScanned: {ex.Message}");
                }
            });
        }
    }

    // ------------------------------------------------------------------ exit

    /// <summary>Runs the injected shutdown callback on a background thread after 250 ms.</summary>
    private void RequestExit()
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0) return; // exit only once

        Action? shutdown = ExitRequested;
        if (shutdown is null)
        {
            Console.Error.WriteLine("[exit] $$app_exit ignored: no shutdown callback registered.");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250).ConfigureAwait(false);
                shutdown();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[exit] shutdown failed: {ex.Message}");
                Environment.Exit(0);
            }
        });
    }

    // ------------------------------------------------------------------ BLE

    private void EnsureConnected()
    {
        lock (_bleGate)
        {
            if (_ble is not null) return;

            try
            {
                if (_bleAddress == 0) _bleAddress = FindAddress();
                if (_bleAddress == 0)
                {
                    _lastError = $"no device address (scan found no {_model.DisplayName})";
                    Console.Error.WriteLine("[ble] no device address (scan found nothing)");
                    _events.Push("midi", "connectfailed", _model.BleDeviceName);
                    return;
                }

                var device = BluetoothLEDevice.FromBluetoothAddressAsync(_bleAddress)
                    .AsTask().GetAwaiter().GetResult();
                if (device is null)
                {
                    _lastError = "BluetoothLEDevice.FromBluetoothAddressAsync returned null";
                    Console.Error.WriteLine("[ble] FromBluetoothAddressAsync -> null");
                    _events.Push("midi", "connectfailed", _model.BleDeviceName);
                    return;
                }

                // Blocking wait on the calling thread; the WinRT continuation stays on this thread.
                _ble = BleMidiConnection.ConnectAsync(device, device.DeviceId, OnMidiData, true, _model)
                           .GetAwaiter().GetResult();

                _lastError = null;
                Console.Error.WriteLine($"[ble] connected: notify={_ble.NotificationsEnabled}");
                _events.Push("midi", "changed");
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Console.Error.WriteLine($"[ble] connect failed: {ex.Message}");
                _ble = null;
                _events.Push("midi", "connectfailed", _model.BleDeviceName);
            }
        }
    }

    private ulong FindAddress()
    {
        try
        {
            var found = new ConcurrentDictionary<ulong, string>();
            var watcher = new Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher
            {
                ScanningMode = Windows.Devices.Bluetooth.Advertisement.BluetoothLEScanningMode.Active,
            };

            watcher.Received += (_, e) =>
            {
                string n = e.Advertisement.LocalName;
                if (!string.IsNullOrEmpty(n) && n.StartsWith(_model.BleNamePrefix, StringComparison.OrdinalIgnoreCase))
                    found[e.BluetoothAddress] = n;
            };

            watcher.Start();
            Thread.Sleep(5000);
            watcher.Stop();

            var hit = found.FirstOrDefault();
            if (hit.Key != 0)
            {
                Console.Error.WriteLine($"[ble] found {hit.Value} {hit.Key:X12}");
                _storage.Set("ble.address", hit.Key.ToString("X12"));
                return hit.Key;
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Console.Error.WriteLine($"[ble] scan failed: {ex.Message}");
        }

        // No advertisement match: fall back to the paired-device list.
        ulong paired = FindPairedAddress();
        if (paired != 0)
        {
            Console.Error.WriteLine($"[ble] not found in advertisements; using paired-device address {paired:X12}");
            _storage.Set("ble.address", paired.ToString("X12"));
            return paired;
        }

        return 0;
    }

    /// <summary>Finds the model's device among paired devices by name; returns 0 if not found.</summary>
    private ulong FindPairedAddress()
    {
        try
        {
            var selector = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var devices = Windows.Devices.Enumeration.DeviceInformation
                .FindAllAsync(selector).AsTask().GetAwaiter().GetResult();

            foreach (var info in devices)
            {
                string name = info.Name ?? "";
                if (!name.StartsWith(_model.BleNamePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var ble = Windows.Devices.Bluetooth.BluetoothLEDevice
                        .FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult();
                    if (ble is null) continue;

                    ulong addr = ble.BluetoothAddress;
                    ble.Dispose();
                    if (addr != 0) return addr;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ble] failed to read paired device address ({name}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Console.Error.WriteLine($"[ble] enumerating paired devices failed: {ex.Message}");
        }
        return 0;
    }

    private void Disconnect()
    {
        lock (_bleGate)
        {
            if (_ble is null) return;
            try { _ble.Dispose(); } catch { }
            _ble = null;
            _events.Push("midi", "changed");
        }
    }

    private void SendMidi(string hex)
    {
        var bytes = ProtocolCodec.HexToBytes(hex);
        if (bytes.Length == 0) return;

        Interlocked.Increment(ref _midiTxCount);
        TraceMidi("TX", Convert.ToHexString(bytes));

        lock (_bleGate)
        {
            if (_ble is null)
            {
                // Warn once per disconnected period.
                if (!_midiNotConnectedWarned)
                {
                    _midiNotConnectedWarned = true;
                    Console.Error.WriteLine("[midi] not connected; send ignored");
                }
                return;
            }
            _midiNotConnectedWarned = false;

            try
            {
                // MTU 96; 20-byte packets; 3 bytes header/timestamp; 17 payload bytes per write.
                const int maxChunk = 20;
                if (bytes.Length <= maxChunk - 3)
                {
                    if (!_ble.SendAsync(bytes).GetAwaiter().GetResult())
                        Console.Error.WriteLine("[midi] write failed");
                    return;
                }

                for (int off = 0; off < bytes.Length; off += maxChunk - 3)
                {
                    int len = Math.Min(maxChunk - 3, bytes.Length - off);
                    var chunk = new byte[len];
                    Array.Copy(bytes, off, chunk, 0, len);
                    if (!_ble.SendAsync(chunk).GetAwaiter().GetResult())
                        Console.Error.WriteLine($"[midi] chunk write failed at {off}");
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Console.Error.WriteLine($"[midi] send failed: {ex.Message}");
            }
        }
    }

    private void OnMidiData(byte[] midi)
    {
        if (midi.Length == 0) return;

        lock (_sysexGate)
        {
            // Per-packet trace (last 200 entries).
            TraceMidi("RX*", $"{midi.Length}B {Convert.ToHexString(midi)}");

            // SysEx is assembled across packets; F7 terminates a message.
            foreach (byte b in midi)
            {
                _sysex.Add(b);
                if (b == 0xF7)
                {
                    var complete = _sysex.ToArray();
                    _sysex.Clear();

                    // Fragments shorter than 2 bytes or without F0 are dropped.
                    if (complete.Length < 2 || complete[0] != 0xF0)
                    {
                        Interlocked.Increment(ref _sysexTruncated);
                        TraceMidi("!!", $"discarding malformed fragment {Convert.ToHexString(complete)}");
                        continue;
                    }

                    Interlocked.Increment(ref _midiRxCount);
                    TraceMidi("RX", Convert.ToHexString(complete));
                    _events.Push("midi", "message", Convert.ToHexString(complete),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
                else if (b == 0xF0 && _sysex.Count > 1)
                {
                    // A new F0 restarts the buffer.
                    Interlocked.Increment(ref _sysexTruncated);
                    TraceMidi("!!", $"previous SysEx missing F7; dropped {_sysex.Count} bytes and restarted");
                    _sysex.Clear();
                    _sysex.Add(b);
                }
            }

            // ALL BACKUP request size is 0x1303F (~77 KB); the accumulation cap is 512 KB.
            if (_sysex.Count > 512 * 1024)
            {
                Interlocked.Increment(ref _sysexTruncated);
                TraceMidi("!!", "SysEx accumulation exceeded 512 KB; clearing");
                _sysex.Clear();
            }
        }
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>Returns the /__debug state as JSON.</summary>
    public string DebugJson(int port, string webRoot)
    {
        bool connected;
        string address;
        string name;
        bool notify;
        string lastError;

        lock (_bleGate)
        {
            connected = _ble is not null;
            address = _bleAddress != 0 ? _bleAddress.ToString("X12") : "";
            name = _ble?.DeviceName ?? "";
            notify = _ble?.NotificationsEnabled ?? false;
            lastError = _lastError ?? "";
        }

        return JsonSerializer.Serialize(new
        {
            commands = _commandLog.ToArray(),
            supportedCommands = SupportedCommands,
            events = _events.Snapshot(),
            ble = new { connected, address, name, notify, lastError },
            midi = new
            {
                tx = Volatile.Read(ref _midiTxCount),
                rx = Volatile.Read(ref _midiRxCount),
                sysexTruncated = Volatile.Read(ref _sysexTruncated),
                pendingSysexBytes = _sysex.Count,
            },
            midiTrace = _midiTrace.ToArray(),
            port,
            webRoot,
        });
    }

    private void TraceMidi(string direction, string detail)
    {
        _midiTrace.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {direction} {detail}");
        while (_midiTrace.Count > MidiTraceLimit) _midiTrace.TryDequeue(out _);
    }

    private void LogCall(string command, IReadOnlyList<string> args)
    {
        _commandLog.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {command} {string.Join("|", args)}");
        while (_commandLog.Count > CommandLogLimit) _commandLog.TryDequeue(out _);
    }

    // ------------------------------------------------------------------ helpers

    private static string Arg(IReadOnlyList<string> args, int index) =>
        index < args.Count ? args[index] : "";

    private static string Q(string value) => ProtocolCodec.Q(value);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
