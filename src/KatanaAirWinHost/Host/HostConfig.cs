using System.Text.Json;
using System.Text.Json.Serialization;

namespace KatanaAirWinHost.Host;

/// <summary>data\host.json: persisted startup settings.</summary>
internal sealed class HostConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Frontend resource directory selected by --web-root or the first-run wizard.</summary>
    public string? WebRoot { get; set; }

    /// <summary>Bluetooth address of the amplifier, as 12 hex digits.</summary>
    public string? DeviceAddress { get; set; }

    /// <summary>Model profile id; see ModelProfiles.</summary>
    public string? Model { get; set; }

    /// <summary>Data directory: KAWH_DATA_DIR or exe directory\data.</summary>
    public static string DataDirectory()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("KAWH_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return Path.GetFullPath(fromEnvironment);

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    public static string FilePath() => Path.Combine(DataDirectory(), "host.json");

    public static HostConfig Load()
    {
        string path = FilePath();
        try
        {
            if (!File.Exists(path)) return new HostConfig();

            return JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(path), JsonOptions)
                   ?? new HostConfig();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[host] read failed (ignored, continuing with defaults): {ex.Message}");
            return new HostConfig();
        }
    }

    public void Save()
    {
        try
        {
            string path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[host] write failed: {ex.Message}");
        }
    }

    /// <summary>Deletes data\host.json; a missing file is ignored.</summary>
    public static void Reset()
    {
        try
        {
            string path = FilePath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[host] reset failed: {ex.Message}");
        }
    }
}
