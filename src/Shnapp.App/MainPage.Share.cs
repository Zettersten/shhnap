using Microsoft.UI.Xaml;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private void Share_Click(object sender, RoutedEventArgs args) => _controller?.ShareCurrentDocument();

    private void CopyCurrentPath_Click(object sender, RoutedEventArgs args) => _controller?.CopyCurrentDocumentPath();

    private void DeleteCurrent_Click(object sender, RoutedEventArgs args)
    {
        if (Document is { } document)
        {
            _controller?.RequestDeleteDocuments([document.Id]);
        }
    }
}
