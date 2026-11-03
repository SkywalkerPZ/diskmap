using DiskMap.App.ViewModels;
using DiskMap.Core;
using DiskMap.Core.Quarantine;
using DiskMap.Core.Rules;
using DiskMap.Core.Settings;
using Microsoft.UI.Xaml;

namespace DiskMap.App;

public partial class App : Application
{
    private Window? _window;

    public static ScanSession Session { get; private set; } = null!;
    /// <summary>Drive passed via --drive by an elevated relaunch; null when started normally.</summary>
    public static string? StartupDrive { get; private set; }
    /// <summary>True when the rule files could not be loaded; the app runs with no rules.</summary>
    public static bool RulesFailed { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var user = UserContext.FromArgs(argv) ?? UserContext.Current();
        var i = Array.IndexOf(argv, "--drive");
        StartupDrive = i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;

        var settings = new SettingsStore(Path.Combine(user.LocalAppData, "DiskMap")).Load();
        var elevated = Elevation.IsElevated();
        if (settings.AlwaysStartElevated && !elevated) Elevation.RelaunchElevated(user, StartupDrive); // false = declined, carry on

        PurgeQuarantines(settings.UndoDays);
        IReadOnlyList<Rule> rules = [];
        try { rules = RuleLoader.LoadAll(Path.Combine(AppContext.BaseDirectory, "Rules", "data")); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { RulesFailed = true; }
        Session = new ScanSession(user, elevated, settings, rules);

        _window = new MainWindow();
        _window.Activate();
    }

    private static void PurgeQuarantines(int undoDays)
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                if (Directory.Exists(Path.Combine(d.RootDirectory.FullName, "DiskMap Quarantine")))
                    new QuarantineStore(d.RootDirectory.FullName).PurgeExpired(undoDays, DateTime.UtcNow);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // one bad drive must not block startup
        }
    }
}
