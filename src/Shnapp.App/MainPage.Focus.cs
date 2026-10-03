using Microsoft.UI.Xaml;

namespace Shnapp.App;

public sealed partial class MainPage
{
    internal void FocusDefaultAction()
    {
        if (ViewModel.LibraryVisibility == Visibility.Visible)
        {
            LibrarySearch.Focus(FocusState.Programmatic);
        }
        else
        {
            DrawingCanvas.Focus(FocusState.Programmatic);
        }
    }
}
