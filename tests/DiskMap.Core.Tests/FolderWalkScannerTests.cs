using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskMap.Core.Scanning;

namespace DiskMap.Core.Tests;

public sealed partial class FolderWalkScannerTests
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpace(string root, out uint spc, out uint bps, out uint free, out uint total);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string link, string existing, nint reserved);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileAttributesW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileAttributes(string path, uint attrs);

    private static readonly string Ext = new string((char)92, 2) + "?" + (char)92;

    private static long Cluster(string path)
    {
        Assert.True(GetDiskFreeSpace(Path.GetPathRoot(path)!, out var spc, out var bps, out _, out _));
        return (long)spc * bps;
    }

    private static Task<ScanResult> Scan(Fixture f) => new FolderWalkScanner().ScanAsync(f.Root, null, CancellationToken.None);

    [Fact]
    public async Task Scan_SumsAllocatedSizes()
    {
        using var f = new Fixture();
        f.File("a.bin", 1);
        f.File("sub/b.bin", 5000);
        var c = Cluster(f.Root);
        var r = await Scan(f);
        Assert.Equal(c + (5000 + c - 1) / c * c, r.Root.Size);
        Assert.Equal(2, r.FileCount);
    }

    [Fact]
    public async Task Scan_CountsHardlinkOnce()
    {
        using var f = new Fixture();
        var a = f.File("a.bin", 5000);
        Assert.True(CreateHardLink(Path.Combine(f.Root, "b.bin"), a, 0));
        var c = Cluster(f.Root);
        Assert.Equal((5000 + c - 1) / c * c, (await Scan(f)).Root.Size);
    }

    [Fact]
    public async Task Scan_DoesNotFollowJunction()
    {
        using var f = new Fixture();
        f.File("real/big.bin", 1024 * 1024);
        var psi = new ProcessStartInfo("cmd", $"/c mklink /J \"{Path.Combine(f.Root, "link")}\" \"{Path.Combine(f.Root, "real")}\"")
        { CreateNoWindow = true, RedirectStandardOutput = true };
        using (var p = Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
        var r = await Scan(f);
        var link = r.Root.Children.Single(n => n.Name == "link");
        Assert.True(link.IsReparsePoint);
        Assert.Empty(link.Children);
        Assert.Equal(0, link.Size);
        var c = Cluster(f.Root);
        Assert.Equal((1024 * 1024 + c - 1) / c * c, r.Root.Size);
    }

    [Fact]
    public async Task Scan_ContinuesPastAccessDenied()
    {
        using var f = new Fixture();
        var locked = f.Dir("locked");
        f.File("locked/x.bin", 100);
        f.File("sibling.bin", 10 * 1024);
        var di = new DirectoryInfo(locked);
        var sec = di.GetAccessControl();
        var me = WindowsIdentity.GetCurrent().User!;
        sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.ListDirectory, AccessControlType.Deny));
        di.SetAccessControl(sec);
        try
        {
            var r = await Scan(f);
            Assert.True(r.Root.Children.Single(n => n.Name == "locked").AccessDenied);
            var c = Cluster(f.Root);
            Assert.Equal((10 * 1024 + c - 1) / c * c, r.Root.Size);
        }
        finally
        {
            sec.RemoveAccessRule(new FileSystemAccessRule(me, FileSystemRights.ListDirectory, AccessControlType.Deny));
            di.SetAccessControl(sec);
        }
    }

    [Fact]
    public async Task Scan_HandlesLongPaths()
    {
        using var f = new Fixture();
        var rel = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('d', 50), 7)); // > 350 chars
        var file = f.File(Path.Combine(rel, "deep.bin"), 5000);
        Assert.True(file.Length > 300);
        // The hardlink is only counted once if the Win32 calls (CreateFile) work on the long path.
        Assert.True(CreateHardLink(Ext + Path.Combine(Path.GetDirectoryName(file)!, "link.bin"), Ext + file, 0));
        var r = await Scan(f);
        var c = Cluster(f.Root);
        Assert.Equal(2, r.FileCount);
        Assert.Equal((5000 + c - 1) / c * c, r.Root.Size);
    }

    [SkippableFact]
    public async Task Scan_CloudPlaceholderCountsZero()
    {
        using var f = new Fixture();
        var p = f.File("cloud.bin", 5000);
        Skip.If(!SetFileAttributes(p, 0x00400000) || ((int)File.GetAttributes(p) & 0x00400000) == 0, "cannot set RECALL_ON_DATA_ACCESS here");
        Assert.Equal(0, (await Scan(f)).Root.Size);
    }
}
