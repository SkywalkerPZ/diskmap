using System.Buffers.Binary;
using System.Text;

namespace DiskMap.Core.Scanning;

/// <summary>What the scanner needs from one MFT FILE record.</summary>
internal sealed class MftRecord
{
    public ulong BaseRecord;            // non-zero for an extension record
    public bool IsDirectory;
    public uint Attributes;             // from $STANDARD_INFORMATION
    public long LastWriteFileTime;
    public List<(ulong Parent, string Name)> Names = new(); // one per hardlink; DOS-only names skipped
    public bool HasData;                // unnamed $DATA, first segment (lowest VCN 0)
    public bool DataResident;
    public long DataSize, DataAllocated;
    public List<(long Lcn, long Length)>? DataRuns; // only when parsed withRuns

    /// <summary>Same rule as FolderWalkScanner: 0 for empty, resident data rounds up to a cluster.</summary>
    public long AllocatedSize(long cluster) =>
        !HasData || DataSize == 0 ? 0
        : DataResident ? (DataSize + cluster - 1) / cluster * cluster
        : DataAllocated;

    public void Merge(MftRecord ext)
    {
        Names.AddRange(ext.Names);
        if (HasData || !ext.HasData) return;
        HasData = true; DataResident = ext.DataResident; DataSize = ext.DataSize; DataAllocated = ext.DataAllocated; DataRuns = ext.DataRuns;
    }
}

/// <summary>Parses NTFS FILE records (layout per the NTFS documentation / ntfs-3g layout.h).</summary>
internal static class MftRecordParser
{
    private const ulong RefMask = 0x0000FFFFFFFFFFFF; // file reference: 48-bit record number + 16-bit sequence
    private const int SectorStride = 512;            // update-sequence stride is 512 regardless of sector size

    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static long I64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt64LittleEndian(b[o..]);

    /// <summary>Applies fixups in place; returns null for unused, torn or malformed records.</summary>
    public static MftRecord? Parse(Span<byte> rec, bool withRuns = false)
    {
        if (rec.Length < 0x30 || !rec[..4].SequenceEqual("FILE"u8) || !ApplyFixups(rec)) return null;
        var flags = U16(rec, 0x16);
        if ((flags & 1) == 0) return null; // not in use
        var r = new MftRecord { BaseRecord = (ulong)I64(rec, 0x20) & RefMask, IsDirectory = (flags & 2) != 0 };
        try
        {
            int off = U16(rec, 0x14), end = (int)Math.Min(U32(rec, 0x18), (uint)rec.Length);
            while (off + 16 <= end)
            {
                var type = U32(rec, off);
                if (type == 0xFFFFFFFF) break;
                var len = (int)U32(rec, off + 4);
                if (len < 16 || len > end - off) return null;
                ReadOnlySpan<byte> a = rec.Slice(off, len);
                bool nonResident = a[8] != 0, unnamed = a[9] == 0;
                switch (type)
                {
                    case 0x10 when !nonResident: // $STANDARD_INFORMATION
                    {
                        var v = Value(a);
                        r.LastWriteFileTime = I64(v, 0x08);
                        r.Attributes = U32(v, 0x20);
                        break;
                    }
                    case 0x30 when !nonResident: // $FILE_NAME
                    {
                        var v = Value(a);
                        if (v[0x41] != 2) // namespace 2 = DOS 8.3 alias of a Win32 name
                            r.Names.Add(((ulong)I64(v, 0) & RefMask, Encoding.Unicode.GetString(v.Slice(0x42, v[0x40] * 2))));
                        break;
                    }
                    case 0x80 when unnamed && !r.HasData: // unnamed $DATA; named streams (ADS) are ignored like GetCompressedFileSize does
                        if (!nonResident)
                        {
                            r.HasData = true; r.DataResident = true; r.DataSize = U32(a, 0x10);
                        }
                        else if (I64(a, 0x10) == 0) // sizes are only valid in the segment starting at VCN 0
                        {
                            var compressedOrSparse = (U16(a, 0x0C) & 0x80FF) != 0;
                            r.HasData = true;
                            r.DataAllocated = compressedOrSparse ? I64(a, 0x40) : I64(a, 0x28);
                            r.DataSize = I64(a, 0x30);
                            if (withRuns) r.DataRuns = ParseRuns(a[U16(a, 0x20)..]);
                        }
                        break;
                }
                off += len;
            }
        }
        catch (ArgumentOutOfRangeException) { return null; } // field points outside the record
        catch (InvalidDataException) { return null; }
        return r;
    }

    private static ReadOnlySpan<byte> Value(ReadOnlySpan<byte> a) => a.Slice(U16(a, 0x14), (int)U32(a, 0x10));

    private static bool ApplyFixups(Span<byte> rec)
    {
        int usa = U16(rec, 0x04), count = U16(rec, 0x06);
        if (count < 2 || usa + count * 2 > rec.Length || (count - 1) * SectorStride > rec.Length) return false;
        var usn = U16(rec, usa);
        for (var k = 1; k < count; k++)
        {
            var pos = k * SectorStride - 2;
            if (U16(rec, pos) != usn) return false; // torn write
            rec[pos] = rec[usa + 2 * k];
            rec[pos + 1] = rec[usa + 2 * k + 1];
        }
        return true;
    }

    /// <summary>Decodes a mapping-pairs array. Sparse runs get Lcn -1.</summary>
    public static List<(long Lcn, long Length)> ParseRuns(ReadOnlySpan<byte> s)
    {
        var runs = new List<(long, long)>();
        long lcn = 0;
        var i = 0;
        while (i < s.Length && s[i] != 0)
        {
            int lenSize = s[i] & 0xF, offSize = s[i] >> 4;
            i++;
            if (lenSize is 0 or > 8 || offSize > 8 || i + lenSize + offSize > s.Length) throw new InvalidDataException("Bad data run.");
            var len = Var(s.Slice(i, lenSize), false);
            i += lenSize;
            if (offSize == 0) { runs.Add((-1, len)); continue; }
            lcn += Var(s.Slice(i, offSize), true);
            i += offSize;
            runs.Add((lcn, len));
        }
        return runs;
    }

    private static long Var(ReadOnlySpan<byte> b, bool signed)
    {
        long v = 0;
        for (var k = b.Length - 1; k >= 0; k--) v = (v << 8) | b[k];
        if (signed && b.Length < 8 && (b[^1] & 0x80) != 0) v -= 1L << (8 * b.Length);
        return v;
    }
}
