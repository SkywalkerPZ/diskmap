using System.Diagnostics;
using System.IO.Enumeration;

namespace DiskMap.Core.Scanning;

public sealed class FolderWalkScanner : IScanner
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const int ProgressEvery = 5000;

    private readonly record struct Entry(string Name, bool IsDirectory, FileAttributes Attributes, long Length, DateTime LastWriteUtc);

    public Task<ScanResult> ScanAsync(string rootPath, IProgress<long>? filesSeen, CancellationToken ct) =>
        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var full = Path.GetFullPath(rootPath);
            var state = new State(ClusterSize(full), filesSeen, ct);
            var root = new FileNode { Name = full, IsDirectory = true, LastWriteUtc = Directory.GetLastWriteTimeUtc(full) };
            Walk(root, full, state);
            filesSeen?.Report(root.FileCount);
            return new ScanResult(root, root.FileCount, sw.Elapsed, false);
        }, ct);

    private sealed class State(long cluster, IProgress<long>? progress, CancellationToken ct)
    {
        public long Cluster = cluster;
        public IProgress<long>? Progress = progress;
        public CancellationToken Ct = ct;
        public long Seen;
        public HashSet<(uint, ulong)> Links = new();
    }

    private static void Walk(FileNode dir, string path, State s)
    {
        s.Ct.ThrowIfCancellationRequested();
        var entries = new List<Entry>();
        try
        {
            var opts = new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false, IgnoreInaccessible = false };
            entries.AddRange(new FileSystemEnumerable<Entry>(path,
                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Attributes, e.Length, e.LastWriteTimeUtc.UtcDateTime), opts));
        }
        catch (UnauthorizedAccessException) { dir.AccessDenied = true; }
        catch (IOException) { } // vanished or unreadable mid-scan; keep what we have

        foreach (var e in entries)
        {
            var childPath = Path.Join(path, e.Name);
            var reparse = (e.Attributes & FileAttributes.ReparsePoint) != 0;
            var node = new FileNode { Name = e.Name, Parent = dir, IsDirectory = e.IsDirectory, IsReparsePoint = reparse, LastWriteUtc = e.LastWriteUtc };
            if (e.IsDirectory)
            {
                if (!reparse) Walk(node, childPath, s); // never follow junctions or symlinks
            }
            else
            {
                node.FileCount = 1;
                node.Size = FileSize(e, childPath, s);
                if (++s.Seen % ProgressEvery == 0) s.Progress?.Report(s.Seen);
            }
            dir.Children.Add(node);
            dir.Size += node.Size;
            dir.FileCount += node.FileCount;
        }
    }

    private static long FileSize(Entry e, string path, State s)
    {
        if ((e.Attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0) return 0; // cloud-only placeholder
        if (e.Length == 0) return 0;
        if (IsRepeatHardlink(path, s)) return 0;
        var p = LongPath(path);
        var low = NativeMethods.GetCompressedFileSize(p, out var high);
        long bytes = low == 0xFFFFFFFF && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() != 0
            ? e.Length
            : ((long)high << 32) | low;
        return (bytes + s.Cluster - 1) / s.Cluster * s.Cluster;
    }

    private static bool IsRepeatHardlink(string path, State s)
    {
        using var h = NativeMethods.CreateFile(LongPath(path), 0, NativeMethods.FileShareAll, 0, NativeMethods.OpenExisting, NativeMethods.FileFlagBackupSemantics, 0);
        if (h.IsInvalid || !NativeMethods.GetFileInformationByHandle(h, out var info) || info.NumberOfLinks <= 1) return false;
        return !s.Links.Add((info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow));
    }

    private static long ClusterSize(string path)
    {
        var root = Path.GetPathRoot(path)!;
        return NativeMethods.GetDiskFreeSpace(root, out var spc, out var bps, out _, out _) ? (long)spc * bps : 4096;
    }

    // Win32 calls need the \?\ prefix past MAX_PATH (.NET adds it itself for its own APIs).
    private static string LongPath(string p) =>
        p.Length < 240 || p.StartsWith(@"\?\") ? p
        : p.StartsWith(@"\\") ? @"\?\UNC\" + p[2..] : @"\?\" + p;
}
