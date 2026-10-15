using Microsoft.UI.Xaml;
using Shnapp.App.Editor;

namespace Shnapp.App;

/// <summary>Routes window chrome actions into the existing editor commands.</summary>
public sealed partial class MainPage
{
    private void FooterBar_SizeChanged(object sender, SizeChangedEventArgs args) =>
        FooterFitButton.Visibility = args.NewSize.Width < 650 ? Visibility.Collapsed : Visibility.Visible;

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
    internal void CopyFromChrome() => Copy_Click(this, new RoutedEventArgs());
    internal void SaveFromChrome() => Save_Click(this, new RoutedEventArgs());

    private void Fullscreen_Click(object sender, RoutedEventArgs args) => ((MainWindow)App.Window).ToggleFullScreen();
}
