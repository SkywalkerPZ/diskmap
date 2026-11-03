using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskMap.Core.Removal;
using DiskMap.Core.Rules;
using DiskMap.Core.Scanning;
using Microsoft.UI.Xaml.Controls;

namespace DiskMap.App.ViewModels;

public sealed partial class CleanItem(RuleMatch match, long bytes, bool elevated) : ObservableObject
{
    public RuleMatch Match { get; } = match;
    public long Bytes { get; } = bytes;
    public bool IsComponentStore => Match.Rule.Method == RemovalMethod.DismComponentCleanup;
    public string Title => Match.Rule.Method == RemovalMethod.Quarantine ? Match.Node.FullPath : Match.Rule.Title;
    /// <summary>Never an exact number for the component store: Windows shares those files.</summary>
    public string SizeText => IsComponentStore ? Str.Get("SizeAfterCleanup") : SizeFormat.Format(Bytes);
    public SafetyLabel Label => Match.Rule.Label;
    public string LabelText => Str.Get("Label" + Label);
    public bool IsBulk => RemovalService.IsBulkEligible(Match.Rule);
    public bool NeedsAdmin => Match.Rule.NeedsAdmin && !elevated;
    public string? AdminTip => NeedsAdmin ? Str.Get("NeedsAdministrator") : null;
    public double RowOpacity => NeedsAdmin ? 0.5 : 1;
    public bool CanCheck => IsBulk && !NeedsAdmin;
    public bool ShowRemove => !IsBulk;
    public bool CanRemove => IsIdle && !NeedsAdmin;

    [ObservableProperty] public partial bool IsChecked { get; set; }
    [ObservableProperty] public partial bool IsIdle { get; set; } = true;
    partial void OnIsIdleChanged(bool value) => OnPropertyChanged(nameof(CanRemove));
}

public sealed record CleanGroup(string Name, string TotalText, SafetyLabel Label, string LabelText, IReadOnlyList<CleanItem> Items);

public sealed partial class CleanUpViewModel : ObservableObject
{
    private readonly ScanSession _session = App.Session;

    public CleanUpViewModel()
    {
        _session.PropertyChanged += OnSessionChanged;
        Rebuild();
    }

    public ObservableCollection<CleanGroup> Groups { get; } = [];

    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string BusyText { get; private set; } = "";
    [ObservableProperty] public partial bool IsResultOpen { get; set; }
    [ObservableProperty] public partial string ResultMessage { get; private set; } = "";
    [ObservableProperty] public partial InfoBarSeverity ResultSeverity { get; private set; }

    public bool NoScan => _session.Current is null;
    public bool NothingToClean => !NoScan && Groups.Count == 0;
    public bool HasGroups => Groups.Count > 0;
    public bool CanClean => !IsBusy && !_session.IsScanning && !_session.IsRemoving && Ticked().Count > 0;
    public string CleanText => Str.Format("CleanButton", SizeFormat.Format(Ticked().Sum(i => i.Bytes)));

    private IEnumerable<CleanItem> AllItems => Groups.SelectMany(g => g.Items);
    private static string KeyOf(CleanItem i) => i.Match.Node.FullPath + "|" + i.Match.Rule.Id;
    public IReadOnlyList<CleanItem> TickedItems() => Ticked();
    private List<CleanItem> Ticked() => AllItems.Where(i => i.IsChecked && i.CanCheck).ToList();

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScanSession.Matches) or nameof(ScanSession.Current)) Rebuild();
        else if (e.PropertyName is nameof(ScanSession.IsScanning) or nameof(ScanSession.IsRemoving))
        {
            OnPropertyChanged(nameof(CanClean));
            UpdateIdle();
        }
    }

    private void Rebuild()
    {
        // keep the user's ticks across rebuilds; only items not seen before start ticked
        var prior = new Dictionary<string, bool>();
        foreach (var old in AllItems) prior.TryAdd(KeyOf(old), old.IsChecked);
        Groups.Clear();
        var matches = _session.Matches;
        long bin = matches.Any(m => m.Rule.Method == RemovalMethod.EmptyRecycleBin) ? BinSize() : 0;
        var items = new List<CleanItem>();
        foreach (var m in matches)
        {
            var isBin = m.Rule.Method == RemovalMethod.EmptyRecycleBin;
            if (isBin && bin == 0) continue; // nothing to empty
            var item = new CleanItem(m, isBin ? bin : m.Node.Size, _session.IsElevated);
            item.IsChecked = item.CanCheck && (!prior.TryGetValue(KeyOf(item), out var was) || was); // greyed-out (needs administrator) items are never ticked
            item.PropertyChanged += OnItemChanged;
            items.Add(item);
        }
        // the component store's size is only an upper bound, so it never counts toward a total
        foreach (var g in items.GroupBy(i => i.Match.Rule.Category)
                     .Select(g => (g.Key, Items: g.OrderByDescending(i => i.Bytes).ToList(), Total: g.Where(i => !i.IsComponentStore).Sum(i => i.Bytes)))
                     .OrderByDescending(g => g.Total))
        {
            var label = g.Items.Select(i => i.Label).OrderBy(Caution).Last();
            Groups.Add(new CleanGroup(g.Key, SizeFormat.Format(g.Total), label, Str.Get("Label" + label), g.Items));
        }
        UpdateIdle();
        foreach (var p in (string[])[nameof(NoScan), nameof(NothingToClean), nameof(HasGroups), nameof(CanClean), nameof(CleanText)]) OnPropertyChanged(p);
    }

    // Group badge = the most cautious label inside the group.
    private static int Caution(SafetyLabel l) => l switch
    {
        SafetyLabel.Safe => 0, SafetyLabel.Rebuildable => 1, SafetyLabel.SafeIfNoHibernate => 2, SafetyLabel.AlreadyDeleted => 3, _ => 4,
    };

    private static long BinSize()
    {
        try { return new ShellRecycleBin().SizeBytes(); }
        catch (ExternalException) { return 0; }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CleanItem.IsChecked)) return;
        OnPropertyChanged(nameof(CanClean));
        OnPropertyChanged(nameof(CleanText));
    }

    private void UpdateIdle()
    {
        var idle = !IsBusy && !_session.IsScanning && !_session.IsRemoving;
        foreach (var i in AllItems) i.IsIdle = idle;
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanClean));
        UpdateIdle();
    }

    /// <summary>Text for the bulk confirmation: how many items and how much, then the undo line.</summary>
    public string BulkConfirmText(IReadOnlyList<CleanItem> t)
    {
        var undo = _session.Settings.UndoDays;
        var line = undo > 0 ? Str.Format("BulkUndo", undo) : $"{Str.Get("BulkPermanent")}\n{Str.Get("CantUndo")}";
        return $"{Str.Format("BulkSummary", t.Count, SizeFormat.Format(t.Sum(i => i.Bytes)))}\n\n{line}";
    }

    public string ConfirmText(CleanItem item) => Confirmation.Body(item.Match.Rule, _session.Settings.UndoDays);

    /// <summary>Removes the ticked items one after another (the caller has confirmed) and reports one combined result.</summary>
    public async Task CleanAsync(IReadOnlyList<CleanItem> confirmed)
    {
        // exactly what the dialog showed, minus anything that stopped being removable or left the tree meanwhile
        var todo = confirmed.Where(i => i.CanCheck && _session.Current is { } cur && FileTree.RootOf(i.Match.Node) == cur.Root).ToList();
        if (!CanClean || todo.Count == 0) return;
        await RunAsync(todo, true);
    }

    /// <summary>Removes one non-bulk item (the caller has confirmed its dialog).</summary>
    public async Task RemoveOneAsync(CleanItem item)
    {
        if (!item.CanRemove || IsBusy) return;
        await RunAsync([item], false);
    }

    private async Task RunAsync(List<CleanItem> todo, bool bulk)
    {
        if (!_session.TryBeginRemoval()) return;
        var removal = _session.Removal; // one service for the whole run: a settings change mid-run must not switch later items to permanent delete
        IsBusy = true;
        IsResultOpen = false;
        long freed = 0, quarantined = 0, skipped = 0;
        int failed = 0;
        string? firstError = null;
        try
        {
            for (var n = 0; n < todo.Count; n++)
            {
                var item = todo[n];
                BusyText = bulk ? Str.Format("Cleaning", n + 1, todo.Count)
                                : Str.Get(item.IsComponentStore ? "RemovingDism" : "Removing");
                var r = await removal.RemoveAsync(item.Match, CancellationToken.None);
                freed += r.BytesFreed; quarantined += r.BytesQuarantined; skipped += r.BytesSkipped;
                if (!r.Success) { failed++; firstError ??= r.Error; }
            }
            var msg = quarantined > 0 ? Str.Format("ResultQuarantined", SizeFormat.Format(quarantined)) : Str.Format("ResultFreed", SizeFormat.Format(freed));
            if (skipped > 0) msg += Str.Format("ResultSkipped", SizeFormat.Format(skipped));
            if (failed == todo.Count) Show(firstError ?? "", InfoBarSeverity.Warning);
            else if (failed > 0) Show(msg + Str.Format("ResultFailed", failed), InfoBarSeverity.Warning);
            else Show(msg, InfoBarSeverity.Success);

            BusyText = Str.Get("UpdatingMap");
            await RescanAsync(todo.Select(i => i.Match.Node.Parent));
        }
        catch (Exception e) // async-void caller: nothing may escape; the removal result stands
        {
            Show(e.Message, InfoBarSeverity.Warning);
        }
        finally { IsBusy = false; BusyText = ""; _session.EndRemoval(); }
    }

    // Rescans each affected parent once, outermost first; a parent already replaced by an ancestor's rescan is skipped.
    private async Task RescanAsync(IEnumerable<FileNode?> parents)
    {
        foreach (var p in parents.OfType<FileNode>().Distinct().OrderBy(p => p.FullPath.Length))
            if (_session.Current is { } cur && FileTree.RootOf(p) == cur.Root) await _session.RescanFolderAsync(p);
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        ResultMessage = message;
        ResultSeverity = severity;
        IsResultOpen = true;
    }
}
