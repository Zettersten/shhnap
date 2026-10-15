using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Shnapp.App;

public sealed partial class MainPage
{
    internal event EventHandler? TitleRenameStateChanged;

    // Keep the existing page commands and their handlers while displaying them
    // in the single window title bar. The canvas remains the full page size.
    internal void AttachTitleChrome(ContentControl commandsHost, StackPanel navigationHost, Grid titleEditorHost)
    {
        ((InteractiveCursorHost)Content).Children.Remove(PrimaryCommandBar);
        PrimaryChromeContent.Children.Remove(BackButton);
        PrimaryChromeContent.Children.Remove(ForwardButton);
        navigationHost.Children.Add(BackButton);
        navigationHost.Children.Add(ForwardButton);

        DocumentChromeTitleHost.Children.Remove(DocumentTitleEditor);
        DocumentTitleEditor.Width = 180;
        DocumentTitleEditor.Height = 32;
        DocumentTitleEditor.Margin = new Thickness(0);
        titleEditorHost.Children.Add(DocumentTitleEditor);

        // The old page headings are superseded by the centered window title.
        PrimaryCommandBar.Content = null;
        commandsHost.Content = PrimaryCommandBar;
    }

    internal void StartTitleRenameFromChrome() => DocumentTitleButton_Click(this, new RoutedEventArgs());
    internal bool IsTitleRenameActive => DocumentTitleEditor.Visibility == Visibility.Visible;
    internal void SetAnnotationToolbarObscured(bool obscured) =>
        AnnotationToolbarHost.Visibility = obscured ? Visibility.Collapsed : Visibility.Visible;
}
