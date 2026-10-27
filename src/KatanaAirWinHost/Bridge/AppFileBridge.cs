using System.Text.Json;

namespace KatanaAirWinHost.Bridge;

/// <summary>Implements <c>$$app_importfile</c>, <c>$$app_importmultiplefile</c>, <c>$$app_exportfile</c> and <c>$$app_exportmultiplefile</c>.</summary>
internal sealed class AppFileBridge
{
    // Import pushes app\fcommand\fimport\f<path>; multi-select pushes a JSON array string.
    // Export copies a file from the host data directory to a save-dialog or directory-picker target.
    private readonly FileDialogBridge _dialogs;
    private readonly EventQueue _events;
    private readonly FileBridge _files;

    public AppFileBridge(FileDialogBridge dialogs, EventQueue events, FileBridge files)
    {
        _dialogs = dialogs;
        _events = events;
        _files = files;
    }

    public void ImportFile(string filterJson)
    {
        string[] extensions = FileDialogBridge.ParseFilterExtensions(filterJson);
        _dialogs.ShowOpen(extensions, multiple: false, paths =>
        {
            if (paths is not { Length: > 0 }) return;
            _events.Push("app", "command", "import", paths[0]);
        });
    }

    public void ImportMultipleFile(string filterJson)
    {
        string[] extensions = FileDialogBridge.ParseFilterExtensions(filterJson);
        _dialogs.ShowOpen(extensions, multiple: true, paths =>
        {
            if (paths is not { Length: > 0 }) return;
            _events.Push("app", "command", "import", JsonSerializer.Serialize(paths));
        });
    }

    public void ExportFile(string sourcePath)
    {
        string source = _files.Resolve(sourcePath);
        if (!File.Exists(source))
        {
            Console.Error.WriteLine($"[app] exportfile: source file does not exist: {source}");
            return;
        }

        string name = System.IO.Path.GetFileName(source);
        string extension = System.IO.Path.GetExtension(source);
        _dialogs.ShowSave(name, extension, target =>
        {
            if (target is null) return;
            _files.CopyToUserPath(source, target);
        });
    }

    public void ExportMultipleFile(string pathsJson)
    {
        string[] sources = ParsePathArray(pathsJson);
        if (sources.Length == 0)
        {
            Console.Error.WriteLine("[app] exportmultiplefile: no valid source files");
            return;
        }

        _dialogs.ShowFolder(folder =>
        {
            if (folder is null) return;

            foreach (string source in sources)
            {
                if (!File.Exists(source))
                {
                    Console.Error.WriteLine($"[app] exportmultiplefile: source file does not exist: {source}");
                    continue;
                }

                string target = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(source));
                _files.CopyToUserPath(source, target);
            }
        });
    }

    private static string[] ParsePathArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            var array = JsonSerializer.Deserialize<string[]>(json);
            if (array is null) return Array.Empty<string>();
            return array.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[app] exportmultiplefile: failed to parse path JSON: {ex.Message}");
            return Array.Empty<string>();
        }
    }
}
