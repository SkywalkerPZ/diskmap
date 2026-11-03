using System.ComponentModel;
using DiskMap.App.Views;
using DiskMap.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiskMap.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ElevationBar.IsOpen = !App.Session.IsElevated;
        RulesBar.IsOpen = App.RulesFailed;
        Nav.SelectedItem = ExploreItem;
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        var page = e.IsSettingsSelected ? typeof(SettingsPage) : (e.SelectedItemContainer?.Tag as string) switch
        {
            "Explore" => typeof(ExplorePage),
            "CleanUp" => typeof(CleanUpPage),
            "Quarantine" => typeof(QuarantinePage),
            _ => null,
        };
        if (page is not null) ContentFrame.Navigate(page);
    }

    // If UAC is declined RelaunchElevated returns false and this instance simply keeps running.
    private void OnRelaunchClick(object sender, RoutedEventArgs e)
    {
        try { Elevation.RelaunchElevated(App.Session.User, App.Session.Drive); }
        catch (Win32Exception) { } // launch failed for a reason other than UAC cancel; stay open
    }
}
