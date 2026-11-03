using System.Runtime.InteropServices;

namespace DiskMap.Core.Removal;

public interface IRecycleBin
{
    long SizeBytes();
    void Empty();
}

/// <summary>All drives (null root), no prompts, no progress UI, no sound.</summary>
public sealed class ShellRecycleBin : IRecycleBin
{
    private const uint NoConfirmation = 1, NoProgressUi = 2, NoSound = 4;

    // natural alignment: shellapi.h only packs this struct on 32-bit (24 bytes on x64)
    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryInfo { public uint Size; public long Bytes; public long Items; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? root, ref QueryInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? root, uint flags);

    public long SizeBytes()
    {
        var info = new QueryInfo { Size = (uint)Marshal.SizeOf<QueryInfo>() };
        Marshal.ThrowExceptionForHR(SHQueryRecycleBinW(null, ref info));
        return info.Bytes;
    }

    public void Empty()
    {
        if (SizeBytes() == 0) return; // SHEmptyRecycleBin reports an error for an already-empty bin
        Marshal.ThrowExceptionForHR(SHEmptyRecycleBinW(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound));
    }
}
