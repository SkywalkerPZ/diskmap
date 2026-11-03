using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskMap.Core.Settings;

namespace DiskMap.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly int[] UndoChoices = [0, 3, 7];
    private readonly ScanSession _session = App.Session;
    private readonly bool _ready;

    public SettingsViewModel()
    {
        var i = Array.IndexOf(UndoChoices, _session.Settings.UndoDays);
        UndoIndex = i >= 0 ? i : 1; // an unexpected value is shown (and behaves) as the 3-day default, never as Off
        AlwaysElevated = _session.Settings.AlwaysStartElevated;
        _ready = true;
    }

    [ObservableProperty] public partial int UndoIndex { get; set; }
    [ObservableProperty] public partial bool AlwaysElevated { get; set; }
    [ObservableProperty] public partial bool IsErrorOpen { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; private set; } = "";

    /// <summary>The assembly's informational version without the build-metadata suffix after the plus sign.</summary>
    public string VersionText
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = v.IndexOf('+');
            return Str.Format("VersionText", plus >= 0 ? v[..plus] : v);
        }
    }

    partial void OnUndoIndexChanged(int value) => Save();
    partial void OnAlwaysElevatedChanged(bool value) => Save();

    private void Save()
    {
        if (!_ready || UndoIndex < 0) return; // RadioButtons reports -1 while it rebinds
        try
        {
            _session.UpdateSettings(new AppSettings(UndoChoices[UndoIndex], AlwaysElevated));
            IsErrorOpen = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Str.Format("SettingsSaveFailed", e.Message); // the change still applies until DiskMap closes
            IsErrorOpen = true;
        }
    }
}
