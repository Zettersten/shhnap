using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Shnapp.App;

public sealed partial class MainPage
{
    internal event EventHandler? TitleRenameStateChanged;
    private bool _chromeEditorAvailable;

    // Keep the existing page commands and their handlers while displaying them
    // in the single window title bar. The canvas remains the full page size.
    internal void AttachTitleChrome(ContentControl commandsHost, StackPanel navigationHost,
        ContentControl toolsHost, ContentControl libraryHost, Grid titleEditorHost)
    {
        ((InteractiveCursorHost)Content).Children.Remove(PrimaryCommandBar);
        PrimaryChromeContent.Children.Remove(BackButton);
        PrimaryChromeContent.Children.Remove(ForwardButton);
        navigationHost.Children.Add(BackButton);
        navigationHost.Children.Add(ForwardButton);
        AnnotationToolbarHost.Children.Remove(Tools);
        toolsHost.Content = Tools;
        PrimaryCommandBar.PrimaryCommands.Remove(LibraryChromeHost);
        LibraryChromeHost.Content = null;
        libraryHost.Content = LibraryNavButton;

        DocumentChromeTitleHost.Children.Remove(DocumentTitleEditor);
        DocumentTitleEditor.Width = 180;
        DocumentTitleEditor.Height = 32;
        DocumentTitleEditor.Margin = new Thickness(0);
        titleEditorHost.Children.Add(DocumentTitleEditor);

        // The title bar keeps only controls; document names remain available
        // through the native window title and the rename action in More.
        PrimaryCommandBar.Content = null;
        commandsHost.Content = PrimaryCommandBar;
    }

    internal bool IsTitleRenameActive => DocumentTitleEditor.Visibility == Visibility.Visible;

    internal void HandleChromeKeyDown(KeyRoutedEventArgs args)
    {
        Page_PreviewKeyDown(this, args);
        Page_KeyDown(this, args);
    }

    internal void HandleChromeKeyUp(KeyRoutedEventArgs args) => Page_KeyUp(this, args);

    internal void SetChromeToolAvailability(bool editorAvailable)
    {
        _chromeEditorAvailable = editorAvailable;
        foreach (UIElement child in Tools.Children)
        {
            if (child is ToolbarActionButton button)
            {
                button.IsEnabled = editorAvailable &&
                    (!ReferenceEquals(button, CameraPhotoToolButton) || _cameraAvailable);
            }
        }
    }
}
