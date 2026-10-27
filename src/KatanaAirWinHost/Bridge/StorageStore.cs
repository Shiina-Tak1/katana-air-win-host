using System.Text.Json;

namespace KatanaAirWinHost.Bridge;

/// <summary>Reads and writes data\storage.json.</summary>
internal sealed class StorageStore
{
    // storage() uses key ""; storage2(k, v) uses one key per call, and an empty v deletes it.
    // Host keys: "ble.address" (device address), "dialog.lastdir" (last file dialog directory).
    private readonly string _path;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public StorageStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "storage.json");
        Load();
    }

    public string Get(string key)
    {
        lock (_gate)
            return _values.TryGetValue(key, out var v) ? v : "";
    }

    public ulong GetBleAddress()
    {
        string text = Get("ble.address");
        return ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out ulong addr) ? addr : 0;
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            if (value.Length == 0) _values.Remove(key);
            else _values[key] = value;
            SaveLocked();
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (_values.Remove(key)) SaveLocked();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            if (dict is null) return;
            lock (_gate)
            {
                foreach (var kv in dict) _values[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[storage] read failed (ignored, continuing with empty storage): {ex.Message}");
        }
    }

    private void SaveLocked()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_values));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[storage] write failed: {ex.Message}");
        }
    }
}
