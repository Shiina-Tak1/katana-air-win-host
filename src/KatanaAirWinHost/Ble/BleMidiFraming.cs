namespace KatanaAirWinHost.Ble;

/// <summary>
/// Frame construction for the Roland-variant BLE-MIDI protocol (not standard BLE-MIDI).
/// header = 0x80 | ((ms13 >> 7) &amp; 0x3F); ts = 0x80 | (ms13 &amp; 0x7F), bit 7 set.
/// Layout [header][ts][F0][7E][00][06][01][ts][F7]; a timestamp precedes every message and the
/// closing F7.
/// </summary>
internal static class BleMidiFraming
{
    private static int _timestamp;

    /// <summary>Wraps a complete MIDI message in a BLE-MIDI frame.</summary>
    public static byte[] Frame(byte[] midi)
    {
        int ts = Interlocked.Increment(ref _timestamp) & 0x1FFF;
        byte header = (byte)(0x80 | ((ts >> 7) & 0x3F));
        byte tsByte = (byte)(0x80 | (ts & 0x7F));

        var frame = new List<byte>(midi.Length + 3) { header, tsByte };

        bool isSysEx = midi.Length >= 2 && midi[0] == 0xF0 && midi[^1] == 0xF7;
        for (int i = 0; i < midi.Length; i++)
        {
            if (isSysEx && i == midi.Length - 1)
                frame.Add(tsByte); // F7 gets its own timestamp
            frame.Add(midi[i]);
        }
        return frame.ToArray();
    }
}

/// <summary>
/// Unframer that keeps SysEx state across packets; one instance per connection.
/// Each packet starts with [header][ts]. Inside a SysEx only 7-bit data and F7 are valid; any other
/// bit-7 byte is a timestamp, including 0xF0. Outside a SysEx only F0 starts a message.
/// </summary>
internal sealed class BleMidiUnframer
{
    private bool _inSysEx;

    public byte[] Process(byte[] packet)
    {
        var midi = new List<byte>(packet.Length);
        int i = 0;

        // Roland variant: [header][ts] with bit 7 set on both bytes.
        if (i < packet.Length && (packet[i] & 0x80) != 0)
        {
            i++;                                                            // header
            if (i < packet.Length && (packet[i] & 0x80) != 0) i++;          // timestamp
        }

        for (; i < packet.Length; i++)
        {
            byte b = packet[i];

            if (_inSysEx)
            {
                // Inside SysEx: F7 ends it, 7-bit data is kept, other bit-7 bytes are timestamps.
                // A timestamp can equal 0xF0; the F7 test precedes the F0 test.
                if (b == 0xF7) { _inSysEx = false; midi.Add(b); }
                else if ((b & 0x80) == 0) midi.Add(b);
                continue;
            }

            // Outside SysEx: only F0 starts a message; other bit-7 bytes are dropped.
            if (b == 0xF0) { _inSysEx = true; midi.Add(b); continue; }
            if ((b & 0x80) != 0) continue;

            midi.Add(b);
        }

        return midi.ToArray();
    }
}
