using System.Text.Json;
using DiskMap.Core.Quarantine;

namespace DiskMap.Core.Tests;

public class QuarantineStoreTests
{
    private static string Top(Fixture f) => Path.Combine(f.Root, "DiskMap Quarantine");
    private static string Q(Fixture f) => Path.Combine(Top(f), System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
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
        Assert.True((new DirectoryInfo(Top(f)).Attributes & FileAttributes.Hidden) != 0);
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
        var a = store.Add(f.File("a.bin", 5), 5, "r", 3);
        var b = store.Add(f.File("b.bin", 5), 5, "r", 3);
        var c = store.Add(f.File("c.bin", 5), 5, "r", 7);
        Backdate(f, a.Id, now.AddDays(-4));
        Backdate(f, b.Id, now.AddDays(-2));
        Backdate(f, c.Id, now.AddDays(-4));
        store = new QuarantineStore(f.Root);

        // each entry keeps the promise made when it was removed: a (3 days, 4 old) goes; b (3, 2 old) and c (7, 4 old) stay
        Assert.Equal(1, store.PurgeExpired(now));
        Assert.Equal(new[] { b.Id, c.Id }.Order(), store.Entries.Select(e => e.Id).Order());
        Assert.False(Directory.Exists(EntryDir(f, a.Id)));
        Assert.Equal(1, store.PurgeExpired(now.AddDays(1.5)));
        Assert.Equal(c.Id, Assert.Single(store.Entries).Id);
        Assert.Equal(1, store.PurgeExpired(now.AddDays(3)));
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
    public void CorruptManifest_AdoptsEntryFolders_AsUnknown_AndBacksUpOnNextSave()
    {
        using var f = new Fixture();
        var e = new QuarantineStore(f.Root).Add(f.File("a.bin", 7), 7, "rule");
        File.WriteAllText(Path.Combine(Q(f), "manifest.json"), "{ not json");
        var s = new QuarantineStore(f.Root);
        Assert.True(s.IsCorrupt);
        Assert.Null(s.CorruptBackupPath);
        var adopted = Assert.Single(s.Entries);
        Assert.Equal(e.Id, adopted.Id);
        Assert.Null(adopted.OriginalPath);
        Assert.Equal("unknown", adopted.RuleId);
        Assert.Equal(7, adopted.Size);
        Assert.True(Directory.Exists(EntryDir(f, e.Id)));
        Assert.Throws<InvalidOperationException>(() => s.Restore(e.Id));

        s.Add(f.File("b.bin", 1), 1, "r"); // first save keeps the bad manifest
        Assert.NotNull(s.CorruptBackupPath);
        Assert.Equal("{ not json", File.ReadAllText(s.CorruptBackupPath!));
        Assert.Equal(2, new QuarantineStore(f.Root).Entries.Count);
        Assert.False(new QuarantineStore(f.Root).IsCorrupt);

        s.EmptyAll(); // adopted entries are purgeable like any other
        Assert.Empty(s.Entries);
        Assert.False(Directory.Exists(EntryDir(f, e.Id)));
    }

    [Fact]
    public void OrphanEntryFolder_IsAdoptedOnLoad()
    {
        using var f = new Fixture();
        new QuarantineStore(f.Root).Add(f.File("a.bin", 1), 1, "r");
        var id = Guid.NewGuid();
        Directory.CreateDirectory(EntryDir(f, id));
        File.WriteAllBytes(Path.Combine(EntryDir(f, id), "lost.bin"), new byte[40]);
        var s = new QuarantineStore(f.Root);
        var o = Assert.Single(s.Entries, x => x.Id == id);
        Assert.Null(o.OriginalPath);
        Assert.Equal(40, o.Size);
        Assert.Equal(2, s.Entries.Count);
    }

    [Fact]
    public void TwoInstances_EachAdd_FreshInstanceSeesBoth()
    {
        using var f = new Fixture();
        var s1 = new QuarantineStore(f.Root);
        var s2 = new QuarantineStore(f.Root);
        s1.Add(f.File("a.bin", 1), 1, "r");
        s2.Add(f.File("b.bin", 2), 2, "r");
        var s3 = new QuarantineStore(f.Root);
        Assert.Equal(2, s3.Entries.Count);
        Assert.Equal(3, s3.TotalSize);
    }

    [Fact]
    public void ParallelInstances_NeverLoseEntries()
    {
        using var f = new Fixture();
        var files = Enumerable.Range(0, 12).Select(i => f.File($"p{i}.bin", 1)).ToArray();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            p => new QuarantineStore(f.Root).Add(p, 1, "r"));
        Assert.Equal(12, new QuarantineStore(f.Root).Entries.Count);
    }

    [Fact]
    public void PurgeExpired_NestedJunction_IsRemovedAsLink_OutsideFileSurvives()
    {
        using var f = new Fixture();
        var outside = f.File(Path.Combine("outside", "precious.txt"), 3);
        var victim = f.Dir("victim");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
        foreach (var a in new[] { "/c", "mklink", "/J", Path.Combine(victim, "j"), Path.GetDirectoryName(outside)! }) psi.ArgumentList.Add(a);
        using (var pr = System.Diagnostics.Process.Start(psi)!) { pr.WaitForExit(); Assert.Equal(0, pr.ExitCode); }
        var store = new QuarantineStore(f.Root);
        var e = store.Add(victim, 0, "r", 0);
        Assert.Equal(1, store.PurgeExpired(DateTime.UtcNow));
        Assert.Empty(store.Entries);
        Assert.False(Directory.Exists(EntryDir(f, e.Id)));
        Assert.True(File.Exists(outside));
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

    [SkippableFact]
    public void Add_Symlink_MovesLinkItself_NotTarget()
    {
        using var f = new Fixture();
        var target = f.File(Path.Combine("real", "keep.txt"), 1);
        var link = Path.Combine(f.Root, "link");
        var made = true;
        try { Directory.CreateSymbolicLink(link, Path.GetDirectoryName(target)!); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { made = false; }
        Skip.IfNot(made, "no symlink privilege");
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
        var locked = store.Add(f.File("locked.bin", 1), 1, "r", 0);
        var free = store.Add(f.File("free.bin", 1), 1, "r", 0);
        using var h = new FileStream(Path.Combine(EntryDir(f, locked.Id), "locked.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(1, store.PurgeExpired(DateTime.UtcNow));
        Assert.Equal(locked.Id, Assert.Single(store.Entries).Id);
        Assert.False(Directory.Exists(EntryDir(f, free.Id)));
    }

    private static void Junction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var pr = System.Diagnostics.Process.Start(psi)!;
        pr.WaitForExit();
        Assert.Equal(0, pr.ExitCode);
    }

    [Fact]
    public void CreatedFolders_HaveProtectedDacls_ForThisAccountOnly()
    {
        using var f = new Fixture();
        new QuarantineStore(f.Root).Add(f.File("a.bin", 1), 1, "r");
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var top = new DirectoryInfo(Top(f)).GetAccessControl();
        Assert.True(top.AreAccessRulesProtected);
        Assert.DoesNotContain(top.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>(),
            r => r.IdentityReference.Equals(me));
        var acct = new DirectoryInfo(Q(f)).GetAccessControl();
        Assert.True(acct.AreAccessRulesProtected);
        var sids = acct.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToHashSet();
        Assert.Equal(new HashSet<string> { "S-1-5-18", "S-1-5-32-544", me.Value }, sids);
    }

    [Fact]
    public void ExistingTopFolder_NotMadeByDiskMap_IsRefusedAndLeftAlone()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Top(f)); // inherited permissions: anyone who could write the parent could have planted it
        var src = f.File("a.bin", 1);
        var store = new QuarantineStore(f.Root);
        Assert.NotNull(store.Untrusted);
        Assert.Throws<IOException>(() => store.Add(src, 1, "r"));
        Assert.True(File.Exists(src));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Top(f)));
        Assert.False(new DirectoryInfo(Top(f)).GetAccessControl().AreAccessRulesProtected); // not "repaired"
    }

    [Fact]
    public void TopFolderJunction_IsRefused_NothingWrittenAtTarget()
    {
        using var f = new Fixture();
        var target = f.Dir("elsewhere");
        Junction(Top(f), target);
        var src = f.File("a.bin", 1);
        var store = new QuarantineStore(f.Root);
        Assert.Throws<IOException>(() => store.Add(src, 1, "r"));
        Assert.Contains("link", store.Untrusted);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void EntryFolderJunction_IsRefused_PurgeNeverReachesTarget()
    {
        using var f = new Fixture();
        var precious = f.File(Path.Combine("outside", "precious.txt"), 3);
        new QuarantineStore(f.Root).Add(f.File("a.bin", 1), 1, "r", 0);
        Junction(Path.Combine(Q(f), Guid.NewGuid().ToString("N")), Path.GetDirectoryName(precious)!);
        var store = new QuarantineStore(f.Root);
        Assert.NotNull(store.Untrusted);
        Assert.Empty(store.Entries);
        Assert.Throws<IOException>(() => store.PurgeExpired(DateTime.UtcNow.AddDays(30)));
        Assert.Throws<IOException>(() => store.EmptyAll());
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void OldManifestWithoutKeepDays_LoadsAsSevenDays()
    {
        using var f = new Fixture();
        var e = new QuarantineStore(f.Root).Add(f.File("a.bin", 1), 1, "r", 3);
        var path = Path.Combine(Q(f), "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { new { e.Id, e.OriginalPath, e.Size, e.RemovedUtc, e.RuleId } }));
        var s = new QuarantineStore(f.Root);
        Assert.Equal(7, Assert.Single(s.Entries).KeepDays);
        Assert.Equal(e.OriginalPath, s.Entries[0].OriginalPath);
        Assert.Equal(0, s.PurgeExpired(DateTime.UtcNow.AddDays(6)));
        Assert.Equal(1, s.PurgeExpired(DateTime.UtcNow.AddDays(7.1)));
    }

    [Fact]
    public void Probe_TellsNotFoundFromCouldNotCheck()
    {
        using var f = new Fixture();
        Assert.False(QuarantineStore.Probe(Path.Combine(f.Root, "missing")));
        Assert.True(QuarantineStore.Probe(f.Root));
        Assert.Null(QuarantineStore.Probe(Path.Combine(f.Root, new string('x', 40000)))); // an I/O error keeps the entry's origin
    }

    [Fact]
    public void Partial_OneEntry_RestoreMergesBackAroundWhatStayed()
    {
        using var f = new Fixture();
        var dir = f.Dir("cache");
        var stayed = f.File(Path.Combine("cache", "in-use.bin"), 1);
        var a = f.File(Path.Combine("cache", "a.bin"), 1);
        var b = f.File(Path.Combine("cache", "sub", "b.bin"), 1);
        var store = new QuarantineStore(f.Root);
        var id = store.BeginPartial(dir, "r", 3);
        store.MoveInto(id, a);
        store.MoveInto(id, Path.GetDirectoryName(b)!);
        store.EndPartial(id, 2);
        var e = Assert.Single(store.Entries);
        Assert.True(e.Partial);
        Assert.Equal(2, e.Size);
        Assert.Equal(3, e.KeepDays);
        Assert.False(File.Exists(a) || File.Exists(b));

        Assert.Equal(dir, store.Restore(id));
        Assert.True(File.Exists(a) && File.Exists(b) && File.Exists(stayed));
        Assert.Empty(store.Entries);
        Assert.False(Directory.Exists(EntryDir(f, id)));
    }

    [Fact]
    public void Partial_NothingMoved_LeavesNoEntry()
    {
        using var f = new Fixture();
        var dir = f.Dir("cache");
        var store = new QuarantineStore(f.Root);
        var id = store.BeginPartial(dir, "r", 3);
        store.EndPartial(id, 0);
        Assert.Empty(store.Entries);
        Assert.False(Directory.Exists(EntryDir(f, id)));
        Assert.True(Directory.Exists(dir));
    }
}
