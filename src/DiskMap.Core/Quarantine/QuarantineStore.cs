using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DiskMap.Core.Quarantine;

/// <summary>OriginalPath is null for an entry adopted from an untracked folder (unknown origin: not restorable).</summary>
public sealed record QuarantineEntry(Guid Id, string? OriginalPath, long Size, DateTime RemovedUtc, string RuleId);

/// <summary>
/// Per-volume hidden quarantine folder: each entry lives at &lt;quarantine&gt;\&lt;id:N&gt;\&lt;original name&gt;,
/// moved by rename only (never copy+delete). Every mutation holds a cross-process lock and re-reads the manifest.
/// The manifest entry is written before the move (intent), and any entry folder missing from the manifest is
/// adopted on load, so nothing inside the quarantine is ever untracked.
/// </summary>
public sealed class QuarantineStore
{
    private const string FolderName = "DiskMap Quarantine";
    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _folder;
    private readonly string _manifest;
    private List<QuarantineEntry> _entries = new();
    private bool _needsBackup;

    public QuarantineStore(string volumeRoot)
    {
        _root = Path.GetFullPath(volumeRoot);
        _folder = Path.Combine(_root, FolderName);
        _manifest = Path.Combine(_folder, "manifest.json");
        lock (_gate) Reload();
    }

    public IReadOnlyList<QuarantineEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }
    public long TotalSize { get { lock (_gate) return _entries.Sum(e => e.Size); } }

    /// <summary>True if the manifest could not be parsed at the last load.</summary>
    public bool IsCorrupt { get; private set; }

    /// <summary>Where the unparseable manifest was preserved (set on the first save after it was found corrupt).</summary>
    public string? CorruptBackupPath { get; private set; }

    public QuarantineEntry Add(string path, long size, string ruleId)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(Path.GetPathRoot(full), Path.GetPathRoot(_root), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"'{full}' is on a different volume than the quarantine ({_root}).");
        var name = Path.GetFileName(full);
        if (name.Length == 0) throw new ArgumentException("Cannot quarantine a volume root.", nameof(path));
        if (full.StartsWith(_folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path is already inside the quarantine.", nameof(path));
        var isDir = (File.GetAttributes(full) & FileAttributes.Directory) != 0; // throws if missing

        return Locked(true, () =>
        {
            var entry = new QuarantineEntry(Guid.NewGuid(), full, size, DateTime.UtcNow, ruleId);
            var entryDir = EntryDir(entry.Id);
            Directory.CreateDirectory(entryDir);
            Save(new List<QuarantineEntry>(_entries) { entry }); // intent first: tracked before anything moves
            try
            {
                Move(full, Path.Combine(entryDir, name), isDir);
            }
            catch (Exception ex)
            {
                try { Save(_entries.Where(e => e.Id != entry.Id).ToList()); TryDeleteEmpty(entryDir); }
                catch (Exception rb)
                {
                    throw new IOException($"Move failed and the manifest could not be rolled back; the entry stays tracked at '{entryDir}'.",
                        new AggregateException(ex, rb));
                }
                throw;
            }
            return entry;
        });
    }

    public string Restore(Guid id) => Locked(false, () =>
    {
        var entry = _entries.FirstOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException($"No quarantine entry {id}.");
        if (entry.OriginalPath is not { } original)
            throw new InvalidOperationException("This entry has an unknown origin and cannot be restored automatically.");
        var entryDir = EntryDir(id);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(original));
        var src = Path.Combine(entryDir, name);
        if (!Exists(src)) throw new FileNotFoundException("Quarantined item is missing.", src);
        var isDir = (File.GetAttributes(src) & FileAttributes.Directory) != 0;

        var parent = Path.GetDirectoryName(original)!;
        Directory.CreateDirectory(parent);
        var target = original;
        for (var n = 1; Exists(target); n++)
        {
            var suffix = n == 1 ? " (restored)" : $" (restored {n})";
            var ext = isDir ? "" : Path.GetExtension(name);
            target = Path.Combine(parent, name[..^ext.Length] + suffix + ext);
        }
        Move(src, target, isDir);
        Save(_entries.Where(e => e.Id != id).ToList());
        TryDeleteEmpty(entryDir);
        return target;
    });

    /// <summary>Removes entries with RemovedUtc at or before nowUtc minus keepDays; returns how many were removed.</summary>
    public int PurgeExpired(int keepDays, DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-keepDays);
        return Locked(false, () => Delete(_entries.Where(e => e.RemovedUtc <= cutoff).ToList()));
    }

    public void EmptyAll() => Locked(false, () => Delete(_entries.ToList()));

    private T Locked<T>(bool create, Func<T> body)
    {
        lock (_gate)
        {
            using var l = AcquireLock(create);
            Reload();
            return body();
        }
    }

    // Cross-process lock; null when there is no quarantine folder (nothing to mutate).
    private FileStream? AcquireLock(bool create)
    {
        if (!Directory.Exists(_folder))
        {
            if (!create) return null;
            Directory.CreateDirectory(_folder);
            File.SetAttributes(_folder, File.GetAttributes(_folder) | FileAttributes.Hidden);
        }
        var path = Path.Combine(_folder, ".lock");
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline) throw new IOException("Quarantine is locked by another process.", ex);
                Thread.Sleep(50);
            }
        }
    }

    // Disk first, then manifest. An entry whose folder cannot be deleted stays in the manifest.
    private int Delete(List<QuarantineEntry> victims)
    {
        var gone = new HashSet<Guid>();
        foreach (var v in victims)
        {
            try { DeleteTree(EntryDir(v.Id)); gone.Add(v.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (gone.Count > 0) Save(_entries.Where(e => !gone.Contains(e.Id)).ToList());
        return gone.Count;
    }

    // Reparse points are removed as links (never followed); a failure leaves the rest in place.
    private static void DeleteTree(string path)
    {
        if (!Exists(path)) return;
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        if ((attrs & FileAttributes.Directory) == 0) { File.Delete(path); return; }
        if ((attrs & FileAttributes.ReparsePoint) == 0)
            foreach (var child in Directory.EnumerateFileSystemEntries(path).ToList()) DeleteTree(child);
        Directory.Delete(path, false);
    }

    private string EntryDir(Guid id) => Path.Combine(_folder, id.ToString("N"));

    private static bool Exists(string p)
    {
        try { File.GetAttributes(p); return true; } catch { return false; }
    }

    private static void TryDeleteEmpty(string dir)
    {
        try { Directory.Delete(dir, false); } catch { }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string from, string to, uint flags);

    // Rename only. Files use MoveFileExW without MOVEFILE_COPY_ALLOWED, so a move across a mount point fails
    // instead of silently copying and deleting. Directory.Move is already rename-only.
    private static void Move(string from, string to, bool isDir)
    {
        if (isDir) { Directory.Move(from, to); return; }
        const char B = '\\';
        string Long(string p) => p.Length > 2 && p[1] == ':' ? "" + B + B + "?" + B + p : p;
        if (!MoveFileEx(Long(from), Long(to), 0))
        {
            var err = new Win32Exception();
            throw new IOException($"Could not move '{from}' to '{to}': {err.Message}", err);
        }
    }

    // Reads the manifest, drops entries whose folder is gone, and adopts entry folders the manifest doesn't know.
    private void Reload()
    {
        var list = new List<QuarantineEntry>();
        IsCorrupt = false;
        if (File.Exists(_manifest))
        {
            var text = ReadManifestText();
            try
            {
                list = JsonSerializer.Deserialize<List<QuarantineEntry>>(text) ?? throw new JsonException();
                if (list.Any(e => e is null)) throw new JsonException();
            }
            catch (JsonException)
            {
                list = new();
                IsCorrupt = true;
                _needsBackup = true;
            }
        }
        if (!Directory.Exists(_folder)) { _entries = new(); return; }
        list = list.Where(e => Directory.Exists(EntryDir(e.Id))).ToList();
        var known = list.Select(e => e.Id).ToHashSet();
        foreach (var dir in Directory.EnumerateDirectories(_folder))
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out var id) || known.Contains(id)) continue;
            list.Add(new QuarantineEntry(id, null, Measure(dir), Directory.GetCreationTimeUtc(dir), "unknown"));
        }
        _entries = list;
    }

    private string ReadManifestText()
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(_manifest); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 5) throw; // a transient read error is not "corrupt"
                Thread.Sleep(100);
            }
        }
    }

    private static long Measure(string dir)
    {
        long total = 0;
        try
        {
            var opt = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opt)) total += f.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    private void Save(List<QuarantineEntry> next)
    {
        if (_needsBackup)
        {
            // keep the unreadable manifest for manual recovery; if that fails we must not overwrite it
            var backup = Path.Combine(_folder, $"manifest.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
            File.Copy(_manifest, backup, true);
            CorruptBackupPath = backup;
            _needsBackup = false;
        }
        var tmp = Path.Combine(_folder, "manifest.tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(next));
        if (File.Exists(_manifest)) File.Replace(tmp, _manifest, null);
        else File.Move(tmp, _manifest);
        _entries = next;
    }
}
