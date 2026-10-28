using System.Diagnostics;

namespace KatanaAirWinHost.Host;

/// <summary>
/// Opens the interface in Edge: an application window (<c>--app=&lt;url&gt;</c>) by default, a normal
/// window with <c>--dev</c>. Without Edge the default browser is opened instead. The launched
/// Edge process is closed by AppHost on <c>$$app_exit</c> and uses a dedicated --user-data-dir.
/// </summary>
internal static class BrowserLauncher
{
    public static Process? Launch(string url, bool devMode)
    {
        string? edge = FindEdge();
        if (edge is null)
        {
            Console.Error.WriteLine("[browser] Microsoft Edge not found; opening the default browser.");
            Console.Error.WriteLine("[browser] " + url);
            OpenDefault(url);
            return null;
        }

        string profile = Path.Combine(Path.GetTempPath(), "kawh-katana-air-profile");

        // --dev: normal window; otherwise an application window.
        string appArg = devMode ? url : $"--app={url}";

        var psi = new ProcessStartInfo(edge)
        {
            UseShellExecute = false,
            Arguments = $"{appArg} --user-data-dir=\"{profile}\" --window-size=1120,940 " +
                        "--no-first-run --no-default-browser-check --disable-features=Translate",
        };

        try
        {
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[browser] failed to start Edge: {ex.Message}");
            Console.Error.WriteLine("[browser] open this URL manually: " + url);
            return null;
        }
    }

    /// <summary>Opens the URL in the default browser through the shell.</summary>
    private static void OpenDefault(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[browser] failed to open the default browser: {ex.Message}");
            Console.Error.WriteLine("[browser] open this URL manually: " + url);
        }
    }

    /// <summary>
    /// Closes the launched browser: CloseMainWindow, then Kill of the process tree after 2 s.
    /// Null is a no-op.
    /// </summary>
    public static void Close(Process? browser)
    {
        if (browser is null) return;

        try
        {
            if (browser.HasExited)
            {
                // An existing Edge instance with the same profile takes the URL and this process
                // exits; the window cannot be closed through this handle.
                Console.Error.WriteLine("[exit] the launched Edge process exited on its own; close the window manually.");
                return;
            }

            browser.CloseMainWindow();

            if (browser.WaitForExit(2000)) return;

            // The --app window runs with its own --user-data-dir and its own process tree.
            browser.Kill(entireProcessTree: true);
            browser.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[exit] failed to close Edge: {ex.Message}");
        }
    }

    private static string? FindEdge()
    {
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        string[] candidates =
        {
            Path.Combine(programFilesX86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(programFiles, @"Microsoft\Edge\Application\msedge.exe"),
        };

        foreach (string candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        return null;
    }
}
