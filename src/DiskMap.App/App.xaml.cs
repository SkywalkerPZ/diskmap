using System.ComponentModel;
using System.Runtime.InteropServices;
using DiskMap.App.ViewModels;
using DiskMap.Core;
using DiskMap.Core.Quarantine;
using DiskMap.Core.Rules;
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

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Crashed(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crashed(e.ExceptionObject as Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var user = UserContext.FromArgs(argv) ?? UserContext.Current();
        var i = Array.IndexOf(argv, "--drive");
        StartupDrive = i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;

        var settings = ScanSession.SettingsStoreFor(user).Load();
        var elevated = Elevation.IsElevated();
        if (settings.AlwaysStartElevated && !elevated)
        {
            // false = declined; a launch error (e.g. a policy that blocks elevation) must not stop DiskMap from starting
            try { Elevation.RelaunchElevated(user, StartupDrive); }
            catch (Win32Exception) { }
        }

        IReadOnlyList<Rule> rules = [];
        try { rules = RuleLoader.LoadAll(Path.Combine(AppContext.BaseDirectory, "Rules", "data")); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { RulesFailed = true; }
        Session = new ScanSession(user, elevated, settings, rules);

        _window = new MainWindow();
        _window.Activate();
        _ = Task.Run(PurgeQuarantines); // after the window is up, off the UI thread
    }

    // Each entry keeps the undo period it was removed with (its KeepDays); the current setting doesn't shorten it.
    private static void PurgeQuarantines()
    {
        foreach (var root in ScanSession.QuarantineRoots())
        {
            try { new QuarantineStore(root).PurgeExpired(DateTime.UtcNow); }
            catch (Exception) { } // one bad drive (or a folder DiskMap refuses to use) must not block the rest
        }
    }

    // Logs to %LOCALAPPDATA%\DiskMap\crash.log and tells the user, as calmly as a crash allows.
    private static void Crashed(Exception? e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskMap");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:O}{Environment.NewLine}{e}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception) { }
        try
        {
            string text;
            try { text = Str.Get("CrashMessage"); } catch (Exception) { text = "DiskMap ran into an unexpected problem and has to close. Details were saved to crash.log in the DiskMap folder of your local app data."; }
            MessageBoxW(0, text, "DiskMap", 0x10); // MB_ICONERROR
        }
        catch (Exception) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
}
