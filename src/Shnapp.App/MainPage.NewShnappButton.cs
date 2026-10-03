using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shnapp.Core;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private static readonly Brush NewShnappArrowHoverBrush =
        new SolidColorBrush(global::Windows.UI.Color.FromArgb(25, 255, 255, 255));

    private static readonly Brush NewShnappArrowPressedBrush =
        new SolidColorBrush(global::Windows.UI.Color.FromArgb(31, 0, 0, 0));

    private void NewShnappButton_Click(SplitButton sender, SplitButtonClickEventArgs args) =>
        _controller?.Capture(CaptureKind.Region);

    private void NewShnappDivider_Tapped(object sender, TappedRoutedEventArgs args)
    {
        NewShnappButton.Flyout?.ShowAt(NewShnappButton);
        args.Handled = true;
    }

    private void NewShnappArrow_PointerEntered(object sender, PointerRoutedEventArgs args) =>
        NewShnappArrowHoverOverlay.Background = NewShnappArrowHoverBrush;

    private void NewShnappArrow_PointerExited(object sender, PointerRoutedEventArgs args) =>
        NewShnappArrowHoverOverlay.Background = null;

    private void NewShnappArrow_PointerPressed(object sender, PointerRoutedEventArgs args) =>
        NewShnappArrowHoverOverlay.Background = NewShnappArrowPressedBrush;

    private void NewShnappArrow_PointerReleased(object sender, PointerRoutedEventArgs args) =>
        NewShnappArrowHoverOverlay.Background = NewShnappArrowHoverBrush;
}
