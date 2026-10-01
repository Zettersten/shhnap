using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private const double GalleryThumbSize = 56;
    private const double GalleryThumbSpacing = 6;

    private IReadOnlyList<ShnappDocument> _galleryDocuments = [];
    private IReadOnlyList<ShnappDocument> _galleryVisibleDocuments = [];
    private readonly Dictionary<Button, double> _galleryTargets = [];
    private readonly UISettings _galleryMotionSettings = new();
    private int _galleryPage;
    private int _galleryPageSize = 1;
    private double _galleryWheelDelta;
    private long _galleryAnimationTimestamp;
    private bool _galleryAnimationRunning;

    /// <summary>Updates the image-only rail from the saved library, newest first.</summary>
    internal void SetGalleryDocuments(IReadOnlyList<ShnappDocument> documents)
    {
        _galleryDocuments = documents.OrderByDescending(document => document.CreatedAt).ToArray();
        _galleryPage = 0;
        _galleryWheelDelta = 0;
        RefreshGalleryDock();
    }

    private void GalleryCanvasHost_SizeChanged(object sender, SizeChangedEventArgs args) => RefreshGalleryDock();

    private void GalleryDock_ActualThemeChanged(FrameworkElement sender, object args) => RefreshGalleryDock();

    private void RefreshGalleryDock()
    {
        if (_controller is null || _editor is null || CanvasHost.ActualHeight < 100 || CanvasHost.ActualWidth < 160)
        {
            StopGalleryAnimation();
            GalleryDock.Visibility = Visibility.Collapsed;
            GalleryDockItems.Children.Clear();
            _galleryTargets.Clear();
            return;
        }

        Guid currentId = _editor.Current.Id;
        _galleryVisibleDocuments = _galleryDocuments.Where(document => document.Id != currentId).ToArray();
        _galleryPageSize = Math.Clamp((int)((CanvasHost.ActualHeight - 26) /
            (GalleryThumbSize + GalleryThumbSpacing)), 1, 6);
        if (_galleryVisibleDocuments.Count == 0)
        {
            StopGalleryAnimation();
            GalleryDock.Visibility = Visibility.Collapsed;
            GalleryDockItems.Children.Clear();
            _galleryTargets.Clear();
            return;
        }

        _galleryPage = Math.Clamp(_galleryPage, 0,
            Math.Max(0, (_galleryVisibleDocuments.Count - 1) / _galleryPageSize));
        RebuildGalleryPage();
    }

    private void RebuildGalleryPage(bool animatePage = false)
    {
        StopGalleryAnimation();
        GalleryDockItems.Children.Clear();
        _galleryTargets.Clear();

        foreach (ShnappDocument document in _galleryVisibleDocuments
            .Skip(_galleryPage * _galleryPageSize).Take(_galleryPageSize))
        {
            var entry = new LibraryEntry(document, _controller!.Library.GetPreviewPath(document.Id));
            var thumbnail = new Image
            {
                Source = entry.Preview,
                Stretch = Stretch.Uniform,
                Tag = entry,
                CanDrag = true,
            };
            var button = new Button
            {
                Tag = entry,
                Content = thumbnail,
                Width = GalleryThumbSize,
                Height = GalleryThumbSize,
                Padding = new Thickness(3),
                CornerRadius = new CornerRadius(8),
                Background = (Brush)Application.Current.Resources["ShnappPreviewBrush"],
                BorderThickness = new Thickness(0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1 },
            };
            AutomationProperties.SetName(button, $"{entry.Title}. Drag to canvas to add a copy, or press Enter to open.");
            AutomationProperties.SetAutomationId(button, $"GalleryShnapp_{entry.Id:N}");
            button.Click += GalleryDockItem_Click;
            button.PointerMoved += GalleryDock_PointerMoved;
            button.PointerWheelChanged += GalleryDock_PointerWheelChanged;
            thumbnail.DragStarting += LibraryPreview_DragStarting;
            GalleryDockItems.Children.Add(button);
            _galleryTargets.Add(button, 1);
        }

        GalleryDock.Visibility = Visibility.Visible;
        GalleryDockItems.Opacity = animatePage && _galleryMotionSettings.AnimationsEnabled ? 0.55 : 1;
    }

    private void GalleryDockItem_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: LibraryEntry entry })
        {
            _controller?.OpenDocument(entry.Id);
        }
    }

    private void GalleryDock_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        double pointerY = args.GetCurrentPoint(GalleryDockItems).Position.Y;
        for (int index = 0; index < GalleryDockItems.Children.Count; index++)
        {
            if (GalleryDockItems.Children[index] is not Button button)
            {
                continue;
            }

            double centerY = index * (GalleryThumbSize + GalleryThumbSpacing) + GalleryThumbSize / 2;
            double distance = (pointerY - centerY) / (GalleryThumbSize + GalleryThumbSpacing);
            double scale = 1 + 0.32 * Math.Exp(-distance * distance / 1.6);
            Canvas.SetZIndex(button, (int)Math.Round(scale * 100));
            _galleryTargets[button] = scale;
        }

        AnimateGalleryTowardTargets();
    }

    private void GalleryDock_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        foreach (Button button in _galleryTargets.Keys)
        {
            _galleryTargets[button] = 1;
        }

        AnimateGalleryTowardTargets();
    }

    private void GalleryDock_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        args.Handled = true;
        int pages = (_galleryVisibleDocuments.Count + _galleryPageSize - 1) / _galleryPageSize;
        if (pages <= 1)
        {
            return;
        }

        _galleryWheelDelta += args.GetCurrentPoint(GalleryDock).Properties.MouseWheelDelta;
        if (Math.Abs(_galleryWheelDelta) < 100)
        {
            return;
        }

        int direction = _galleryWheelDelta < 0 ? 1 : -1;
        _galleryWheelDelta = 0;
        _galleryPage = (_galleryPage + direction + pages) % pages;
        RebuildGalleryPage(animatePage: true);
        GalleryDock_PointerMoved(sender, args);
    }

    private void AnimateGalleryTowardTargets()
    {
        // A pointer can move over the dock dozens of times per second. One shared rendering
        // callback eases all visible neighbors without creating a storyboard per pointer event.
        if (!_galleryMotionSettings.AnimationsEnabled)
        {
            StopGalleryAnimation();
            foreach ((Button button, double target) in _galleryTargets)
            {
                if (button.RenderTransform is ScaleTransform transform)
                {
                    transform.ScaleX = target;
                    transform.ScaleY = target;
                }
            }

            GalleryDockItems.Opacity = 1;

            return;
        }

        if (!_galleryAnimationRunning)
        {
            _galleryAnimationTimestamp = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += GalleryAnimation_Rendering;
            _galleryAnimationRunning = true;
        }
    }

    private void GalleryAnimation_Rendering(object? sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = Math.Clamp(Stopwatch.GetElapsedTime(_galleryAnimationTimestamp, now).TotalMilliseconds, 0, 50);
        _galleryAnimationTimestamp = now;
        double step = 1 - Math.Exp(-elapsed / 42);
        bool settled = true;
        foreach ((Button button, double target) in _galleryTargets)
        {
            if (button.RenderTransform is not ScaleTransform transform)
            {
                continue;
            }

            double next = transform.ScaleX + (target - transform.ScaleX) * step;
            if (Math.Abs(next - target) < 0.005)
            {
                next = target;
            }
            else
            {
                settled = false;
            }

            transform.ScaleX = next;
            transform.ScaleY = next;
        }

        if (GalleryDockItems.Opacity < 0.995)
        {
            GalleryDockItems.Opacity += (1 - GalleryDockItems.Opacity) * step;
            settled = false;
        }
        else
        {
            GalleryDockItems.Opacity = 1;
        }

        if (settled)
        {
            StopGalleryAnimation();
        }
    }

    private void StopGalleryAnimation()
    {
        if (_galleryAnimationRunning)
        {
            CompositionTarget.Rendering -= GalleryAnimation_Rendering;
            _galleryAnimationRunning = false;
        }
    }
}
