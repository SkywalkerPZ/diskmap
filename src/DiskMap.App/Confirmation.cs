using DiskMap.Core.Rules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiskMap.App;

/// <summary>Confirmation dialogs and wording shared by Explore, Clean up and Quarantine.</summary>
internal static class Confirmation
{
    private static bool _open;

    /// <summary>True if the user chose the primary button. Refused (false) while another dialog is open: a second ShowAsync throws.</summary>
    public static async Task<bool> AskAsync(XamlRoot root, string title, string body, string primary)
    {
        if (_open) return false;
        _open = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = title,
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = primary,
                CloseButtonText = Str.Get("ConfirmCancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _open = false; }
    }

    /// <summary>The undo line that applies to a rule: restorable for N days, deleted for good (undo Off), or not undoable at all.</summary>
    public static string UndoLine(Rule rule, int undoDays) =>
        rule.Undoable && undoDays > 0 ? Str.Format("ConfirmUndo", undoDays)
        : rule.Undoable ? $"{Str.Get("DeletedPermanently")}\n{Str.Get("CantUndo")}"
        : Str.Get("CantUndo");

    public static string Body(Rule rule, int undoDays) => $"{rule.Consequence}\n\n{UndoLine(rule, undoDays)}";
}
