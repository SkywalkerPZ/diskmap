using System.Collections.Concurrent;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskMap.Core.Quarantine;
using DiskMap.Core.Removal;
using DiskMap.Core.Rules;
using DiskMap.Core.Scanning;
using DiskMap.Core.Settings;

namespace DiskMap.App.ViewModels;

/// <summary>The one scan the whole app looks at: who we are, the rules, the latest result and what matched.</summary>
public sealed partial class ScanSession : ObservableObject
{
    private readonly ConcurrentDictionary<string, QuarantineStore> _stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public ScanSession(UserContext user, bool isElevated, AppSettings settings, IReadOnlyList<Rule> rules)
    {
        User = user;
        IsElevated = isElevated;
        Settings = settings;
        Rules = rules;
        Removal = new RemovalService(settings, user, _windowsDir, isElevated, new ProcessRunner(), new ShellRecycleBin(),
            path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace, StoreFor);
    }

    public UserContext User { get; }
    public bool IsElevated { get; }
    public IReadOnlyList<Rule> Rules { get; }
    public AppSettings Settings { get; }
    public RemovalService Removal { get; }

    [ObservableProperty] public partial ScanResult? Current { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<RuleMatch> Matches { get; private set; } = [];
    [ObservableProperty] public partial bool IsScanning { get; private set; }
    [ObservableProperty] public partial long FilesSeen { get; private set; }
    [ObservableProperty] public partial TimeSpan Elapsed { get; private set; }
    /// <summary>The drive of the latest scan, e.g. C: root; null before the first scan.</summary>
    [ObservableProperty] public partial string? Drive { get; private set; }
    /// <summary>"Scanning… 412,000 files" while scanning, then "1,204,331 files in 3.4 s (fast scan)".</summary>
    [ObservableProperty] public partial string Status { get; private set; } = "";

    /// <summary>One QuarantineStore per volume, shared by every removal.</summary>
    public QuarantineStore StoreFor(string path) =>
        _stores.GetOrAdd(Path.GetPathRoot(Path.GetFullPath(path))!, root => new QuarantineStore(root));

    public async Task ScanAsync(string drive, CancellationToken ct = default)
    {
        if (IsScanning) return; // a second concurrent scan is refused; the flag is set before any await
        IsScanning = true;
        Drive = drive;
        FilesSeen = 0;
        var sw = Stopwatch.StartNew();
        var done = false; // Progress<T> posts asynchronously; stale callbacks must not overwrite the final values
        var progress = new Progress<long>(n =>
        {
            if (done) return;
            FilesSeen = n; Elapsed = sw.Elapsed; Status = Str.Format("StatusScanning", n);
        });
        try
        {
            ScanResult result;
            try { result = await ScannerFactory.For(drive, IsElevated).ScanAsync(drive, progress, ct); }
            catch (NotSupportedException) { result = await new FolderWalkScanner().ScanAsync(drive, progress, ct); }
            var matches = await Task.Run(() => RuleMatcher.MatchAll(result.Root, Rules, User, _windowsDir, DateTime.UtcNow), ct);
            done = true;
            Current = result;
            Matches = matches;
            FilesSeen = result.FileCount;
            Elapsed = result.Elapsed;
            Status = Str.Format(result.UsedFastScan ? "StatusDoneFast" : "StatusDone", result.FileCount, result.Elapsed.TotalSeconds);
        }
        finally { done = true; IsScanning = false; }
    }

    /// <summary>
    /// Rescans one folder of the current tree in place (or drops it if it has gone), fixes the sizes above it and
    /// re-runs the rules. Does nothing while another scan runs or if the folder is not in the current tree.
    /// </summary>
    public async Task RescanFolderAsync(FileNode folder, CancellationToken ct = default)
    {
        if (IsScanning || Current is not { } cur || FileTree.RootOf(folder) != cur.Root) return;
        IsScanning = true;
        try
        {
            var path = folder.FullPath;
            if (Directory.Exists(path))
            {
                ScanResult fresh;
                try { fresh = await ScannerFactory.For(path, IsElevated).ScanAsync(path, null, ct); }
                catch (NotSupportedException) { fresh = await new FolderWalkScanner().ScanAsync(path, null, ct); }
                FileTree.ReplaceContents(folder, fresh.Root); // back on the UI thread, so the map never draws a half-edited tree
            }
            else FileTree.Remove(folder);
            Matches = await Task.Run(() => RuleMatcher.MatchAll(cur.Root, Rules, User, _windowsDir, DateTime.UtcNow), ct);
            Current = cur with { FileCount = cur.Root.FileCount };
        }
        finally { IsScanning = false; }
    }
}
