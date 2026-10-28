using KatanaAirWinHost.Bridge;

namespace KatanaAirWinHost.Host;

/// <summary>First-run web root selection: folder picker, layout validation, version check.</summary>
internal static class FirstRun
{
    private const string Title = "Katana Air Win Host";
    private const string ExpectedVersion = "2.1.0";
    private const string ExpectedBuild = "74";

    /// <summary>Files a frontend directory must contain, relative to the web root.</summary>
    private static readonly string[] RequiredFiles =
    {
        "index.html",
        "import.js",
        "license.div",
        @"export\item.json",
        @"export\layout.div",
        @"js\lib\native.js",
        @"js\config\product_setting.js",
        @"js\config\address_map.js",
    };

    /// <summary>
    /// Runs the selection loop and saves the chosen directory to host.json. Returns null on cancel.
    /// </summary>
    public static string? Prompt(FileDialogBridge dialogs, string? initial)
    {
        string? folder = initial;
        while (true)
        {
            folder = dialogs.PickFolder(folder);
            if (folder is null) return null;

            string[] missing = MissingFiles(folder);
            if (missing.Length > 0)
            {
                Dialogs.Warning(Title,
                    "The selected folder is not a frontend directory. Missing:" +
                    Environment.NewLine + string.Join(Environment.NewLine, missing));
                continue;
            }

            FrontendInfo info = FrontendInfoReader.Read(folder);
            if (info.Version != ExpectedVersion || info.Build != ExpectedBuild)
            {
                Dialogs.Warning(Title,
                    $"Frontend version {Describe(info.Version)}, build {Describe(info.Build)}; " +
                    $"expected version {ExpectedVersion}, build {ExpectedBuild}. " +
                    "This frontend may be incompatible.");
            }

            HostConfig config = HostConfig.Load();
            config.WebRoot = folder;
            config.Save();
            return folder;
        }
    }

    private static string[] MissingFiles(string folder) =>
        RequiredFiles.Where(relative => !File.Exists(Path.Combine(folder, relative))).ToArray();

    private static string Describe(string value) => value.Length > 0 ? value : "unknown";
}
