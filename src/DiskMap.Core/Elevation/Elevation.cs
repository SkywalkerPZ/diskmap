using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using DiskMap.Core.Rules;

namespace DiskMap.Core;

public static class Elevation
{
    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>One command-line string, each value quoted per Windows argv rules (see Quote).</summary>
    public static string BuildRelaunchArgs(UserContext user, string? drive)
    {
        var args = user.ToArgs().ToList();
        if (drive is not null) { args.Add("--drive"); args.Add(drive); }
        return string.Join(' ', args.Select(Quote));
    }

    /// <summary>Starts an elevated copy and exits this process. Returns false if the user declines UAC.</summary>
    public static bool RelaunchElevated(UserContext user, string? drive)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, BuildRelaunchArgs(user, drive))
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return false; } // ERROR_CANCELLED
        Environment.Exit(0);
        return true;
    }

    // Backslashes are literal unless they precede a quote, so double those (incl. trailing ones before the closing quote).
    private static string Quote(string s)
    {
        const char Bs = (char)92;
        var sb = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in s)
        {
            if (c == Bs) { slashes++; continue; }
            sb.Append(Bs, c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        return sb.Append(Bs, slashes * 2).Append('"').ToString();
    }
}
