using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskMap.Core.Scanning;

/// <summary>
/// Unelevated scan. Each folder is opened once (FILE_FLAG_OPEN_REPARSE_POINT) and read in bulk with
/// GetFileInformationByHandleEx, which already carries every entry's allocation size, file id and reparse tag, so plain
/// files are never opened. Hardlinks count once: every file id seen is remembered (the link count is not part of a
/// directory listing, so all ids are kept, about 24 bytes per file). Only compressed, sparse or (non-link) reparse files
/// are opened, always with FILE_FLAG_OPEN_REPARSE_POINT, to read their real allocation.
/// </summary>
public sealed class FolderWalkScanner : IScanner
{
    private const uint RecallOnOpen = 0x00040000, RecallOnDataAccess = 0x00400000, Offline = 0x1000;
    private const uint Sparse = 0x200, ReparsePoint = 0x400, Compressed = 0x800;
    private const int ProgressEvery = 5000;

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
        public HashSet<long> Ids = new();
    }

    private static void Walk(FileNode dir, string path, State s)
    {
        s.Ct.ThrowIfCancellationRequested();
        var entries = new List<NativeMethods.DirEntry>();
        using (var h = NativeMethods.CreateFile(LongPath(path), NativeMethods.FileListDirectory | NativeMethods.Synchronize, NativeMethods.FileShareAll, 0,
                   NativeMethods.OpenExisting, NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint, 0))
        {
            // access denied is shown; a folder that vanished or can't be read mid-scan keeps what we have
            var err = h.IsInvalid ? Marshal.GetLastPInvokeError() : NativeMethods.ReadDirectory(h, entries);
            if (err == 5) dir.AccessDenied = true;
        }

        foreach (var e in entries)
        {
            var childPath = Path.Join(path, e.Name);
            var node = new FileNode { Name = e.Name, Parent = dir, IsDirectory = e.IsDirectory, IsReparsePoint = e.IsReparsePoint, LastWriteUtc = Utc(e.LastWriteFileTime) };
            if (e.IsDirectory)
            {
                // never follow junctions or symlinks; cloud-sync folders (non-link reparse points) are real folders,
                // unless their listing isn't on disk yet (reading it would make the sync client fetch it)
                if (!e.IsReparsePoint || (!NativeMethods.IsNameSurrogate(e.ReparseTag) && (e.Attributes & RecallOnOpen) == 0))
                    Walk(node, childPath, s);
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

    private static long FileSize(NativeMethods.DirEntry e, string path, State s)
    {
        if ((e.Attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0) return 0; // cloud-only placeholder
        if (e.EndOfFile == 0) return 0;
        if (e.FileId != 0 && !s.Ids.Add(e.FileId)) return 0; // another name of a file already counted (hardlink)
        if (e.IsReparsePoint && NativeMethods.IsNameSurrogate(e.ReparseTag)) return 0; // a file symlink: the link itself has no data
        var bytes = (e.Attributes & (Compressed | Sparse | ReparsePoint)) != 0 ? Opened(path, e) : -1;
        if (bytes < 0) bytes = e.AllocationSize > 0 ? e.AllocationSize : e.EndOfFile;
        return (bytes + s.Cluster - 1) / s.Cluster * s.Cluster;
    }

    /// <summary>
    /// Real allocation of a compressed, sparse or WOF (CompactOS) file, from a handle that never follows a link:
    /// a WOF file keeps its bytes in the WofCompressedData stream (as the MFT scan counts it). -1 when unreadable.
    /// </summary>
    private static unsafe long Opened(string path, NativeMethods.DirEntry e)
    {
        using var h = NativeMethods.CreateFile(LongPath(path), NativeMethods.FileReadAttributes | NativeMethods.Synchronize, NativeMethods.FileShareAll, 0,
            NativeMethods.OpenExisting, NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint, 0);
        if (h.IsInvalid) return -1;
        if (e.ReparseTag == NativeMethods.TagWof)
        {
            var buf = stackalloc byte[4096];
            if (!NativeMethods.GetFileInformationByHandleEx(h, 7, buf, 4096)) return -1; // FileStreamInfo
            long total = 0;
            for (var off = 0; ; )
            {
                var p = buf + off;
                var name = new string((char*)(p + 24), 0, (int)(*(uint*)(p + 4) / 2));
                if (name is "::$DATA" or ":WofCompressedData:$DATA") total += *(long*)(p + 16);
                var next = *(uint*)p;
                if (next == 0) break;
                off += (int)next;
            }
            return total;
        }
        long* info = stackalloc long[2]; // FILE_COMPRESSION_INFO starts with CompressedFileSize (what GetCompressedFileSizeW reports)
        return NativeMethods.GetFileInformationByHandleEx(h, 8, info, 16) ? info[0] : -1;
    }

    private static DateTime Utc(long fileTime) =>
        fileTime is >= 0 and <= 2650467743999999999 ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;

    private static long ClusterSize(string path)
    {
        var root = Path.GetPathRoot(path)!;
        return NativeMethods.GetDiskFreeSpace(root, out var spc, out var bps, out _, out _) ? (long)spc * bps : 4096;
    }

    // Win32 extended-length prefix: two backslashes + "?" + backslash. Built from chars to keep the literal unambiguous.
    private static readonly string Ext = new string((char)92, 2) + "?" + (char)92;

    // Win32 calls need the extended prefix past MAX_PATH (.NET adds it itself for its own APIs).
    private static string LongPath(string p) =>
        p.Length < 240 || p.StartsWith(Ext) ? p
        : p.StartsWith(new string((char)92, 2)) ? Ext + "UNC" + (char)92 + p[2..] : Ext + p;
}
