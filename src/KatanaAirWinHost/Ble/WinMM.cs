using System.Runtime.InteropServices;

namespace KatanaAirWinHost.Ble;

/// <summary>
/// WinMM (winmm.dll) MIDI device enumeration. USB MIDI endpoints appear here; KATANA-AIR
/// BLE-MIDI is exposed only through Windows MIDI Services.
/// </summary>
internal static class WinMM
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MIDIINCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MIDIOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")]
    public static extern uint midiInGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "midiInGetDevCapsW", CharSet = CharSet.Unicode)]
    public static extern uint midiInGetDevCaps(IntPtr uDeviceID, ref MIDIINCAPS caps, uint cbMidiInCaps);

    [DllImport("winmm.dll")]
    public static extern uint midiOutGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "midiOutGetDevCapsW", CharSet = CharSet.Unicode)]
    public static extern uint midiOutGetDevCaps(IntPtr uDeviceID, ref MIDIOUTCAPS caps, uint cbMidiOutCaps);
}

internal sealed record MidiPortDescriptor(int Index, string Name);

internal static class MidiEnum
{
    public static List<MidiPortDescriptor> Inputs()
    {
        var list = new List<MidiPortDescriptor>();
        uint n = WinMM.midiInGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            var caps = new WinMM.MIDIINCAPS();
            if (WinMM.midiInGetDevCaps((IntPtr)i, ref caps, (uint)Marshal.SizeOf<WinMM.MIDIINCAPS>()) == 0)
                list.Add(new MidiPortDescriptor((int)i, caps.szPname ?? $"IN {i}"));
        }
        return list;
    }

    public static List<MidiPortDescriptor> Outputs()
    {
        var list = new List<MidiPortDescriptor>();
        uint n = WinMM.midiOutGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            var caps = new WinMM.MIDIOUTCAPS();
            if (WinMM.midiOutGetDevCaps((IntPtr)i, ref caps, (uint)Marshal.SizeOf<WinMM.MIDIOUTCAPS>()) == 0)
                list.Add(new MidiPortDescriptor((int)i, caps.szPname ?? $"OUT {i}"));
        }
        return list;
    }
}
