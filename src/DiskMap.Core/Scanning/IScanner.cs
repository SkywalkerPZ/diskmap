namespace DiskMap.Core.Scanning;

public interface IScanner
{
    Task<ScanResult> ScanAsync(string rootPath, IProgress<long>? filesSeen, CancellationToken ct);
}
