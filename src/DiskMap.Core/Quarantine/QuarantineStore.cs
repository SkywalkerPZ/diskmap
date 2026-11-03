using System.Text.Json;

namespace DiskMap.Core.Quarantine;

public sealed record QuarantineEntry(Guid Id, string OriginalPath, long Size, DateTime RemovedUtc, string RuleId);

/// <summary>
/// Per-volume hidden quarantine folder: each entry lives at &lt;quarantine&gt;\&lt;id:N&gt;\&lt;original name&gt;,
/// moved by rename only (never copy+delete). The manifest is only ever written after the disk change,
/// and entries are only dropped from it after their folders are gone, so files are never untracked.
/// </summary>
public sealed class QuarantineStore
{
    private const string FolderName = "DiskMap Quarantine";
    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _folder;
    private readonly string _manifest;
    private List<QuarantineEntry> _entries = new();
    private bool _corrupt;

    public QuarantineStore(string volumeRoot)
    {
        _root = Path.GetFullPath(volumeRoot);
        _folder = Path.Combine(_root, FolderName);
        _manifest = Path.Combine(_folder, "manifest.json");
        Load();
    }

    public IReadOnlyList<QuarantineEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }
    public long TotalSize { get { lock (_gate) return _entries.Sum(e => e.Size); } }

    public QuarantineEntry Add(string path, long size, string ruleId)
    {
        lock (_gate)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!string.Equals(Path.GetPathRoot(full), Path.GetPathRoot(_root), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"'{full}' is on a different volume than the quarantine ({_root}).");
            var name = Path.GetFileName(full);
            if (name.Length == 0) throw new ArgumentException("Cannot quarantine a volume root.", nameof(path));
            if (full.StartsWith(_folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Path is already inside the quarantine.", nameof(path));
            var isDir = (File.GetAttributes(full) & FileAttributes.Directory) != 0; // throws if missing

            var entry = new QuarantineEntry(Guid.NewGuid(), full, size, DateTime.UtcNow, ruleId);
            var entryDir = EntryDir(entry.Id);
            var dest = Path.Combine(entryDir, name);
            EnsureFolder();
            Directory.CreateDirectory(entryDir);
            try
            {
                if (isDir) Directory.Move(full, dest); else File.Move(full, dest);
            }
            catch { TryDeleteEmpty(entryDir); throw; }
            try
            {
                var next = new List<QuarantineEntry>(_entries) { entry };
                Save(next);
            }
            catch
            {
                // manifest not written: put the item back so nothing is left untracked
                try { if (isDir) Directory.Move(dest, full); else File.Move(dest, full); TryDeleteEmpty(entryDir); } catch { }
                throw;
            }
            return entry;
        }
    }

    public string Restore(Guid id)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException($"No quarantine entry {id}.");
            var entryDir = EntryDir(id);
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(entry.OriginalPath));
            var src = Path.Combine(entryDir, name);
            if (!Exists(src)) throw new FileNotFoundException("Quarantined item is missing.", src);
            var isDir = (File.GetAttributes(src) & FileAttributes.Directory) != 0;

            var parent = Path.GetDirectoryName(entry.OriginalPath)!;
            Directory.CreateDirectory(parent);
            var target = entry.OriginalPath;
            for (var n = 1; Exists(target); n++)
            {
                var suffix = n == 1 ? " (restored)" : $" (restored {n})";
                var ext = isDir ? "" : Path.GetExtension(name);
                target = Path.Combine(parent, name[..^ext.Length] + suffix + ext);
            }
            if (isDir) Directory.Move(src, target); else File.Move(src, target);
            Save(_entries.Where(e => e.Id != id).ToList());
            TryDeleteEmpty(entryDir);
            return target;
        }
    }

    /// <summary>Removes entries with RemovedUtc at or before nowUtc minus keepDays; returns how many were removed.</summary>
    public int PurgeExpired(int keepDays, DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-keepDays);
        lock (_gate) return Delete(_entries.Where(e => e.RemovedUtc <= cutoff).ToList());
    }

    public void EmptyAll()
    {
        lock (_gate) Delete(_entries.ToList());
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

    private void EnsureFolder()
    {
        if (Directory.Exists(_folder)) return;
        Directory.CreateDirectory(_folder);
        File.SetAttributes(_folder, File.GetAttributes(_folder) | FileAttributes.Hidden);
    }

    private void Load()
    {
        if (!File.Exists(_manifest)) return;
        try
        {
            _entries = JsonSerializer.Deserialize<List<QuarantineEntry>>(File.ReadAllText(_manifest)) ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _entries = new(); // entry folders are never touched on a bad manifest
            _corrupt = true;
        }
    }

    private void Save(List<QuarantineEntry> next)
    {
        if (_corrupt)
        {
            // keep the unreadable manifest for manual recovery instead of overwriting it
            try { File.Copy(_manifest, Path.Combine(_folder, $"manifest.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json")); } catch { }
            _corrupt = false;
        }
        var tmp = Path.Combine(_folder, "manifest.tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(next));
        if (File.Exists(_manifest)) File.Replace(tmp, _manifest, null);
        else File.Move(tmp, _manifest);
        _entries = next;
    }
}
