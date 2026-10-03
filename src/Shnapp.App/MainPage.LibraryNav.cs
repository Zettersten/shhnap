using Microsoft.UI.Xaml;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private void LibraryNav_Click(object sender, RoutedEventArgs args)
    {
        _controller?.OpenLibrary();
        if (!ViewModel.HasDocument)
        {
            // Clicking an already selected toggle must not clear the page indicator.
            LibraryNavButton.IsChecked = true;
        }
    }
}
