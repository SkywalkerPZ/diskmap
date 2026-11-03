using DiskMap.Core.Removal;

namespace DiskMap.Core.Tests;

public class SafeDeleteTests
{
    private static void Junction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var pr = System.Diagnostics.Process.Start(psi)!;
        pr.WaitForExit();
        Assert.Equal(0, pr.ExitCode);
    }

    [Fact]
    public void DeleteTree_RemovesTree_ReadOnlyIncluded_JunctionAsLinkOnly()
    {
        using var f = new Fixture();
        var precious = f.File(Path.Combine("outside", "precious.txt"), 3);
        var victim = f.Dir("victim");
        var ro = f.File(Path.Combine("victim", "deep", "ro.txt"), 2);
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        Junction(Path.Combine(victim, "deep", "j"), Path.GetDirectoryName(precious)!);

        SafeDelete.DeleteTree(victim);

        Assert.False(Directory.Exists(victim));
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void DeleteTree_TopJunction_RemovesOnlyTheLink()
    {
        using var f = new Fixture();
        var precious = f.File(Path.Combine("outside", "precious.txt"), 3);
        var link = Path.Combine(f.Root, "link");
        Junction(link, Path.GetDirectoryName(precious)!);

        SafeDelete.DeleteTree(link);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void DeleteTree_LockedFile_StaysAndThrowsBusy_RestIsDeleted()
    {
        using var f = new Fixture();
        var dir = f.Dir("d");
        var locked = f.File(Path.Combine("d", "locked.bin"), 1);
        var other = f.File(Path.Combine("d", "sub", "other.bin"), 1);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.ThrowsAny<IOException>(() => SafeDelete.DeleteTree(dir));
            Assert.Equal(32, ex.HResult & 0xFFFF); // sharing violation, so the caller can split the unit
        }
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(other));
    }

    [Fact]
    public void DeleteTree_Missing_ThrowsNotFound()
    {
        using var f = new Fixture();
        Assert.ThrowsAny<FileNotFoundException>(() => SafeDelete.DeleteTree(Path.Combine(f.Root, "nope")));
    }
}
