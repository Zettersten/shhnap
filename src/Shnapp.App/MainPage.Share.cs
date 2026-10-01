using Microsoft.UI.Xaml;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private void Share_Click(object sender, RoutedEventArgs args) => _controller?.ShareCurrentDocument();
}
