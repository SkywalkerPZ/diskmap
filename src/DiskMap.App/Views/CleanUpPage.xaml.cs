using DiskMap.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiskMap.App.Views;

public sealed partial class CleanUpPage : Page
{
    public CleanUpPage() => InitializeComponent();

    public CleanUpViewModel ViewModel { get; } = new();

    private async void OnCleanClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanClean) return;
        var ticked = ViewModel.TickedItems(); // taken once: the dialog and the removal use the same list
        if (!await Confirmation.AskAsync(XamlRoot, Str.Get("BulkTitle"), ViewModel.BulkConfirmText(ticked), Str.Get("BulkConfirm"))) return;
        await ViewModel.CleanAsync(ticked);
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CleanItem item) return;
        if (!await Confirmation.AskAsync(XamlRoot, Str.Format("ConfirmTitle", item.Title), ViewModel.ConfirmText(item), Str.Get("ConfirmRemove"))) return;
        await ViewModel.RemoveOneAsync(item);
    }
}
