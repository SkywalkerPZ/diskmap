using DiskMap.Core.Rules;

namespace DiskMap.Core.Safety;

/// <summary>The last gate before anything is removed: a path must be covered by the rule AND clear every rail. Fails closed.</summary>
public static class SafetyRails
{
    public static bool CanRemove(string fullPath, Rule rule, UserContext user, string windowsDir, out string reason)
    {
        try { reason = Check(fullPath, rule, user, windowsDir); }
        catch (Exception e) { reason = "Could not verify the path (" + e.GetType().Name + ")."; }
        return reason.Length == 0;
    }

    private static readonly char[] Seps = ['\\', '/'];

    /// <summary>Normalised path split into segments; a drive root is the single segment "C:".</summary>
    internal static string[] Segs(string path) =>
        LongForm(Path.GetFullPath(path)).Split(Seps, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Expands 8.3 short names so an 8.3 Temp still compares equal to the long scan path; unchanged if the path does not exist.</summary>
    internal static string LongForm(string full)
    {
        var sb = new System.Text.StringBuilder(1024);
        var n = GetLongPathNameW(full, sb, sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : full;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetLongPathNameW(string path, System.Text.StringBuilder buf, int size);

    /// <summary>Expanded anchor segments, or null when a token used by the template is empty (it would widen the anchor) or the result is unusable.</summary>
    internal static string[]? Anchor(string template, UserContext user, string windowsDir)
    {
        var tokens = new (string T, string V)[] { ("{UserProfile}", user.ProfilePath), ("{LocalAppData}", user.LocalAppData),
            ("{AppData}", user.AppData), ("{Temp}", user.Temp), ("{Downloads}", user.Downloads), ("{Windows}", windowsDir), ("{Sid}", user.Sid) };
        if (tokens.Any(t => template.Contains(t.T) && string.IsNullOrWhiteSpace(t.V))) return null;
        try
        {
            var e = user.Expand(template, windowsDir);
            return Path.IsPathFullyQualified(e) ? Segs(e) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>True when <paramref name="pattern"/> is a whole-segment prefix of (or equal to) <paramref name="path"/>; a lone <c>*</c> segment matches any one segment.</summary>
    internal static bool IsPrefix(string[] pattern, string[] path)
    {
        if (pattern.Length > path.Length) return false;
        for (var i = 0; i < pattern.Length; i++)
            if (pattern[i] != "*" && !string.Equals(pattern[i], path[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string Check(string fullPath, Rule rule, UserContext user, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !Path.IsPathFullyQualified(fullPath)) return "Not a full path.";
        var p = Segs(fullPath);
        if (p.Length < 2) return "Drive roots are never removable.";

        var profile = Segs(user.ProfilePath);
        var win = Segs(windowsDir);
        var downloads = new[] { Segs(user.Downloads), Segs(Path.Combine(user.ProfilePath, "Downloads")) };
        var protectedDirs = new List<string[]> { profile, Segs(user.AppData), Segs(user.LocalAppData) };
        protectedDirs.AddRange(downloads);
        foreach (var n in new[] { "Documents", "Desktop", "Pictures", "Videos", "Music" })
            protectedDirs.Add(Segs(Path.Combine(user.ProfilePath, n)));
        var programFiles = ProgramFiles(win).Select(Segs).ToList();

        if (IsPrefix(p, profile)) return "The profile folder, or a folder containing it.";
        if (protectedDirs.Any(d => IsPrefix(p, d))) return "A whole user folder.";
        if (programFiles.Any(d => IsPrefix(d, p))) return "Program Files is never removable.";
        if (IsPrefix(p, win)) return "The Windows folder itself.";
        // anything under the folder that holds the profiles must be inside OUR profile (never another user's)
        var profiles = profile[..^1];
        if (profiles.Length >= 2 && IsPrefix(profiles, p) && !IsPrefix(profile, p)) return "Inside another user's profile.";

        var from = Covered(p, rule, user, windowsDir, profile, win, protectedDirs, downloads, programFiles);
        if (from < 0) return "Not covered by rule '" + rule.Id + "'.";

        // never touch a junction/symlink on the way down from the rule's root
        for (var n = p.Length; n >= from; n--)
        {
            var cur = n == 1 ? p[0] + Path.DirectorySeparatorChar : string.Join(Path.DirectorySeparatorChar, p.Take(n));
            FileAttributes a;
            try { a = File.GetAttributes(cur); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((a & FileAttributes.ReparsePoint) != 0) return "Contains a junction or symbolic link.";
        }
        return "";
    }

    /// <summary>Segment count from which reparse checks start, or -1 when the rule does not cover the path.</summary>
    private static int Covered(string[] p, Rule rule, UserContext user, string windowsDir, string[] profile, string[] win,
        List<string[]> protectedDirs, string[][] downloads, List<string[]> programFiles)
    {
        var fileRule = rule.Extensions.Count > 0 || rule.MinAgeDays is not null;
        foreach (var t in rule.Paths)
        {
            var a = Anchor(t, user, windowsDir);
            if (a is null || a.Length < 2 || IsPrefix(a, win) || programFiles.Any(d => IsPrefix(a, d)) || IsPrefix(a, profile)) continue;
            // an anchor that is, or contains, a protected folder is too broad, except Downloads for a file-filtered rule
            if (protectedDirs.Any(d => IsPrefix(a, d)) && !(fileRule && downloads.Any(d => IsPrefix(a, d) && IsPrefix(d, a)))) continue;
            if (IsPrefix(win, a) && !t.StartsWith("{Windows}", StringComparison.Ordinal)) continue; // only {Windows} rules may reach in
            if (!IsPrefix(a, p)) continue;
            if (fileRule)
            {
                if (p.Length != a.Length + 1) continue; // direct children only, never the folder itself or nested files
                if (rule.Extensions.Count > 0 && !rule.Extensions.Contains(Path.GetExtension(p[^1]), StringComparer.OrdinalIgnoreCase)) continue;
            }
            if (fileRule && !FileOk(string.Join(Path.DirectorySeparatorChar, p), rule)) continue;
            return a.Length;
        }
        if (rule.FolderNames.Count == 0 || fileRule || !IsPrefix(profile, p) || p.Length == profile.Length) return -1;
        // installed software and tool configs live here; their node_modules/.gradle do not come back on a build
        if (IsPrefix(Segs(user.AppData), p) || IsPrefix(Segs(user.LocalAppData), p) || p[profile.Length].StartsWith('.')) return -1;
        for (var i = profile.Length; i < p.Length; i++)
        {
            if (!rule.FolderNames.Contains(p[i], StringComparer.OrdinalIgnoreCase)) continue;
            if (rule.RequireSibling.Count == 0 || HasSibling(string.Join(Path.DirectorySeparatorChar, p.Take(i)), rule.RequireSibling))
                return profile.Length + 1;
        }
        return -1;
    }

    private static bool FileOk(string path, Rule rule)
    {
        if (Directory.Exists(path)) return false;
        if (rule.MinAgeDays is not { } d) return true;
        // the age cannot be verified for a file that is not there: refuse
        return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(d);
    }

    private static bool HasSibling(string dir, IReadOnlyList<string> globs)
    {
        try { return globs.Any(g => Directory.EnumerateFiles(dir, g).Any()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> ProgramFiles(string[] win)
    {
        var drive = win[0] + Path.DirectorySeparatorChar;
        var names = new[] { "Program Files", "Program Files (x86)" };
        return names.Select(n => "C:" + Path.DirectorySeparatorChar + n).Concat(names.Select(n => Path.Combine(drive, n)))
            .Concat(new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" }.Select(Environment.GetEnvironmentVariable)
                .OfType<string>().Where(v => v.Length > 0 && Path.IsPathFullyQualified(v)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
