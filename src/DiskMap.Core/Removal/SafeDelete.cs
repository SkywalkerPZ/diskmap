using System.ComponentModel;
using System.Runtime.InteropServices;
using DiskMap.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core.Removal;

/// <summary>
/// Deletes a file or a folder tree through handles only. The top item is opened with FILE_FLAG_OPEN_REPARSE_POINT;
/// every child is opened relative to its parent's handle (never by path) with FILE_OPEN_REPARSE_POINT, and its type
/// is checked on that handle. A junction or symlink, even one swapped in mid-delete, is deleted as a link and never
/// entered. A failure leaves that item in place; the rest is still deleted and the first error is thrown at the end.
/// </summary>
public static unsafe partial class SafeDelete
{
    private const uint ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000;
    private const uint FileOpen = 1, OptDirectory = 0x1, OptSynchronous = 0x20, OptBackupIntent = 0x4000, OptOpenReparsePoint = 0x00200000;
    private const int FileAttributeTagInfo = 9, FileDispositionInfo = 4, FileDispositionInfoEx = 21;
    private const uint DispDelete = 1, DispPosix = 2, DispIgnoreReadOnly = 0x10;

    /// <summary>Throws FileNotFoundException/DirectoryNotFoundException if the item is already gone, IOException (HResult = the Win32 error) otherwise.</summary>
    public static void DeleteTree(string path)
    {
        var full = Path.GetFullPath(path);
        var attrs = File.GetAttributes(full); // throws when it is gone
        var dir = (attrs & FileAttributes.Directory) != 0 && (attrs & FileAttributes.ReparsePoint) == 0;
        using var h = NativeMethods.CreateFile(LongPath(full), Access(dir), ShareAll, 0, OpenExisting, BackupSemantics | NativeMethods.FileFlagOpenReparsePoint, 0);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err is 2 or 3) throw new FileNotFoundException("Already gone.", full);
            throw Error(err, full);
        }
        Exception? first = null;
        Delete(h, full, ref first);
        if (first is not null) throw first;
    }

    private static uint Access(bool dir) =>
        NativeMethods.Delete | NativeMethods.FileReadAttributes | NativeMethods.Synchronize | (dir ? NativeMethods.FileListDirectory : 0);

    private static void Delete(SafeFileHandle h, string display, ref Exception? first)
    {
        uint* info = stackalloc uint[2]; // FILE_ATTRIBUTE_TAG_INFO: attributes, reparse tag
        if (!NativeMethods.GetFileInformationByHandleEx(h, FileAttributeTagInfo, info, 8)) { first ??= Error(Marshal.GetLastPInvokeError(), display); return; }
        var isDir = (info[0] & 0x10) != 0 && (info[0] & 0x400) == 0; // decided on the handle, not on an earlier look at the path
        if (isDir)
        {
            var kids = new List<NativeMethods.DirEntry>();
            var err = NativeMethods.ReadDirectory(h, kids);
            if (err != 0) first ??= Error(err, display);
            foreach (var k in kids)
            {
                var childDir = k.IsDirectory && !k.IsReparsePoint;
                var st = OpenRelative(h, k.Name, childDir, out var ch);
                if (st != 0)
                {
                    var code = RtlNtStatusToDosError(st);
                    if (code is not (2 or 3)) first ??= Error(code, Path.Join(display, k.Name)); // vanished meanwhile: fine
                    continue;
                }
                using (ch) Delete(ch, Path.Join(display, k.Name), ref first);
            }
        }
        if (!MarkDeleted(h, out var e) && e is not (2 or 3)) first ??= Error(e, display);
    }

    private static bool MarkDeleted(SafeFileHandle h, out int err)
    {
        var flags = DispDelete | DispPosix | DispIgnoreReadOnly;
        if (NativeMethods.SetFileInformationByHandle(h, FileDispositionInfoEx, &flags, 4)) { err = 0; return true; }
        err = Marshal.GetLastPInvokeError();
        if (err is not (1 or 50 or 87)) return false;
        byte del = 1; // file systems without the extended form (FAT): classic delete-on-close
        if (NativeMethods.SetFileInformationByHandle(h, FileDispositionInfo, &del, 1)) { err = 0; return true; }
        err = Marshal.GetLastPInvokeError();
        return false;
    }

    private static int OpenRelative(SafeFileHandle parent, string name, bool dir, out SafeFileHandle handle)
    {
        fixed (char* n = name)
        {
            var us = new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = n };
            var added = false;
            try
            {
                parent.DangerousAddRef(ref added);
                var oa = new ObjectAttributes
                {
                    Length = sizeof(ObjectAttributes), RootDirectory = parent.DangerousGetHandle(), ObjectName = &us, Attributes = 0x40, // OBJ_CASE_INSENSITIVE
                };
                var opts = OptSynchronous | OptBackupIntent | OptOpenReparsePoint | (dir ? OptDirectory : 0);
                var st = NtCreateFile(out var raw, Access(dir), &oa, out _, 0, 0, ShareAll, FileOpen, opts, 0, 0);
                handle = new SafeFileHandle(st == 0 ? raw : 0, true);
                return st;
            }
            finally { if (added) parent.DangerousRelease(); }
        }
    }

    private static IOException Error(int code, string path) =>
        new($"Could not delete '{path}': {new Win32Exception(code).Message}", unchecked((int)0x80070000) | (code & 0xFFFF));

    // Win32 extended-length prefix: two backslashes + "?" + backslash, built from chars to keep the literal unambiguous.
    private static readonly string Ext = new string((char)92, 2) + "?" + (char)92;

    private static string LongPath(string p) =>
        p.Length < 240 || p.StartsWith(Ext) ? p
        : p.StartsWith(new string((char)92, 2)) ? Ext + "UNC" + (char)92 + p[2..] : Ext + p;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public char* Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public nint Status, Information; }

    [LibraryImport("ntdll.dll")]
    private static partial int NtCreateFile(out nint handle, uint access, ObjectAttributes* oa, out IoStatusBlock iosb, nint allocationSize,
        uint fileAttributes, uint share, uint disposition, uint options, nint eaBuffer, uint eaLength);

    [LibraryImport("ntdll.dll")]
    private static partial int RtlNtStatusToDosError(int status);
}
