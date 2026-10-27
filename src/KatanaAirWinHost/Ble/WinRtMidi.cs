using Windows.Devices.Enumeration;
using Windows.Devices.Midi;

namespace KatanaAirWinHost.Ble;

internal sealed record WinRtMidiDevice(string Id, string Name);

/// <summary>
/// Windows MIDI Services (WinRT) device enumeration. KATANA-AIR BLE-MIDI appears here as
/// "KATANA-AIR MIDI (Bluetooth MIDI IN/OUT)". Not called at startup: GetDeviceSelector() lets the
/// Windows Bluetooth stack claim the speaker, after which direct GATT access fails with Unreachable.
/// </summary>
internal static class WinRtMidi
{
    public static List<WinRtMidiDevice> Inputs() => ListAsync(true).GetAwaiter().GetResult();

    public static List<WinRtMidiDevice> Outputs() => ListAsync(false).GetAwaiter().GetResult();

    private static async Task<List<WinRtMidiDevice>> ListAsync(bool input)
    {
        var list = new List<WinRtMidiDevice>();
        string selector = input ? MidiInPort.GetDeviceSelector() : MidiOutPort.GetDeviceSelector();
        var devices = await DeviceInformation.FindAllAsync(selector);
        foreach (var d in devices)
        {
            if (string.IsNullOrEmpty(d.Id)) continue;
            list.Add(new WinRtMidiDevice(d.Id, d.Name ?? ""));
        }
        return list;
    }
}
