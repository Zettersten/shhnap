using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shnapp.Core;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private void LibraryEmptyCapture_Click(SplitButton sender, SplitButtonClickEventArgs args) =>
        _controller?.Capture(CaptureKind.Region);

    private void LibraryEmptyCapture_Loaded(object sender, RoutedEventArgs args)
    {
        // The stock template reserves right padding for its narrow arrow segment.
        // Center the chevron inside our square 54 px segment instead.
        if (FindNamedDescendant(LibraryEmptyCapture, "SecondaryButton") is Button secondary)
        {
            secondary.Padding = new Thickness(0);
            secondary.HorizontalContentAlignment = HorizontalAlignment.Center;
        }
    }

    private static DependencyObject? FindNamedDescendant(DependencyObject root, string name)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element && element.Name == name)
            {
                return child;
            }

            if (FindNamedDescendant(child, name) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private void LibraryEmpty_SizeChanged(object sender, SizeChangedEventArgs args) => LayoutLibraryEmpty();

    private void LibraryEmptyContent_SizeChanged(object sender, SizeChangedEventArgs args) => LayoutLibraryEmptyScene();

    private void LayoutLibraryEmpty()
    {
        double width = LibraryEmpty.ViewportWidth > 0 ? LibraryEmpty.ViewportWidth : LibraryEmpty.ActualWidth;
        double height = LibraryEmpty.ViewportHeight > 0 ? LibraryEmpty.ViewportHeight : LibraryEmpty.ActualHeight;
        if (XamlRoot is { } root && root.Size.Width > 0)
        {
            width = Math.Min(width, root.Size.Width);
        }
        if (width <= 0 || height <= 0)
        {
            return;
        }

        bool hasNoResults = _library.Count > 0 && ViewModel.Library.Count == 0;
        double minimumHeight = width < 720 ? 760 : 640;
        LibraryEmptyContent.Width = width;
        LibraryEmptyContent.Height = hasNoResults ? height : Math.Max(height, minimumHeight);
        LayoutLibraryEmptyScene();
    }

    private void LayoutLibraryEmptyScene()
    {
        double width = LibraryEmptyContent.Width;
        double height = LibraryEmptyContent.Height;
        if (width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height))
        {
            return;
        }

        const double sourceWidth = 1448;
        const double sourceHeight = 1086;
        double scale = Math.Max(width / sourceWidth, height / sourceHeight);
        LibraryEmptyScene.RenderTransform = new CompositeTransform
        {
            ScaleX = scale,
            ScaleY = scale,
            TranslateX = (width - sourceWidth * scale) / 2,
        };
        LibraryEmptyArtwork.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };

        bool compact = width < 720;
        LibraryEmptyIntro.Margin = compact ? new Thickness(18, 24, 18, 0) : new Thickness(24, 48, 24, 0);
        LibraryEmptyIntro.MaxWidth = Math.Min(820, width - (compact ? 36 : 48));
        LibraryEmptyLogo.Width = compact ? 192 : width < 1100 ? 260 : 300;
        LibraryEmptyLogo.Height = compact ? 60 : 88;
        LibraryEmptyTitle.FontSize = compact ? 28 : width < 1100 ? 34 : 38;
        LibraryEmptyDescription.FontSize = compact ? 15 : width < 1100 ? 17 : 19;
        LibraryEmptyCaptureShell.Width = compact ? 272 : 306;
        LibraryEmptySparkleHost.Width = LibraryEmptyCaptureShell.Width - 54;

        bool highContrast = _themeSettings?.HighContrast ?? _accessibility.HighContrast;
        LibraryEmptyScene.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        LibraryEmptyLogo.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        if (highContrast)
        {
            UISettings colors = new();
            LibraryEmptyArtwork.Background = new SolidColorBrush(colors.GetColorValue(UIColorType.Background));
            SolidColorBrush foreground = new(colors.GetColorValue(UIColorType.Foreground));
            LibraryEmptyTitle.Foreground = foreground;
            LibraryEmptyDescription.Foreground = foreground;
        }
        else
        {
            LibraryEmptyArtwork.Background = null;
            LibraryEmptyTitle.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 248, 250, 255));
            LibraryEmptyDescription.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 200, 214, 242));
        }
    }
}
