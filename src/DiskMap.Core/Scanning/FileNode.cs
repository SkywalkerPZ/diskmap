namespace DiskMap.Core.Scanning;

public sealed class FileNode
{
    public required string Name { get; init; }
    public FileNode? Parent { get; set; }
    public bool IsDirectory { get; init; }
    public bool IsReparsePoint { get; init; }
    /// <summary>Allocated bytes; for a directory, the sum over its children.</summary>
    public long Size { get; set; }
    public long FileCount { get; set; }
    public DateTime LastWriteUtc { get; init; }
    public List<FileNode> Children { get; } = new();
    public bool AccessDenied { get; set; }

    /// <summary>Built by walking parents; not stored per node. The root's Name is its full path.</summary>
    public string FullPath => Parent is null ? Name : Path.Join(Parent.FullPath, Name);
}
