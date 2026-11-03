namespace DiskMap.Core.Scanning;

/// <summary>In-place edits after a partial rescan; every ancestor's Size and FileCount are kept in step.</summary>
public static class FileTree
{
    /// <summary>Gives <paramref name="target"/> the children, size and count of <paramref name="fresh"/>, a new scan of the same folder.</summary>
    public static void ReplaceContents(FileNode target, FileNode fresh)
    {
        var size = fresh.Size - target.Size;
        var files = fresh.FileCount - target.FileCount;
        foreach (var old in target.Children) old.Parent = null; // detached nodes must no longer resolve to the live root
        target.Children.Clear();
        foreach (var c in fresh.Children) { c.Parent = target; target.Children.Add(c); }
        target.AccessDenied = fresh.AccessDenied;
        Adjust(target, size, files);
    }

    /// <summary>Drops a node that no longer exists on disk.</summary>
    public static void Remove(FileNode node)
    {
        if (node.Parent is not { } p || !p.Children.Remove(node)) return;
        Adjust(p, -node.Size, -node.FileCount);
        node.Parent = null;
    }

    /// <summary>The top of the tree <paramref name="node"/> hangs from; for a detached node, the top of its detached branch.</summary>
    public static FileNode RootOf(FileNode node)
    {
        while (node.Parent is { } p) node = p;
        return node;
    }

    private static void Adjust(FileNode? n, long size, long files)
    {
        for (; n is not null; n = n.Parent) { n.Size += size; n.FileCount += files; }
    }
}
