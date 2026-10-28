using System.Runtime.InteropServices;
using System.Text;

namespace KatanaAirWinHost.Host;

/// <summary>
/// Attaches the WinExe process to the console of the parent process. No console is allocated when
/// there is no parent console.
/// </summary>
internal static class ConsoleHost
{
    private const int AttachParentProcess = -1;

    public static void Attach()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess)) return;

            Console.SetOut(CreateWriter(Console.OpenStandardOutput()));
            Console.SetError(CreateWriter(Console.OpenStandardError()));
        }
        catch (Exception)
        {
            // No parent console: standard output and error stay redirected streams.
        }
    }

    private static StreamWriter CreateWriter(Stream stream) =>
        new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
