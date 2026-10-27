namespace KatanaAirWinHost.Http;

/// <summary>
/// Inserts /__host/head.js after &lt;head&gt; and /__host/tail.js before &lt;/body&gt; in index.html.
/// head.js runs before the official js/lib/native.js; tail.js runs after all official scripts.
/// A missing resource skips that injection and writes one stderr line.
/// </summary>
internal static class HtmlInjector
{
    private const string HeadTag = "<script src=\"/__host/head.js\"></script>";
    private const string TailTag = "<script src=\"/__host/tail.js\"></script>";

    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object WarnGate = new();

    public static string Inject(string html)
    {
        bool hasHead = InjectResources.Exists(InjectResources.Head);
        bool hasTail = InjectResources.Exists(InjectResources.Tail);

        if (!hasHead) WarnMissing(InjectResources.Head);
        if (!hasTail) WarnMissing(InjectResources.Tail);

        if (hasHead) html = InsertAfterHead(html, HeadTag);
        if (hasTail) html = InsertBeforeBodyEnd(html, TailTag);

        return html;
    }

    private static void WarnMissing(string fileName)
    {
        lock (WarnGate)
        {
            if (!Warned.Add(fileName)) return;
        }
        Console.Error.WriteLine($"[inject] embedded resource inject/{fileName} is missing; injection skipped.");
    }

    private static string InsertAfterHead(string html, string tag)
    {
        int head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (head < 0)
        {
            WarnOnce("no-head");
            return tag + "\n" + html;
        }

        int close = html.IndexOf('>', head);
        if (close < 0)
        {
            WarnOnce("no-head");
            return tag + "\n" + html;
        }

        // After <head ...>: head.js runs before the official js/lib/native.js.
        int at = close + 1;
        return html.Insert(at, "\n" + tag);
    }

    private static string InsertBeforeBodyEnd(string html, string tag)
    {
        int bodyEnd = html.LastIndexOf("</body", StringComparison.OrdinalIgnoreCase);
        if (bodyEnd < 0)
        {
            // No </body>: the tag is appended at the end.
            WarnOnce("no-body");
            return html + "\n" + tag + "\n";
        }

        // Before </body>: tail.js runs after all official scripts.
        return html.Insert(bodyEnd, tag + "\n");
    }

    private static void WarnOnce(string key)
    {
        lock (WarnGate)
        {
            if (!Warned.Add(key)) return;
        }

        if (key == "no-head")
            Console.Error.WriteLine("[inject] <head> not found in index.html; head.js inserted at the start of the document.");
        else
            Console.Error.WriteLine("[inject] </body> not found in index.html; tail.js appended at the end of the document; the position may be wrong.");
    }
}
