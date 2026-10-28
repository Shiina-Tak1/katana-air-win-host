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
        ModelProfile? model = ModelProfiles.Find(options.ModelId);
        if (model is null)
        {
            Console.Error.WriteLine($"unknown model '{options.ModelId}'");
            return 2;
        }

        string? webRoot = ResolveWebRoot(options.WebRoot);
        if (webRoot is null) return 1;

        string dataDir = ResolveDataDir();
        Directory.CreateDirectory(dataDir);

        WarnMissingInjectScripts();

        var storage = new StorageStore(dataDir);
        var events = new EventQueue();
        using var dialogs = new FileDialogBridge(storage);
        var files = new FileBridge(dataDir);
        var appFiles = new AppFileBridge(dialogs, events, files);
        var yt = new YtPlayerBridge(events);
        var btx = new BtxBridge(events);
        using var bridge = new NativeBridge(files, appFiles, dialogs, yt, btx, events, storage, options.Address, model);

        HttpServer server;
        try
        {
            server = new HttpServer(webRoot, options.Port, bridge.Handle, port => bridge.DebugJson(port, webRoot));
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"Cannot listen on the local port ({(options.Port is null ? "random" : options.Port.ToString())}): {ex.Message}");
            if (options.Port is not null)
                Console.Error.WriteLine($"Port {options.Port} may already be in use by another process.");
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
    /// Web root: --web-root, then KAWH_WEB_ROOT, then web/index.html up to 8 directories above the
    /// exe. Returns null when no directory is found.
    /// </summary>
    private static string? ResolveWebRoot(string? fromCommandLine)
    {
        if (!string.IsNullOrWhiteSpace(fromCommandLine))
        {
            string full = Path.GetFullPath(fromCommandLine);
            if (HasIndexHtml(full)) return full;

            Console.Error.WriteLine($"--web-root directory does not contain index.html: {full}");
            return null;
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable("KAWH_WEB_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            string full = Path.GetFullPath(fromEnvironment);
            if (HasIndexHtml(full)) return full;

            Console.Error.WriteLine($"KAWH_WEB_ROOT directory does not contain index.html: {full}");
            return null;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < WebSearchDepth && dir is not null; depth++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "web");
            if (HasIndexHtml(candidate)) return candidate;
        }

        Console.Error.WriteLine("Frontend resources not found (web/index.html).");
        Console.Error.WriteLine("  Search order: --web-root -> KAWH_WEB_ROOT -> web/index.html up to 8 levels above the exe directory");
        Console.Error.WriteLine($"  Start point: {AppContext.BaseDirectory}");
        Console.Error.WriteLine("See README.md for frontend resource setup, or use --web-root <dir>.");
        return null;
    }

    private static bool HasIndexHtml(string directory) =>
        Directory.Exists(directory) && File.Exists(Path.Combine(directory, "index.html"));

    /// <summary>Data directory: KAWH_DATA_DIR or exe directory\data.</summary>
    private static string ResolveDataDir()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("KAWH_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return Path.GetFullPath(fromEnvironment);

        return Path.Combine(AppContext.BaseDirectory, "data");
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
