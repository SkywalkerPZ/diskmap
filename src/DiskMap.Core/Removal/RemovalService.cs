using System.Security.AccessControl;
using System.Security.Principal;
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
        // An administrator's token acting in a standard user's folders: that user could swap a parent folder for a junction mid-removal.
        if (rule.IsPerUser && !IsLaunchingAccount(user)) return Fail("This item belongs to the account that started DiskMap.");
        try
        {
            return rule.Method switch
            {
                RemovalMethod.Quarantine => await Task.Run(() => RemovePath(match, ct), ct),
                RemovalMethod.DismComponentCleanup => await RunTool(SystemTool("dism.exe"), "/Online /Cleanup-Image /StartComponentCleanup /ResetBase", ct),
                RemovalMethod.PowercfgHibernateOff => await RunTool(SystemTool("powercfg.exe"), "/hibernate off", ct),
                RemovalMethod.EmptyRecycleBin => await Task.Run(EmptyBin, ct),
                _ => throw new InvalidOperationException("Unknown removal method " + rule.Method),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return Fail(e.Message); }
    }

    private static RemovalResult Fail(string error) => new(false, 0, 0, 0, error);

    /// <summary>System tools by full path, never found through the search path.</summary>
    public static string SystemTool(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    /// <summary>
    /// False when an administrator elevated DiskMap for another account. Then per-user rules are not offered: the shell's
    /// Recycle Bin calls would hit the administrator's bin, and the launching user could redirect removals in their own folders.
    /// </summary>
    public static bool IsLaunchingAccount(UserContext user) =>
        string.Equals(WindowsIdentity.GetCurrent().User?.Value, user.Sid, StringComparison.OrdinalIgnoreCase);

    private async Task<RemovalResult> RunTool(string exe, string args, CancellationToken ct)
    {
        var vol = Path.GetPathRoot(windowsDir)!;
        var before = freeBytes(vol);
        var o = await runner.RunAsync(exe, args, ct);
        var freed = Math.Max(0, freeBytes(vol) - before);
        if (o.ExitCode == 0) return new(true, freed, 0, 0, null);
        return new(false, freed, 0, 0, ToolError(Path.GetFileNameWithoutExtension(exe), o));
    }

    /// <summary>DISM's "Error: N" line and the message after it; otherwise just the exit code (never the banner).</summary>
    internal static string ToolError(string name, ProcessOutput o)
    {
        var lines = o.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var i = lines.FindIndex(l => l.StartsWith("Error:", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return $"{name} exited with code {o.ExitCode}.";
        return i + 1 < lines.Count && !lines[i + 1].Contains("log file", StringComparison.OrdinalIgnoreCase) ? lines[i] + " " + lines[i + 1] : lines[i];
    }

    // Freed = what the bin held before minus what it holds after (all drives, like the size that was shown).
    private RemovalResult EmptyBin()
    {
        if (!IsLaunchingAccount(user)) return Fail("This Recycle Bin belongs to another account.");
        var before = bin.SizeBytes();
        bin.Empty();
        return new(true, Math.Max(0, before - bin.SizeBytes()), 0, 0, null);
    }

    private sealed class Ctx(Rule rule, QuarantineStore? store, int keepDays)
    {
        public Rule Rule = rule;
        public int KeepDays = keepDays;
        public QuarantineStore? Store = store; // null = permanent delete
        public long Quarantined, Skipped;
        public bool Any;
        public string? Reason, Error;
    }

    private RemovalResult RemovePath(RuleMatch match, CancellationToken ct)
    {
        var rule = match.Rule;
        var path = Path.TrimEndingDirectorySeparator(match.Node.FullPath);
        var undo = settings.UndoDays > 0;
        var vol = Path.GetPathRoot(path)!;

        var items = new List<(string Path, long Size)>();
        var anchor = IsAnchor(path, rule);
        if (anchor)
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

        var ctx = new Ctx(rule, undo ? storeFor(vol) : null, settings.UndoDays);
        var before = freeBytes(vol);
        foreach (var (p, size) in items) Unit(ctx, p, size, anchor, ct);
        var freed = undo ? 0 : Math.Max(0, freeBytes(vol) - before);
        if (ctx.Any || ctx.Skipped == 0) return new(true, freed, ctx.Quarantined, ctx.Skipped, null);
        return new(false, freed, ctx.Quarantined, ctx.Skipped, ctx.Reason ?? ctx.Error ?? "Nothing could be removed (files in use).");
    }

    private bool IsAnchor(string path, Rule rule)
    {
        if (!Directory.Exists(path)) return false;
        var p = SafetyRails.Segs(path);
        return rule.Paths.Any(t => SafetyRails.Anchor(t, user, windowsDir) is { } a && a.Length == p.Length && SafetyRails.IsPrefix(a, p));
    }

    private static bool Gone(Exception e) => e is FileNotFoundException or DirectoryNotFoundException;

    // Sharing violation, lock violation or access denied on the source: the only reasons to split an item up.
    private static bool Busy(Exception e)
    {
        static bool Code(int c) => c is 5 or 32 or 33;
        if (e is UnauthorizedAccessException) return true;
        if (((uint)e.HResult & 0xFFFF0000) == 0x80070000 && Code(e.HResult & 0xFFFF)) return true;
        return e.InnerException is System.ComponentModel.Win32Exception w && Code(w.NativeErrorCode);
    }

    /// <summary>
    /// Removes one unit (move to quarantine, or permanent delete) as a whole. Only if that fails for a busy reason
    /// and the unit is a real directory does it descend, trying each child on its own, so a locked file only splits
    /// its own branch. In quarantine mode the children that do move all go into ONE entry for the unit (under their
    /// relative path), so Restore puts the partial tree back in one step. A vanished unit is already gone; any other
    /// failure skips the unit. Permanent deletes go through SafeDelete (by handle, links never entered).
    /// </summary>
    private void Unit(Ctx c, string path, long size, bool checkRails, CancellationToken ct, Guid? into = null)
    {
        ct.ThrowIfCancellationRequested();
        if (checkRails && !SafetyRails.CanRemove(path, c.Rule, user, windowsDir, out var why)) { c.Reason ??= why; c.Skipped += size; return; }
        if (ForeignInWindowsTemp(path)) { c.Reason ??= "Belongs to another account, so it was left in place."; c.Skipped += Math.Max(size, 1); return; }
        FileAttributes a;
        try { a = File.GetAttributes(path); }
        catch (Exception e) when (Gone(e)) { return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { c.Skipped += Math.Max(size, 1); c.Error ??= e.Message; return; }
        var splittable = (a & FileAttributes.Directory) != 0 && (a & FileAttributes.ReparsePoint) == 0;
        // a recursive delete that fails midway may already have removed part of the tree
        var sizeBefore = splittable && c.Store is null ? Measure(path) : 0;

        try
        {
            if (c.Store is { } store)
            {
                if (into is { } id) store.MoveInto(id, path);
                else store.Add(path, size, c.Rule.Id, c.KeepDays);
                c.Quarantined += size;
            }
            else SafeDelete.DeleteTree(path);
            c.Any = true;
            return;
        }
        catch (Exception e) when (Gone(e)) { return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!Busy(e)) { c.Skipped += Math.Max(size, 1); c.Error ??= e.Message; return; }
            if (!splittable) { c.Skipped += Math.Max(size, 1); return; }
            if (c.Store is null && Measure(path) < sizeBefore) c.Any = true;
        }

        List<string> kids;
        try { kids = Directory.EnumerateFileSystemEntries(path).ToList(); }
        catch (Exception e) when (Gone(e)) { return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { c.Skipped += Math.Max(size, 1); c.Error ??= e.Message; return; }

        var entry = into;
        var startedHere = c.Store is not null && into is null;
        var before = c.Quarantined;
        if (startedHere)
        {
            try { entry = c.Store!.BeginPartial(path, c.Rule.Id, c.KeepDays); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { c.Skipped += Math.Max(size, 1); c.Error ??= e.Message; return; }
        }
        foreach (var k in kids) Unit(c, k, Measure(k), true, ct, entry);
        if (startedHere)
        {
            try { c.Store!.EndPartial(entry!.Value, c.Quarantined - before); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { c.Error ??= e.Message; } // the entry stays tracked
        }
        // empty directories are cleaned up; one that still holds skipped files stays
        try { Directory.Delete(path, false); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static readonly string[] TrustedOwners =
        ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"]; // SYSTEM, Administrators, TrustedInstaller

    /// <summary>
    /// Anyone can create items in Windows\Temp; an elevated cleanup leaves the ones another standard account owns
    /// (only SYSTEM, Administrators, TrustedInstaller or the launching user count). Fails closed.
    /// </summary>
    private bool ForeignInWindowsTemp(string path)
    {
        var temp = SafetyRails.Segs(Path.Combine(windowsDir, "Temp"));
        var p = SafetyRails.Segs(path);
        if (p.Length <= temp.Length || !SafetyRails.IsPrefix(temp, p)) return false;
        try
        {
            var sec = (File.GetAttributes(path) & FileAttributes.Directory) != 0
                ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            var owner = sec.GetOwner(typeof(SecurityIdentifier))?.Value;
            return owner is null || !(TrustedOwners.Contains(owner) || string.Equals(owner, user.Sid, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (Gone(e)) { return false; } // already gone: nothing to protect
        catch (Exception) { return true; }
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
