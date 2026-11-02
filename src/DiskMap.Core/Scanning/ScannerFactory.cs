namespace DiskMap.Core.Scanning;

public static class ScannerFactory
{
    /// <summary>
    /// MftScanner for elevated scans of NTFS volumes, else FolderWalkScanner.
    /// MftScanner throws NotSupportedException when it cannot read the MFT; callers then rerun with FolderWalkScanner.
    /// </summary>
    public static IScanner For(string rootPath, bool isElevated)
    {
        if (isElevated)
        {
            try
            {
                if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(rootPath))!).DriveFormat == "NTFS") return new MftScanner();
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { } // UNC path, drive not ready
        }
        return new FolderWalkScanner();
    }
}
