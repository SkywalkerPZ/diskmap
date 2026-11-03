using DiskMap.Core.Quarantine;
using DiskMap.Core.Rules;
using DiskMap.Core.Safety;
using DiskMap.Core.Settings;

namespace DiskMap.Core.Removal;

public sealed record RemovalResult(bool Success, long BytesFreed, long BytesQuarantined, long BytesSkipped, string? Error);

/// <summary>
/// The only code that deletes or moves user files. Every path it touches (the item, or each child when a rule's
/// anchor folder is being emptied) passes SafetyRails.CanRemove first. Reparse points are never followed.
/// </summary>
public sealed class RemovalService(AppSettings settings, UserContext user, string windowsDir, bool isElevated,
    IProcessRunner runner, IRecycleBin bin, Func<string, long> freeBytes, Func<string, QuarantineStore> storeFor)
{
    public static bool IsBulkEligible(Rule rule) =>
        rule.Method == RemovalMethod.Quarantine && rule.Label is SafetyLabel.Safe or SafetyLabel.Rebuildable;

    public async Task<RemovalResult> RemoveAsync(RuleMatch match, CancellationToken ct)
    {
        var rule = match.Rule;
        if (rule.NeedsAdmin && !isElevated) return Fail("Needs administrator");
        try
        {
            return rule.Method switch
            {
                RemovalMethod.Quarantine => await Task.Run(() => RemovePath(match, ct), ct),
                RemovalMethod.DismComponentCleanup => await RunTool("DISM", "/Online /Cleanup-Image /StartComponentCleanup /ResetBase", ct),
                RemovalMethod.PowercfgHibernateOff => await RunTool("powercfg", "/hibernate off", ct),
                _ => await Task.Run(EmptyBin, ct),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return Fail(e.Message); }
    }

    private static RemovalResult Fail(string error) => new(false, 0, 0, 0, error);

    private async Task<RemovalResult> RunTool(string exe, string args, CancellationToken ct)
    {
        var vol = Path.GetPathRoot(windowsDir)!;
        var before = freeBytes(vol);
        var o = await runner.RunAsync(exe, args, ct);
        var freed = Math.Max(0, freeBytes(vol) - before);
        if (o.ExitCode == 0) return new(true, freed, 0, 0, null);
        var line = o.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? $"{exe} exited with code {o.ExitCode}";
        return new(false, freed, 0, 0, line);
    }

    private RemovalResult EmptyBin()
    {
        var vol = Path.GetPathRoot(windowsDir)!;
        var before = freeBytes(vol);
        bin.Empty();
        return new(true, Math.Max(0, freeBytes(vol) - before), 0, 0, null);
    }

    private RemovalResult RemovePath(RuleMatch match, CancellationToken ct)
    {
        var rule = match.Rule;
        var path = Path.TrimEndingDirectorySeparator(match.Node.FullPath);
        var undo = settings.UndoDays > 0;
        var vol = Path.GetPathRoot(path)!;

        var items = new List<(string Path, long Size)>();
        if (IsAnchor(path, rule))
        {
            // the anchor folder stays (apps expect it); only its contents go
            foreach (var c in Directory.EnumerateFileSystemEntries(path))
                items.Add((c, match.Node.Children.FirstOrDefault(n => string.Equals(n.Name, Path.GetFileName(c), StringComparison.OrdinalIgnoreCase))?.Size ?? Measure(c)));
        }
        else
        {
            if (!SafetyRails.CanRemove(path, rule, user, windowsDir, out var why)) return Fail(why);
            items.Add((path, match.Node.Size));
        }

        var before = freeBytes(vol);
        long quarantined = 0, skipped = 0;
        var removedAny = false;
        string? firstReason = null;
        foreach (var (p, size) in items)
        {
            ct.ThrowIfCancellationRequested();
            if (!SafetyRails.CanRemove(p, rule, user, windowsDir, out var reason)) { firstReason ??= reason; skipped += size; continue; }
            if (undo)
            {
                var (q, s) = Quarantine(p, size, rule.Id, vol);
                quarantined += q; skipped += s;
                removedAny |= q > 0 || s == 0;
            }
            else
            {
                var s = DeleteTree(p);
                skipped += s;
                removedAny |= s == 0 || s < size;
            }
        }
        var freed = undo ? 0 : Math.Max(0, freeBytes(vol) - before);
        if (removedAny || skipped == 0) return new(true, freed, quarantined, skipped, null);
        return new(false, freed, quarantined, skipped, firstReason ?? "Nothing could be removed (files in use).");
    }

    private bool IsAnchor(string path, Rule rule)
    {
        if (!Directory.Exists(path)) return false;
        var p = SafetyRails.Segs(path);
        return rule.Paths.Any(t => SafetyRails.Anchor(t, user, windowsDir) is { } a && a.Length == p.Length && SafetyRails.IsPrefix(a, p));
    }

    // Whole item by rename first; if something is in use, move each file on its own and leave the locked ones.
    private (long Quarantined, long Skipped) Quarantine(string path, long size, string ruleId, string vol)
    {
        var store = storeFor(vol);
        try { store.Add(path, size, ruleId); return (size, 0); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.Directory) == 0 || (attrs & FileAttributes.ReparsePoint) != 0) return (0, size);
        long q = 0, s = 0;
        foreach (var f in Leaves(path))
        {
            var len = Measure(f);
            try { store.Add(f, len, ruleId); q += len; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { s += Math.Max(len, 1); }
        }
        PruneEmpty(path);
        return (q, s);
    }

    // Files and reparse points below dir; a reparse point is a leaf and is never entered.
    private static List<string> Leaves(string dir)
    {
        var list = new List<string>();
        foreach (var e in Directory.EnumerateFileSystemEntries(dir))
        {
            var a = File.GetAttributes(e);
            if ((a & FileAttributes.Directory) != 0 && (a & FileAttributes.ReparsePoint) == 0) list.AddRange(Leaves(e));
            else list.Add(e);
        }
        return list;
    }

    // Removes directories left empty; a directory still holding anything (skipped files) is kept.
    private static void PruneEmpty(string dir)
    {
        foreach (var e in Directory.EnumerateDirectories(dir))
            if ((File.GetAttributes(e) & FileAttributes.ReparsePoint) == 0) PruneEmpty(e);
        try { Directory.Delete(dir, false); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Permanent delete; returns the bytes that could not be deleted. A link is deleted as a link, never entered.</summary>
    private static long DeleteTree(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
            if ((a & FileAttributes.Directory) == 0) { File.Delete(path); return 0; }
            long skipped = 0;
            if ((a & FileAttributes.ReparsePoint) == 0)
                foreach (var c in Directory.EnumerateFileSystemEntries(path).ToList()) skipped += DeleteTree(c);
            if (skipped == 0) Directory.Delete(path, false);
            return skipped;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Math.Max(Measure(path), 1); }
    }

    // Logical bytes; reparse points are not followed.
    private static long Measure(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            if ((a & FileAttributes.Directory) == 0) return new FileInfo(path).Length;
            if ((a & FileAttributes.ReparsePoint) != 0) return 0;
            var opt = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            return new DirectoryInfo(path).EnumerateFiles("*", opt).Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
}
