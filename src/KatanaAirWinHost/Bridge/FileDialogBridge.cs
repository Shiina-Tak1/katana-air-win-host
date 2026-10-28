using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace KatanaAirWinHost.Bridge;

/// <summary>File dialogs: comdlg32 GetOpenFileNameW/GetSaveFileNameW and shell32 SHBrowseForFolderW.</summary>
internal sealed class FileDialogBridge : IDisposable
{
    // Dialogs run on a dedicated STA thread; commands reply "v" and results arrive as events.
    // Multi-select result layout: "directory\0file1\0file2\0\0".

    private const int PathCapacity = 260;             // MAX_PATH
    private const int MultiSelectCapacity = 64 * 1024; // multi-select buffer

    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_ALLOWMULTISELECT = 0x00000200;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_EXPLORER = 0x00080000;

    private const uint BIF_RETURNONLYFSDIRS = 0x00000001;
    private const uint BIF_EDITBOX = 0x00000010;
    private const uint BIF_NEWDIALOGSTYLE = 0x00000040;

    private readonly StorageStore _storage;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public FileDialogBridge(StorageStore storage)
    {
        _storage = storage;

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "kawh-file-dialogs",
        };
        // Common dialogs require STA.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    // ------------------------------------------------------------------ public API

    /// <summary>Shows the save dialog; <paramref name="onResult"/> receives null on cancel.</summary>
    public void ShowSave(string suggestedName, string extension, Action<string?> onResult)
    {
        Schedule(() =>
        {
            string? result = null;
            try { result = AskSave(suggestedName, extension); }
            catch (Exception ex) { Console.Error.WriteLine($"[dialog] save dialog failed: {ex.Message}"); }
            if (result is not null) Remember(System.IO.Path.GetDirectoryName(result));
            onResult(result);
        });
    }

    /// <summary>Shows the open dialog; <paramref name="onResult"/> receives the selected paths or null on cancel.</summary>
    public void ShowOpen(string[] extensions, bool multiple, Action<string[]?> onResult)
    {
        Schedule(() =>
        {
            string[]? result = null;
            try { result = AskOpen(extensions, multiple); }
            catch (Exception ex) { Console.Error.WriteLine($"[dialog] open dialog failed: {ex.Message}"); }
            if (result is { Length: > 0 }) Remember(System.IO.Path.GetDirectoryName(result[0]));
            onResult(result);
        });
    }

    /// <summary>Shows the folder picker; <paramref name="onResult"/> receives null on cancel.</summary>
    public void ShowFolder(Action<string?> onResult)
    {
        Schedule(() =>
        {
            string? result = null;
            try { result = AskFolder(null, "Select export folder"); }
            catch (Exception ex) { Console.Error.WriteLine($"[dialog] folder picker failed: {ex.Message}"); }
            if (result is not null) Remember(result);
            onResult(result);
        });
    }

    /// <summary>
    /// Shows the folder picker on the STA thread and waits for the result; null on cancel or when the
    /// dialog thread is unavailable.
    /// </summary>
    public string? PickFolder(string? initial)
    {
        string? result = null;
        using var done = new ManualResetEventSlim(false);

        bool scheduled = Schedule(() =>
        {
            try { result = AskFolder(initial, "Select web folder"); }
            catch (Exception ex) { Console.Error.WriteLine($"[dialog] folder picker failed: {ex.Message}"); }
            finally { done.Set(); }
        });

        if (!scheduled) return null;

        done.Wait();
        return result;
    }

    /// <summary>Parses a filter JSON array such as ["kat"] into extensions; a bare string is accepted.</summary>
    public static string[] ParseFilterExtensions(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();

        string text = json.Trim();
        try
        {
            if (text.StartsWith('['))
            {
                var array = JsonSerializer.Deserialize<string[]>(text);
                return CleanExtensions(array);
            }
            if (text.StartsWith('"'))
            {
                var single = JsonSerializer.Deserialize<string>(text);
                return CleanExtensions(string.IsNullOrEmpty(single) ? null : new[] { single });
            }
        }
        catch (JsonException)
        {
            // Falls through to the fallback below.
        }

        // Non-JSON input is treated as a single bare extension.
        return text.StartsWith('{') ? Array.Empty<string>() : CleanExtensions(new[] { text });
    }

    // ------------------------------------------------------------------ STA thread

    private void Pump()
    {
        try { OleInitialize(IntPtr.Zero); } catch { }

        try
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception ex) { Console.Error.WriteLine($"[dialog] dialog task failed: {ex.Message}"); }
            }
        }
        finally
        {
            try { OleUninitialize(); } catch { }
        }
    }

    private bool Schedule(Action action)
    {
        if (_disposed) return false;
        try
        {
            _queue.Add(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ implementations

    private string? AskSave(string suggestedName, string extension)
    {
        string ext = NormalizeExtension(extension);
        string name = EnsureExtension(NormalizeFileName(suggestedName, "untitled"), ext);

        string dir = DefaultDirectory();
        string initial = System.IO.Path.Combine(dir, name);

        int capacity = Math.Max(PathCapacity, initial.Length + 2);
        var arena = new DialogArena();
        try
        {
            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = OwnerWindow(),
                hInstance = IntPtr.Zero,
                lpstrFilter = arena.Alloc(BuildFilter(ext.Length > 0 ? new[] { ext } : Array.Empty<string>())),
                lpstrCustomFilter = IntPtr.Zero,
                nMaxCustFilter = 0,
                nFilterIndex = 1,
                lpstrFile = arena.AllocBuffer(initial, capacity),
                nMaxFile = capacity,
                lpstrFileTitle = IntPtr.Zero,
                nMaxFileTitle = 0,
                lpstrInitialDir = arena.Alloc(dir),
                lpstrTitle = arena.Alloc("Save File"),
                Flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST,
                nFileOffset = 0,
                nFileExtension = 0,
                lpstrDefExt = ext.Length > 0 ? arena.Alloc(ext) : IntPtr.Zero,
                lCustData = IntPtr.Zero,
                lpfnHook = IntPtr.Zero,
                lpTemplateName = IntPtr.Zero,
                pvReserved = IntPtr.Zero,
                dwReserved = 0,
                FlagsEx = 0,
            };

            if (!TryShowSave(ref ofn))
            {
                return null;
            }
            return ReadSingleString(ofn.lpstrFile);
        }
        finally
        {
            arena.Free();
        }
    }

    private string[]? AskOpen(string[] extensions, bool multiple)
    {
        int capacity = multiple ? MultiSelectCapacity : PathCapacity;
        int flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST;
        if (multiple) flags |= OFN_ALLOWMULTISELECT;

        var arena = new DialogArena();
        try
        {
            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = OwnerWindow(),
                hInstance = IntPtr.Zero,
                lpstrFilter = arena.Alloc(BuildFilter(extensions)),
                lpstrCustomFilter = IntPtr.Zero,
                nMaxCustFilter = 0,
                nFilterIndex = 1,
                lpstrFile = arena.AllocBuffer("", capacity),
                nMaxFile = capacity,
                lpstrFileTitle = IntPtr.Zero,
                nMaxFileTitle = 0,
                lpstrInitialDir = arena.Alloc(DefaultDirectory()),
                lpstrTitle = arena.Alloc(multiple ? "Select files to import (multiple)" : "Select file to import"),
                Flags = flags,
                nFileOffset = 0,
                nFileExtension = 0,
                lpstrDefExt = IntPtr.Zero,
                lCustData = IntPtr.Zero,
                lpfnHook = IntPtr.Zero,
                lpTemplateName = IntPtr.Zero,
                pvReserved = IntPtr.Zero,
                dwReserved = 0,
                FlagsEx = 0,
            };

            if (!TryShowOpen(ref ofn))
            {
                return null;
            }

            // Multi-select buffer; PtrToStringUni stops at the first '\0'.
            string raw = ReadMultiString(ofn.lpstrFile, capacity);
            if (!multiple) return new[] { raw };
            return ParseMultiSelection(raw);
        }
        finally
        {
            arena.Free();
        }
    }

    private string? AskFolder(string? initial, string title)
    {
        IntPtr root = IntPtr.Zero;
        string startDir = Directory.Exists(initial) ? initial! : DefaultDirectory();
        if (SHParseDisplayName(startDir, IntPtr.Zero, out IntPtr parsed, 0, out _) == 0)
            root = parsed;

        IntPtr displayName = Marshal.AllocHGlobal(PathCapacity * sizeof(char));
        try
        {
            var bi = new BROWSEINFOW
            {
                hwndOwner = OwnerWindow(),
                pidlRoot = root,
                pszDisplayName = displayName,
                lpszTitle = title,
                ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX,
                lpfn = IntPtr.Zero,
                lParam = IntPtr.Zero,
                iImage = 0,
            };

            IntPtr pidl = SHBrowseForFolderW(ref bi);
            if (pidl == IntPtr.Zero) return null;

            try
            {
                var path = new StringBuilder(PathCapacity);
                return SHGetPathFromIDListW(pidl, path) ? path.ToString() : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(displayName);
            if (root != IntPtr.Zero) Marshal.FreeCoTaskMem(root);
        }
    }

    // ------------------------------------------------------------------ result handling

    private static string[] ParseMultiSelection(string raw)
    {
        var items = new List<string>();
        foreach (string part in raw.Split('\0'))
            if (part.Length > 0) items.Add(part);

        if (items.Count == 0) return Array.Empty<string>();
        if (items.Count == 1) return new[] { items[0] };

        // First segment is the directory; later segments are file names.
        string dir = items[0];
        var result = new List<string>(items.Count - 1);
        for (int i = 1; i < items.Count; i++)
        {
            string item = items[i];
            result.Add(System.IO.Path.IsPathRooted(item) ? item : System.IO.Path.Combine(dir, item));
        }
        return result.ToArray();
    }

    private void Remember(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        if (Directory.Exists(folder)) _storage.Set("dialog.lastdir", folder);
    }

    private string DefaultDirectory()
    {
        string last = _storage.Get("dialog.lastdir");
        if (last.Length > 0 && Directory.Exists(last)) return last;

        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (docs.Length > 0 && Directory.Exists(docs)) return docs;

        return AppContext.BaseDirectory;
    }

    // ------------------------------------------------------------------ filter strings / file names

    private static string[] CleanExtensions(string[]? raw)
    {
        if (raw is null) return Array.Empty<string>();
        var list = new List<string>();
        foreach (string item in raw)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            string ext = NormalizeExtension(item);
            if (ext.Length > 0 && !list.Contains(ext, StringComparer.OrdinalIgnoreCase)) list.Add(ext);
        }
        return list.ToArray();
    }

    private static string NormalizeExtension(string? extension) =>
        string.IsNullOrWhiteSpace(extension) ? "" : extension.Trim().TrimStart('.').ToLowerInvariant();

    private static string NormalizeFileName(string? suggested, string fallback)
    {
        string name = string.IsNullOrWhiteSpace(suggested) ? fallback : suggested.Trim();

        // A full path is reduced to its file name.
        name = System.IO.Path.GetFileName(name.Replace('/', '\\'));

        foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return name.Length == 0 ? fallback : name;
    }

    private static string EnsureExtension(string name, string extension)
    {
        if (extension.Length == 0) return name;
        return name.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase)
            ? name
            : name + "." + extension;
    }

    private static string BuildFilter(string[] extensions)
    {
        var sb = new StringBuilder();

        var exts = extensions.Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (exts.Length > 0)
        {
            string patterns = string.Join(";", exts.Select(e => "*." + e));
            string label = string.Join("/", exts.Select(e => e.ToUpperInvariant())) + " files";
            sb.Append(label).Append(" (").Append(patterns).Append(')').Append('\0');
            sb.Append(patterns).Append('\0');
        }

        // lpstrFilter entries are '\0'-separated; the string ends with a double '\0'.
        sb.Append("All files (*.*)").Append('\0').Append("*.*").Append('\0').Append('\0');
        return sb.ToString();
    }

    private static void LogDialogError(string what, uint code, IntPtr owner)
    {
        // code == 0 means the user cancelled; not an error.
        if (code == 0) return;

        string meaning = code switch
        {
            0xFFFF => "CDERR_DIALOGFAILURE (dialog could not be created; commonly an invalid owner handle or insufficient resources)",
            0x3002 => "CDERR_INITIALIZATION (initialization failed, usually out of memory)",
            0x3003 => "CDERR_NOHINSTANCE (missing hInstance)",
            0x3004 => "CDERR_LOADRESFAILURE (failed to load dialog resources)",
            0x3005 => "CDERR_LOCKRESFAILURE (failed to lock dialog resources)",
            0x3006 => "CDERR_LOADSTRFAILURE (failed to load strings)",
            _ => "unknown code",
        };

        Console.Error.WriteLine(
            $"[dialog] {what} dialog failed, CommDlgExtendedError=0x{code:X} {meaning}; " +
            $"owner=0x{owner.ToInt64():X}(IsWindow={IsWindow(owner)}), " +
            $"foreground=0x{GetForegroundWindow().ToInt64():X}");
    }

    // ------------------------------------------------------------------ P/Invoke

    /// <summary>OPENFILENAMEW; string fields are IntPtr; SizeOf is 152 bytes on x64.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        public IntPtr lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    /// <summary>Owns the unmanaged allocations of one dialog call.</summary>
    private sealed class DialogArena
    {
        private readonly List<IntPtr> _blocks = new();

        /// <summary>Allocates a NUL-terminated UTF-16 string; embedded '\0' is preserved.</summary>
        public IntPtr Alloc(string? value)
        {
            if (value is null) return IntPtr.Zero;
            IntPtr p = Marshal.StringToHGlobalUni(value);
            _blocks.Add(p);
            return p;
        }

        /// <summary>Allocates a zeroed buffer and copies initial into it.</summary>
        public IntPtr AllocBuffer(string? initial, int charCapacity)
        {
            IntPtr p = Marshal.AllocHGlobal(charCapacity * sizeof(char));
            _blocks.Add(p);

            for (int i = 0; i < charCapacity; i++) Marshal.WriteInt16(p, i * sizeof(char), 0);

            if (!string.IsNullOrEmpty(initial))
            {
                char[] chars = initial.ToCharArray();
                int n = Math.Min(chars.Length, charCapacity - 1);
                if (n > 0) Marshal.Copy(chars, 0, p, n);
            }
            return p;
        }

        public void Free()
        {
            foreach (IntPtr p in _blocks)
            {
                try { Marshal.FreeHGlobal(p); } catch { }
            }
            _blocks.Clear();
        }
    }

    /// <summary>Reads a NUL-terminated string.</summary>
    private static string ReadSingleString(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        return Marshal.PtrToStringUni(p) ?? "";
    }

    /// <summary>Reads a multi-select result until two consecutive NULs.</summary>
    private static string ReadMultiString(IntPtr p, int charCapacity)
    {
        if (p == IntPtr.Zero) return "";

        var sb = new StringBuilder(charCapacity);
        bool seenNul = false;

        for (int i = 0; i < charCapacity; i++)
        {
            char c = (char)Marshal.ReadInt16(p, i * sizeof(char));
            if (c == '\0')
            {
                if (seenNul) break;   // second consecutive NUL: end
                seenNul = true;
                sb.Append('\0');
            }
            else
            {
                seenNul = false;
                sb.Append(c);
            }
        }

        return sb.ToString().TrimEnd('\0');
    }

    // An owner window in another process fails with CDERR_DIALOGFAILURE (0xFFFF).
    /// <summary>Returns the foreground window when it belongs to this process, otherwise 0.</summary>
    private static IntPtr OwnerWindow()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsWindow(foreground)) return IntPtr.Zero;

        try
        {
            GetWindowThreadProcessId(foreground, out uint pid);
            return pid == (uint)Environment.ProcessId ? foreground : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Shows the open dialog, retrying once without an owner on failure.</summary>
    private static bool TryShowOpen(ref OPENFILENAME ofn)
    {
        if (GetOpenFileNameW(ref ofn)) return true;

        uint code = CommDlgExtendedError();
        if (code == 0) return false;            // user cancelled; not an error
        LogDialogError("open", code, ofn.hwndOwner);
        if (ofn.hwndOwner == IntPtr.Zero) return false;

        Console.Error.WriteLine("[dialog] retrying without owner");
        ofn.hwndOwner = IntPtr.Zero;
        if (GetOpenFileNameW(ref ofn)) return true;
        LogDialogError("open (retry)", CommDlgExtendedError(), IntPtr.Zero);
        return false;
    }

    private static bool TryShowSave(ref OPENFILENAME ofn)
    {
        if (GetSaveFileNameW(ref ofn)) return true;

        uint code = CommDlgExtendedError();
        if (code == 0) return false;
        LogDialogError("save", code, ofn.hwndOwner);
        if (ofn.hwndOwner == IntPtr.Zero) return false;

        Console.Error.WriteLine("[dialog] retrying without owner");
        ofn.hwndOwner = IntPtr.Zero;
        if (GetSaveFileNameW(ref ofn)) return true;
        LogDialogError("save (retry)", CommDlgExtendedError(), IntPtr.Zero);
        return false;
    }


    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFOW
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetOpenFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME openFileName);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetSaveFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAME saveFileName);

    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHBrowseForFolderW")]
    private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFOW browseInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetPathFromIDListW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder path);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _queue.CompleteAdding(); } catch { }
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { }
    }
}
