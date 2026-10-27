using System.Reflection;

namespace KatanaAirWinHost.Http;

/// <summary>
/// Page injection scripts embedded in the exe (csproj wildcard <c>Web\inject\*.js</c>, LogicalName
/// <c>inject/&lt;file name&gt;</c>). head.js runs after &lt;head&gt;; tail.js runs before &lt;/body&gt;.
/// A missing file does not fail the build and only disables that injection.
/// </summary>
internal static class InjectResources
{
    public const string Head = "head.js";
    public const string Tail = "tail.js";

    private const string Prefix = "inject/";

    private static readonly Assembly Assembly = typeof(InjectResources).Assembly;

    /// <summary>File names of all embedded injection scripts (without the inject/ prefix).</summary>
    public static IReadOnlyList<string> Names()
    {
        var names = new List<string>();
        foreach (string resource in Assembly.GetManifestResourceNames())
            if (resource.StartsWith(Prefix, StringComparison.Ordinal))
                names.Add(resource[Prefix.Length..]);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public static bool Exists(string fileName) =>
        Names().Contains(fileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the resource for a file name; null when the name is invalid or missing.</summary>
    public static byte[]? Get(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.Contains("..", StringComparison.Ordinal))
            return null;

        foreach (char c in fileName)
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '_' && c != '-')
                return null;

        using Stream? stream = Assembly.GetManifestResourceStream(Prefix + fileName);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
