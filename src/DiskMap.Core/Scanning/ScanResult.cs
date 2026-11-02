namespace DiskMap.Core.Scanning;

public sealed record ScanResult(FileNode Root, long FileCount, TimeSpan Elapsed, bool UsedFastScan);
