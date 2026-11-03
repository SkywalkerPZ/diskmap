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
        public long SizeBytes() => 5;
        public void Empty() => Emptied++;
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
        e.Fx.File(Path.Combine("Users", "tester", "proj", "package.json"), 2);

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
        e.Fx.File(Path.Combine("Users", "tester", "proj", "package.json"), 2);
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
        Assert.Equal(("DISM", "/Online /Cleanup-Image /StartComponentCleanup /ResetBase"), Assert.Single(e.Runner.Calls));
    }

    [Fact]
    public async Task Dism_NonZeroExit_FailsWithFirstLine()
    {
        using var e = new Env();
        e.Runner.Result = new ProcessOutput(5, "\r\nAccess is denied.\r\nmore");
        var r = await e.Svc(3, elevated: true).RemoveAsync(M("windows-update", Node(Path.Combine(e.Win, "WinSxS"), 1, true)), default);
        Assert.False(r.Success);
        Assert.Equal("Access is denied.", r.Error);
    }

    [Fact]
    public async Task Hibernate_RunsExactCommand()
    {
        using var e = new Env();
        var r = await e.Svc(3, elevated: true).RemoveAsync(M("hibernation", Node(Path.Combine(e.Fx.Root, "hiberfil.sys"), 1, false)), default);
        Assert.True(r.Success);
        Assert.Equal(("powercfg", "/hibernate off"), Assert.Single(e.Runner.Calls));
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
}
