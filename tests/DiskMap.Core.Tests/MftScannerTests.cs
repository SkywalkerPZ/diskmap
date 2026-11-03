using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using DiskMap.Core.Scanning;

namespace DiskMap.Core.Tests;

public sealed partial class MftScannerTests
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string link, string existing, nint reserved);

    private static bool IsElevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [Fact]
    public void Factory_PicksFolderWalkWhenNotElevated()
    {
        using var f = new Fixture();
        Assert.IsType<FolderWalkScanner>(ScannerFactory.For(f.Root, false));
    }

    [SkippableFact]
    public async Task Mft_MatchesFolderWalkTotals()
    {
        Skip.IfNot(IsElevated(), "MFT scan needs an elevated process");
        using var f = new Fixture();
        for (var i = 0; i < 200; i++) f.File($"d{i % 7}/f{i}.bin", i * 37);
        var a = f.File("real/big.bin", 1024 * 1024);
        Assert.True(CreateHardLink(Path.Combine(f.Root, "hard.bin"), a, 0));
        var psi = new ProcessStartInfo("cmd", $"/c mklink /J \"{Path.Combine(f.Root, "link")}\" \"{Path.Combine(f.Root, "real")}\"")
        { CreateNoWindow = true, RedirectStandardOutput = true };
        using (var p = Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }

        var walk = await new FolderWalkScanner().ScanAsync(f.Root, null, CancellationToken.None);
        var mft = await new MftScanner().ScanAsync(f.Root, null, CancellationToken.None);
        Assert.True(mft.UsedFastScan);
        Assert.Equal(walk.Root.Size, mft.Root.Size);
        Assert.Equal(walk.FileCount, mft.FileCount);
        Assert.Equal(walk.Root.FileCount, mft.Root.FileCount);
    }

    [SkippableFact]
    public async Task Mft_ThrowsNotSupportedWhenNotElevated()
    {
        Skip.If(IsElevated(), "only meaningful unelevated");
        using var f = new Fixture();
        await Assert.ThrowsAsync<NotSupportedException>(() => new MftScanner().ScanAsync(f.Root, null, CancellationToken.None));
    }

    // ---- Pure parser tests: FILE records built by hand following the NTFS on-disk layout. ----

    private const int RecSize = 1024;
    private const ushort Usn = 7;

    private static void W16(Span<byte> b, int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b[o..], (ushort)v);
    private static void W32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b[o..], v);
    private static void W64(Span<byte> b, int o, long v) => BinaryPrimitives.WriteInt64LittleEndian(b[o..], v);
    private static int Pad8(int n) => (n + 7) & ~7;

    /// <summary>A FILE record: header, USA at 0x30 (3 entries for 2 sectors), attributes from 0x38, fixups applied.</summary>
    private static byte[] Record(ushort flags, params byte[][] attrs) => RecordWithBase(flags, 0, attrs);

    private static byte[] RecordWithBase(ushort flags, long baseRef, params byte[][] attrs)
    {
        var r = new byte[RecSize];
        "FILE"u8.CopyTo(r);
        W16(r, 0x04, 0x30); W16(r, 0x06, 3);
        W16(r, 0x10, 1); W16(r, 0x12, 1);
        W16(r, 0x14, 0x38); W16(r, 0x16, flags);
        var off = 0x38;
        foreach (var a in attrs) { a.CopyTo(r, off); off += a.Length; }
        W32(r, off, 0xFFFFFFFF); off += 8;
        W32(r, 0x18, (uint)off); W32(r, 0x1C, RecSize); W64(r, 0x20, baseRef);
        W16(r, 0x30, Usn);
        for (var k = 1; k <= 2; k++)
        {
            var pos = k * 512 - 2;
            r[0x30 + 2 * k] = r[pos]; r[0x30 + 2 * k + 1] = r[pos + 1];
            W16(r, pos, Usn);
        }
        return r;
    }

    private static byte[] Resident(uint type, byte[] value, string name = "")
    {
        var valOff = Pad8(0x18 + name.Length * 2);
        var a = new byte[Pad8(valOff + value.Length)];
        W32(a, 0, type); W32(a, 4, (uint)a.Length);
        a[9] = (byte)name.Length; W16(a, 0x0A, 0x18);
        Encoding.Unicode.GetBytes(name).CopyTo(a, 0x18);
        W32(a, 0x10, (uint)value.Length); W16(a, 0x14, valOff);
        value.CopyTo(a, valOff);
        return a;
    }

    private static byte[] NonResident(uint type, long lowestVcn, long allocated, long dataSize, byte[] runs,
        ushort flags = 0, long compressed = 0, string name = "")
    {
        var hdr = (flags & 0x80FF) != 0 ? 0x48 : 0x40;
        var runsOff = Pad8(hdr + name.Length * 2);
        var a = new byte[Pad8(runsOff + runs.Length)];
        W32(a, 0, type); W32(a, 4, (uint)a.Length);
        a[8] = 1; a[9] = (byte)name.Length; W16(a, 0x0A, hdr); W16(a, 0x0C, flags);
        Encoding.Unicode.GetBytes(name).CopyTo(a, hdr);
        W64(a, 0x10, lowestVcn); W64(a, 0x18, lowestVcn);
        W16(a, 0x20, runsOff);
        W64(a, 0x28, allocated); W64(a, 0x30, dataSize); W64(a, 0x38, dataSize);
        if (hdr == 0x48) W64(a, 0x40, compressed);
        runs.CopyTo(a, runsOff);
        return a;
    }

    private static byte[] StdInfo(long lastWriteFt, uint attrs = 0x20)
    {
        var v = new byte[0x48];
        W64(v, 0x08, lastWriteFt); W32(v, 0x20, attrs);
        return Resident(0x10, v);
    }

    private static byte[] FileName(long parent, string name, byte ns = 1)
    {
        var v = new byte[0x42 + name.Length * 2];
        W64(v, 0, parent | (1L << 48)); // sequence number in the top 16 bits must be masked off
        v[0x40] = (byte)name.Length; v[0x41] = ns;
        Encoding.Unicode.GetBytes(name).CopyTo(v, 0x42);
        return Resident(0x30, v);
    }

    private static readonly byte[] SomeRuns = [0x11, 0x01, 0x05, 0x00];
    private static readonly long T = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc).ToFileTimeUtc();

    [Fact]
    public void Parse_AppliesFixupsAcrossSectorBoundary()
    {
        var name = new string('n', 199) + "Z"; // name bytes span offset 510-511, the first sector's fixup slot
        var r = Record(1, StdInfo(T), FileName(5, name));
        Assert.Equal(Usn, BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(510)));
        var p = MftRecordParser.Parse(r)!;
        Assert.Equal(name, Assert.Single(p.Names).Name);
        Assert.Equal(5UL, p.Names[0].Parent);
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), DateTime.FromFileTimeUtc(p.LastWriteFileTime));
        Assert.False(p.IsDirectory);
    }

    [Fact]
    public void Parse_RejectsTornRecordAndUnusedRecord()
    {
        var torn = Record(1, StdInfo(T), FileName(5, "a"));
        W16(torn, 1022, 0x1234);
        Assert.Null(MftRecordParser.Parse(torn));
        Assert.Null(MftRecordParser.Parse(Record(0, StdInfo(T), FileName(5, "a"))));
        Assert.Null(MftRecordParser.Parse(new byte[RecSize]));
    }

    [Fact]
    public void Parse_DirectoryFlagAndAttributes()
    {
        var p = MftRecordParser.Parse(Record(3, StdInfo(T, 0x410), FileName(5, "dir")))!;
        Assert.True(p.IsDirectory);
        Assert.Equal(0x410u, p.Attributes);
    }

    [Fact]
    public void Parse_ResidentDataRoundsUpToCluster()
    {
        var one = MftRecordParser.Parse(Record(1, StdInfo(T), FileName(5, "a"), Resident(0x80, [42])))!;
        Assert.Equal(4096, one.AllocatedSize(4096));
        var empty = MftRecordParser.Parse(Record(1, StdInfo(T), FileName(5, "b"), Resident(0x80, [])))!;
        Assert.Equal(0, empty.AllocatedSize(4096));
        var none = MftRecordParser.Parse(Record(1, StdInfo(T), FileName(5, "c")))!;
        Assert.Equal(0, none.AllocatedSize(4096));
    }

    [Fact]
    public void Parse_NonResidentUsesAllocatedOrCompressedSize()
    {
        var plain = MftRecordParser.Parse(Record(1, StdInfo(T), FileName(5, "a"), NonResident(0x80, 0, 12288, 9000, SomeRuns)))!;
        Assert.Equal(12288, plain.AllocatedSize(4096));
        var comp = MftRecordParser.Parse(Record(1, StdInfo(T), FileName(5, "b"), NonResident(0x80, 0, 65536, 60000, SomeRuns, flags: 1, compressed: 8192)))!;
        Assert.Equal(8192, comp.AllocatedSize(4096));
        var sparse = MftRecordParser.Parse(Record(1, StdInfo(T), FileName(5, "c"), NonResident(0x80, 0, 1 << 20, 1 << 20, SomeRuns, flags: 0x8000, compressed: 4096)))!;
        Assert.Equal(4096, sparse.AllocatedSize(4096));
    }

    [Fact]
    public void Parse_IgnoresNamedStreamsAndLaterSegments()
    {
        var p = MftRecordParser.Parse(Record(1, StdInfo(T), FileName(5, "a"),
            NonResident(0x80, 0, 1 << 20, 1 << 20, SomeRuns, name: "Zone.Identifier"),
            NonResident(0x80, 16, 1 << 20, 1 << 20, SomeRuns)))!;
        Assert.False(p.HasData);
        Assert.Equal(0, p.AllocatedSize(4096));
    }

    [Fact]
    public void Parse_HardlinksKeepWin32AndPosixNamesButSkipDosNames()
    {
        var p = MftRecordParser.Parse(Record(1, StdInfo(T),
            FileName(5, "LongName.txt", 1), FileName(5, "LONGNA~1.TXT", 2), FileName(40, "other.txt", 0), FileName(41, "both.txt", 3)))!;
        Assert.Equal([(5UL, "LongName.txt"), (40UL, "other.txt"), (41UL, "both.txt")], p.Names);
    }

    [Fact]
    public void Parse_WofCompressedFileCountsBackingStream()
    {
        // CompactOS layout: reparse attribute, sparse unnamed $DATA with nothing allocated, bytes in WofCompressedData.
        var p = MftRecordParser.Parse(Record(1, StdInfo(T, 0x420 | 0x200), FileName(5, "notepad.exe"),
            NonResident(0x80, 0, 1 << 20, 200000, SomeRuns, flags: 0x8000, compressed: 0),
            NonResident(0x80, 0, 81920, 80000, SomeRuns, name: "WofCompressedData")))!;
        Assert.Equal(81920, p.AllocatedSize(4096));
        var resident = MftRecordParser.Parse(Record(1, StdInfo(T, 0x420 | 0x200), FileName(5, "tiny.dll"),
            NonResident(0x80, 0, 65536, 3000, SomeRuns, flags: 0x8000, compressed: 0),
            Resident(0x80, new byte[300], "WofCompressedData")))!;
        Assert.Equal(4096, resident.AllocatedSize(4096));
    }

    [Fact]
    public void ParseRuns_HandlesRelativeNegativeAndSparseRuns()
    {
        byte[] runs = [0x11, 0x02, 0x0A, 0x11, 0x04, 0x1E, 0x11, 0x01, 0xF6, 0x01, 0x03, 0x21, 0x10, 0x00, 0x01, 0x00];
        Assert.Equal([(10L, 2L), (40L, 4L), (30L, 1L), (-1L, 3L), (286L, 16L)], MftRecordParser.ParseRuns(runs));
    }

    // ---- A synthetic volume: $MFT in two fragments, read through record 0's data runs. ----

    private const int Cluster = 4096;

    private static void Put(byte[] vol, int index, byte[] rec)
    {
        // Records 0-7 live at LCN 10-11, records 8-27 at LCN 40-44.
        var lcn = index < 8 ? 10 + index / 4 : 40 + (index - 8) / 4;
        rec.CopyTo(vol, lcn * Cluster + index % 4 * RecSize);
    }

    private static MftRecord?[] SyntheticMft()
    {
        var vol = new byte[64 * Cluster];
        Put(vol, 0, Record(1, StdInfo(T, 6), FileName(5, "$MFT"), NonResident(0x80, 0, 7 * Cluster, 28 * RecSize, [0x11, 0x02, 0x0A, 0x11, 0x05, 0x1E, 0x00])));
        Put(vol, 5, Record(3, StdInfo(T, 6), FileName(5, ".")));
        Put(vol, 11, Record(3, StdInfo(T, 6), FileName(5, "$Extend")));
        Put(vol, 16, Record(3, StdInfo(T), FileName(5, "a")));
        Put(vol, 17, Record(1, StdInfo(T), FileName(16, "x.bin"), Resident(0x80, [1])));
        Put(vol, 18, Record(3, StdInfo(T, 0x410), FileName(5, "link")));
        Put(vol, 19, Record(1, StdInfo(T), FileName(18, "inside.bin"), NonResident(0x80, 0, 1 << 20, 1 << 20, SomeRuns)));
        Put(vol, 20, Record(1, StdInfo(T), FileName(16, "big.bin"), NonResident(0x80, 0, 8192, 5000, SomeRuns)));
        Put(vol, 21, Record(1, StdInfo(T), FileName(5, "h1"), FileName(5, "H1~1", 2), FileName(16, "h2", 0), NonResident(0x80, 0, 4096, 100, SomeRuns)));
        Put(vol, 22, RecordWithBase(1, 23 | (1L << 48), NonResident(0x80, 0, 12288, 12000, SomeRuns)));
        Put(vol, 23, Record(1, StdInfo(T), FileName(5, "frag.bin"), Resident(0x20, new byte[32])));
        Put(vol, 24, Record(1, StdInfo(T, 0x400000), FileName(16, "cloud.bin"), NonResident(0x80, 0, 8192, 8000, SomeRuns)));
        Put(vol, 26, Record(0, StdInfo(T), FileName(5, "deleted.bin"), Resident(0x80, [1])));
        var torn = Record(1, StdInfo(T), FileName(5, "torn.bin"), Resident(0x80, [1]));
        W16(torn, 510, 0x4321);
        Put(vol, 27, torn);
        return MftScanner.ReadRecords((off, buf) => vol.AsSpan((int)off, buf.Length).CopyTo(buf), Cluster, RecSize, 10, null, CancellationToken.None);
    }

    [Fact]
    public void ReadRecords_FollowsFragmentedMftAndMergesExtensionRecords()
    {
        var recs = SyntheticMft();
        Assert.Equal(28, recs.Length);
        Assert.Equal("big.bin", recs[20]!.Names[0].Name); // only reachable through the second data run
        Assert.Null(recs[22]); // extension record folded into its base
        Assert.Equal(12288, recs[23]!.AllocatedSize(Cluster));
        Assert.Null(recs[25]); Assert.Null(recs[26]); Assert.Null(recs[27]);
    }

    [Fact]
    public void BuildTree_MatchesFolderWalkSemantics()
    {
        var root = MftScanner.BuildTree(SyntheticMft(), 5, "root", Cluster);
        Assert.Equal("root", root.Name);
        Assert.Equal(["a", "frag.bin", "h1", "link"], root.Children.Select(c => c.Name).Order(StringComparer.Ordinal));
        var link = root.Children.Single(c => c.Name == "link");
        Assert.True(link.IsReparsePoint); Assert.True(link.IsDirectory);
        Assert.Empty(link.Children); Assert.Equal(0, link.Size);
        // x 4096 + big 8192 + one hardlink 4096 + frag 12288; cloud counts 0
        Assert.Equal(28672, root.Size);
        Assert.Equal(6, root.FileCount); // x, big, h1, h2, frag, cloud
        var a = root.Children.Single(c => c.Name == "a");
        Assert.Same(root, a.Parent);
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), a.LastWriteUtc);
    }

    [Fact]
    public void BuildTree_SubtreeKeepsHardlinkSizeWhenOtherLinkIsOutside()
    {
        var a = MftScanner.BuildTree(SyntheticMft(), 16, "a", Cluster);
        Assert.Equal(4096 + 8192 + 4096, a.Size); // x, big, h2 (h1 is outside the subtree)
        Assert.Equal(4, a.FileCount);
        Assert.Null(a.Parent);
    }

    private static byte[] ReparseAttr(uint tag)
    {
        var v = new byte[8]; // tag, data length, reserved
        W32(v, 0, tag);
        return Resident(0xC0, v);
    }

    [Fact]
    public void Parse_ReadsReparseTag_OnlyNameSurrogatesAreLinks()
    {
        var cloud = MftRecordParser.Parse(Record(3, StdInfo(T, 0x410), FileName(5, "OneDrive"), ReparseAttr(0x9000601A)))!;
        Assert.Equal(0x9000601Au, cloud.ReparseTag);
        Assert.False(cloud.IsLink);
        var junction = MftRecordParser.Parse(Record(3, StdInfo(T, 0x410), FileName(5, "j"), ReparseAttr(0xA0000003)))!;
        Assert.Equal(0xA0000003u, junction.ReparseTag);
        Assert.True(junction.IsLink);
        var symlink = MftRecordParser.Parse(Record(3, StdInfo(T, 0x410), FileName(5, "s"), ReparseAttr(0xA000000C)))!;
        Assert.True(symlink.IsLink);
        var unknown = MftRecordParser.Parse(Record(3, StdInfo(T, 0x410), FileName(5, "u")))!; // tag not readable here: treated as a link
        Assert.True(unknown.IsLink);
        var plain = MftRecordParser.Parse(Record(3, StdInfo(T, 0x10), FileName(5, "d")))!;
        Assert.False(plain.IsLink);
    }

    [Fact]
    public void BuildTree_DescendsCloudFolder_ButNotJunction()
    {
        var recs = new MftRecord?[20];
        recs[5] = MftRecordParser.Parse(Record(3, StdInfo(T, 6), FileName(5, ".")));
        recs[16] = MftRecordParser.Parse(Record(3, StdInfo(T, 0x410), FileName(5, "OneDrive"), ReparseAttr(0x9000601A)));
        recs[17] = MftRecordParser.Parse(Record(1, StdInfo(T), FileName(16, "doc.bin"), NonResident(0x80, 0, 8192, 8000, SomeRuns)));
        recs[18] = MftRecordParser.Parse(Record(3, StdInfo(T, 0x410), FileName(5, "j"), ReparseAttr(0xA0000003)));
        recs[19] = MftRecordParser.Parse(Record(1, StdInfo(T), FileName(18, "elsewhere.bin"), NonResident(0x80, 0, 4096, 4000, SomeRuns)));
        var root = MftScanner.BuildTree(recs, 5, "root", Cluster);
        var od = root.Children.Single(c => c.Name == "OneDrive");
        Assert.True(od.IsReparsePoint); // still never matched by rules
        Assert.Equal("doc.bin", Assert.Single(od.Children).Name);
        Assert.Empty(root.Children.Single(c => c.Name == "j").Children);
        Assert.Equal(8192, root.Size);
    }
}
