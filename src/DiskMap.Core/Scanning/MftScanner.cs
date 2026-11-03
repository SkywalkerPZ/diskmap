using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskMap.Core.Scanning;

/// <summary>
/// Reads the NTFS master file table straight from the volume (needs elevation), the way WizTree does.
/// Any failure surfaces as NotSupportedException so callers can rerun with FolderWalkScanner.
/// </summary>
public sealed class MftScanner : IScanner
{
    internal delegate void VolumeReader(long offset, Span<byte> buffer);

    private const int RootDirectoryRecord = 5;
    private const int FirstUserRecord = 16;   // 0-15 are NTFS metafiles ($MFT, $Bitmap, $Extend...), hidden from directory listings
    private const uint ReparsePoint = 0x400;
    private const uint CloudOnly = 0x00040000 | 0x00400000 | 0x1000; // recall-on-open, recall-on-data-access, offline
    private const int ChunkBytes = 4 << 20;

    public Task<ScanResult> ScanAsync(string rootPath, IProgress<long>? filesSeen, CancellationToken ct) =>
        Task.Run(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var full = Path.GetFullPath(rootPath);
                var drive = Path.GetPathRoot(full);
                if (drive is not { Length: 3 } || drive[1] != ':') throw new NotSupportedException("MFT scan needs a drive-letter path.");
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new NotSupportedException("Scan root is a reparse point.");
                var (rootSerial, rootRecord) = FileId(full);

                var bs = (char)92;
                using var vol = NativeMethods.CreateFile($"{bs}{bs}.{bs}{drive[..2]}", NativeMethods.GenericRead, NativeMethods.FileShareReadWrite, 0, NativeMethods.OpenExisting, 0, 0);
                if (vol.IsInvalid) throw new NotSupportedException($"Cannot open volume {drive} (error {Marshal.GetLastPInvokeError()}).");
                var data = VolumeData(vol);
                if ((uint)BinaryPrimitives.ReadUInt64LittleEndian(data) != rootSerial) throw new NotSupportedException("Scan root resolves to another volume.");
                long cluster = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44));
                var recordSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48));
                var mftLcn = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(64));

                var records = ReadRecords((off, buf) =>
                {
                    if (RandomAccess.Read(vol, buf, off) != buf.Length) throw new IOException("Short read from volume.");
                }, cluster, recordSize, mftLcn, filesSeen, ct);
                var root = BuildTree(records, rootRecord, full, cluster);
                filesSeen?.Report(root.FileCount);
                return new ScanResult(root, root.FileCount, sw.Elapsed, true);
            }
            catch (Exception e) when (e is not (OperationCanceledException or NotSupportedException))
            {
                throw new NotSupportedException("Fast MFT scan failed: " + e.Message, e);
            }
        }, ct);

    private static (uint Serial, ulong Record) FileId(string path)
    {
        using var h = NativeMethods.CreateFile(path, 0, NativeMethods.FileShareAll, 0, NativeMethods.OpenExisting, NativeMethods.FileFlagBackupSemantics, 0);
        if (h.IsInvalid || !NativeMethods.GetFileInformationByHandle(h, out var info))
            throw new NotSupportedException($"Cannot open scan root (error {Marshal.GetLastPInvokeError()}).");
        return (info.VolumeSerialNumber, (((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) & 0x0000FFFFFFFFFFFF);
    }

    private static unsafe byte[] VolumeData(Microsoft.Win32.SafeHandles.SafeFileHandle vol)
    {
        var buf = new byte[128]; // NTFS_VOLUME_DATA_BUFFER is 96 bytes
        fixed (byte* p = buf)
            if (!NativeMethods.DeviceIoControl(vol, NativeMethods.FsctlGetNtfsVolumeData, null, 0, p, (uint)buf.Length, out _, 0))
                throw new NotSupportedException($"Not an NTFS volume (error {Marshal.GetLastPInvokeError()}).");
        return buf;
    }

    /// <summary>
    /// Reads every FILE record by following $MFT's own data runs (record 0), indexed by record number.
    /// Extension records (overflow from $ATTRIBUTE_LIST) are folded into their base record, so the list itself is never parsed.
    /// </summary>
    internal static MftRecord?[] ReadRecords(VolumeReader read, long cluster, int recordSize, long mftStartLcn, IProgress<long>? progress, CancellationToken ct)
    {
        // shortcut: needs whole records per cluster (true for every cluster size >= 1 KB); smaller clusters fall back to the folder walk.
        if (recordSize < 1024 || cluster < recordSize || cluster % recordSize != 0) throw new NotSupportedException("Unsupported cluster/record size.");
        var chunk = new byte[Math.Max(ChunkBytes / cluster, 1) * cluster];
        read(mftStartLcn * cluster, chunk.AsSpan(0, (int)cluster));
        var mft = MftRecordParser.Parse(chunk.AsSpan(0, recordSize), withRuns: true);
        if (mft?.DataRuns is not { } runs || mft.DataResident) throw new NotSupportedException("$MFT record 0 is unreadable.");
        // shortcut: a very fragmented $MFT keeps later runs in an extension record; detect it and fall back.
        if (runs.Sum(r => r.Length) * cluster < mft.DataAllocated) throw new NotSupportedException("$MFT data runs continue in an extension record.");
        var count = mft.DataSize / recordSize;
        if (count > Array.MaxLength) throw new NotSupportedException("$MFT too large.");

        var records = new MftRecord?[count];
        long index = 0;
        foreach (var (lcn, length) in runs)
        {
            for (long pos = 0, bytes = length * cluster; pos < bytes && index < count; pos += chunk.Length)
            {
                ct.ThrowIfCancellationRequested();
                var span = chunk.AsSpan(0, (int)Math.Min(chunk.Length, bytes - pos));
                if (lcn < 0) span.Clear(); else read(lcn * cluster + pos, span);
                for (var o = 0; o + recordSize <= span.Length && index < count; o += recordSize, index++)
                    records[index] = MftRecordParser.Parse(span.Slice(o, recordSize));
                progress?.Report(index);
            }
        }

        for (var i = 0; i < records.Length; i++)
        {
            if (records[i] is not { BaseRecord: not 0 } ext) continue;
            records[i] = null;
            if (ext.BaseRecord < (ulong)records.Length && records[ext.BaseRecord] is { BaseRecord: 0 } b) b.Merge(ext);
        }
        return records;
    }

    /// <summary>Builds the subtree under rootRecord with FolderWalkScanner's rules (see its FileSize/Walk).</summary>
    internal static FileNode BuildTree(MftRecord?[] records, ulong rootRecord, string rootName, long cluster)
    {
        if (rootRecord >= (ulong)records.Length || records[rootRecord] is not { IsDirectory: true } rootRec)
            throw new NotSupportedException("Scan root not found in the MFT.");
        var kids = new List<(int Child, string Name)>?[records.Length];
        for (var i = FirstUserRecord; i < records.Length; i++)
            if (records[i] is { } r)
                foreach (var (parent, name) in r.Names)
                    if (parent < (ulong)records.Length && parent != (ulong)i) (kids[parent] ??= new()).Add((i, name));

        var root = new FileNode { Name = rootName, IsDirectory = true, LastWriteUtc = Utc(rootRec.LastWriteFileTime) };
        Fill(root, (int)rootRecord, records, kids, new bool[records.Length], new HashSet<int>(), cluster);
        return root;
    }

    private static void Fill(FileNode dir, int rec, MftRecord?[] records, List<(int Child, string Name)>?[] kids, bool[] visited, HashSet<int> sized, long cluster)
    {
        if (visited[rec]) return; // guards against parent loops in a corrupt MFT
        visited[rec] = true;
        if (kids[rec] is not { } list) return;
        foreach (var (child, name) in list)
        {
            var r = records[child]!;
            var reparse = (r.Attributes & ReparsePoint) != 0;
            var node = new FileNode { Name = name, Parent = dir, IsDirectory = r.IsDirectory, IsReparsePoint = reparse, LastWriteUtc = Utc(r.LastWriteFileTime) };
            if (r.IsDirectory)
            {
                if (!r.IsLink) Fill(node, child, records, kids, visited, sized, cluster); // never follow junctions or symlinks; cloud-sync folders are real
            }
            else
            {
                node.FileCount = 1;
                // Hardlinks: the first name seen inside this subtree carries the size.
                node.Size = (r.Attributes & CloudOnly) != 0 || !sized.Add(child) ? 0 : r.AllocatedSize(cluster);
            }
            dir.Children.Add(node);
            dir.Size += node.Size;
            dir.FileCount += node.FileCount;
        }
    }

    private static DateTime Utc(long fileTime) =>
        fileTime is >= 0 and <= 2650467743999999999 ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;
}
