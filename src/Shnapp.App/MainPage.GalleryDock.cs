using System.Diagnostics;
using Microsoft.UI;
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
    private const double GalleryHiddenOffset = -96;
    private const double GalleryRevealDistance = 28;
    private const double GalleryHideDistance = 122;

    private IReadOnlyList<ShnappSummary> _galleryDocuments = [];
    private IReadOnlyList<ShnappSummary> _galleryVisibleDocuments = [];
    private readonly Dictionary<Button, double> _galleryTargets = [];
    private readonly UISettings _galleryMotionSettings = new();
    private int _galleryPage;
    private int _galleryPageSize = 1;
    private double _galleryWheelDelta;
    private long _galleryAnimationTimestamp;
    private bool _galleryAnimationRunning;
    private bool _galleryAvailable;
    private double _galleryRevealTarget;
    private Button? _galleryHoveredButton;
    private Button? _galleryPressedButton;
    private Point _galleryPressedPosition;
    private bool _galleryDragStarted;
    private Guid _galleryLastOpenedId;
    private long _galleryLastOpenedAt;

    /// <summary>Updates the image-only rail from the saved library, newest first.</summary>
    internal void SetGalleryDocuments(IReadOnlyList<ShnappSummary> documents)
    {
        _galleryDocuments = documents.OrderByDescending(document => document.CreatedAt).ToArray();
        _galleryPage = 0;
        _galleryWheelDelta = 0;
        RefreshGalleryDock();
    }

    private void GalleryCanvasHost_SizeChanged(object sender, SizeChangedEventArgs args) => RefreshGalleryDock();

    private void GalleryDock_ActualThemeChanged(FrameworkElement sender, object args) => RefreshGalleryDock();

    private void GalleryCanvasHost_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_galleryAvailable)
        {
            return;
        }

        var point = args.GetCurrentPoint(CanvasHost);
        if (point.Position.X <= GalleryRevealDistance)
        {
            SetGalleryReveal(true);
        }
        else if (point.Position.X > GalleryHideDistance && !point.Properties.IsLeftButtonPressed &&
            !GalleryHasKeyboardFocus())
        {
            SetGalleryReveal(false);
        }
    }

    private void GalleryCanvasHost_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (!GalleryHasKeyboardFocus())
        {
            SetGalleryReveal(false);
        }
    }

    private bool GalleryHasKeyboardFocus()
    {
        DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, GalleryDock))
            {
                return true;
            }

            focused = VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    private void ToggleGallery_Click(object sender, RoutedEventArgs args) => ToggleGallery();

    private void ToggleGalleryAccelerator_Invoked(KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_editor is null || !_galleryAvailable)
        {
            return;
        }

        ToggleGallery();
        args.Handled = true;
    }

    private void ToggleGallery()
    {
        if (!_galleryAvailable)
        {
            return;
        }

        bool reveal = _galleryRevealTarget == 0;
        SetGalleryReveal(reveal);
        if (reveal)
        {
            (GalleryDockItems.Children.OfType<Button>().FirstOrDefault() ?? GalleryNext)
                .Focus(FocusState.Keyboard);
        }
        else
        {
            DrawingCanvas.Focus(FocusState.Programmatic);
        }
    }

    private void RefreshGalleryDock()
    {
        if (_controller is null || _editor is null || CanvasHost.ActualHeight < 128 || CanvasHost.ActualWidth < 160)
        {
            StopGalleryAnimation();
            _galleryAvailable = false;
            _galleryRevealTarget = 0;
            GalleryDock.Visibility = Visibility.Collapsed;
            GalleryDock.Opacity = 0;
            if (GalleryDock.RenderTransform is TranslateTransform hiddenTransform)
            {
                hiddenTransform.X = GalleryHiddenOffset;
            }
            GalleryDockItems.Children.Clear();
            _galleryTargets.Clear();
            return;
        }

        Guid currentId = _editor.Current.Id;
        _galleryVisibleDocuments = _galleryDocuments.Where(document => document.Id != currentId).ToArray();
        _galleryPageSize = Math.Clamp((int)((CanvasHost.ActualHeight - 88) /
            (GalleryThumbSize + GalleryThumbSpacing)), 1, 6);
        if (_galleryVisibleDocuments.Count == 0)
        {
            StopGalleryAnimation();
            _galleryAvailable = false;
            _galleryRevealTarget = 0;
            GalleryDock.Visibility = Visibility.Collapsed;
            GalleryDock.Opacity = 0;
            if (GalleryDock.RenderTransform is TranslateTransform hiddenTransform)
            {
                hiddenTransform.X = GalleryHiddenOffset;
            }
            GalleryDockItems.Children.Clear();
            _galleryTargets.Clear();
            return;
        }

        _galleryAvailable = true;
        _galleryPage = Math.Clamp(_galleryPage, 0,
            Math.Max(0, (_galleryVisibleDocuments.Count - 1) / _galleryPageSize));
        RebuildGalleryPage();
    }

    private void RebuildGalleryPage(bool animatePage = false)
    {
        StopGalleryAnimation();
        GalleryDockItems.Children.Clear();
        _galleryTargets.Clear();
        _galleryHoveredButton = null;
        _galleryPressedButton = null;

        int index = 0;
        foreach (ShnappSummary document in _galleryVisibleDocuments
            .Skip(_galleryPage * _galleryPageSize).Take(_galleryPageSize))
        {
            string dockPreview = _controller!.Library.GetGalleryPreviewPath(document.Id);
            var entry = new LibraryEntry(document,
                File.Exists(dockPreview) ? dockPreview : _controller.Library.GetPreviewPath(document.Id),
                previewPixelWidth: 160);
            var thumbnail = new Image
            {
                Source = entry.Preview,
                Stretch = Stretch.UniformToFill,
                Tag = entry,
                CanDrag = true,
            };
            var transparent = new SolidColorBrush(Colors.Transparent);
            var focusBorder = (Brush)Application.Current.Resources["ShnappBlueBrush"];
            var button = new Button
            {
                Tag = entry,
                Content = thumbnail,
                Width = GalleryThumbSize,
                Height = GalleryThumbSize,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Background = transparent,
                BorderBrush = focusBorder,
                BorderThickness = new Thickness(0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1 },
            };
            button.Resources["ButtonBackgroundPointerOver"] = transparent;
            button.Resources["ButtonBackgroundPressed"] = transparent;
            button.Resources["ButtonBackgroundDisabled"] = transparent;
            button.Resources["ButtonBorderBrushPointerOver"] = focusBorder;
            button.Resources["ButtonBorderBrushPressed"] = focusBorder;
            AutomationProperties.SetName(button, $"{entry.Title}. Click to open or drag to the canvas to add a copy.");
            AutomationProperties.SetAutomationId(button, $"GalleryShnapp_{entry.Id:N}");
            button.Click += GalleryDockItem_Click;
            thumbnail.Tapped += GalleryDockItem_Tapped;
            thumbnail.DragStarting += GalleryDockItem_DragStarting;
            thumbnail.DropCompleted += GalleryDockItem_DropCompleted;
            button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(GalleryDockItem_PointerPressed), true);
            button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(GalleryDockItem_PointerReleased), true);
            button.PointerEntered += GalleryDockItem_PointerEntered;
            button.PointerExited += GalleryDockItem_PointerExited;
            button.GotFocus += GalleryDockItem_FocusChanged;
            button.LostFocus += GalleryDockItem_FocusChanged;
            button.PointerMoved += GalleryDock_PointerMoved;
            button.PointerWheelChanged += GalleryDock_PointerWheelChanged;
            Canvas.SetLeft(button, 1);
            Canvas.SetTop(button, index * (GalleryThumbSize + GalleryThumbSpacing));
            GalleryDockItems.Children.Add(button);
            _galleryTargets.Add(button, 1);
            index++;
        }

        GalleryDockItems.Height = Math.Max(0, index * (GalleryThumbSize + GalleryThumbSpacing) - GalleryThumbSpacing);
        GalleryPrevious.IsEnabled = _galleryPage > 0;
        GalleryPrevious.Opacity = GalleryPrevious.IsEnabled ? 1 : 0.3;
        GalleryNext.IsEnabled = (_galleryPage + 1) * _galleryPageSize < _galleryVisibleDocuments.Count;
        GalleryNext.Opacity = GalleryNext.IsEnabled ? 1 : 0.3;
        GalleryDock.Visibility = _galleryRevealTarget > 0 ? Visibility.Visible : Visibility.Collapsed;
        GalleryDockItems.Opacity = animatePage && _galleryMotionSettings.AnimationsEnabled ? 0.55 : 1;
        if (GalleryDock.Visibility == Visibility.Visible &&
            (GalleryDockItems.Opacity < 1 || Math.Abs(GalleryDock.Opacity - _galleryRevealTarget) > 0.005))
        {
            AnimateGalleryTowardTargets();
        }
    }

    private void GalleryDockItem_Click(object sender, RoutedEventArgs args)
    {
        if (!_galleryDragStarted && sender is Button { Tag: LibraryEntry entry })
        {
            ActivateGalleryEntry(entry);
        }
    }

    private void GalleryDockItem_Tapped(object sender, TappedRoutedEventArgs args)
    {
        if (!_galleryDragStarted && sender is Image { Tag: LibraryEntry entry })
        {
            ActivateGalleryEntry(entry);
            args.Handled = true;
        }
    }

    private void GalleryDockItem_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Button button && args.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            _galleryPressedButton = button;
            _galleryPressedPosition = args.GetCurrentPoint(button).Position;
            _galleryDragStarted = false;
        }
    }

    private void GalleryDockItem_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Button { Tag: LibraryEntry entry } button ||
            !ReferenceEquals(_galleryPressedButton, button))
        {
            return;
        }

        _galleryPressedButton = null;
        Point released = args.GetCurrentPoint(button).Position;
        if (!_galleryDragStarted && Math.Abs(released.X - _galleryPressedPosition.X) < 12 &&
            Math.Abs(released.Y - _galleryPressedPosition.Y) < 12)
        {
            ActivateGalleryEntry(entry);
        }
    }

    private void GalleryDockItem_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        _galleryDragStarted = true;
        _galleryPressedButton = null;
        LibraryPreview_DragStarting(sender, args);
    }

    private void GalleryDockItem_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        _galleryDragStarted = false;
        _galleryPressedButton = null;
        SetGalleryReveal(false);
    }

    private void ActivateGalleryEntry(LibraryEntry entry)
    {
        // A Button can report Click as well as PointerReleased for the same gesture.
        long now = Stopwatch.GetTimestamp();
        if (_galleryLastOpenedId == entry.Id && _galleryLastOpenedAt != 0 &&
            Stopwatch.GetElapsedTime(_galleryLastOpenedAt, now).TotalMilliseconds < 600)
        {
            return;
        }

        _galleryLastOpenedId = entry.Id;
        _galleryLastOpenedAt = now;
        _controller?.OpenDocument(entry.Id);
    }

    private void GalleryDockItem_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Button button)
        {
            _galleryHoveredButton = button;
            UpdateGalleryButtonBorder(button);
        }
    }

    private void GalleryDockItem_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Button button)
        {
            if (ReferenceEquals(_galleryHoveredButton, button))
            {
                _galleryHoveredButton = null;
            }

            UpdateGalleryButtonBorder(button);
        }
    }

    private void GalleryDockItem_FocusChanged(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            UpdateGalleryButtonBorder(button);
        }
    }

    private void UpdateGalleryButtonBorder(Button button)
    {
        bool highlighted = ReferenceEquals(_galleryHoveredButton, button) || button.FocusState != FocusState.Unfocused;
        button.BorderThickness = new Thickness(highlighted ? 2 : 0);
    }

    private void GalleryPrevious_Click(object sender, RoutedEventArgs args) => ChangeGalleryPage(-1);

    private void GalleryNext_Click(object sender, RoutedEventArgs args) => ChangeGalleryPage(1);

    private void ChangeGalleryPage(int direction)
    {
        int pages = (_galleryVisibleDocuments.Count + _galleryPageSize - 1) / _galleryPageSize;
        int next = Math.Clamp(_galleryPage + direction, 0, Math.Max(0, pages - 1));
        if (next != _galleryPage)
        {
            _galleryPage = next;
            RebuildGalleryPage(animatePage: true);
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
        ChangeGalleryPage(direction);
        GalleryDock_PointerMoved(sender, args);
    }

    private void SetGalleryReveal(bool reveal)
    {
        if (!_galleryAvailable || _galleryRevealTarget == (reveal ? 1 : 0))
        {
            return;
        }

        _galleryRevealTarget = reveal ? 1 : 0;
        if (reveal)
        {
            GalleryDock.Visibility = Visibility.Visible;
        }

        AnimateGalleryTowardTargets();
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
            GalleryDock.Opacity = _galleryRevealTarget;
            if (GalleryDock.RenderTransform is TranslateTransform translation)
            {
                translation.X = GalleryHiddenOffset * (1 - _galleryRevealTarget);
            }

            if (_galleryRevealTarget == 0)
            {
                GalleryDock.Visibility = Visibility.Collapsed;
            }

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

        double reveal = GalleryDock.Opacity + (_galleryRevealTarget - GalleryDock.Opacity) * step;
        if (Math.Abs(reveal - _galleryRevealTarget) < 0.005)
        {
            reveal = _galleryRevealTarget;
        }
        else
        {
            settled = false;
        }

        GalleryDock.Opacity = reveal;
        if (GalleryDock.RenderTransform is TranslateTransform translation)
        {
            translation.X = GalleryHiddenOffset * (1 - reveal);
        }

        if (settled)
        {
            if (_galleryRevealTarget == 0)
            {
                GalleryDock.Visibility = Visibility.Collapsed;
            }

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
