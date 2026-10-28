using System.Diagnostics;
using System.Net.Sockets;
using KatanaAirWinHost.Ble;
using KatanaAirWinHost.Bridge;
using KatanaAirWinHost.Cli;
using KatanaAirWinHost.Http;
using KatanaAirWinHost.Model;

namespace KatanaAirWinHost.Host;

/// <summary>
/// Assembles the host: web root, data directory, HttpServer, NativeBridge, lifecycle.
/// Startup does not enumerate WinRT MIDI: MidiInPort.GetDeviceSelector() lets the Windows Bluetooth
/// stack claim the speaker, after which direct GATT access fails with Unreachable.
/// </summary>
internal static class AppHost
{
    private const int WebSearchDepth = 8;

    public static int Run(CliOptions options)
    {
        Dialogs.Enabled = !options.NoUi;

        string dataDir = HostConfig.DataDirectory();
        Directory.CreateDirectory(dataDir);

        if (options.Reset) HostConfig.Reset();

        HostConfig config = HostConfig.Load();

        ModelProfile? profile = ModelProfiles.Find(options.ModelId ?? config.Model ?? ModelProfiles.Default.Id);
        if (profile is null)
        {
            Dialogs.Error("Katana Air Win Host",
                $"unknown model '{config.Model}'; available: {string.Join(", ", ModelProfiles.All.Select(p => p.Id))}");
            return 1;
        }

        ModelProfile model = profile with { BleNamePrefix = options.BleNamePrefix ?? profile.BleNamePrefix };

        var storage = new StorageStore(dataDir);
        using var dialogs = new FileDialogBridge(storage);

        string? webRoot = ResolveWebRoot(options, config, dialogs);
        if (webRoot is null) return 1;

        WarnMissingInjectScripts();

        var events = new EventQueue();
        var files = new FileBridge(dataDir);
        var appFiles = new AppFileBridge(dialogs, events, files);
        var yt = new YtPlayerBridge(events);
        var btx = new BtxBridge(events);
        using var bridge = new NativeBridge(files, appFiles, dialogs, yt, btx, events, storage,
            ResolveAddress(options, config), model);

        HttpServer server;
        try
        {
            server = new HttpServer(
                webRoot,
                options.Port,
                bridge.Handle,
                port => bridge.DebugJson(port, webRoot),
                port => bridge.StatusJson(port, webRoot),
                bridge.Reconnect);
        }
        catch (SocketException ex)
        {
            string portText = options.Port is null ? "a system-assigned port" : $"port {options.Port}";
            Console.Error.WriteLine($"Cannot listen on {portText}: {ex.Message}");
            if (options.Port is not null)
                Console.Error.WriteLine($"Port {options.Port} may already be in use by another process.");

            Dialogs.Error("Katana Air Win Host",
                $"Cannot listen on {portText}: {ex.Message}" +
                (options.Port is null
                    ? ""
                    : $"{Environment.NewLine}Port {options.Port} may already be in use by another process."));
            return 1;
        }

        using (server)
        {
            PrintBanner(webRoot, dataDir, server.Port, model);

            // --no-browser: no browser process; browser stays null.
            Process? browser = options.NoBrowser
                ? null
                : BrowserLauncher.Launch($"http://localhost:{server.Port}/index.html", options.Dev);

            // The browser window is not the file dialog owner: a foreign-process owner makes
            // GetOpenFileNameW fail with CDERR_DIALOGFAILURE (0xFFFF). See FileDialogBridge.OwnerWindow().

            // $$app_exit: NativeBridge returns "v" to the browser, then calls this on a background thread.
            bridge.ExitRequested = () => Shutdown(server, bridge, dialogs, browser);

            WaitForShutdown();

            // Close the window opened by this process on Ctrl+C exit.
            BrowserLauncher.Close(browser);
        }

        return 0;
    }

    /// <summary>
    /// $$app_exit shutdown: close the Edge window, dispose the bridge and dialogs, stop HTTP, exit 0.
    /// Environment.Exit is the exit path; WinRT event sources and background threads are still
    /// running, and each Dispose is guarded separately.
    /// </summary>
    private static void Shutdown(HttpServer server, NativeBridge bridge, FileDialogBridge dialogs, Process? browser)
    {
        BrowserLauncher.Close(browser);

        try { bridge.Dispose(); } catch (Exception ex) { Console.Error.WriteLine($"[exit] failed to dispose bridge: {ex.Message}"); }
        try { dialogs.Dispose(); } catch (Exception ex) { Console.Error.WriteLine($"[exit] failed to release dialog thread: {ex.Message}"); }
        try { server.Dispose(); } catch (Exception ex) { Console.Error.WriteLine($"[exit] failed to stop HTTP service: {ex.Message}"); }

        Environment.Exit(0);
    }

    // ------------------------------------------------------------------ path resolution

    /// <summary>
    /// Web root precedence: --web-root, KAWH_WEB_ROOT, host.json webRoot, web/index.html up to eight
    /// directories above the exe, then the first-run folder picker. Errors go to Dialogs and stderr.
    /// </summary>
    private static string? ResolveWebRoot(CliOptions options, HostConfig config, FileDialogBridge dialogs)
    {
        if (!string.IsNullOrWhiteSpace(options.WebRoot))
        {
            string full = Path.GetFullPath(options.WebRoot);
            if (HasIndexHtml(full)) return full;

            Console.Error.WriteLine($"--web-root directory does not contain index.html: {full}");
            Dialogs.Error("Katana Air Win Host",
                $"--web-root directory does not contain index.html:{Environment.NewLine}{full}");
            return null;
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable("KAWH_WEB_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            string full = Path.GetFullPath(fromEnvironment);
            if (HasIndexHtml(full)) return full;

            Console.Error.WriteLine($"KAWH_WEB_ROOT directory does not contain index.html: {full}");
            Dialogs.Error("Katana Air Win Host",
                $"KAWH_WEB_ROOT directory does not contain index.html:{Environment.NewLine}{full}");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(config.WebRoot))
        {
            string saved = config.WebRoot;
            if (HasIndexHtml(saved)) return Path.GetFullPath(saved);

            Console.Error.WriteLine($"[host] saved webRoot is not a frontend directory, ignored: {saved}");
        }

        string? found = FindWebRootAboveExe();
        if (found is not null) return found;

        if (options.NoUi)
        {
            PrintWebRootHelp();
            Dialogs.Error("Katana Air Win Host",
                "Frontend resources not found (web/index.html). Start with --web-root <dir> or set KAWH_WEB_ROOT.");
            return null;
        }

        string? picked = FirstRun.Prompt(dialogs, config.WebRoot);
        if (picked is not null) return picked;

        PrintWebRootHelp();
        Dialogs.Error("Katana Air Win Host", "Frontend resources not found (web/index.html); folder selection was canceled.");
        return null;
    }

    private static string? FindWebRootAboveExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < WebSearchDepth && dir is not null; depth++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "web");
            if (HasIndexHtml(candidate)) return candidate;
        }

        return null;
    }

    private static void PrintWebRootHelp()
    {
        Console.Error.WriteLine("Frontend resources not found (web/index.html).");
        Console.Error.WriteLine("  Search order: --web-root -> KAWH_WEB_ROOT -> host.json webRoot -> web/index.html up to 8 levels above the exe directory");
        Console.Error.WriteLine($"  Start point: {AppContext.BaseDirectory}");
        Console.Error.WriteLine("See README.md for frontend resource setup, or use --web-root <dir>.");
    }

    private static bool HasIndexHtml(string directory) =>
        Directory.Exists(directory) && File.Exists(Path.Combine(directory, "index.html"));

    /// <summary>
    /// Device address precedence: --addr, host.json deviceAddress, then storage.json ble.address
    /// (resolved by NativeBridge). An invalid saved address is ignored.
    /// </summary>
    private static string? ResolveAddress(CliOptions options, HostConfig config)
    {
        if (!string.IsNullOrWhiteSpace(options.Address)) return options.Address;

        string? saved = config.DeviceAddress;
        if (string.IsNullOrWhiteSpace(saved)) return null;

        string address = saved.Trim();
        if (address.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            address = address[2..];

        if (address.Length == 0 || address.Length > 16 || !address.All(Uri.IsHexDigit))
        {
            Console.Error.WriteLine($"[host] invalid saved deviceAddress ignored: {saved}");
            return null;
        }

        return address.ToUpperInvariant();
    }

    private static void WarnMissingInjectScripts()
    {
        if (!InjectResources.Exists(InjectResources.Head))
            Console.Error.WriteLine("[inject] warning: inject/head.js is not embedded in the exe.");

        if (!InjectResources.Exists(InjectResources.Tail))
            Console.Error.WriteLine("[inject] warning: inject/tail.js is not embedded in the exe.");
    }

    // ------------------------------------------------------------------ banner / lifecycle

    private static void PrintBanner(string webRoot, string dataDir, int port, ModelProfile model)
    {
        Console.WriteLine($"KatanaAirWinHost - Windows host for {model.FrontendName}");
        Console.WriteLine($"  Open  : http://localhost:{port}/");
        Console.WriteLine($"  Status: http://localhost:{port}/__status   (state page)");
        Console.WriteLine($"  Debug : http://localhost:{port}/__debug   (commands / events / BLE status)");
        Console.WriteLine($"  Web   : {webRoot}");
        Console.WriteLine($"  Data  : {dataDir}");

        // WinMM input ports, diagnostics only. BLE devices are not listed; an empty or USB-only
        // list is expected.
        var inputs = MidiEnum.Inputs();
        Console.WriteLine($"  MIDI : {(inputs.Count > 0 ? string.Join(", ", inputs.Select(d => d.Name)) : "(none)")}   (WinMM/USB ports, diagnostics only)");
        Console.WriteLine();
    }

    private static void WaitForShutdown()
    {
        Console.WriteLine("Press Ctrl+C to exit.");

        using var done = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            done.Set();
        };

        done.Wait();
    }
}
