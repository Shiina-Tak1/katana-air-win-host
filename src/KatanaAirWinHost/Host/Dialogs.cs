using System.Runtime.InteropServices;

namespace KatanaAirWinHost.Host;

/// <summary>Message boxes for errors that must be visible without a console.</summary>
internal static class Dialogs
{
    /// <summary>False under --no-ui: messages are written to stderr only.</summary>
    public static bool Enabled { get; set; } = true;

    public static void Error(string title, string message) => Show(0x00000010, title, message);

    public static void Warning(string title, string message) => Show(0x00000030, title, message);

    private static void Show(uint icon, string title, string message)
    {
        Console.Error.WriteLine($"[ui] {title}: {message}");
        if (!Enabled || !Environment.UserInteractive) return;

        try
        {
            MessageBoxW(IntPtr.Zero, message, title, icon | 0x00000000);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ui] message box failed: {ex.Message}");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
