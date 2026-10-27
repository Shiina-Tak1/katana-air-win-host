using System.Text.Json;

namespace KatanaAirWinHost.Bridge;

/// <summary>Forwards <c>$$btx_*</c> commands to the page as <c>app\fcommand\f__btx\f{"cmd":...,"args":[...]}</c>; page events return via <c>$$host_event</c> with prop "btx".</summary>
internal sealed class BtxBridge
{
    // tail.js dispatches param1 == "__btx" on $native.app.event.command; sendMsgToBTX arguments are
    // JSON text (quotes, commas) already decoded by HttpServer.
    private const string CommandPrefix = "$$btx_";

    private readonly EventQueue _events;

    public BtxBridge(EventQueue events)
    {
        _events = events;
    }

    public static bool Handles(string command) => command.StartsWith(CommandPrefix, StringComparison.Ordinal);

    /// <summary>Queues one $$btx_ command and returns "v".</summary>
    public string Handle(string command, IReadOnlyList<string> args)
    {
        string cmd = command[CommandPrefix.Length..];

        string json = JsonSerializer.Serialize(new { cmd, args });
        _events.Push("app", "command", "__btx", json);
        return ProtocolCodec.Enc(null);
    }
}
