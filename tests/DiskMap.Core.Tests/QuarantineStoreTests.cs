using System.Text.Json;
using DiskMap.Core.Quarantine;

namespace DiskMap.Core.Tests;

public class QuarantineStoreTests
{
    private static string Q(Fixture f) => Path.Combine(f.Root, "DiskMap Quarantine");
    private static string EntryDir(Fixture f, Guid id) => Path.Combine(Q(f), id.ToString("N"));

    [Fact]
    public void Add_MovesByRename_AndRecordsEntry()
    {
        using var f = new Fixture();
        var src = f.File(Path.Combine("proj", "node_modules", "a.bin"), 100);
        var dir = Path.GetDirectoryName(src)!;
        var store = new QuarantineStore(f.Root);
        Assert.Empty(store.Entries);
        Assert.False(Directory.Exists(Q(f)));

        var e = store.Add(dir, 100, "r1");

        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(EntryDir(f, e.Id), "node_modules", "a.bin")));
        Assert.Equal(100, store.TotalSize);
        Assert.Equal(dir, e.OriginalPath);
        Assert.Single(store.Entries);
        Assert.True((new DirectoryInfo(Q(f)).Attributes & FileAttributes.Hidden) != 0);
    }

    [Fact]
    public void Restore_PutsItemBackExactly()
    {
        using var f = new Fixture();
        var src = f.File(Path.Combine("d", "setup.exe"), 10);
        var store = new QuarantineStore(f.Root);
        var e = store.Add(src, 10, "r");
        Assert.False(File.Exists(src));
        Assert.Equal(src, store.Restore(e.Id));
        Assert.True(File.Exists(src));
        Assert.Empty(store.Entries);
        Assert.False(Directory.Exists(EntryDir(f, e.Id)));
    }

    [Fact]
    public void Restore_RecreatesMissingParents()
    {
        using var f = new Fixture();
        var src = f.File(Path.Combine("a", "b", "x.txt"), 1);
        var store = new QuarantineStore(f.Root);
        var e = store.Add(src, 1, "r");
        Directory.Delete(Path.Combine(f.Root, "a"), true);
        Assert.Equal(src, store.Restore(e.Id));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void Restore_WhenTargetExists_UsesRestoredSuffix()
    {
        using var f = new Fixture();
        var store = new QuarantineStore(f.Root);
        var dir = f.Dir("node_modules");
        var e1 = store.Add(dir, 0, "r");
        f.Dir("node_modules");
        Assert.Equal(dir + " (restored)", store.Restore(e1.Id));
        var e2 = store.Add(dir, 0, "r");
        f.Dir("node_modules");
        Assert.Equal(dir + " (restored 2)", store.Restore(e2.Id));
    }

    [Fact]
    public void Restore_File_InsertsSuffixBeforeExtension()
    {
        using var f = new Fixture();
        var store = new QuarantineStore(f.Root);
        var p = f.File("setup.exe", 1);
        var e = store.Add(p, 1, "r");
        f.File("setup.exe", 1);
        Assert.Equal(Path.Combine(f.Root, "setup (restored).exe"), store.Restore(e.Id));
    }

    [Fact]
    public void PurgeExpired_DeletesOnlyOlderThanKeepDays()
    {
        using var f = new Fixture();
        var store = new QuarantineStore(f.Root);
        var now = DateTime.UtcNow;
        var a = store.Add(f.File("a.bin", 5), 5, "r");
        var b = store.Add(f.File("b.bin", 5), 5, "r");
        Backdate(f, a.Id, now.AddDays(-4));
        Backdate(f, b.Id, now.AddDays(-2));
        store = new QuarantineStore(f.Root);

        Assert.Equal(1, store.PurgeExpired(3, now));
        var left = Assert.Single(store.Entries);
        Assert.Equal(b.Id, left.Id);
        Assert.False(Directory.Exists(EntryDir(f, a.Id)));
        Assert.True(Directory.Exists(EntryDir(f, b.Id)));
        Assert.Equal(1, store.PurgeExpired(0, now));
        Assert.Empty(store.Entries);
    }

    // Rewrites RemovedUtc in the manifest so tests can age entries.
    private static void Backdate(Fixture f, Guid id, DateTime when)
    {
        var path = Path.Combine(Q(f), "manifest.json");
        var list = JsonSerializer.Deserialize<List<QuarantineEntry>>(File.ReadAllText(path))!;
        var i = list.FindIndex(x => x.Id == id);
        list[i] = list[i] with { RemovedUtc = when };
        File.WriteAllText(path, JsonSerializer.Serialize(list));
    }

    [Fact]
    public void Manifest_SurvivesNewInstance()
    {
        using var f = new Fixture();
        var e = new QuarantineStore(f.Root).Add(f.File("a.bin", 7), 7, "rule");
        var s = new QuarantineStore(f.Root);
        Assert.Equal(e, Assert.Single(s.Entries));
        Assert.Equal(7, s.TotalSize);
    }

    [Fact]
    public void CorruptManifest_LoadsEmpty_AndNeverDeletesEntryFolders()
    {
        using var f = new Fixture();
        var e = new QuarantineStore(f.Root).Add(f.File("a.bin", 7), 7, "rule");
        File.WriteAllText(Path.Combine(Q(f), "manifest.json"), "{ not json");
        var s = new QuarantineStore(f.Root);
        Assert.Empty(s.Entries);
        Assert.True(Directory.Exists(EntryDir(f, e.Id)));
        s.PurgeExpired(0, DateTime.UtcNow);
        s.EmptyAll();
        Assert.True(Directory.Exists(EntryDir(f, e.Id)));
    }

    [Fact]
    public void Add_HandlesLongPaths()
    {
        using var f = new Fixture();
        var rel = Path.Combine(string.Concat(Enumerable.Range(0, 6).Select(i => new string('d', 50) + i + Path.DirectorySeparatorChar)), "file.bin");
        var src = f.File(rel, 3);
        Assert.True(src.Length > 300);
        var store = new QuarantineStore(f.Root);
        var e = store.Add(src, 3, "r");
        Assert.False(File.Exists(src));
        Assert.Equal(src, store.Restore(e.Id));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void Add_DifferentVolume_ThrowsIOException_AndChangesNothing()
    {
        using var f = new Fixture();
        var store = new QuarantineStore(f.Root);
        var other = (Path.GetPathRoot(f.Root)!.StartsWith('C') ? "D:" : "C:") + Path.DirectorySeparatorChar;
        // the volume check must come before any existence check or disk change
        Assert.Throws<IOException>(() => store.Add(Path.Combine(other, "nope.txt"), 1, "r"));
        Assert.False(Directory.Exists(Q(f)));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void Add_Symlink_MovesLinkItself_NotTarget()
    {
        using var f = new Fixture();
        var target = f.File(Path.Combine("real", "keep.txt"), 1);
        var link = Path.Combine(f.Root, "link");
        try { Directory.CreateSymbolicLink(link, Path.GetDirectoryName(target)!); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; } // no symlink privilege
        var store = new QuarantineStore(f.Root);
        store.Add(link, 0, "r");
        Assert.True(File.Exists(target));
        Assert.False(Directory.Exists(link));
        store.EmptyAll();
        Assert.True(File.Exists(target));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void EmptyAll_DeletesEverything()
    {
        using var f = new Fixture();
        var store = new QuarantineStore(f.Root);
        store.Add(f.File("a.bin", 1), 1, "r");
        store.Add(f.Dir("dd"), 0, "r");
        store.EmptyAll();
        Assert.Empty(store.Entries);
        Assert.Empty(Directory.GetDirectories(Q(f)));
    }

    [Fact]
    public void PurgeExpired_LockedEntry_IsKept_OthersStillPurged()
    {
        using var f = new Fixture();
        var store = new QuarantineStore(f.Root);
        var locked = store.Add(f.File("locked.bin", 1), 1, "r");
        var free = store.Add(f.File("free.bin", 1), 1, "r");
        using var h = new FileStream(Path.Combine(EntryDir(f, locked.Id), "locked.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(1, store.PurgeExpired(0, DateTime.UtcNow));
        Assert.Equal(locked.Id, Assert.Single(store.Entries).Id);
        Assert.False(Directory.Exists(EntryDir(f, free.Id)));
    }
}
