using System.Text.RegularExpressions;

namespace KatanaAirWinHost.Host;

/// <summary>Frontend version and build; empty strings when unknown.</summary>
internal sealed record FrontendInfo(string Version, string Build);

/// <summary>Reads the version and build from js\config\product_setting.js.</summary>
internal static class FrontendInfoReader
{
    private static readonly Regex VersionPattern =
        new(@"\bversion\s*:\s*['""]([^'""]*)['""]", RegexOptions.CultureInvariant);

    private static readonly Regex BuildPattern =
        new(@"\bbuild\s*:\s*['""]?([0-9]+)['""]?", RegexOptions.CultureInvariant);

    public static FrontendInfo Read(string webRoot)
    {
        string path = Path.Combine(webRoot, "js", "config", "product_setting.js");

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception)
        {
            return new FrontendInfo("", "");
        }

        return new FrontendInfo(Extract(VersionPattern, text), Extract(BuildPattern, text));
    }

    private static string Extract(Regex pattern, string text)
    {
        Match match = pattern.Match(text);
        return match.Success ? match.Groups[1].Value : "";
    }
}
