using Microsoft.UI.Xaml;
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

    private void InitializeCaptureButtonRims()
    {
        WireCaptureButtonRim(NewShnappCaptureShell, NewShnappRimNormal,
            NewShnappRimHover, NewShnappRimPressed);
        WireCaptureButtonRim(LibraryEmptyCaptureShell, LibraryEmptyRimNormal,
            LibraryEmptyRimHover, LibraryEmptyRimPressed);
    }

    private static void WireCaptureButtonRim(FrameworkElement shell, Border normal, Border hover, Border pressed)
    {
        bool isPressed = false;
        Border current = normal;
        void Show(Border active)
        {
            if (ReferenceEquals(active, current))
            {
                return;
            }

            current = active;
            normal.Visibility = ReferenceEquals(active, normal) ? Visibility.Visible : Visibility.Collapsed;
            hover.Visibility = ReferenceEquals(active, hover) ? Visibility.Visible : Visibility.Collapsed;
            pressed.Visibility = ReferenceEquals(active, pressed) ? Visibility.Visible : Visibility.Collapsed;
        }

        shell.AddHandler(UIElement.PointerEnteredEvent,
            new PointerEventHandler((_, _) => Show(isPressed ? pressed : hover)), true);
        shell.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler((_, args) =>
            {
                var point = args.GetCurrentPoint(shell).Position;
                if (point.X >= 0 && point.X < shell.ActualWidth &&
                    point.Y >= 0 && point.Y < shell.ActualHeight)
                {
                    Show(isPressed ? pressed : hover);
                }
            }), true);
        shell.AddHandler(UIElement.PointerExitedEvent,
            new PointerEventHandler((_, args) =>
            {
                var point = args.GetCurrentPoint(shell).Position;
                if (point.X >= 0 && point.X < shell.ActualWidth &&
                    point.Y >= 0 && point.Y < shell.ActualHeight)
                {
                    return;
                }

                isPressed = false;
                Show(normal);
            }), true);
        shell.PointerCaptureLost += (_, _) =>
        {
            isPressed = false;
            Show(normal);
        };
        shell.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) =>
            {
                isPressed = true;
                Show(pressed);
            }), true);
        shell.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler((_, _) =>
            {
                isPressed = false;
                Show(hover);
            }), true);
    }

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
