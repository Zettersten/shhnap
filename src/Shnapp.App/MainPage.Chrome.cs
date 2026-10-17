using Microsoft.UI.Xaml;
using Shnapp.App.Editor;
using Shnapp.Core;

namespace Shnapp.App;

/// <summary>Routes window chrome actions into the existing editor commands.</summary>
public sealed partial class MainPage
{
    private void FooterBar_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        double width = args.NewSize.Width;
        FooterFitButton.Visibility = width < 650 ? Visibility.Collapsed : Visibility.Visible;
        FooterFullscreenButton.Visibility = width < 520 ? Visibility.Collapsed : Visibility.Visible;
        FooterGalleryButton.Visibility = width < 430 ? Visibility.Collapsed : Visibility.Visible;

        // Equal side columns keep the utilities centered at regular widths. At
        // very narrow widths, reserve the zoom controls' actual width first.
        FooterRightColumn.Width = width < 370
            ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
    }

    internal void FocusEditorFromChrome()
    {
        if (_editor is null)
        {
            return;
        }

        SetTool(EditorTool.Select);
        CanvasHost.Focus(FocusState.Programmatic);
    }

    internal void UndoFromChrome() => Undo_Click(this, new RoutedEventArgs());
    internal void RedoFromChrome() => Redo_Click(this, new RoutedEventArgs());
    internal void CaptureRegionFromChrome() => _controller?.Capture(CaptureKind.Region);
    private void SaveCopy_Click(object sender, RoutedEventArgs args) => _controller?.CloneCurrentDocument();

    private void Fullscreen_Click(object sender, RoutedEventArgs args) => ((MainWindow)App.Window).ToggleFullScreen();
}
