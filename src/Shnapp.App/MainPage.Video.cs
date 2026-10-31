using Microsoft.UI.Xaml;
using Shnapp.Core;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private void VideoCapture_Click(object sender, RoutedEventArgs args) =>
        _controller?.RecordVideo(Enum.Parse<CaptureKind>((string)((FrameworkElement)sender).Tag));
}
