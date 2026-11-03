using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskMap.Core.Removal;
using DiskMap.Core.Rules;
using DiskMap.Core.Scanning;
using Microsoft.UI.Xaml.Controls;

namespace DiskMap.App.ViewModels;

/// <summary>One breadcrumb step; BreadcrumbBar shows ToString().</summary>
public sealed record Crumb(FileNode Node)
{
    public override string ToString() => Node.Name;
}

public sealed partial class ExploreViewModel : ObservableObject
{
    private readonly ScanSession _session = App.Session;
    private readonly string _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private readonly List<string> _driveRoots = [];
    private bool _started;

    public ExploreViewModel()
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            string label;
            try { label = d.IsReady && d.VolumeLabel.Length > 0 ? d.VolumeLabel : Str.Get("LocalDisk"); }
            catch (IOException) { label = Str.Get("LocalDisk"); }
            _driveRoots.Add(d.RootDirectory.FullName);
            Drives.Add($"{label} ({d.Name.TrimEnd(Path.DirectorySeparatorChar)})");
        }
        _session.PropertyChanged += OnSessionChanged;
    }

    /// <summary>Raised when the current tree changed in place and the map must lay itself out again.</summary>
    public event EventHandler? MapChanged;

    public List<string> Drives { get; } = [];
    public ObservableCollection<Crumb> Crumbs { get; } = [];

    [ObservableProperty] public partial int DriveIndex { get; set; } = -1;
    [ObservableProperty] public partial FileNode? ZoomRoot { get; private set; }
    [ObservableProperty] public partial FileNode? Selected { get; set; }
    [ObservableProperty] public partial RuleMatch? Match { get; private set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string BusyText { get; private set; } = "";
    [ObservableProperty] public partial bool IsEstimating { get; private set; }
    [ObservableProperty] public partial string EstimateText { get; private set; } = "";
    [ObservableProperty] public partial bool IsResultOpen { get; set; }
    [ObservableProperty] public partial string ResultMessage { get; private set; } = "";
    [ObservableProperty] public partial InfoBarSeverity ResultSeverity { get; private set; }

    public string Status => _session.Status;
    public bool IsScanning => _session.IsScanning;
    public bool CanChangeDrive => !IsScanning && !IsBusy;

    public bool HasSelection => Selected is not null;
    public bool NoSelection => Selected is null;
    public string SelectedName => Selected?.Name ?? "";
    public string SelectedPath => Selected?.FullPath ?? "";
    public string SelectedSize => Selected is null ? "" : IsComponentStore ? Str.Get("SizeAfterCleanup") : Str.Format("SizeOnDisk", SizeFormat.Format(Selected.Size));
    public string SelectedModified => Selected is null || Selected.LastWriteUtc == default ? ""
        : Str.Format("Modified", Selected.LastWriteUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

    public bool HasMatch => Match is not null;
    public SafetyLabel Label => Match?.Rule.Label ?? SafetyLabel.Safe;
    public string LabelText => Match is null ? "" : Str.Get("Label" + Match.Rule.Label);
    public string MatchTitle => Match?.Rule.Title ?? "";
    public string Explanation => Match?.Rule.Explanation ?? "";
    public string Consequence => Match?.Rule.Consequence ?? "";
    public bool IsComponentStore => Match?.Rule.Method == RemovalMethod.DismComponentCleanup;
    /// <summary>Shown greyed out with "Needs administrator", never hidden.</summary>
    public bool NeedsAdmin => Match is { Rule.NeedsAdmin: true } && !_session.IsElevated;
    public string? AdminTip => NeedsAdmin ? Str.Get("NeedsAdministrator") : null;
    public bool CanRemove => Match is not null && !NeedsAdmin && !IsBusy && !IsScanning;
    public bool CanEstimate => IsComponentStore && !NeedsAdmin && !IsEstimating;
    public bool HasEstimate => EstimateText.Length > 0;

    /// <summary>Confirmation body: the consequence, plus the undo line that applies.</summary>
    public string ConfirmText => Match is not { } m ? ""
        : m.Rule.Undoable && _session.Settings.UndoDays > 0
            ? $"{m.Rule.Consequence}\n\n{Str.Format("ConfirmUndo", _session.Settings.UndoDays)}"
            : m.Rule.Undoable // undo is Off: the files that would have gone to quarantine are deleted for good
                ? $"{m.Rule.Consequence}\n\n{Str.Get("DeletedPermanently")}\n{Str.Get("CantUndo")}"
                : $"{m.Rule.Consequence}\n\n{Str.Get("CantUndo")}";

    /// <summary>Scans the startup drive the first time the page loads; later loads keep the existing scan.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        if (_session.Current is { } cur)
        {
            DriveIndex = IndexOf(_session.Drive);
            ResetZoom(cur.Root);
            return;
        }
        var i = IndexOf(App.StartupDrive);
        if (i < 0) i = IndexOf(Path.GetPathRoot(Environment.SystemDirectory));
        DriveIndex = i >= 0 ? i : _driveRoots.Count > 0 ? 0 : -1; // triggers the scan
    }

    private int IndexOf(string? drive) =>
        drive is null ? -1 : _driveRoots.FindIndex(r => string.Equals(r.TrimEnd(Path.DirectorySeparatorChar), drive.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));

    partial void OnDriveIndexChanged(int value)
    {
        if (!_started || value < 0 || value >= _driveRoots.Count) return;
        if (string.Equals(_driveRoots[value], _session.Drive, StringComparison.OrdinalIgnoreCase) && _session.Current is not null) return;
        _ = ScanDriveAsync(_driveRoots[value]);
    }

    private async Task ScanDriveAsync(string drive)
    {
        try { await _session.ScanAsync(drive); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowResult(Str.Format("ScanFailed", drive, e.Message), InfoBarSeverity.Warning);
        }
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScanSession.Status): OnPropertyChanged(nameof(Status)); break;
            case nameof(ScanSession.IsScanning):
                OnPropertyChanged(nameof(IsScanning));
                OnPropertyChanged(nameof(CanChangeDrive));
                OnPropertyChanged(nameof(CanRemove));
                break;
            case nameof(ScanSession.Current) when _session.Current is { } cur && (ZoomRoot is null || FileTree.RootOf(ZoomRoot) != cur.Root):
                ResetZoom(cur.Root); // a new scan; an in-place rescan keeps the same root
                break;
        }
    }

    private void ResetZoom(FileNode root)
    {
        Selected = null;
        ZoomRoot = root;
        Crumbs.Clear();
        Crumbs.Add(new Crumb(root));
    }

    /// <summary>Zooms into a folder below the current zoom root (double-click on the map).</summary>
    public void ZoomInto(FileNode node)
    {
        var dir = node.IsDirectory ? node : node.Parent;
        if (dir is null || dir == ZoomRoot || dir.Children.Count == 0) return;
        var path = new Stack<FileNode>();
        for (var n = dir; n is not null && n != ZoomRoot; n = n.Parent) path.Push(n);
        foreach (var n in path) Crumbs.Add(new Crumb(n));
        ZoomRoot = dir;
        Selected = dir; // a parent is always covered by its children on the map, so zooming is how a folder gets selected
    }

    public void ZoomTo(int crumbIndex)
    {
        if (crumbIndex < 0 || crumbIndex >= Crumbs.Count) return;
        while (Crumbs.Count > crumbIndex + 1) Crumbs.RemoveAt(Crumbs.Count - 1);
        ZoomRoot = Crumbs[crumbIndex].Node;
        Selected = ZoomRoot;
    }

    partial void OnSelectedChanged(FileNode? value)
    {
        Match = value is null ? null : RuleMatcher.MatchNode(value, _session.Rules, _session.User, _windowsDir, DateTime.UtcNow);
        EstimateText = "";
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(NoSelection));
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedPath));
        OnPropertyChanged(nameof(SelectedSize));
        OnPropertyChanged(nameof(SelectedModified));
    }

    partial void OnMatchChanged(RuleMatch? value)
    {
        foreach (var p in (string[])[nameof(HasMatch), nameof(Label), nameof(LabelText), nameof(MatchTitle), nameof(Explanation),
                     nameof(Consequence), nameof(IsComponentStore), nameof(NeedsAdmin), nameof(AdminTip), nameof(CanRemove), nameof(CanEstimate), nameof(SelectedSize)])
            OnPropertyChanged(p);
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanChangeDrive));
    }

    partial void OnIsEstimatingChanged(bool value) => OnPropertyChanged(nameof(CanEstimate));
    partial void OnEstimateTextChanged(string value) => OnPropertyChanged(nameof(HasEstimate));

    /// <summary>Removes the selected match (the caller has already confirmed), reports the result and rescans its parent folder.</summary>
    public async Task RemoveAsync()
    {
        if (!CanRemove || Match is not { } match) return;
        IsBusy = true;
        IsResultOpen = false;
        BusyText = Str.Get(match.Rule.Method == RemovalMethod.DismComponentCleanup ? "RemovingDism" : "Removing");
        try
        {
            var r = await _session.Removal.RemoveAsync(match, CancellationToken.None);
            if (r.Success)
            {
                var msg = r.BytesQuarantined > 0 ? Str.Format("ResultQuarantined", SizeFormat.Format(r.BytesQuarantined))
                                                 : Str.Format("ResultFreed", SizeFormat.Format(r.BytesFreed));
                if (r.BytesSkipped > 0) msg += Str.Format("ResultSkipped", SizeFormat.Format(r.BytesSkipped));
                ShowResult(msg, InfoBarSeverity.Success);
            }
            else ShowResult(r.Error ?? "", InfoBarSeverity.Warning);

            Selected = null;
            if (match.Node.Parent is { } parent)
            {
                BusyText = Str.Get("UpdatingMap");
                await _session.RescanFolderAsync(parent);
                DropDetached();
                MapChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowResult(e.Message, InfoBarSeverity.Warning); // the rescan failed; the removal result above already stands
        }
        finally { IsBusy = false; BusyText = ""; }
    }

    /// <summary>
    /// After a rescan replaced part of the tree: zooms out to the nearest breadcrumb still in the live tree and
    /// clears a selection that was replaced, so removed items are neither drawn nor offered for removal again.
    /// </summary>
    private void DropDetached()
    {
        if (_session.Current is not { } cur) return;
        bool Live(FileNode n) => FileTree.RootOf(n) == cur.Root;
        if (ZoomRoot is { } z && !Live(z))
        {
            var i = Crumbs.Count - 1;
            while (i > 0 && !Live(Crumbs[i].Node)) i--;
            if (Live(Crumbs[i].Node)) ZoomTo(i);
            else ResetZoom(cur.Root);
        }
        if (Selected is { } s && !Live(s)) Selected = null;
    }

    /// <summary>Runs DISM /AnalyzeComponentStore (minutes) and shows "about X–Y GB (estimate)".</summary>
    public async Task EstimateAsync()
    {
        if (!CanEstimate) return;
        var forMatch = Match;
        IsEstimating = true;
        EstimateText = "";
        string text;
        try
        {
            var o = await new ProcessRunner().RunAsync("DISM", "/Online /Cleanup-Image /AnalyzeComponentStore", CancellationToken.None);
            text = ComponentStoreEstimate.Parse(o.StdOut) is { } e
                ? Str.Format("EstimateResult", SizeFormat.Range(e.Low, e.High))
                : Str.Get("EstimateFailed");
        }
        catch (Exception) { text = Str.Get("EstimateFailed"); } // called from an async void handler: nothing may escape
        finally { IsEstimating = false; }
        if (Match == forMatch) EstimateText = text; // the user may have selected something else meanwhile
    }

    public void OpenInExplorer()
    {
        if (Selected is not { } n) return;
        var path = n.FullPath;
        var args = n.IsDirectory ? $"\"{path}\"" : $"/select,\"{path}\"";
        try { Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = false })?.Dispose(); }
        catch (Win32Exception e) { ShowResult(e.Message, InfoBarSeverity.Warning); }
    }

    private void ShowResult(string message, InfoBarSeverity severity)
    {
        ResultMessage = message;
        ResultSeverity = severity;
        IsResultOpen = true;
    }
}
