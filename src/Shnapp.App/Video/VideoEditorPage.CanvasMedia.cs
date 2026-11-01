using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shnapp.Core;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.System;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private enum MediaHandle { Move, NorthWest, North, NorthEast, East, SouthEast, South, SouthWest, West }

    private readonly Canvas _canvasMedia = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = true,
    };
    private readonly Dictionary<Guid, Border> _previewVideoHitViews = [];
    private readonly List<(MediaHandle Handle, Border View)> _canvasMediaHandles = [];
    private readonly Border _canvasMediaFrame = new()
    {
        Background = new SolidColorBrush(Colors.Transparent),
        BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 10, 132, 255)),
        BorderThickness = new Thickness(1),
        Visibility = Visibility.Collapsed,
    };
    private readonly ContentControl _canvasMediaFocusTarget = new()
    {
        Width = 1,
        Height = 1,
        Opacity = .01,
        IsTabStop = true,
        Visibility = Visibility.Collapsed,
    };
    private readonly Border _canvasVideoProxy = new()
    {
        Visibility = Visibility.Collapsed,
        Opacity = .88,
        IsHitTestVisible = false,
    };
    private readonly Image _canvasVideoProxyImage = new()
    {
        Stretch = Stretch.Fill,
        IsHitTestVisible = false,
    };
    private readonly ToggleButton _canvasMediaCropButton = new()
    {
        Width = 28,
        Height = 28,
        MinWidth = 0,
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Colors.Transparent),
        BorderThickness = new Thickness(0),
        Content = new FontIcon
        {
            Glyph = "\uE7A8",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 12,
        },
    };
    private readonly Button _canvasMediaResetCropButton = new()
    {
        Width = 28,
        Height = 28,
        MinWidth = 0,
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Colors.Transparent),
        BorderThickness = new Thickness(0),
        Content = new FontIcon
        {
            Glyph = "\uE777",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 12,
        },
    };
    private readonly Button _canvasMediaPropertiesButton = new()
    {
        Width = 28,
        Height = 28,
        MinWidth = 0,
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Colors.Transparent),
        BorderThickness = new Thickness(0),
        Content = new FontIcon
        {
            Glyph = "\uE713",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 12,
        },
    };
    private readonly Button _canvasMediaShapeButton = new()
    {
        Width = 28,
        Height = 28,
        MinWidth = 0,
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Colors.Transparent),
        BorderThickness = new Thickness(0),
        Content = new FontIcon
        {
            Glyph = "\uE7FB",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 12,
        },
    };
    private readonly Button _canvasMediaDeleteButton = new()
    {
        Width = 28,
        Height = 28,
        MinWidth = 0,
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Colors.Transparent),
        BorderThickness = new Thickness(0),
        Content = new FontIcon
        {
            Glyph = "\uE74D",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 12,
        },
    };
    private readonly Border _canvasMediaQuickbar = new()
    {
        Visibility = Visibility.Collapsed,
        Background = new SolidColorBrush(ColorHelper.FromArgb(229, 25, 33, 48)),
        BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(150, 115, 132, 155)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(3, 2, 3, 2),
    };
    private TimeSpan[] _canvasMediaBoundaries = [];
    private TimeSpan _canvasMediaIntervalStart = TimeSpan.MaxValue;
    private TimeSpan _canvasMediaIntervalEnd = TimeSpan.MinValue;
    private bool _canvasMediaTransitionActive;
    private PausedSource[] _activeVideoHitVisuals = [];
    private TimelineKind _canvasMediaSelectedKind;
    private Guid _canvasMediaSelectedId;
    private FrameworkElement? _canvasMediaCapture;
    private uint? _canvasMediaPointerId;
    private Point _canvasMediaPointerStart;
    private MediaHandle _canvasMediaGesture;
    private VideoRect _canvasMediaStartBounds;
    private VideoRect _canvasMediaStartCrop;
    private VideoRect? _canvasMediaDraftBounds;
    private VideoRect? _canvasMediaDraftCrop;
    private bool? _canvasMediaAxisLockHorizontal;
    private bool _canvasMediaNudgeEdit;
    private Guid _canvasProxyAssetId;
    private long _canvasProxyGeneration;

    private void InitializeCanvasMedia()
    {
        UpdateCanvasMediaTheme();
        ActualThemeChanged += (_, _) => UpdateCanvasMediaTheme();
        VideoPlayer.IsTabStop = true;
        AutomationProperties.SetName(VideoPlayer, "Video canvas");
        AutomationProperties.SetHelpText(VideoPlayer,
            "With an image or video selected, arrow keys move it one pixel; hold Shift for ten pixels.");
        PreviewHost.Children.Insert(PreviewHost.Children.IndexOf(_previewImages) + 1,
            _canvasMedia);

        var proxyCanvas = new Canvas { IsHitTestVisible = false };
        proxyCanvas.Children.Add(_canvasVideoProxyImage);
        _canvasVideoProxy.Child = proxyCanvas;
        _canvasMedia.Children.Add(_canvasVideoProxy);
        AutomationProperties.SetName(_canvasMediaFocusTarget,
            "Selected video canvas media");
        AutomationProperties.SetHelpText(_canvasMediaFocusTarget,
            "Arrow keys move the selected image or video by one pixel; hold Shift for ten pixels. Space plays or pauses.");
        _canvasMediaFocusTarget.GotFocus += (_, _) =>
            _canvasMediaFrame.BorderThickness = new Thickness(2);
        _canvasMediaFocusTarget.LostFocus += (_, _) =>
            _canvasMediaFrame.BorderThickness = new Thickness(1);
        VideoPlayer.GotFocus += (_, _) =>
            _canvasMediaFrame.BorderThickness = new Thickness(2);
        VideoPlayer.LostFocus += (_, _) =>
            _canvasMediaFrame.BorderThickness = new Thickness(1);
        _canvasMedia.Children.Add(_canvasMediaFocusTarget);

        _canvasMediaFrame.PointerPressed += MediaSurface_PointerPressed;
        _canvasMediaFrame.PointerMoved += MediaSurface_PointerMoved;
        _canvasMediaFrame.PointerReleased += MediaSurface_PointerReleased;
        _canvasMediaFrame.PointerCaptureLost += MediaSurface_PointerCaptureLost;
        _canvasMedia.Children.Add(_canvasMediaFrame);

        foreach (MediaHandle handle in Enum.GetValues<MediaHandle>())
        {
            if (handle == MediaHandle.Move) continue;
            var view = new Border
            {
                Width = handle is MediaHandle.North or MediaHandle.East or
                    MediaHandle.South or MediaHandle.West ? 9 : 12,
                Height = handle is MediaHandle.North or MediaHandle.East or
                    MediaHandle.South or MediaHandle.West ? 9 : 12,
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 10, 132, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Tag = handle,
                Visibility = Visibility.Collapsed,
            };
            view.PointerPressed += MediaHandle_PointerPressed;
            view.PointerMoved += MediaSurface_PointerMoved;
            view.PointerReleased += MediaSurface_PointerReleased;
            view.PointerCaptureLost += MediaSurface_PointerCaptureLost;
            AutomationProperties.SetName(view, $"Resize or crop {handle.ToString().ToLowerInvariant()}");
            ToolTipService.SetToolTip(view,
                "Drag to resize or crop · hold Shift to keep proportions");
            _canvasMediaHandles.Add((handle, view));
            _canvasMedia.Children.Add(view);
        }

        ToolTipService.SetToolTip(_canvasMediaCropButton,
            "Crop source · Ctrl+Shift+C · hold Shift to keep proportions");
        AutomationProperties.SetName(_canvasMediaCropButton, "Crop selected media");
        ToolTipService.SetToolTip(_canvasMediaResetCropButton,
            "Reset source crop · Ctrl+Shift+R");
        AutomationProperties.SetName(_canvasMediaResetCropButton, "Reset selected media crop");
        _canvasMediaResetCropButton.Click += CanvasMediaResetCrop_Click;
        ToolTipService.SetToolTip(_canvasMediaPropertiesButton, "Show clip properties");
        AutomationProperties.SetName(_canvasMediaPropertiesButton, "Show clip properties");
        AutomationProperties.SetAutomationId(_canvasMediaPropertiesButton,
            "ShowSelectedMediaProperties");
        _canvasMediaPropertiesButton.Click += CanvasMediaProperties_Click;
        InitializeVideoShapeMenu();
        ToolTipService.SetToolTip(_canvasMediaDeleteButton, "Remove selected media · Delete");
        AutomationProperties.SetName(_canvasMediaDeleteButton, "Remove selected media");
        _canvasMediaDeleteButton.Click += (_, _) => DeleteSelected(ripple: false);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        actions.Children.Add(_canvasMediaCropButton);
        actions.Children.Add(_canvasMediaResetCropButton);
        actions.Children.Add(_canvasMediaShapeButton);
        actions.Children.Add(_canvasMediaPropertiesButton);
        actions.Children.Add(_canvasMediaDeleteButton);
        _canvasMediaQuickbar.Child = actions;
        _canvasMedia.Children.Add(_canvasMediaQuickbar);
        RegisterCanvasMediaShortcuts();
    }

    private void UpdateCanvasMediaTheme()
    {
        string key = _accessibility.HighContrast ? "HighContrast" :
            ActualTheme == ElementTheme.Light ? "Light" : "Default";
        if (Application.Current.Resources.ThemeDictionaries[key] is not ResourceDictionary theme)
            return;
        _canvasMediaQuickbar.Background = (Brush)theme["ShnappImagePanelBrush"];
        _canvasMediaQuickbar.BorderBrush = (Brush)theme["ShnappFloatingEdgeBrush"];
    }

    private void ReleaseCanvasMedia()
    {
        ++_canvasProxyGeneration;
        _canvasMediaPointerId = null;
        _canvasMediaCapture = null;
        foreach (Border hit in _previewVideoHitViews.Values)
            _previewImages.Children.Remove(hit);
        _previewVideoHitViews.Clear();
        _canvasMediaFrame.Visibility = Visibility.Collapsed;
        _canvasMediaFocusTarget.Visibility = Visibility.Collapsed;
        _canvasVideoProxy.Visibility = Visibility.Collapsed;
        _canvasMediaQuickbar.Visibility = Visibility.Collapsed;
        foreach (var (_, view) in _canvasMediaHandles)
            view.Visibility = Visibility.Collapsed;
    }

    private static bool CanvasLayerVisible(VideoProject project, Guid id, Guid layerId) =>
        !project.HiddenLayerIds.Contains(layerId == Guid.Empty ? id : layerId);

    private void RefreshCanvasMedia()
    {
        if (_editor is null || _closed) return;
        VideoProject project = _editor.Current;
        var boundaries = project.VideoOverlays
            .SelectMany(item => new[] { item.Start, item.End }).ToList();
        foreach (VideoTransition transition in project.Transitions)
            if (transition.Lane == VideoTransitionLane.VideoOverlay &&
                VideoTransitionGeometry.TryGetSpan(project, transition,
                    out VideoTransitionSpan span))
            {
                boundaries.Add(span.Start);
                boundaries.Add(span.End);
            }
        boundaries.Sort();
        _canvasMediaBoundaries = [.. boundaries];
        _canvasMediaIntervalStart = TimeSpan.MaxValue;
        _canvasMediaIntervalEnd = TimeSpan.MinValue;
        UpdateCanvasMediaForPlayhead(_playhead);
    }

    private void UpdateCanvasMediaForPlayhead(TimeSpan position)
    {
        UpdatePausedFrame(position);
        if (_editor is null || _closed) return;
        if (position >= _canvasMediaIntervalStart &&
            position < _canvasMediaIntervalEnd)
        {
            if (_canvasMediaTransitionActive)
            {
                _activeVideoHitVisuals = VideoHitVisualsAt(
                    _editor.Current, position);
                SyncVideoHitViews();
                LayoutVideoHitTargets();
            }
            return;
        }

        int next = Array.BinarySearch(_canvasMediaBoundaries, position);
        if (next < 0) next = ~next;
        else while (next < _canvasMediaBoundaries.Length &&
            _canvasMediaBoundaries[next] <= position) next++;
        _canvasMediaIntervalStart = next > 0
            ? _canvasMediaBoundaries[next - 1] : TimeSpan.MinValue;
        _canvasMediaIntervalEnd = next < _canvasMediaBoundaries.Length
            ? _canvasMediaBoundaries[next] : TimeSpan.MaxValue;

        VideoProject project = _editor.Current;
        _canvasMediaTransitionActive = project.Transitions.Any(item =>
            item.Lane == VideoTransitionLane.VideoOverlay &&
            VideoTransitionGeometry.TryGetSpan(project, item,
                out VideoTransitionSpan span) &&
            position >= span.Start && position < span.End);
        _activeVideoHitVisuals = VideoHitVisualsAt(project, position);
        SyncVideoHitViews();
        UpdateCanvasMediaBounds();
    }

    private void SyncVideoHitViews()
    {
        HashSet<Guid> activeIds = _activeVideoHitVisuals.Select(item =>
            item.ItemId).ToHashSet();
        foreach (Guid id in _previewVideoHitViews.Keys.Where(item =>
            !activeIds.Contains(item)).ToArray())
        {
            _previewImages.Children.Remove(_previewVideoHitViews[id]);
            _previewVideoHitViews.Remove(id);
        }
        foreach (PausedSource visual in _activeVideoHitVisuals)
        {
            if (_previewVideoHitViews.ContainsKey(visual.ItemId)) continue;
            var hit = new Border
            {
                Background = new SolidColorBrush(Colors.Transparent),
                Tag = (TimelineKind.VideoOverlay, visual.ItemId),
            };
            hit.PointerPressed += MediaSurface_PointerPressed;
            hit.PointerMoved += MediaSurface_PointerMoved;
            hit.PointerReleased += MediaSurface_PointerReleased;
            hit.PointerCaptureLost += MediaSurface_PointerCaptureLost;
            _previewVideoHitViews.Add(visual.ItemId, hit);
            _previewImages.Children.Add(hit);
        }
    }

    private static PausedSource[] VideoHitVisualsAt(VideoProject project,
        TimeSpan position)
    {
        HashSet<Guid> overlayIds = project.VideoOverlays.Select(item =>
            item.Id).ToHashSet();
        return ActivePausedOverlays(project, position)
            .Where(item => overlayIds.Contains(item.ItemId) &&
                item.Opacity > .001).ToArray();
    }

    private void UpdateCanvasMediaBounds()
    {
        if (_editor is null || !double.IsFinite(VideoPlayer.Width) ||
            !double.IsFinite(VideoPlayer.Height) ||
            VideoPlayer.Width <= 0 || VideoPlayer.Height <= 0) return;
        _canvasMedia.Width = VideoPlayer.Width;
        _canvasMedia.Height = VideoPlayer.Height;
        var clip = new Rect(0, 0, VideoPlayer.Width, VideoPlayer.Height);
        _canvasMedia.Clip = new RectangleGeometry { Rect = clip };
        LayoutVideoHitTargets();
        RefreshCanvasMediaSelection();
    }

    private void LayoutVideoHitTargets()
    {
        if (_editor is null || VideoPlayer.Width <= 0 ||
            VideoPlayer.Height <= 0) return;
        VideoProject project = _editor.Current;
        double scaleX = VideoPlayer.Width / project.Width;
        double scaleY = VideoPlayer.Height / project.Height;
        foreach (PausedSource visual in _activeVideoHitVisuals)
        {
            if (!_previewVideoHitViews.TryGetValue(visual.ItemId, out Border? hit))
                continue;
            Canvas.SetLeft(hit, visual.Bounds.X * scaleX);
            Canvas.SetTop(hit, visual.Bounds.Y * scaleY);
            hit.Width = visual.Bounds.Width * scaleX;
            hit.Height = visual.Bounds.Height * scaleY;
            ApplyVideoShapeClip(hit, visual.Shape, hit.Width, hit.Height);
            Canvas.SetZIndex(hit, visual.ZIndex + 2);
        }
    }

    private void RefreshCanvasMediaSelection()
    {
        if (_editor is null || _closed) return;
        VideoProject project = _editor.Current;
        VideoRect? bounds = null;
        VideoRect? crop = null;
        Guid assetId = Guid.Empty;
        if (_selectedKind == TimelineKind.Image &&
            _visiblePreviewImageDefinitions.TryGetValue(_selectedId, out ImageOverlay? image))
        {
            bounds = image.Bounds;
            crop = image.SourceCrop;
        }
        else if (_selectedKind == TimelineKind.VideoOverlay &&
            _previewVideoHitViews.ContainsKey(_selectedId) &&
            project.VideoOverlays.FirstOrDefault(item => item.Id == _selectedId)
                is { } video)
        {
            bounds = video.Bounds;
            crop = video.SourceCrop;
            assetId = video.AssetId;
        }
        if (bounds is null)
        {
            _canvasMediaFrame.Visibility = Visibility.Collapsed;
            _canvasMediaFocusTarget.Visibility = Visibility.Collapsed;
            _canvasMediaQuickbar.Visibility = Visibility.Collapsed;
            _canvasVideoProxy.Visibility = Visibility.Collapsed;
            foreach (var (_, view) in _canvasMediaHandles)
                view.Visibility = Visibility.Collapsed;
            return;
        }
        if (_canvasMediaSelectedId != _selectedId ||
            _canvasMediaSelectedKind != _selectedKind)
        {
            _canvasMediaCropButton.IsChecked = false;
            _canvasMediaSelectedId = _selectedId;
            _canvasMediaSelectedKind = _selectedKind;
            if (assetId != Guid.Empty) _ = LoadCanvasVideoProxyAsync(assetId, _selectedId);
        }
        _canvasMediaFrame.Tag = (_selectedKind, _selectedId);
        _canvasMediaFrame.Visibility = Visibility.Visible;
        _canvasMediaFocusTarget.Visibility = Visibility.Visible;
        _canvasMediaQuickbar.Visibility = Visibility.Visible;
        bool trackLocked = IsSelectedTrackLocked();
        foreach (var (_, view) in _canvasMediaHandles)
            view.Visibility = trackLocked ? Visibility.Collapsed : Visibility.Visible;
        _canvasMediaCropButton.IsEnabled = !trackLocked;
        _canvasMediaResetCropButton.IsEnabled = !trackLocked && crop is not null;
        _canvasMediaShapeButton.IsEnabled = !trackLocked;
        _canvasMediaDeleteButton.IsEnabled = !trackLocked;
        _canvasMediaShapeButton.Visibility = _selectedKind == TimelineKind.VideoOverlay
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshVideoShapeMenuSelection();
        LayoutCanvasMedia(_canvasMediaDraftBounds ?? bounds.Value,
            _canvasMediaDraftCrop ?? crop ?? new VideoRect(0, 0, 1, 1));
    }

    private void LayoutCanvasMedia(VideoRect bounds, VideoRect crop)
    {
        if (_editor is null || VideoPlayer.Width <= 0 || VideoPlayer.Height <= 0)
            return;
        double sx = VideoPlayer.Width / _editor.Current.Width;
        double sy = VideoPlayer.Height / _editor.Current.Height;
        double left = bounds.X * sx;
        double top = bounds.Y * sy;
        double width = bounds.Width * sx;
        double height = bounds.Height * sy;
        Canvas.SetLeft(_canvasMediaFrame, left);
        Canvas.SetTop(_canvasMediaFrame, top);
        _canvasMediaFrame.Width = width;
        _canvasMediaFrame.Height = height;
        foreach ((MediaHandle handle, Border view) in _canvasMediaHandles)
        {
            double x = handle switch
            {
                MediaHandle.NorthWest or MediaHandle.West or MediaHandle.SouthWest => left,
                MediaHandle.NorthEast or MediaHandle.East or MediaHandle.SouthEast => left + width,
                _ => left + width / 2,
            };
            double y = handle switch
            {
                MediaHandle.NorthWest or MediaHandle.North or MediaHandle.NorthEast => top,
                MediaHandle.SouthWest or MediaHandle.South or MediaHandle.SouthEast => top + height,
                _ => top + height / 2,
            };
            Canvas.SetLeft(view, x - view.Width / 2);
            Canvas.SetTop(view, y - view.Height / 2);
        }
        double barWidth = _selectedKind == TimelineKind.VideoOverlay ? 162 : 131;
        Canvas.SetLeft(_canvasMediaQuickbar,
            Math.Clamp(left, 0, Math.Max(0, VideoPlayer.Width - barWidth)));
        Canvas.SetTop(_canvasMediaQuickbar,
            Math.Clamp(top >= 39 ? top - 38 : top + height + 10,
                0, Math.Max(0, VideoPlayer.Height - 34)));
        if (_canvasVideoProxy.Visibility == Visibility.Visible)
            LayoutCanvasVideoProxy(bounds, crop);
    }

    private void LayoutCanvasVideoProxy(VideoRect bounds, VideoRect crop)
    {
        if (_editor is null || VideoPlayer.Width <= 0 || VideoPlayer.Height <= 0)
            return;
        double sx = VideoPlayer.Width / _editor.Current.Width;
        double sy = VideoPlayer.Height / _editor.Current.Height;
        double width = bounds.Width * sx;
        double height = bounds.Height * sy;
        Canvas.SetLeft(_canvasVideoProxy, bounds.X * sx);
        Canvas.SetTop(_canvasVideoProxy, bounds.Y * sy);
        _canvasVideoProxy.Width = width;
        _canvasVideoProxy.Height = height;
        VideoOverlayShape shape = _editor.Current.VideoOverlays.FirstOrDefault(item =>
            item.Id == _selectedId)?.Shape ?? VideoOverlayShape.Rectangle;
        ApplyVideoShapeClip(_canvasVideoProxy, shape, width, height);
        _canvasVideoProxyImage.Width = width / crop.Width;
        _canvasVideoProxyImage.Height = height / crop.Height;
        Canvas.SetLeft(_canvasVideoProxyImage,
            -crop.X * _canvasVideoProxyImage.Width);
        Canvas.SetTop(_canvasVideoProxyImage,
            -crop.Y * _canvasVideoProxyImage.Height);
    }

    private void MediaSurface_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ValueTuple<TimelineKind, Guid> tag } target ||
            tag.Item1 is not (TimelineKind.Image or TimelineKind.VideoOverlay) ||
            _editor is null || !args.GetCurrentPoint(target).Properties.IsLeftButtonPressed)
            return;
        SelectClip(tag.Item1, tag.Item2, focusTimeline: false);
        if (_selectedKind != tag.Item1 || _selectedId != tag.Item2) return;
        BeginMediaGesture(target, args, MediaHandle.Move);
    }

    private void MediaHandle_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: MediaHandle handle } target ||
            _editor is null || !args.GetCurrentPoint(target).Properties.IsLeftButtonPressed)
            return;
        BeginMediaGesture(target, args, handle);
    }

    private void BeginMediaGesture(FrameworkElement target, PointerRoutedEventArgs args,
        MediaHandle gesture)
    {
        if (_editor is null) return;
        if (IsSelectedTrackLocked())
        {
            StatusText.Text = "Unlock the track to move, resize, or crop this item.";
            args.Handled = true;
            return;
        }
        if (_selectedKind == TimelineKind.VideoOverlay &&
            _editor.Current.Transitions.Any(item =>
                item.Lane == VideoTransitionLane.VideoOverlay &&
                (item.OutgoingId == _selectedId ||
                    item.IncomingId == _selectedId) &&
                VideoTransitionGeometry.TryGetSpan(_editor.Current, item,
                    out VideoTransitionSpan span) &&
                _playhead >= span.Start && _playhead < span.End))
        {
            StatusText.Text = "Seek outside the transition to move, resize, or crop this video.";
            args.Handled = true;
            return;
        }
        if (!target.CapturePointer(args.Pointer)) return;
        _player.Pause();
        _canvasMediaCapture = target;
        _canvasMediaPointerId = args.Pointer.PointerId;
        _canvasMediaPointerStart = args.GetCurrentPoint(_canvasMedia).Position;
        _canvasMediaGesture = gesture;
        _canvasMediaStartBounds = _selectedKind switch
        {
            TimelineKind.Image => _editor.Current.ImageOverlays.First(item =>
                item.Id == _selectedId).Bounds,
            TimelineKind.VideoOverlay => _editor.Current.VideoOverlays.First(item =>
                item.Id == _selectedId).Bounds,
            _ => default,
        };
        _canvasMediaStartCrop = _selectedKind switch
        {
            TimelineKind.Image => _editor.Current.ImageOverlays.First(item =>
                item.Id == _selectedId).SourceCrop ?? new VideoRect(0, 0, 1, 1),
            TimelineKind.VideoOverlay => _editor.Current.VideoOverlays.First(item =>
                item.Id == _selectedId).SourceCrop ?? new VideoRect(0, 0, 1, 1),
            _ => new VideoRect(0, 0, 1, 1),
        };
        _canvasMediaDraftBounds = _canvasMediaStartBounds;
        _canvasMediaDraftCrop = _canvasMediaStartCrop;
        _canvasMediaAxisLockHorizontal = null;
        // The canvas does not request timeline focus. Still restore focus after
        // inspector layout, which may materialize its first number field.
        Guid selectedId = _selectedId;
        DispatcherQueue.TryEnqueue(() => FocusCanvasMediaSelection(selectedId));
        if (_selectedKind == TimelineKind.VideoOverlay &&
            _pausedOverlayViews.TryGetValue(_selectedId, out var paused) &&
            paused.Image.Source is BitmapImage pausedFrame)
            _canvasVideoProxyImage.Source = pausedFrame;
        if (_selectedKind == TimelineKind.VideoOverlay &&
            _canvasVideoProxyImage.Source is not null)
        {
            _canvasVideoProxy.Visibility = Visibility.Visible;
            if (_pausedOverlayViews.TryGetValue(_selectedId, out paused))
                paused.Frame.Visibility = Visibility.Collapsed;
        }
        args.Handled = true;
    }

    private void FocusCanvasMediaSelection(Guid selectedId)
    {
        if (_closed || selectedId != _selectedId ||
            _selectedKind is not (TimelineKind.Image or TimelineKind.VideoOverlay) ||
            _canvasMediaFrame.Visibility != Visibility.Visible) return;
        if (_canvasMediaFocusTarget.Focus(FocusState.Programmatic) &&
            CanvasMediaHasKeyboardFocus()) return;
        VideoPlayer.Focus(FocusState.Programmatic);
    }

    private void CanvasMediaProperties_Click(object sender, RoutedEventArgs args)
    {
        if (_selectedKind is not (TimelineKind.Image or TimelineKind.VideoOverlay) ||
            _selectedId == Guid.Empty) return;
        SetInspectorOpen(true);
        RefreshInspector();
    }

    private void MediaSurface_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_canvasMediaPointerId != args.Pointer.PointerId ||
            !ReferenceEquals(sender, _canvasMediaCapture) || _editor is null ||
            VideoPlayer.Width <= 0 || VideoPlayer.Height <= 0) return;
        UpdateCanvasMediaDraft(args);
        args.Handled = true;
    }

    private void UpdateCanvasMediaDraft(PointerRoutedEventArgs args)
    {
        if (_editor is null || VideoPlayer.Width <= 0 || VideoPlayer.Height <= 0)
            return;
        var point = args.GetCurrentPoint(_canvasMedia).Position;
        double dx = (point.X - _canvasMediaPointerStart.X) *
            _editor.Current.Width / VideoPlayer.Width;
        double dy = (point.Y - _canvasMediaPointerStart.Y) *
            _editor.Current.Height / VideoPlayer.Height;
        bool shiftHeld = (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0 ||
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) &
             global::Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool keepSquare = _selectedKind == TimelineKind.VideoOverlay &&
            _editor.Current.VideoOverlays.FirstOrDefault(item =>
                item.Id == _selectedId) is { } selectedVideo &&
            VideoOverlayCropPresets.IsSquare(selectedVideo.Shape);
        VideoRect bounds;
        VideoRect crop = _canvasMediaStartCrop;
        if (_canvasMediaGesture == MediaHandle.Move)
        {
            if (shiftHeld)
            {
                if (_canvasMediaAxisLockHorizontal is null &&
                    Math.Max(Math.Abs(dx), Math.Abs(dy)) >= 3)
                    _canvasMediaAxisLockHorizontal = Math.Abs(dx) >= Math.Abs(dy);
                if (_canvasMediaAxisLockHorizontal is true) dy = 0;
                else if (_canvasMediaAxisLockHorizontal is false) dx = 0;
                else dx = dy = 0;
            }
            else _canvasMediaAxisLockHorizontal = null;
            bounds = _canvasMediaStartBounds with
            {
                X = Math.Clamp(_canvasMediaStartBounds.X + dx, -65_536, 65_536),
                Y = Math.Clamp(_canvasMediaStartBounds.Y + dy, -65_536, 65_536),
            };
        }
        else if (_canvasMediaCropButton.IsChecked == true)
        {
            bounds = CropCanvasMedia(_canvasMediaStartBounds, crop,
                _canvasMediaGesture, dx, dy, shiftHeld || keepSquare, out crop);
        }
        else
        {
            bounds = ResizeCanvasMedia(_canvasMediaStartBounds,
                _canvasMediaGesture, dx, dy, shiftHeld || keepSquare);
        }
        _canvasMediaDraftBounds = bounds;
        _canvasMediaDraftCrop = crop;
        LayoutCanvasMedia(bounds, crop);
        if (_selectedKind == TimelineKind.Image &&
            _visiblePreviewImageDefinitions.TryGetValue(_selectedId, out ImageOverlay? image))
            LayoutPreviewImage(image, bounds, crop);
        else if (_selectedKind == TimelineKind.VideoOverlay &&
            _pausedOverlayViews.TryGetValue(_selectedId, out var paused))
            LayoutPausedOverlay(bounds, crop, paused.Frame, paused.Image,
                _editor.Current.VideoOverlays.FirstOrDefault(item =>
                    item.Id == _selectedId)?.Shape ?? VideoOverlayShape.Rectangle);
    }

    private static VideoRect ResizeCanvasMedia(VideoRect old, MediaHandle handle,
        double dx, double dy, bool maintainProportions)
    {
        if (maintainProportions)
            return ResizeCanvasMediaProportionally(old, handle, dx, dy);
        double left = old.X;
        double top = old.Y;
        double right = old.X + old.Width;
        double bottom = old.Y + old.Height;
        if (handle is MediaHandle.NorthWest or MediaHandle.West or MediaHandle.SouthWest)
            left = Math.Min(right - 16, left + dx);
        if (handle is MediaHandle.NorthEast or MediaHandle.East or MediaHandle.SouthEast)
            right = Math.Max(left + 16, right + dx);
        if (handle is MediaHandle.NorthWest or MediaHandle.North or MediaHandle.NorthEast)
            top = Math.Min(bottom - 16, top + dy);
        if (handle is MediaHandle.SouthWest or MediaHandle.South or MediaHandle.SouthEast)
            bottom = Math.Max(top + 16, bottom + dy);
        return new VideoRect(Math.Clamp(left, -65_536, 65_536),
            Math.Clamp(top, -65_536, 65_536),
            Math.Clamp(right - left, 16, 65_536),
            Math.Clamp(bottom - top, 16, 65_536));
    }

    private static VideoRect ResizeCanvasMediaProportionally(VideoRect old,
        MediaHandle handle, double dx, double dy, VideoRect? sourceCrop = null)
    {
        bool west = handle is MediaHandle.NorthWest or MediaHandle.West or
            MediaHandle.SouthWest;
        bool east = handle is MediaHandle.NorthEast or MediaHandle.East or
            MediaHandle.SouthEast;
        bool north = handle is MediaHandle.NorthWest or MediaHandle.North or
            MediaHandle.NorthEast;
        bool south = handle is MediaHandle.SouthWest or MediaHandle.South or
            MediaHandle.SouthEast;
        double widthScale = west ? (old.Width - dx) / old.Width :
            east ? (old.Width + dx) / old.Width : 1;
        double heightScale = north ? (old.Height - dy) / old.Height :
            south ? (old.Height + dy) / old.Height : 1;
        double scale = west || east
            ? north || south
                ? Math.Abs(widthScale - 1) >= Math.Abs(heightScale - 1)
                    ? widthScale : heightScale
                : widthScale
            : heightScale;
        double minimum = Math.Max(16 / old.Width, 16 / old.Height);
        double maximum = Math.Min(65_536 / old.Width, 65_536 / old.Height);
        // Imported projects can contain a valid media rectangle narrower than
        // 16 px. Keep its ratio when the minimum size cannot fit the 65,536 px cap.
        scale = Math.Clamp(scale, Math.Min(minimum, maximum), maximum);

        VideoRect AtScale(double factor)
        {
            double width = old.Width * factor;
            double height = old.Height * factor;
            double x = west ? old.X + old.Width - width :
                east ? old.X : old.X + (old.Width - width) / 2;
            double y = north ? old.Y + old.Height - height :
                south ? old.Y : old.Y + (old.Height - height) / 2;
            return new VideoRect(x, y, width, height);
        }

        if (sourceCrop is { } crop && scale > 1)
        {
            double sourceLeft = old.X - crop.X * old.Width / crop.Width;
            double sourceTop = old.Y - crop.Y * old.Height / crop.Height;
            double sourceRight = old.X + (1 - crop.X) * old.Width / crop.Width;
            double sourceBottom = old.Y + (1 - crop.Y) * old.Height / crop.Height;
            bool Fits(VideoRect candidate) =>
                candidate.X >= sourceLeft && candidate.Y >= sourceTop &&
                candidate.X + candidate.Width <= sourceRight &&
                candidate.Y + candidate.Height <= sourceBottom;
            if (!Fits(AtScale(scale)))
            {
                double low = 1;
                double high = scale;
                for (int index = 0; index < 48; index++)
                {
                    double middle = (low + high) / 2;
                    if (Fits(AtScale(middle))) low = middle;
                    else high = middle;
                }
                scale = low;
            }
        }
        VideoRect next = AtScale(scale);
        return next with
        {
            X = Math.Clamp(next.X, -65_536, 65_536),
            Y = Math.Clamp(next.Y, -65_536, 65_536),
        };
    }

    private static VideoRect CropForBounds(VideoRect old, VideoRect crop,
        VideoRect next)
    {
        double x = Math.Clamp(crop.X + (next.X - old.X) *
            crop.Width / old.Width, 0, 1);
        double y = Math.Clamp(crop.Y + (next.Y - old.Y) *
            crop.Height / old.Height, 0, 1);
        return new VideoRect(x, y,
            Math.Min(crop.Width * next.Width / old.Width, 1 - x),
            Math.Min(crop.Height * next.Height / old.Height, 1 - y));
    }

    private static VideoRect CropCanvasMedia(VideoRect old, VideoRect crop,
        MediaHandle handle, double dx, double dy, bool maintainProportions,
        out VideoRect resultCrop)
    {
        if (maintainProportions)
        {
            VideoRect next = ResizeCanvasMediaProportionally(old, handle, dx,
                dy, crop);
            resultCrop = CropForBounds(old, crop, next);
            return next;
        }
        double left = old.X;
        double top = old.Y;
        double right = old.X + old.Width;
        double bottom = old.Y + old.Height;
        if (handle is MediaHandle.NorthWest or MediaHandle.West or MediaHandle.SouthWest)
            left = Math.Clamp(left + dx,
                old.X - crop.X * old.Width / crop.Width, right - 16);
        if (handle is MediaHandle.NorthEast or MediaHandle.East or MediaHandle.SouthEast)
            right = Math.Clamp(right + dx, left + 16,
                old.X + (1 - crop.X) * old.Width / crop.Width);
        if (handle is MediaHandle.NorthWest or MediaHandle.North or MediaHandle.NorthEast)
            top = Math.Clamp(top + dy,
                old.Y - crop.Y * old.Height / crop.Height, bottom - 16);
        if (handle is MediaHandle.SouthWest or MediaHandle.South or MediaHandle.SouthEast)
            bottom = Math.Clamp(bottom + dy, top + 16,
                old.Y + (1 - crop.Y) * old.Height / crop.Height);
        var nextBounds = new VideoRect(left, top, right - left, bottom - top);
        resultCrop = CropForBounds(old, crop, nextBounds);
        return nextBounds;
    }

    private void MediaSurface_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_canvasMediaPointerId != args.Pointer.PointerId ||
            !ReferenceEquals(sender, _canvasMediaCapture)) return;
        UpdateCanvasMediaDraft(args);
        VideoRect? bounds = _canvasMediaDraftBounds;
        VideoRect? crop = _canvasMediaDraftCrop;
        _canvasMediaPointerId = null;
        _canvasMediaCapture = null;
        _canvasMediaDraftBounds = null;
        _canvasMediaDraftCrop = null;
        _canvasMediaAxisLockHorizontal = null;
        if (sender is FrameworkElement target) target.ReleasePointerCapture(args.Pointer);
        _canvasVideoProxy.Visibility = Visibility.Collapsed;
        if (_pausedOverlayViews.TryGetValue(_selectedId, out var paused))
            paused.Frame.Visibility = Visibility.Visible;
        if (_editor is not null && bounds is { } next && crop is { } nextCrop)
        {
            VideoRect? sourceCrop = nextCrop == new VideoRect(0, 0, 1, 1)
                ? null : nextCrop;
            if (_selectedKind == TimelineKind.Image &&
                _editor.Current.ImageOverlays.FirstOrDefault(item =>
                    item.Id == _selectedId) is { } image &&
                (image.Bounds != next || image.SourceCrop != sourceCrop))
                TryEdit(() => _editor.UpdateImageOverlay(image with
                {
                    Bounds = next,
                    SourceCrop = sourceCrop,
                }));
            else if (_selectedKind == TimelineKind.VideoOverlay &&
                _editor.Current.VideoOverlays.FirstOrDefault(item =>
                    item.Id == _selectedId) is { } video &&
                (video.Bounds != next || video.SourceCrop != sourceCrop))
                TryEdit(() => _editor.UpdateVideoOverlay(video with
                {
                    Bounds = next,
                    SourceCrop = sourceCrop,
                }));
        }
        RefreshCanvasMediaSelection();
        FocusCanvasMediaSelection(_selectedId);
        Guid selectedId = _selectedId;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => FocusCanvasMediaSelection(selectedId));
        args.Handled = true;
    }

    private void MediaSurface_PointerCaptureLost(object sender,
        PointerRoutedEventArgs args)
    {
        if (_canvasMediaPointerId != args.Pointer.PointerId) return;
        _canvasMediaPointerId = null;
        _canvasMediaCapture = null;
        _canvasMediaDraftBounds = null;
        _canvasMediaDraftCrop = null;
        _canvasMediaAxisLockHorizontal = null;
        _canvasVideoProxy.Visibility = Visibility.Collapsed;
        if (_pausedOverlayViews.TryGetValue(_selectedId, out var paused))
            paused.Frame.Visibility = Visibility.Visible;
        RefreshCanvasMediaSelection();
    }

    private void CanvasMediaResetCrop_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null) return;
        if (_selectedKind == TimelineKind.Image &&
            _editor.Current.ImageOverlays.FirstOrDefault(item =>
                item.Id == _selectedId) is { SourceCrop: not null } image)
            TryEdit(() => _editor.UpdateImageOverlay(image with { SourceCrop = null }));
        else if (_selectedKind == TimelineKind.VideoOverlay &&
            _editor.Current.VideoOverlays.FirstOrDefault(item =>
                item.Id == _selectedId) is { SourceCrop: not null } video)
            TryEdit(() => _editor.UpdateVideoOverlay(video with { SourceCrop = null }));
    }

    private void RegisterCanvasMediaShortcuts()
    {
        var crop = new KeyboardAccelerator
        {
            Key = VirtualKey.C,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
        };
        crop.Invoked += CanvasCropShortcut_Invoked;
        KeyboardAccelerators.Add(crop);
        var reset = new KeyboardAccelerator
        {
            Key = VirtualKey.R,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
        };
        reset.Invoked += CanvasResetCropShortcut_Invoked;
        KeyboardAccelerators.Add(reset);
        foreach (VirtualKey key in new[] { VirtualKey.Up, VirtualKey.Down })
            foreach (VirtualKeyModifiers modifiers in new[]
                { VirtualKeyModifiers.None, VirtualKeyModifiers.Shift })
            {
                var nudge = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
                nudge.Invoked += CanvasNudgeShortcut_Invoked;
                KeyboardAccelerators.Add(nudge);
            }
    }

    private void CanvasCropShortcut_Invoked(KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_closed || _editor is null || IsTextEntryFocused() ||
            _canvasMediaFrame.Visibility != Visibility.Visible ||
            IsSelectedTrackLocked()) return;
        _canvasMediaCropButton.IsChecked = _canvasMediaCropButton.IsChecked != true;
        StatusText.Text = _canvasMediaCropButton.IsChecked == true
            ? "Crop mode · drag a handle · Shift keeps proportions"
            : "Resize mode · drag a handle · Shift keeps proportions";
        args.Handled = true;
    }

    private void CanvasResetCropShortcut_Invoked(KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_closed || _editor is null || IsTextEntryFocused() ||
            _canvasMediaFrame.Visibility != Visibility.Visible ||
            !_canvasMediaResetCropButton.IsEnabled || IsSelectedTrackLocked()) return;
        CanvasMediaResetCrop_Click(this, new RoutedEventArgs());
        StatusText.Text = "Source crop reset";
        args.Handled = true;
    }

    private bool CanvasMediaHasKeyboardFocus()
    {
        if (_editor is null || _closed ||
            _canvasMediaFrame.Visibility != Visibility.Visible ||
            _selectedKind is not (TimelineKind.Image or TimelineKind.VideoOverlay))
            return false;
        DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, VideoPlayer) ||
                ReferenceEquals(focused, _canvasMediaFocusTarget)) return true;
            focused = VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    private bool NudgeCanvasMedia(VirtualKey key, bool shift)
    {
        if (!CanvasMediaHasKeyboardFocus() || _editor is null) return false;
        if (IsSelectedTrackLocked())
        {
            StatusText.Text = "Unlock the track to move this item.";
            return true;
        }
        if (_selectedKind == TimelineKind.VideoOverlay &&
            _editor.Current.Transitions.Any(item =>
                item.Lane == VideoTransitionLane.VideoOverlay &&
                (item.OutgoingId == _selectedId || item.IncomingId == _selectedId) &&
                VideoTransitionGeometry.TryGetSpan(_editor.Current, item,
                    out VideoTransitionSpan span) &&
                _playhead >= span.Start && _playhead < span.End))
        {
            StatusText.Text = "Seek outside the transition to move this video.";
            return true;
        }
        _player.Pause();
        double step = shift ? 10 : 1;
        double dx = key == VirtualKey.Left ? -step :
            key == VirtualKey.Right ? step : 0;
        double dy = key == VirtualKey.Up ? -step :
            key == VirtualKey.Down ? step : 0;
        if (dx == 0 && dy == 0) return false;
        if (_selectedKind == TimelineKind.Image &&
            _editor.Current.ImageOverlays.FirstOrDefault(item =>
                item.Id == _selectedId) is { } image)
        {
            VideoRect next = image.Bounds with
            {
                X = Math.Clamp(image.Bounds.X + dx, -65_536, 65_536),
                Y = Math.Clamp(image.Bounds.Y + dy, -65_536, 65_536),
            };
            if (next != image.Bounds)
                ApplyCanvasMediaNudge(() =>
                    _editor.UpdateImageOverlay(image with { Bounds = next }), next);
        }
        else if (_selectedKind == TimelineKind.VideoOverlay &&
            _editor.Current.VideoOverlays.FirstOrDefault(item =>
                item.Id == _selectedId) is { } video)
        {
            VideoRect next = video.Bounds with
            {
                X = Math.Clamp(video.Bounds.X + dx, -65_536, 65_536),
                Y = Math.Clamp(video.Bounds.Y + dy, -65_536, 65_536),
            };
            if (next != video.Bounds)
                ApplyCanvasMediaNudge(() =>
                    _editor.UpdateVideoOverlay(video with { Bounds = next }), next);
        }
        else return false;
        return true;
    }

    private void ApplyCanvasMediaNudge(Action update, VideoRect bounds)
    {
        // Image transition backdrops cache both participants' bounds. Let the
        // full refresh rebuild those rare layouts after a nudge.
        _canvasMediaNudgeEdit = _selectedKind != TimelineKind.Image ||
            !_imagePreviewTransitions.Any(item =>
                item.Outgoing.Id == _selectedId ||
                item.Incoming.Id == _selectedId);
        try
        {
            if (TryEdit(update)) UpdateNudgedCanvasMedia(bounds);
        }
        finally
        {
            _canvasMediaNudgeEdit = false;
        }
    }

    private void UpdateNudgedCanvasMedia(VideoRect bounds)
    {
        if (_editor is null) return;
        if (_selectedKind == TimelineKind.Image &&
            _editor.Current.ImageOverlays.FirstOrDefault(item =>
                item.Id == _selectedId) is { } image)
        {
            if (_visiblePreviewImageDefinitions.ContainsKey(image.Id))
            {
                _visiblePreviewImageDefinitions[image.Id] = image;
                LayoutPreviewImage(image, bounds,
                    image.SourceCrop ?? new VideoRect(0, 0, 1, 1));
            }
        }
        else if (_selectedKind == TimelineKind.VideoOverlay &&
            _editor.Current.VideoOverlays.FirstOrDefault(item =>
                item.Id == _selectedId) is { } video)
        {
            for (int index = 0; index < _activeVideoHitVisuals.Length; index++)
                if (_activeVideoHitVisuals[index].ItemId == video.Id)
                    _activeVideoHitVisuals[index] = _activeVideoHitVisuals[index]
                        with { Bounds = bounds };
            for (int index = 0; index < _pausedActiveSources.Length; index++)
                if (_pausedActiveSources[index].ItemId == video.Id)
                    _pausedActiveSources[index] = _pausedActiveSources[index]
                        with { Bounds = bounds };
            LayoutVideoHitTargets();
            if (_pausedOverlayViews.TryGetValue(video.Id, out var paused))
                LayoutPausedOverlay(bounds,
                    video.SourceCrop ?? new VideoRect(0, 0, 1, 1),
                    paused.Frame, paused.Image, video.Shape);
        }
        RefreshCanvasMediaSelection();
        bool loadingInspector = _loadingInspector;
        _loadingInspector = true;
        try
        {
            OverlayX.Value = bounds.X;
            OverlayY.Value = bounds.Y;
        }
        finally
        {
            _loadingInspector = loadingInspector;
        }
    }

    private void CanvasNudgeShortcut_Invoked(KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = NudgeCanvasMedia(sender.Key,
            (sender.Modifiers & VirtualKeyModifiers.Shift) != 0);
    }

    private async Task LoadCanvasVideoProxyAsync(Guid assetId, Guid overlayId)
    {
        if (_editor is null || _store is null) return;
        long generation = ++_canvasProxyGeneration;
        _canvasProxyAssetId = assetId;
        if (_timelineThumbnailCache.TryGetValue(assetId, out BitmapImage? cached))
            _canvasVideoProxyImage.Source = cached;
        else _canvasVideoProxyImage.Source = null;
        try
        {
            VideoProject project = _editor.Current;
            VideoOverlay? overlay = project.VideoOverlays.FirstOrDefault(item =>
                item.Id == overlayId);
            VideoAsset? asset = project.Assets.FirstOrDefault(item =>
                item.Id == assetId);
            if (overlay is null || asset is null) return;
            StorageFile file = await StorageFile.GetFileFromPathAsync(
                _store.ResolveAssetPath(project, asset));
            MediaClip clip = await MediaClip.CreateFromFileAsync(file);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            TimeSpan sourceAt = overlay.SourceIn +
                TimeSpan.FromTicks(Math.Clamp((_playhead - overlay.Start).Ticks, 0,
                    overlay.Duration.Ticks - 1));
            using var stream = await composition.GetThumbnailAsync(sourceAt,
                Math.Min(640, project.Width), Math.Min(360, project.Height),
                VideoFramePrecision.NearestFrame);
            var image = new BitmapImage { DecodePixelWidth = 640 };
            await image.SetSourceAsync(stream);
            if (_closed || generation != _canvasProxyGeneration ||
                _canvasMediaSelectedId != overlayId) return;
            _canvasVideoProxyImage.Source = image;
        }
        catch
        {
            // A shell timeline thumbnail remains usable if precise frame extraction fails.
        }
    }
}
