using DiskMap.App.ViewModels;
using DiskMap.Core.Rules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DiskMap.App.Views;

public sealed partial class ExplorePage : Page
{
    public ExplorePage()
    {
        InitializeComponent();
        Map.NodeTapped += (_, node) => ViewModel.Selected = node;
        Map.ZoomRequested += (_, node) => ViewModel.ZoomInto(node);
        ViewModel.MapChanged += (_, _) => Map.Refresh();
        Loaded += (_, _) => ViewModel.Start();
    }

    public ExploreViewModel ViewModel { get; } = new();

    /// <summary>Calm, translucent badge colours that work on light and dark cards; only "Check first" uses amber.</summary>
    public static Brush BadgeBrush(SafetyLabel label) => new SolidColorBrush(label switch
    {
        SafetyLabel.Safe => Color.FromArgb(0x48, 0x3A, 0x9D, 0x5D),
        SafetyLabel.SafeIfNoHibernate => Color.FromArgb(0x48, 0x3A, 0x7F, 0xC1),
        SafetyLabel.Rebuildable => Color.FromArgb(0x48, 0x2F, 0x9A, 0x9A),
        SafetyLabel.CheckFirst => Color.FromArgb(0x60, 0xD9, 0x9A, 0x2B),
        _ => Color.FromArgb(0x48, 0x8A, 0x8A, 0x8A),
    });

    private void OnCrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args) => ViewModel.ZoomTo(args.Index);

    private void OnOpenClick(object sender, RoutedEventArgs e) => ViewModel.OpenInExplorer();

    private async void OnEstimateClick(object sender, RoutedEventArgs e) => await ViewModel.EstimateAsync();

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Str.Format("ConfirmTitle", ViewModel.SelectedName),
            Content = new TextBlock { Text = ViewModel.ConfirmText, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Str.Get("ConfirmRemove"),
            CloseButtonText = Str.Get("ConfirmCancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.RemoveAsync();
    }
}
