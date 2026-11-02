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
        try { Purge(Root); } catch { /* cleanup must never fail a test */ }
    }

    // Deletes a tree without following junctions/symlinks; undoes ACL denies and read-only bits on the way.
    private static void Purge(string path)
    {
        var di = new DirectoryInfo(path);
        if (!di.Exists) return;
        if ((di.Attributes & FileAttributes.ReparsePoint) != 0) { di.Delete(); return; } // removes the link only
        try { di.Attributes = FileAttributes.Directory; } catch { }
        try
        {
            var sec = di.GetAccessControl();
            foreach (System.Security.AccessControl.FileSystemAccessRule r in
                     sec.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
                if (r.AccessControlType == System.Security.AccessControl.AccessControlType.Deny) sec.RemoveAccessRule(r);
            di.SetAccessControl(sec);
        }
        catch { }
        foreach (var f in di.EnumerateFileSystemInfos())
        {
            try
            {
                if (f is DirectoryInfo) Purge(f.FullName);
                else { f.Attributes = FileAttributes.Normal; f.Delete(); }
            }
            catch { }
        }
        di.Delete();
    }
}
