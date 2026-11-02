using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DiskMap.Core.Rules;

/// <summary>The per-user folders rule templates expand against; passed to the elevated helper as arguments.</summary>
public sealed record UserContext(string Sid, string ProfilePath, string LocalAppData, string AppData, string Temp, string Downloads)
{
    public static UserContext Current()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new UserContext(
            WindowsIdentity.GetCurrent().User!.Value,
            profile,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
            KnownDownloads() ?? Path.Combine(profile, "Downloads"));
    }

    public string[] ToArgs() =>
    [
        "--user-sid", Sid, "--user-profile", ProfilePath, "--user-localappdata", LocalAppData,
        "--user-appdata", AppData, "--user-temp", Temp, "--user-downloads", Downloads,
    ];

    public static UserContext? FromArgs(string[] args)
    {
        string? Get(string key)
        {
            var i = Array.IndexOf(args, key);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var v = new[] { "--user-sid", "--user-profile", "--user-localappdata", "--user-appdata", "--user-temp", "--user-downloads" }
            .Select(Get).ToArray();
        return v.Any(s => s is null) ? null : new UserContext(v[0]!, v[1]!, v[2]!, v[3]!, v[4]!, v[5]!);
    }

    /// <summary>Replaces the tokens only; <c>*</c> is left for the matcher.</summary>
    public string Expand(string template, string windowsDir) => template
        .Replace("{UserProfile}", ProfilePath).Replace("{LocalAppData}", LocalAppData)
        .Replace("{AppData}", AppData).Replace("{Temp}", Temp).Replace("{Downloads}", Downloads)
        .Replace("{Windows}", windowsDir).Replace("{Sid}", Sid);

    private static string? KnownDownloads()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
        }
        catch { return null; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
