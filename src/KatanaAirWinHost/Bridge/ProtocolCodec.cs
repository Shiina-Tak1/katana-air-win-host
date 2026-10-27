using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KatanaAirWinHost.Bridge;

/// <summary>Encoding and decoding helpers for the <c>$$</c> protocol.</summary>
internal static class ProtocolCodec
{
    // Return values: 'v' undefined, 'b1'/'b0' bool, 'd<n>' int, 'f<n>' float, 's<text>' string;
    // other leading characters raise an Error in the frontend.
    public static string Enc(object? value) => value switch
    {
        null => "v",
        bool b => b ? "b1" : "b0",
        int i => "d" + i.ToString(CultureInfo.InvariantCulture),
        long l => "d" + l.ToString(CultureInfo.InvariantCulture),
        float f => "f" + f.ToString("R", CultureInfo.InvariantCulture),
        double d => "f" + d.ToString("R", CultureInfo.InvariantCulture),
        _ => "s" + value,
    };

    /// <summary>Serializes a string as a quoted JSON string literal.</summary>
    public static string Q(string value) => JsonSerializer.Serialize(value);

    /// <summary>Converts hex text to bytes; non-hex characters are skipped.</summary>
    public static byte[] HexToBytes(string hex)
    {
        var clean = new StringBuilder(hex.Length);
        foreach (char c in hex)
            if (Uri.IsHexDigit(c)) clean.Append(c);

        string s = clean.ToString();
        var bytes = new byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return bytes;
    }

    public static string HexToAscii(string hex)
    {
        try { return Encoding.UTF8.GetString(HexToBytes(hex)); }
        catch { return ""; }
    }

    public static string AsciiToHex(string text) => Convert.ToHexString(Encoding.UTF8.GetBytes(text));
}
