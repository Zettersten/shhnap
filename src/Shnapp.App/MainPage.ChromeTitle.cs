using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Shnapp.App;

public sealed partial class MainPage
{
    internal event EventHandler? TitleRenameStateChanged;
    private bool _editingToolsVisible;
    private bool _annotationToolbarObscured;

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

        // The title bar keeps only controls; document names remain available
        // through the native window title and the rename action in More.
        PrimaryCommandBar.Content = null;
        commandsHost.Content = PrimaryCommandBar;
    }

    internal bool IsTitleRenameActive => DocumentTitleEditor.Visibility == Visibility.Visible;
    internal bool EditingToolsVisible => _editingToolsVisible;

    internal void SetEditingToolsVisible(bool visible)
    {
        _editingToolsVisible = visible;
        UpdateAnnotationToolbarVisibility();
        CanvasHost.Focus(FocusState.Programmatic);
    }

    internal void SetAnnotationToolbarObscured(bool obscured)
    {
        _annotationToolbarObscured = obscured;
        UpdateAnnotationToolbarVisibility();
    }

    private void UpdateAnnotationToolbarVisibility()
    {
        AnnotationToolbarHost.Visibility = _editingToolsVisible && !_annotationToolbarObscured
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateInspectorPanelPlacement(EditorHost.ActualWidth < 860);
    }

    private void UpdateInspectorPanelPlacement(bool narrow) =>
        InspectorPanel.Margin = new Thickness(12,
            narrow && AnnotationToolbarHost.Visibility == Visibility.Visible ? 120 : 60,
            12, 50);
}
