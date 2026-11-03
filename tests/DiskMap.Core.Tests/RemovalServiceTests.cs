using System.Diagnostics;
using DiskMap.Core.Quarantine;
using DiskMap.Core.Removal;
using DiskMap.Core.Rules;
using DiskMap.Core.Safety;
using DiskMap.Core.Scanning;
using DiskMap.Core.Settings;

namespace DiskMap.Core.Tests;

public class RemovalServiceTests
{
    private static readonly IReadOnlyList<Rule> Rules = RuleLoader.LoadAll(Path.Combine(AppContext.BaseDirectory, "Rules", "data"));
    private static Rule R(string id) => Rules.Single(r => r.Id == id);
    private static string TempRel(params string[] parts) => Path.Combine(new[] { "Users", "tester", "AppData", "Local", "Temp" }.Concat(parts).ToArray());

    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string Exe, string Args)> Calls { get; } = new();
        public ProcessOutput Result { get; set; } = new(0, "");
        public Task<ProcessOutput> RunAsync(string exe, string args, CancellationToken ct) { Calls.Add((exe, args)); return Task.FromResult(Result); }
    }

    private sealed class FakeBin : IRecycleBin
    {
        public int Emptied;
        public long Size = 5;
        public long SizeBytes() => Size;
        public void Empty() { Emptied++; Size = 0; }
    }

    private sealed class Env : IDisposable
    {
        public Fixture Fx = new();
        public FakeRunner Runner = new();
        public FakeBin Bin = new();
        public string Win => Path.Combine(Fx.Root, "Windows");
        public QuarantineStore Store;
        public Env() { Store = new QuarantineStore(Fx.Root); }
        public RemovalService Svc(int undoDays, bool elevated = false, Func<string, long>? free = null) =>
            new(new AppSettings(undoDays), Fx.User, Win, elevated, Runner, Bin, free ?? (_ => 1000), _ => Store);
        public void Dispose() => Fx.Dispose();
    }

    private static FileNode Node(string path, long size, bool dir, params FileNode[] kids)
    {
        var n = new FileNode { Name = path, IsDirectory = dir, Size = size };
        foreach (var k in kids) { k.Parent = n; n.Children.Add(k); }
        return n;
    }

    private static FileNode Child(string name, long size, bool dir) => new() { Name = name, IsDirectory = dir, Size = size };

    private static RuleMatch M(string rule, FileNode n) => new(R(rule), n);

    [Fact]
    public async Task Quarantine_WhenUndoOn_MovesAndReportsQuarantined()
    {
        using var e = new Env();
        var f = e.Fx.File(TempRel("junk", "a.bin"), 100);
        e.Fx.File(TempRel("junk", "b.bin"), 100);
        var dir = Path.GetDirectoryName(f)!;

        var r = await e.Svc(3).RemoveAsync(M("temp", Node(dir, 200, true)), default);

        Assert.True(r.Success, r.Error);
        Assert.Equal(200, r.BytesQuarantined);
        Assert.Equal(0, r.BytesFreed);
        Assert.False(Directory.Exists(dir));
        Assert.Single(e.Store.Entries);
    }

    [Fact]
    public async Task Quarantine_WhenUndoOff_DeletesPermanently()
    {
        using var e = new Env();
        var dir = Path.GetDirectoryName(e.Fx.File(TempRel("junk", "a.bin"), 100))!;
        var calls = 0;

        var r = await e.Svc(0, free: _ => calls++ == 0 ? 1000 : 1500).RemoveAsync(M("temp", Node(dir, 100, true)), default);

        Assert.True(r.Success, r.Error);
        Assert.Equal(500, r.BytesFreed);
        Assert.Equal(0, r.BytesQuarantined);
        Assert.False(Directory.Exists(dir));
        Assert.Empty(e.Store.Entries);
        Assert.False(Directory.Exists(Path.Combine(e.Fx.Root, "DiskMap Quarantine")));
    }

    [Fact]
    public async Task QuarantineRemove_SkipsLockedFile()
    {
        using var e = new Env();
        var locked = e.Fx.File(TempRel("junk", "a.bin"), 100);
        var other = e.Fx.File(TempRel("junk", "sub", "b.bin"), 50);
        var dir = Path.GetDirectoryName(locked)!;

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = await e.Svc(3).RemoveAsync(M("temp", Node(dir, 150, true)), default);

            Assert.True(r.Success, r.Error);
            Assert.True(r.BytesSkipped > 0);
            Assert.Equal(50, r.BytesQuarantined);
        }
        Assert.False(File.Exists(other));
        Assert.True(File.Exists(locked));
        Assert.False(Directory.Exists(Path.Combine(dir, "sub"))); // emptied directory pruned
    }

    [Fact]
    public async Task Anchor_IsEmptied_NotRemoved()
    {
        using var e = new Env();
        var temp = e.Fx.User.Temp;
        e.Fx.File(TempRel("x.tmp"), 10);
        e.Fx.File(TempRel("d", "y.tmp"), 20);
        var node = Node(temp, 30, true, Child("x.tmp", 10, false), Child("d", 20, true));

        var r = await e.Svc(3).RemoveAsync(M("temp", node), default);

        Assert.True(r.Success, r.Error);
        Assert.True(Directory.Exists(temp));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp));
        Assert.Equal(30, r.BytesQuarantined);
        Assert.Equal(2, e.Store.Entries.Count);
    }

    [Fact]
    public async Task Anchor_PermanentDelete_EmptiesButKeepsFolder()
    {
        using var e = new Env();
        var temp = e.Fx.User.Temp;
        e.Fx.File(TempRel("d", "y.tmp"), 20);

        var r = await e.Svc(0).RemoveAsync(M("temp", Node(temp, 20, true)), default);

        Assert.True(r.Success, r.Error);
        Assert.True(Directory.Exists(temp));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp));
    }

    [Fact]
    public async Task FolderNameMatch_IsRemovedWhole()
    {
        using var e = new Env();
        var nm = Path.GetDirectoryName(e.Fx.File(Path.Combine("Users", "tester", "proj", "node_modules", "a.js"), 10))!;
        e.Fx.File(Path.Combine("Users", "tester", "proj", "package-lock.json"), 2);

        var r = await e.Svc(3).RemoveAsync(M("dev-caches", Node(nm, 10, true)), default);

        Assert.True(r.Success, r.Error);
        Assert.False(Directory.Exists(nm));
        Assert.True(Directory.Exists(Path.GetDirectoryName(nm)));
    }

    [Fact]
    public async Task PermanentDelete_NeverFollowsJunction()
    {
        using var e = new Env();
        var nm = Path.GetDirectoryName(e.Fx.File(Path.Combine("Users", "tester", "proj", "node_modules", "a.js"), 10))!;
        e.Fx.File(Path.Combine("Users", "tester", "proj", "package-lock.json"), 2);
        var outside = e.Fx.File(Path.Combine("outside", "keep.txt"), 5);
        var link = Path.Combine(nm, "link");
        var psi = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(Path.GetDirectoryName(outside)!);
        using (var p = Process.Start(psi)!)
        {
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
        }
        Assert.True(Directory.Exists(link));

        var r = await e.Svc(0).RemoveAsync(M("dev-caches", Node(nm, 10, true)), default);

        Assert.True(r.Success, r.Error);
        Assert.False(Directory.Exists(nm));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Refuses_WhenRailsBlock()
    {
        using var e = new Env();
        var path = Path.Combine("C:" + Path.DirectorySeparatorChar, "Program Files", "x");
        SafetyRails.CanRemove(path, R("temp"), e.Fx.User, e.Win, out var why);

        var r = await e.Svc(3).RemoveAsync(M("temp", Node(path, 1, false)), default);

        Assert.False(r.Success);
        Assert.Equal(why, r.Error);
        Assert.NotEmpty(why);
        Assert.Empty(e.Store.Entries);
    }

    [Fact]
    public async Task Refuses_NeedsAdminWhenNotElevated()
    {
        using var e = new Env();
        var r = await e.Svc(3, elevated: false).RemoveAsync(M("windows-update", Node(Path.Combine(e.Win, "WinSxS"), 1, true)), default);
        Assert.False(r.Success);
        Assert.Equal("Needs administrator", r.Error);
        Assert.Empty(e.Runner.Calls);
    }

    [Fact]
    public async Task Dism_RunsExactCommand()
    {
        using var e = new Env();
        var r = await e.Svc(3, elevated: true).RemoveAsync(M("windows-update", Node(Path.Combine(e.Win, "WinSxS"), 1, true)), default);
        Assert.True(r.Success);
        Assert.Equal((RemovalService.SystemTool("dism.exe"), "/Online /Cleanup-Image /StartComponentCleanup /ResetBase"), Assert.Single(e.Runner.Calls));
    }

    [Fact]
    public async Task Dism_NonZeroExit_FailsWithErrorLine()
    {
        using var e = new Env();
        e.Runner.Result = new ProcessOutput(5, "\r\nDeployment Image Servicing and Management tool\r\nVersion: 10.0\r\n\r\nError: 5\r\n\r\nAccess is denied.\r\n\r\nThe DISM log file can be found at C:\\Windows\\Logs\\DISM\\dism.log\r\n");
        var r = await e.Svc(3, elevated: true).RemoveAsync(M("windows-update", Node(Path.Combine(e.Win, "WinSxS"), 1, true)), default);
        Assert.False(r.Success);
        Assert.Equal("Error: 5 Access is denied.", r.Error);
    }

    [Fact]
    public async Task Hibernate_RunsExactCommand()
    {
        using var e = new Env();
        var r = await e.Svc(3, elevated: true).RemoveAsync(M("hibernation", Node(Path.Combine(e.Fx.Root, "hiberfil.sys"), 1, false)), default);
        Assert.True(r.Success);
        Assert.Equal((RemovalService.SystemTool("powercfg.exe"), "/hibernate off"), Assert.Single(e.Runner.Calls));
    }

    [Fact]
    public async Task RecycleBin_UsesBin()
    {
        using var e = new Env();
        var r = await e.Svc(3).RemoveAsync(M("recycle-bin", Node(Path.Combine(e.Fx.Root, "bin"), 1, true)), default);
        Assert.True(r.Success);
        Assert.Equal(1, e.Bin.Emptied);
    }

    [Fact]
    public void BulkEligible_OnlySafeOrRebuildableQuarantineRules()
    {
        Assert.True(RemovalService.IsBulkEligible(R("temp")));
        Assert.True(RemovalService.IsBulkEligible(R("dev-caches")));
        foreach (var id in new[] { "old-installers", "windows-update", "hibernation", "recycle-bin" })
            Assert.False(RemovalService.IsBulkEligible(R(id)), id);
    }

    [Fact]
    public void QueryInfo_HasNativeSizeOnX64()
    {
        if (Environment.Is64BitProcess) Assert.Equal(24, System.Runtime.InteropServices.Marshal.SizeOf<ShellRecycleBin.QueryInfo>());
    }

    [Fact]
    public async Task Anchor_ChildVanishesBeforeRemoval_IsCountedAsGone()
    {
        using var e = new Env();
        var temp = e.Fx.User.Temp;
        e.Fx.File(TempRel("x.tmp"), 10);
        e.Fx.File(TempRel("d", "y.tmp"), 20);
        var z = e.Fx.File(TempRel("z.tmp"), 99);
        var store = e.Store;
        // storeFor runs after the children were enumerated, so z.tmp vanishes in between
        var svc = new RemovalService(new AppSettings(3), e.Fx.User, e.Win, false, e.Runner, e.Bin, _ => 1000, _ => { File.Delete(z); return store; });

        var r = await svc.RemoveAsync(M("temp", Node(temp, 30, true, Child("x.tmp", 10, false), Child("d", 20, true))), default);

        Assert.True(r.Success, r.Error);
        Assert.Equal(30, r.BytesQuarantined);
        Assert.Equal(0, r.BytesSkipped);
        Assert.Equal(2, e.Store.Entries.Count);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp));
    }

    [Fact]
    public async Task LockedStore_FailsWithoutPerFileAttempts()
    {
        using var e = new Env();
        var dir = Path.GetDirectoryName(e.Fx.File(TempRel("junk", "a.bin"), 10))!;
        e.Fx.File(TempRel("junk", "b.bin"), 10);
        e.Store.Add(e.Fx.File("dummy.bin", 1), 1, "r"); // creates the quarantine folders
        using var hold = new FileStream(Path.Combine(e.Store.Folder, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var sw = Stopwatch.StartNew();

        var r = await e.Svc(3).RemoveAsync(M("temp", Node(dir, 20, true)), default);

        Assert.False(r.Success);
        Assert.Contains("locked", r.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6)); // one 2 s lock wait, not one per file
        Assert.True(File.Exists(Path.Combine(dir, "a.bin")) && File.Exists(Path.Combine(dir, "b.bin")));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    public async Task LockedFileDeepInside_OnlySplitsItsOwnBranch(int undoDays)
    {
        using var e = new Env();
        var nm = Path.Combine(e.Fx.Root, "Users", "tester", "proj", "node_modules");
        string? locked = null;
        for (var d = 0; d < 5; d++)
            for (var i = 0; i < 10; i++)
            {
                var f = e.Fx.File(Path.Combine("Users", "tester", "proj", "node_modules", "sub" + d, "f" + i + ".js"), 10);
                if (d == 2 && i == 3) locked = f;
            }
        e.Fx.File(Path.Combine("Users", "tester", "proj", "package-lock.json"), 2);

        RemovalResult r;
        using (new FileStream(locked!, FileMode.Open, FileAccess.Read, FileShare.None))
            r = await e.Svc(undoDays).RemoveAsync(M("dev-caches", Node(nm, 500, true)), default);

        Assert.True(r.Success, r.Error);
        Assert.True(r.BytesSkipped > 0);
        Assert.Equal(new[] { locked }, Directory.EnumerateFiles(nm, "*", SearchOption.AllDirectories).ToArray());
        if (undoDays == 0) { Assert.Empty(e.Store.Entries); return; }
        // one entry for the one unit (node_modules), holding 4 whole branches + 9 files; restore puts all 49 back
        var entry = Assert.Single(e.Store.Entries);
        Assert.True(entry.Partial);
        Assert.Equal(nm, e.Store.Restore(entry.Id));
        Assert.Equal(50, Directory.EnumerateFiles(nm, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task Dism_NoErrorLine_ReportsExitCodeNotBanner()
    {
        using var e = new Env();
        e.Runner.Result = new ProcessOutput(87, "\r\nDeployment Image Servicing and Management tool\r\nVersion: 10.0\r\n");
        var r = await e.Svc(3, elevated: true).RemoveAsync(M("windows-update", Node(Path.Combine(e.Win, "WinSxS"), 1, true)), default);
        Assert.Equal("dism exited with code 87.", r.Error);
    }

    [Fact]
    public async Task RecycleBin_FreedIsBinSizeBeforeMinusAfter()
    {
        using var e = new Env();
        e.Bin.Size = 500;
        var r = await e.Svc(3, free: _ => 0).RemoveAsync(M("recycle-bin", Node(Path.Combine(e.Fx.Root, "bin"), 1, true)), default);
        Assert.True(r.Success, r.Error);
        Assert.Equal(500, r.BytesFreed);
    }

    [Fact]
    public async Task RecycleBin_OfAnotherAccount_IsNeverEmptied()
    {
        using var e = new Env();
        var other = e.Fx.User with { Sid = "S-1-5-21-1-2-3-1001" }; // elevated with an administrator's password: the token is not the launching user
        var svc = new RemovalService(new AppSettings(3), other, e.Win, true, e.Runner, e.Bin, _ => 0, _ => e.Store);
        var r = await svc.RemoveAsync(M("recycle-bin", Node(Path.Combine(e.Fx.Root, "bin"), 1, true)), default);
        Assert.False(r.Success);
        Assert.Equal(0, e.Bin.Emptied);
    }

    [Fact]
    public async Task PerUserItem_WhenRunningAsAnotherAccount_IsNeverTouched()
    {
        using var e = new Env();
        var f = e.Fx.File(TempRel("junk", "a.bin"), 100);
        var dir = Path.GetDirectoryName(f)!;
        var other = e.Fx.User with { Sid = "S-1-5-21-1-2-3-1001" }; // the launching user's folders, but an administrator's token
        var svc = new RemovalService(new AppSettings(0), other, e.Win, true, e.Runner, e.Bin, _ => 0, _ => e.Store);
        var r = await svc.RemoveAsync(M("temp", Node(dir, 100, true)), default);
        Assert.False(r.Success);
        Assert.True(File.Exists(f));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WindowsTemp_OnlyItemsOwnedByTrustedAccountsOrTheUser_AreRemoved(bool ownedByUser)
    {
        using var e = new Env();
        var temp = Path.Combine(e.Win, "Temp");
        var file = e.Fx.File(Path.Combine("Windows", "Temp", "x.tmp"), 10); // owned by the account running the tests
        var user = ownedByUser ? e.Fx.User : e.Fx.User with { Sid = "S-1-5-21-1-2-3-1001" };
        var svc = new RemovalService(new AppSettings(0), user, e.Win, true, e.Runner, e.Bin, _ => 0, _ => e.Store);
        var r = await svc.RemoveAsync(M("windows-temp", Node(temp, 10, true, Child("x.tmp", 10, false))), default);
        Assert.Equal(!ownedByUser, File.Exists(file));
        if (!ownedByUser) Assert.True(r.BytesSkipped > 0);
    }
}
