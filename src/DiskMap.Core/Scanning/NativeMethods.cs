using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core.Scanning;

internal static partial class NativeMethods
{
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileShareAll = 7; // read | write | delete
    public const uint OpenExisting = 3;
    public const uint GenericRead = 0x80000000;
    public const uint FileShareReadWrite = 3;
    public const uint FsctlGetNtfsVolumeData = 0x00090064;

    [StructLayout(LayoutKind.Sequential)]
    public struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint GetCompressedFileSize(string name, out uint high);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, uint inSize, void* outBuf, uint outSize, out uint returned, nint overlapped);

    public const uint FileListDirectory = 0x0001;
    public const uint FileReadAttributes = 0x0080;
    public const uint Delete = 0x00010000;
    public const uint Synchronize = 0x00100000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;
    public const uint NameSurrogate = 0x20000000;   // reparse tag bit: junctions, symlinks, mount points
    public const uint TagWof = 0x80000017;

    /// <summary>True when a reparse tag makes the item stand in for another name (a link): never follow it.</summary>
    public static bool IsNameSurrogate(uint tag) => (tag & NameSurrogate) != 0;

    /// <summary>One directory entry read straight from the directory handle (no per-file open).</summary>
    public readonly record struct DirEntry(string Name, uint Attributes, uint ReparseTag, long EndOfFile, long AllocationSize, long FileId, long LastWriteFileTime)
    {
        public bool IsDirectory => (Attributes & 0x10) != 0;
        public bool IsReparsePoint => (Attributes & 0x400) != 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, void* buf, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, void* buf, uint size);

    private const int FileIdBothDirectoryInfo = 10, FileFullDirectoryInfo = 14;

    /// <summary>
    /// Reads every entry of a directory opened with FILE_LIST_DIRECTORY (skipping "." and ".."). Returns 0, or the
    /// Win32 error that stopped the read (entries read before it are kept). FileId is 0 where the file system has none.
    /// For a reparse point the EaSize slot of the entry carries its reparse tag.
    /// </summary>
    public static unsafe int ReadDirectory(SafeFileHandle dir, List<DirEntry> into)
    {
        var buf = new byte[64 * 1024];
        var cls = FileIdBothDirectoryInfo;
        fixed (byte* b = buf)
        {
            while (true)
            {
                if (!GetFileInformationByHandleEx(dir, cls, b, (uint)buf.Length))
                {
                    var err = Marshal.GetLastPInvokeError();
                    if (err == 18) return 0; // ERROR_NO_MORE_FILES
                    if (cls == FileIdBothDirectoryInfo && into.Count == 0 && err is 1 or 50 or 87) { cls = FileFullDirectoryInfo; continue; } // no file ids here
                    return err;
                }
                var off = 0;
                while (true)
                {
                    var e = b + off;
                    var attrs = *(uint*)(e + 56);
                    var nameOff = cls == FileIdBothDirectoryInfo ? 104 : 68;
                    var name = new string((char*)(e + nameOff), 0, (int)(*(uint*)(e + 60) / 2));
                    if (name is not ("." or ".."))
                        into.Add(new DirEntry(name, attrs, (attrs & 0x400) != 0 ? *(uint*)(e + 64) : 0, *(long*)(e + 40), *(long*)(e + 48),
                            cls == FileIdBothDirectoryInfo ? *(long*)(e + 96) : 0, *(long*)(e + 24)));
                    var next = *(uint*)e;
                    if (next == 0) break;
                    off += (int)next;
                }
            }
        }
    }
}
