using DiskMap.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiskMap.App.Views;

public sealed partial class QuarantinePage : Page
{
    public QuarantinePage()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Refresh();
    }

    public QuarantineViewModel ViewModel { get; } = new();

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuarantineItem item) await ViewModel.RestoreAsync(item);
    }

    private async void OnEmptyClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEmpty) return;
        if (!await Confirmation.AskAsync(XamlRoot, Str.Get("EmptyConfirmTitle"), ViewModel.EmptyConfirmText, Str.Get("EmptyConfirm"))) return;
        await ViewModel.EmptyAsync();
    }
}
