using DiskMap.Core.Scanning;

namespace DiskMap.Core.Tests;

public class FileTreeTests
{
    private static FileNode Dir(string name, params FileNode[] kids)
    {
        var d = new FileNode { Name = name, IsDirectory = true };
        foreach (var k in kids) { k.Parent = d; d.Children.Add(k); d.Size += k.Size; d.FileCount += k.FileCount; }
        return d;
    }

    private static FileNode File(string name, long size) => new() { Name = name, Size = size, FileCount = 1 };

    [Fact]
    public void ReplaceContents_SwapsChildrenAndFixesAncestors()
    {
        var proj = Dir("proj", File("package.json", 10), Dir("node_modules", File("a", 100), File("b", 50)));
        var root = Dir(@"C:\", Dir("Users", proj), File("pagefile.sys", 1000));
        var fresh = Dir(@"C:\Users\proj", File("package.json", 10));

        FileTree.ReplaceContents(proj, fresh);

        Assert.Equal(10, proj.Size);
        Assert.Equal(1, proj.FileCount);
        Assert.Same(proj, Assert.Single(proj.Children).Parent);
        Assert.Equal(10, proj.Parent!.Size);
        Assert.Equal(1010, root.Size);
        Assert.Equal(2, root.FileCount);
    }

    [Fact]
    public void Remove_DetachesAndFixesAncestors()
    {
        var nm = Dir("node_modules", File("a", 100));
        var root = Dir("r", Dir("proj", File("package.json", 10), nm));

        FileTree.Remove(nm);

        Assert.Null(nm.Parent);
        Assert.Equal(10, root.Size);
        Assert.Equal(1, root.FileCount);
        Assert.Same(root, FileTree.RootOf(root.Children[0].Children[0]));
    }
}
