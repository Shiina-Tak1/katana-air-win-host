using System.Diagnostics;

namespace KatanaAirWinHost.Host;

/// <summary>Opens the Windows Bluetooth settings page with pairing instructions.</summary>
internal static class PairingGuide
{
    /// <summary>HRESULT: the device must be paired in Windows before GATT access.</summary>
    public const int NotPairedHResult = unchecked((int)0x8065000F);

    public static void Show()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pair] failed to open Bluetooth settings: {ex.Message}");
        }

        Dialogs.Warning("Pairing required",
            "The device is not paired with Windows.\n\n" +
            "1. Turn the amplifier on.\n" +
            "2. On the amplifier, hold the Bluetooth button until the indicator blinks.\n" +
            "3. In Settings > Bluetooth & devices, add \"KATANA-AIR MIDI\".\n" +
            "4. Return to this window and press Reconnect.");
    }
}
