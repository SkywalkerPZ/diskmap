using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DiskMap.Core.Removal;

namespace DiskMap.Core.Quarantine;

/// <summary>
/// OriginalPath is null for an entry adopted from an untracked folder (unknown origin: not restorable).
/// KeepDays is the undo period promised when the item was removed (old manifests load as 7).
/// Partial: a folder that could only be moved in part (some files were in use); Restore merges it back.
/// </summary>
public sealed record QuarantineEntry(Guid Id, string? OriginalPath, long Size, DateTime RemovedUtc, string RuleId, int KeepDays = 7, bool Partial = false);

/// <summary>
/// Per-volume, per-account quarantine: &lt;volume&gt;\DiskMap Quarantine\&lt;SID of this process's account&gt;\&lt;id:N&gt;\&lt;original name&gt;,
/// moved by rename only (never copy+delete). Both folders are created with a protected DACL (the shared top folder lets
/// other accounts only create their own subfolder; the account folder grants SYSTEM, Administrators and the account only),
/// and both, plus every entry folder, are checked before each use: a link, or an owner or DACL DiskMap didn't set, makes
/// the store refuse to work (see Untrusted) instead of repairing anything. Deletion goes through SafeDelete (by handle).
/// Every mutation holds a cross-process lock and re-reads the manifest. The manifest entry is written before the move
/// (intent), and any entry folder missing from the manifest is adopted on load, so nothing inside is ever untracked.
/// </summary>
public sealed class QuarantineStore
{
    public const string FolderName = "DiskMap Quarantine";
    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _top;
    private readonly string _folder;
    private readonly string _manifest;
    private readonly SecurityIdentifier _me;
    private List<QuarantineEntry> _entries = new();
    private bool _needsBackup;

    /// <summary>The account folder belongs to the account this process runs as (for an elevated process: the administrator).</summary>
    public QuarantineStore(string volumeRoot)
    {
        _root = Path.GetFullPath(volumeRoot);
        _top = Path.Combine(_root, FolderName);
        _me = WindowsIdentity.GetCurrent().User!;
        _folder = Path.Combine(_top, _me.Value);
        _manifest = Path.Combine(_folder, "manifest.json");
        lock (_gate) Reload();
    }

    internal string Folder => _folder;

    public IReadOnlyList<QuarantineEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }
    public long TotalSize { get { lock (_gate) return _entries.Sum(e => e.Size); } }

    /// <summary>True if the manifest could not be parsed at the last load.</summary>
    public bool IsCorrupt { get; private set; }

    /// <summary>Why the quarantine folder is not used (a link, or permissions DiskMap didn't set); null when it is fine.</summary>
    public string? Untrusted { get; private set; }

    /// <summary>Where the unparseable manifest was preserved (set on the first save after it was found corrupt).</summary>
    public string? CorruptBackupPath { get; private set; }

    public QuarantineEntry Add(string path, long size, string ruleId, int keepDays = 7)
    {
        var (full, name, isDir) = Source(path);
        return Locked(true, () =>
        {
            var entry = new QuarantineEntry(Guid.NewGuid(), full, size, DateTime.UtcNow, ruleId, keepDays);
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

    /// <summary>
    /// Starts one entry for a folder that can only be moved in part. Its children then go in with MoveInto (under
    /// their path relative to the folder) and EndPartial records the size; Restore merges them back.
    /// </summary>
    public Guid BeginPartial(string folderPath, string ruleId, int keepDays)
    {
        var (full, name, isDir) = Source(folderPath);
        if (!isDir) throw new ArgumentException("Only a folder can be quarantined in part.", nameof(folderPath));
        return Locked(true, () =>
        {
            var entry = new QuarantineEntry(Guid.NewGuid(), full, 0, DateTime.UtcNow, ruleId, keepDays, true);
            Directory.CreateDirectory(Path.Combine(EntryDir(entry.Id), name));
            Save(new List<QuarantineEntry>(_entries) { entry });
            return entry.Id;
        });
    }

    /// <summary>Moves one item from inside a partial entry's folder into that entry. No manifest write.</summary>
    public void MoveInto(Guid id, string childPath)
    {
        var (full, _, isDir) = Source(childPath);
        Locked(false, () =>
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id && e.Partial) ?? throw new KeyNotFoundException($"No partial quarantine entry {id}.");
            var unit = entry.OriginalPath!;
            if (!full.StartsWith(unit + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Not inside the entry's folder.", nameof(childPath));
            var dest = Path.Combine(EntryDir(id), Path.GetFileName(unit), Path.GetRelativePath(unit, full));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            Move(full, dest, isDir);
            return 0;
        }, reload: false);
    }

    /// <summary>Records a partial entry's final size, or drops it when nothing was moved in.</summary>
    public void EndPartial(Guid id, long size) => Locked(false, () =>
    {
        if (_entries.FirstOrDefault(e => e.Id == id) is not { } entry) return 0;
        var inner = Path.Combine(EntryDir(id), Path.GetFileName(entry.OriginalPath!));
        if (!Directory.Exists(inner) || !Directory.EnumerateFileSystemEntries(inner).Any())
        {
            Save(_entries.Where(e => e.Id != id).ToList());
            TryDeleteEmpty(inner);
            TryDeleteEmpty(EntryDir(id));
        }
        else Save(_entries.Select(e => e.Id == id ? e with { Size = size } : e).ToList());
        return 0;
    });

    private (string Full, string Name, bool IsDir) Source(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(Path.GetPathRoot(full), Path.GetPathRoot(_root), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"'{full}' is on a different volume than the quarantine ({_root}).");
        var name = Path.GetFileName(full);
        if (name.Length == 0) throw new ArgumentException("Cannot quarantine a volume root.", nameof(path));
        if (string.Equals(full, _top, StringComparison.OrdinalIgnoreCase) || full.StartsWith(_top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path is already inside the quarantine.", nameof(path));
        return (full, name, (File.GetAttributes(full) & FileAttributes.Directory) != 0); // throws if missing
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
        string target;
        if (entry.Partial && isDir)
        {
            target = original; // the folder kept the files that were in use: put the rest back around them
            MergeBack(src, original);
        }
        else
        {
            target = FreeName(parent, name, isDir);
            Move(src, target, isDir);
        }
        Save(_entries.Where(e => e.Id != id).ToList());
        TryDeleteEmpty(entryDir);
        return target;
    });

    // Moves each item back; folders that exist on both sides are merged, anything else in the way gets a "(restored)" name.
    private static void MergeBack(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var child in new DirectoryInfo(from).EnumerateFileSystemInfos().ToList())
        {
            var isDir = (child.Attributes & FileAttributes.Directory) != 0;
            var realDir = isDir && (child.Attributes & FileAttributes.ReparsePoint) == 0;
            var dest = Path.Combine(to, child.Name);
            if (realDir && Exists(dest) && (File.GetAttributes(dest) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory)
                MergeBack(child.FullName, dest);
            else Move(child.FullName, FreeName(to, child.Name, isDir), isDir);
        }
        Directory.Delete(from, false);
    }

    private static string FreeName(string parent, string name, bool isDir)
    {
        var target = Path.Combine(parent, name);
        for (var n = 1; Exists(target); n++)
        {
            var suffix = n == 1 ? " (restored)" : $" (restored {n})";
            var ext = isDir ? "" : Path.GetExtension(name);
            target = Path.Combine(parent, name[..^ext.Length] + suffix + ext);
        }
        return target;
    }

    /// <summary>Removes the entries whose own keep period (KeepDays from RemovedUtc) has passed; returns how many were removed.</summary>
    public int PurgeExpired(DateTime nowUtc) =>
        Locked(false, () => Delete(_entries.Where(e => e.RemovedUtc.AddDays(e.KeepDays) <= nowUtc).ToList()));

    public void EmptyAll() => Locked(false, () => Delete(_entries.ToList()));

    private T Locked<T>(bool create, Func<T> body, bool reload = true)
    {
        lock (_gate)
        {
            if (create) CreateFolders();
            if (Check() is { } why) { Untrusted = why; _entries = new(); throw new IOException(why); }
            using var l = Exists(_folder) ? AcquireLock() : null; // null: no quarantine folder, nothing to mutate
            if (reload || l is null) Reload();
            if (Untrusted is { } u) throw new IOException(u);
            return body();
        }
    }

    // ---- folder security ----

    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier AuthUsersSid = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier OwnerRightsSid = new("S-1-3-4");

    // Everyone may only create their own account folder in the shared top folder (and read its attributes).
    private const FileSystemRights TopUserRights = FileSystemRights.Traverse | FileSystemRights.ReadAttributes |
        FileSystemRights.ReadPermissions | FileSystemRights.Synchronize | FileSystemRights.CreateDirectories;
    // OWNER RIGHTS replaces the owner's implicit WRITE_DAC, so whoever created the top folder can't open it up later.
    private const FileSystemRights OwnerRights = FileSystemRights.ReadPermissions | FileSystemRights.WriteAttributes | FileSystemRights.Synchronize;

    private enum Kind { Top, Account, Entry }

    private static DirectorySecurity TopSecurity()
    {
        var s = new DirectorySecurity();
        s.SetAccessRuleProtection(true, false);
        const InheritanceFlags All = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        s.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(AuthUsersSid, TopUserRights, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(OwnerRightsSid, OwnerRights, AccessControlType.Allow));
        return s;
    }

    private DirectorySecurity AccountSecurity()
    {
        var s = new DirectorySecurity();
        s.SetAccessRuleProtection(true, false);
        const InheritanceFlags All = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { SystemSid, AdminsSid, _me })
            s.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
        return s;
    }

    // Created with their DACL in one step (no window with inherited permissions). An existing folder is never changed.
    private void CreateFolders()
    {
        if (!Exists(_top))
        {
            Create(TopSecurity(), _top);
            try { File.SetAttributes(_top, File.GetAttributes(_top) | FileAttributes.Hidden); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (CheckDir(_top, Kind.Top) is not null) return; // never create anything inside a folder we don't trust
        if (!Exists(_folder)) Create(AccountSecurity(), _folder);
    }

    private static void Create(DirectorySecurity sec, string path)
    {
        try { sec.CreateDirectory(path); }
        catch (IOException) when (Exists(path)) { } // someone else made it first; it is checked next
    }

    /// <summary>Null when the existing quarantine folders are safe to use (or don't exist yet), else why not.</summary>
    private string? Check()
    {
        if (!Exists(_top)) return null;
        if (CheckDir(_top, Kind.Top) is { } t) return Refusal(_top, t);
        if (!Exists(_folder)) return null;
        return CheckDir(_folder, Kind.Account) is { } a ? Refusal(_folder, a) : null;
    }

    private static string Refusal(string path, string why) =>
        $"DiskMap won't use the quarantine folder '{path}' because {why}. Nothing in it was changed; an administrator can delete it so DiskMap creates a new one.";

    private string? CheckDir(string path, Kind kind)
    {
        try
        {
            var a = File.GetAttributes(path);
            if ((a & FileAttributes.ReparsePoint) != 0) return "it is a link (junction or symbolic link)";
            if ((a & FileAttributes.Directory) == 0) return "it is not a folder";
            var sec = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (kind != Kind.Entry && !sec.AreAccessRulesProtected) return "its permissions were not set by DiskMap";
            var ownerLimited = false;
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (r.AccessControlType == AccessControlType.Deny) continue;
                var sid = (SecurityIdentifier)r.IdentityReference;
                if (sid == SystemSid || sid == AdminsSid) continue;
                if (kind == Kind.Top)
                {
                    var thisFolderOnly = r.InheritanceFlags == InheritanceFlags.None;
                    if (sid == AuthUsersSid && thisFolderOnly && (r.FileSystemRights & ~TopUserRights) == 0) continue;
                    if (sid == OwnerRightsSid && thisFolderOnly && (r.FileSystemRights & ~OwnerRights) == 0) { ownerLimited = true; continue; }
                }
                else if (sid == _me) continue;
                return "it gives other accounts access";
            }
            var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            var trustedOwner = owner == SystemSid || owner == AdminsSid || (kind != Kind.Top && owner == _me);
            if (!trustedOwner && !(kind == Kind.Top && ownerLimited)) return "it is owned by another account";
            return null;
        }
        catch (Exception e) // fails closed: anything we can't verify is not used
        {
            return "its permissions couldn't be checked (" + e.Message + ")";
        }
    }

    // ---- disk ----

    private FileStream AcquireLock()
    {
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
            try { SafeDelete.DeleteTree(EntryDir(v.Id)); gone.Add(v.Id); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { gone.Add(v.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (gone.Count > 0) Save(_entries.Where(e => !gone.Contains(e.Id)).ToList());
        return gone.Count;
    }

    private string EntryDir(Guid id) => Path.Combine(_folder, id.ToString("N"));

    private static bool Exists(string p)
    {
        try { File.GetAttributes(p); return true; } catch { return false; }
    }

    /// <summary>True/false when the folder is known to exist or not; null when it couldn't be checked (an I/O error).</summary>
    internal static bool? Probe(string p)
    {
        try { return (File.GetAttributes(p) & FileAttributes.Directory) != 0; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void TryDeleteEmpty(string dir)
    {
        try { Directory.Delete(dir, false); } catch { }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string from, string to, uint flags);

    // Win32 extended-length prefix: two backslashes + "?" + backslash, built from chars to keep the literal unambiguous.
    private static readonly string Ext = new string((char)92, 2) + "?" + (char)92;

    // Rename only. Files use MoveFileExW without MOVEFILE_COPY_ALLOWED, so a move across a mount point fails
    // instead of silently copying and deleting. Directory.Move is already rename-only.
    private static void Move(string from, string to, bool isDir)
    {
        if (isDir) { Directory.Move(from, to); return; }
        string Long(string p) => p.Length > 2 && p[1] == ':' ? Ext + p : p;
        if (!MoveFileEx(Long(from), Long(to), 0))
        {
            var err = new Win32Exception();
            throw new IOException($"Could not move '{from}' to '{to}': {err.Message}", err);
        }
    }

    // Checks the folders, reads the manifest, drops entries whose folder is gone, and adopts entry folders the manifest doesn't know.
    private void Reload()
    {
        IsCorrupt = false;
        _entries = new();
        if ((Untrusted = Check()) is not null || !Exists(_folder)) return;
        var list = new List<QuarantineEntry>();
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
        var dirs = new DirectoryInfo(_folder).EnumerateDirectories().ToList();
        foreach (var d in dirs)
            if (CheckDir(d.FullName, Kind.Entry) is { } why) { Untrusted = Refusal(d.FullName, why); return; }
        list = list.Where(e => Probe(EntryDir(e.Id)) != false).ToList(); // an entry we couldn't check keeps its origin
        var known = list.Select(e => e.Id).ToHashSet();
        foreach (var d in dirs)
        {
            if (!Guid.TryParseExact(d.Name, "N", out var id) || known.Contains(id)) continue;
            list.Add(new QuarantineEntry(id, null, Measure(d.FullName), d.CreationTimeUtc, "unknown"));
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
