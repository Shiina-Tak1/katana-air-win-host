using System.Text.Json;

namespace KatanaAirWinHost.Bridge;

/// <summary>YouTube bridge: forwards <c>$$ytplayer_&lt;cmd&gt;</c> to the page-side player; page events return as <c>$$host_event</c> payloads (onReady, onStateChange, onCurrentTime, onAvailablePlaybackRates as a JSON array string, onPlaybackRate, onDuration, error).</summary>
internal sealed class YtPlayerBridge
{
    // Command channel: app\fcommand\f__yt\f{"cmd":"<cmd>","args":[...]}, dispatched by tail.js on param1 == "__yt".
    // Event channel: $$host_event payload "ytplayer|onStateChange|1", '|' replaced with '\f' and enqueued without a prop prefix.
    private const string CommandPrefix = "$$ytplayer_";

    private readonly EventQueue _events;

    public YtPlayerBridge(EventQueue events)
    {
        _events = events;
    }

    public static bool Handles(string command) => command.StartsWith(CommandPrefix, StringComparison.Ordinal);

    /// <summary>Queues a page command for the page-side player and returns "v".</summary>
    public string Handle(string command, IReadOnlyList<string> args)
    {
        string cmd = command[CommandPrefix.Length..];
        string json = JsonSerializer.Serialize(new { cmd, args });
        _events.Push("app", "command", "__yt", json);
        return ProtocolCodec.Enc(null);
    }

    /// <summary>Replaces '|' with '\f' in the payload and enqueues it as one event.</summary>
    public string HandleHostEvent(string payload)
    {
        if (payload.Length == 0) return ProtocolCodec.Enc(null);

        string ev = payload.Replace('|', '\f');
        _events.PushRaw(ev);
        return ProtocolCodec.Enc(null);
    }
}
