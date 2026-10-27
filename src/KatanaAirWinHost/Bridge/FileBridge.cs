using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace KatanaAirWinHost.Bridge;

/// <summary><c>$$fs_*</c>: host-side file system implementation.</summary>
internal sealed class FileBridge
{
    // Relative paths resolve against the data directory; absolute paths are used as-is.
    // writeString emits UTF-8 without BOM; readData returns hex text.

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _dataDir;

    public FileBridge(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(_dataDir);
    }

    public static string Separator => "\\";

    /// <summary>Returns the data directory, or the temporary/documents directory with a trailing separator.</summary>
    public string PathFor(string where)
    {
        if (string.Equals(where, "temporary", StringComparison.OrdinalIgnoreCase))
            return EnsureDirectory("temporary");

        if (string.Equals(where, "documents", StringComparison.OrdinalIgnoreCase))
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (docs.Length > 0 && Directory.Exists(docs)) return docs + System.IO.Path.DirectorySeparatorChar;
            return EnsureDirectory("documents");
        }

        return _dataDir;
    }

    private string EnsureDirectory(string name)
    {
        string dir = System.IO.Path.Combine(_dataDir, name);
        Directory.CreateDirectory(dir);
        return dir + System.IO.Path.DirectorySeparatorChar;
    }

    /// <summary>Resolves a relative path against the data directory; absolute paths are returned unchanged.</summary>
    public string Resolve(string file)
    {
        if (string.IsNullOrEmpty(file)) return _dataDir;
        if (System.IO.Path.IsPathRooted(file)) return file;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(_dataDir, file));
    }

    /// <summary>Returns the file content as UTF-8 text, or "" when the file is missing.</summary>
    public string ReadString(string file)
    {
        try
        {
            string p = Resolve(file);
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : "";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fs] readString failed {file}: {ex.Message}");
            return "";
        }
    }

    /// <summary>Returns the file content as hex text.</summary>
    public string ReadHex(string file)
    {
        try
        {
            string p = Resolve(file);
            return File.Exists(p) ? Convert.ToHexString(File.ReadAllBytes(p)) : "";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fs] readData failed {file}: {ex.Message}");
            return "";
        }
    }

    /// <summary>Writes the file as UTF-8 without BOM.</summary>
    public void WriteString(string file, string text)
    {
        try
        {
            string p = Resolve(file);
            string? dir = System.IO.Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(p, text, Utf8NoBom);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fs] writeString failed {file}: {ex.Message}");
        }
    }

    /// <summary>Appends text as UTF-8 without BOM.</summary>
    public void AppendString(string file, string text)
    {
        try
        {
            string p = Resolve(file);
            string? dir = System.IO.Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(p, text, Utf8NoBom);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fs] appendString failed {file}: {ex.Message}");
        }
    }

    /// <summary>Returns size, mtime and directory flag as JSON.</summary>
    public string StatJson(string file)
    {
        try
        {
            string p = Resolve(file);
            if (File.Exists(p))
            {
                var fi = new FileInfo(p);
                long mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                return $"{{\"size\":{fi.Length},\"mtime\":{mtime},\"directory\":false}}";
            }
            if (Directory.Exists(p)) return "{\"size\":0,\"mtime\":0,\"directory\":true}";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fs] stat failed {file}: {ex.Message}");
        }
        return "{}";
    }

    /// <summary>Returns the folder entries as a JSON array.</summary>
    public string ContentsJson(string folder)
    {
        try
        {
            string p = Resolve(folder);
            if (!Directory.Exists(p)) return "[]";

            var items = new List<string>();
            foreach (string entry in Directory.GetFileSystemEntries(p))
            {
                bool isDir = Directory.Exists(entry);
                long size = isDir ? 0 : new FileInfo(entry).Length;
                string name = System.IO.Path.GetFileName(entry);
                items.Add($"{{\"name\":{ProtocolCodec.Q(name)},\"size\":{size}," +
                          $"\"directory\":{(isDir ? "true" : "false")}}}");
            }
            return "[" + string.Join(",", items) + "]";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fs] contents failed {folder}: {ex.Message}");
            return "[]";
        }
    }

    /// <summary>Returns the logical drive roots as a JSON array.</summary>
    public static string VolumesJson() => JsonSerializer.Serialize(Directory.GetLogicalDrives());

    public void Mkdir(string folder)
    {
        try { Directory.CreateDirectory(Resolve(folder)); }
        catch (Exception ex) { Console.Error.WriteLine($"[fs] mkdir failed {folder}: {ex.Message}"); }
    }

    public void Unlink(string file)
    {
        try
        {
            string p = Resolve(file);
            if (File.Exists(p)) File.Delete(p);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[fs] unlink failed {file}: {ex.Message}"); }
    }

    public void Copy(string from, string to)
    {
        try
        {
            string src = Resolve(from);
            string dst = Resolve(to);
            if (!File.Exists(src)) { Console.Error.WriteLine($"[fs] copy: source file not found: {src}"); return; }

            string? dir = System.IO.Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, dst, overwrite: true);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[fs] copy failed {from} -> {to}: {ex.Message}"); }
    }

    public void Move(string from, string to)
    {
        try
        {
            string src = Resolve(from);
            string dst = Resolve(to);
            if (!File.Exists(src)) { Console.Error.WriteLine($"[fs] move: source file not found: {src}"); return; }

            string? dir = System.IO.Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Move(src, dst, overwrite: true);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[fs] move failed {from} -> {to}: {ex.Message}"); }
    }

    public void Unzip(string zip, string folder)
    {
        try
        {
            string src = Resolve(zip);
            if (!File.Exists(src)) { Console.Error.WriteLine($"[fs] unzip: source file not found: {src}"); return; }

            string dst = string.IsNullOrEmpty(folder)
                ? System.IO.Path.Combine(_dataDir, System.IO.Path.GetFileNameWithoutExtension(src))
                : Resolve(folder);
            Directory.CreateDirectory(dst);
            ZipFile.ExtractToDirectory(src, dst, overwriteFiles: true);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[fs] unzip failed {zip} -> {folder}: {ex.Message}"); }
    }

    /// <summary>Copies a file to a user-selected path; returns false on failure.</summary>
    public bool CopyToUserPath(string sourcePath, string targetPath)
    {
        try
        {
            string src = Resolve(sourcePath);
            if (!File.Exists(src)) { Console.Error.WriteLine($"[fs] export: source file not found: {src}"); return false; }

            string? dir = System.IO.Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, targetPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fs] export failed {sourcePath} -> {targetPath}: {ex.Message}");
            return false;
        }
    }
}
