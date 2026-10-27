using System.Globalization;

namespace KatanaAirWinHost.Cli;

/// <summary>Command-line parsing and --help text.</summary>
internal sealed class CliOptions
{
    public const string ModeApp = "app";
    public const string ModeScan = "ble-scan";
    public const string ModePair = "ble-pair";
    public const string ModeDiag = "ble-diag";
    public const string ModeListWinMmMidi = "list-midi";
    public const string ModeListWinRtMidi = "list-winrt-midi";
    public const string ModeHelp = "help";

    public string Mode { get; private set; } = ModeApp;

    /// <summary>--dev: normal Edge window (address bar, F12 available).</summary>
    public bool Dev { get; private set; }

    /// <summary>--no-browser: start the service only.</summary>
    public bool NoBrowser { get; private set; }

    /// <summary>--port &lt;n&gt;: fixed port; null lets the system assign one.</summary>
    public int? Port { get; private set; }

    /// <summary>--web-root &lt;dir&gt;: frontend resource directory.</summary>
    public string? WebRoot { get; private set; }

    /// <summary>--addr &lt;HEX12&gt;: known Bluetooth address (skips scanning).</summary>
    public string? Address { get; private set; }

    public static string HelpText =>
        """
        KatanaAirWinHost - Windows host for BOSS TONE STUDIO for KATANA-AIR

        Usage:
          KatanaAirWinHost.exe [options]               start the local service and open the UI
          KatanaAirWinHost.exe <diagnostic command> [options]
                                                       run one diagnostic command without the service

        Startup options:
          --dev                   open a normal Edge window (address bar, F12 available)
          --no-browser            start the service only, do not open a browser
          --port <n>              fixed listen port (default: a free port assigned by the system)
          --web-root <dir>        frontend resource directory (index.html required)

        Diagnostic commands (only one per run):
          --ble-scan [--addr <HEX12>]    scan and connect to the BLE-MIDI link, print received data
          --ble-pair [--addr <HEX12>]    pair with the amplifier
          --ble-diag                     list paired BLE devices and their GATT services
          --list-midi                    list legacy WinMM MIDI ports
          --list-winrt-midi              list WinRT MIDI ports

        Other:
          --addr <HEX12>          explicit Bluetooth address
                                  - with a diagnostic command: the scan is skipped
                                  - with a startup mode: overrides ble.address from storage.json
                                    for this run only; the value is not written back
          --help, -h, /?          show this help

        Environment variables:
          KAWH_WEB_ROOT           frontend resource directory (same as --web-root, lower priority)
          KAWH_DATA_DIR           data directory (default: exe directory\data)
          KAWH_PROXY_OFF=1        disable the /__proxy cloud proxy (always returns 403)

        Documentation:
          README.md            build, run, and frontend resource setup
          HOST-INTERFACES.md   host interfaces: HTTP routes, $$ protocol, CLI, environment
                               variables, BLE-MIDI transport, diagnostics
        """;

    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        options = new CliOptions();
        error = null;

        string mode = ModeApp;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help":
                case "-h":
                case "/?":
                    options.Mode = ModeHelp;
                    return true;

                case "--dev":
                    options.Dev = true;
                    break;

                case "--no-browser":
                    options.NoBrowser = true;
                    break;

                case "--port":
                {
                    if (!TryTakeValue(args, ref i, "--port", out string text, out error)) return false;
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) ||
                        port < 1 || port > 65535)
                    {
                        error = $"--port expects a port number between 1 and 65535, got: {text}";
                        return false;
                    }

                    options.Port = port;
                    break;
                }

                case "--web-root":
                {
                    if (!TryTakeValue(args, ref i, "--web-root", out string dir, out error)) return false;
                    if (dir.Trim().Length == 0)
                    {
                        error = "--web-root requires a non-empty directory";
                        return false;
                    }

                    options.WebRoot = dir;
                    break;
                }

                case "--addr":
                {
                    if (!TryTakeValue(args, ref i, "--addr", out string hex, out error)) return false;

                    string address = hex.Trim();
                    if (address.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        address = address[2..];

                    if (address.Length == 0 || address.Length > 16 || !address.All(Uri.IsHexDigit))
                    {
                        error = $"--addr expects a hexadecimal Bluetooth address (usually 12 digits), e.g. FBB7D06DC373, got: {hex}";
                        return false;
                    }

                    options.Address = address.ToUpperInvariant();
                    break;
                }

                case "--ble-scan":
                    if (!TrySwitchMode(ref mode, ModeScan, out error)) return false;
                    break;

                case "--ble-pair":
                    if (!TrySwitchMode(ref mode, ModePair, out error)) return false;
                    break;

                case "--ble-diag":
                    if (!TrySwitchMode(ref mode, ModeDiag, out error)) return false;
                    break;

                case "--list-midi":
                    if (!TrySwitchMode(ref mode, ModeListWinMmMidi, out error)) return false;
                    break;

                case "--list-winrt-midi":
                    if (!TrySwitchMode(ref mode, ModeListWinRtMidi, out error)) return false;
                    break;

                default:
                    error = args[i].StartsWith('-')
                        ? $"Unknown option: {args[i]} (see --help for the option list)"
                        : $"Unexpected argument: {args[i]} (see --help for the option list)";
                    return false;
            }
        }

        options.Mode = mode;
        return true;
    }

    private static bool TryTakeValue(string[] args, ref int index, string flag, out string value, out string? error)
    {
        error = null;

        if (index + 1 >= args.Length)
        {
            value = "";
            error = $"{flag} requires a value";
            return false;
        }

        value = args[index + 1];
        index++;
        return true;
    }

    private static bool TrySwitchMode(ref string mode, string requested, out string? error)
    {
        error = null;

        if (mode != ModeApp && mode != requested)
        {
            error = $"Only one command may be specified at a time (already have {mode}, also got {requested})";
            return false;
        }

        mode = requested;
        return true;
    }
}
