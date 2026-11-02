namespace DiskMap.Core.Tests;

/// <summary>Temp directory tree for tests; deleted on dispose.</summary>
public sealed class Fixture : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("diskmap-").FullName;

    /// <summary>Creates a file of the given size (creating parent dirs) and returns its full path.</summary>
    public string File(string relPath, int bytes, DateTime? lastWriteUtc = null)
    {
        var path = Path.Combine(Root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        if (lastWriteUtc is { } t) System.IO.File.SetLastWriteTimeUtc(path, t);
        return path;
    }

    public string Dir(string relPath) => Directory.CreateDirectory(Path.Combine(Root, relPath)).FullName;

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}
