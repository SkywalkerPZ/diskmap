using System.Runtime.InteropServices;

using DiskMap.Core.Rules;

namespace DiskMap.Core.Tests;

public class ElevationTests
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string cmd, out int argc);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr p);

    private static string[] SplitLikeWindows(string args)
    {
        var p = CommandLineToArgvW("app.exe " + args, out var n);
        try { return Enumerable.Range(1, n - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(p); }
    }

    [Fact]
    public void RelaunchArgs_CarryUserAndDrive()
    {
        var ctx = new UserContext("S-1-5-21-1", @"C:\Users\Jo Doe", @"C:\Users\Jo Doe\AppData\Local",
            @"C:\Users\Jo Doe\AppData\Roaming", @"C:\Temp\x y\", @"D:\");
        var argv = SplitLikeWindows(Elevation.BuildRelaunchArgs(ctx, @"D:\"));

        Assert.Equal(ctx, UserContext.FromArgs(argv));
        var i = Array.IndexOf(argv, "--drive");
        Assert.Equal(@"D:\", argv[i + 1]);
    }

    [Fact]
    public void RelaunchArgs_OmitDriveWhenNull() =>
        Assert.DoesNotContain("--drive", Elevation.BuildRelaunchArgs(UserContext.Current(), null));
}
