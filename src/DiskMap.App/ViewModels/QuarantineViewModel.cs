using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskMap.Core.Quarantine;
using Microsoft.UI.Xaml.Controls;

namespace DiskMap.App.ViewModels;

public sealed record QuarantineItem(QuarantineEntry Entry, QuarantineStore Store)
{
    public bool IsUnknown => Entry.OriginalPath is null;
    public string PathText => Entry.OriginalPath ?? Str.Get("UnknownOrigin");
    public string SizeText => SizeFormat.Format(Entry.Size);
    public string RemovedText => Str.Format("RemovedOn", Entry.RemovedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
    /// <summary>Unknown-origin entries can only be emptied.</summary>
    public bool CanRestore => !IsUnknown;
}

public sealed partial class QuarantineViewModel : ObservableObject
{
    private readonly ScanSession _session = App.Session;
    private List<QuarantineStore> _stores = [];

    public ObservableCollection<QuarantineItem> Items { get; } = [];

    [ObservableProperty] public partial string Header { get; private set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial bool IsResultOpen { get; set; }
    [ObservableProperty] public partial string ResultMessage { get; private set; } = "";
    [ObservableProperty] public partial InfoBarSeverity ResultSeverity { get; private set; }
    [ObservableProperty] public partial bool IsCorrupt { get; private set; }
    [ObservableProperty] public partial string CorruptMessage { get; private set; } = "";

    public bool IsEmpty => Items.Count == 0;
    public bool CanEmpty => !IsBusy && Items.Count > 0;
    public long Total => _stores.Sum(s => s.TotalSize);
    public string EmptyConfirmText => Str.Format("EmptyConfirmBody", SizeFormat.Format(Total));

    /// <summary>Lists every fixed drive's quarantine; never creates a quarantine folder.</summary>
    public void Refresh()
    {
        try { Load(); }
        catch (Exception e) // called from a Loaded handler: nothing may escape
        {
            Show(Str.Format("QuarantineLoadFailed", e.Message), InfoBarSeverity.Warning);
        }
    }

    private void Load()
    {
        var roots = ScanSession.QuarantineRoots().ToList();
        _stores = roots.Select(_session.StoreFor).ToList();
        Items.Clear();
        foreach (var s in _stores)
            foreach (var e in s.Entries.OrderByDescending(e => e.RemovedUtc)) Items.Add(new QuarantineItem(e, s));
        Header = Str.Format("QuarantineHeader", SizeFormat.Format(Total));
        var bad = roots.Where((_, i) => _stores[i].IsCorrupt).ToList();
        var refused = roots.Where((_, i) => _stores[i].Untrusted is not null).ToList();
        IsCorrupt = bad.Count > 0 || refused.Count > 0;
        CorruptMessage = string.Join(" ", new[]
        {
            bad.Count > 0 ? Str.Format("QuarantineCorrupt", string.Join(", ", bad)) : null,
            refused.Count > 0 ? Str.Format("QuarantineUntrusted", string.Join(", ", refused)) : null,
        }.OfType<string>());
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanEmpty));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanEmpty));

    public async Task RestoreAsync(QuarantineItem item)
    {
        if (IsBusy || !item.CanRestore) return;
        IsBusy = true;
        IsResultOpen = false;
        try
        {
            var path = await Task.Run(() => item.Store.Restore(item.Entry.Id));
            Show(Str.Format("RestoredTo", path), InfoBarSeverity.Success);
        }
        catch (KeyNotFoundException) { Show(Str.Get("RestoreGone"), InfoBarSeverity.Warning); }
        catch (InvalidOperationException) { Show(Str.Get("RestoreUnknown"), InfoBarSeverity.Warning); }
        catch (FileNotFoundException) { Show(Str.Get("RestoreMissing"), InfoBarSeverity.Warning); }
        catch (Exception e) { Show(Str.Format("RestoreFailed", e.Message), InfoBarSeverity.Warning); } // async-void caller: nothing may escape
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    /// <summary>Deletes everything in quarantine for good (the caller has confirmed).</summary>
    public async Task EmptyAsync()
    {
        if (!CanEmpty) return;
        IsBusy = true;
        IsResultOpen = false;
        try
        {
            string? error = null;
            await Task.Run(() =>
            {
                foreach (var s in _stores)
                {
                    try { s.EmptyAll(); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error ??= e.Message; }
                }
            });
            if (error is not null) Show(Str.Format("EmptyFailed", error), InfoBarSeverity.Warning);
            else if (Total > 0) Show(Str.Format("EmptyPartial", SizeFormat.Format(Total)), InfoBarSeverity.Warning);
            else Show(Str.Get("EmptyDone"), InfoBarSeverity.Success);
        }
        catch (Exception e) { Show(Str.Format("EmptyFailed", e.Message), InfoBarSeverity.Warning); } // async-void caller: nothing may escape
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        ResultMessage = message;
        ResultSeverity = severity;
        IsResultOpen = true;
    }
}
