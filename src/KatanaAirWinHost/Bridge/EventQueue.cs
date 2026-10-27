using System.Collections.Concurrent;

namespace KatanaAirWinHost.Bridge;

/// <summary>Event queue from host to frontend.</summary>
internal sealed class EventQueue
{
    // Event string format: prop \f type \f arg1 \f arg2 ... (fields separated by '\f', 0x0C).
    // Argument strings must not contain '\f'. The frontend polls one event per $$app_getevent call.

    /// <summary>Maximum queued events; the oldest are dropped first.</summary>
    public const int MaxQueued = 2000;

    /// <summary>Retained events exposed by /__debug.</summary>
    public const int DebugHistory = 50;

    private readonly ConcurrentQueue<string> _queue = new();
    private readonly Queue<string> _history = new();
    private readonly object _historyGate = new();

    /// <summary>Enqueues one event; fields are joined with '\f'.</summary>
    public void Push(string prop, string type, params object?[] args)
    {
        var parts = new List<string>(args.Length + 2) { prop, type };
        foreach (var a in args)
            parts.Add(a?.ToString() ?? "");
        PushRaw(string.Join('\f', parts));
    }

    /// <summary>Enqueues a pre-assembled event string.</summary>
    public void PushRaw(string ev)
    {
        if (string.IsNullOrEmpty(ev)) return;

        _queue.Enqueue(ev);
        while (_queue.Count > MaxQueued && _queue.TryDequeue(out _)) { }

        lock (_historyGate)
        {
            _history.Enqueue(ev);
            while (_history.Count > DebugHistory) _history.Dequeue();
        }
    }

    /// <summary>Dequeues one event, or null when the queue is empty.</summary>
    public string? Pop() => _queue.TryDequeue(out var ev) ? ev : null;

    /// <summary>Returns the retained events; the queue is unchanged.</summary>
    public string[] Snapshot()
    {
        lock (_historyGate)
            return _history.ToArray();
    }
}
