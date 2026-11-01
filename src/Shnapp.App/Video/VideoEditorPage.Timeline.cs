using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Shnapp.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private enum TimelineKind { None, Video, Audio, Image }

    private TimelineKind _selectedKind;
    private Guid _selectedId;
    private (TimeSpan Start, TimeSpan End)? _selectedRange;
    private double _pixelsPerSecond = 28;
    private double _timelineZoom = 1;
    private bool _timelineSnapEnabled = true;
    private double _lastTimelineViewportWidth = double.NaN;
    private double _renderedWindowStartX;
    private double _renderedWindowEndX;
    private uint? _scrubbingPointerId;
    private ClipDrag? _clipDrag;
    private Line? _playheadLine;
    private bool _loadingInspector;
    private readonly Dictionary<Guid, Button> _clipButtons = new();
    private sealed record ClipDrag(Button Surface, uint PointerId, TimelineKind Kind, Guid Id,
        TimeSpan Start, TimeSpan Duration, double PressX, double CanvasLeft, string Hint,
        int FrameRate, double[] SnapTargets);
    private static readonly double[] TimelineTickIntervals =
        [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600,
         7200, 14400, 28800, 43200, 86400];

    private void RefreshTimeline()
    {
        if (_editor is null || TimelineCanvas is null) return;
        VideoProject project = _editor.Current;
        Dictionary<Guid, VideoAsset> assets = project.Assets.ToDictionary(item => item.Id);
        var lanes = new List<(string Label, Color Fill)>();
        var imageLanes = new Dictionary<Guid, int>();
        Color imageColor = Color.FromArgb(255, 125, 95, 157);
        Color videoColor = Color.FromArgb(255, 52, 112, 162);
        Color systemColor = Color.FromArgb(255, 66, 126, 112);
        Color microphoneColor = Color.FromArgb(255, 95, 118, 93);
        Color addedAudioColor = Color.FromArgb(255, 119, 101, 70);

        // Higher image layers appear above lower ones, as they do in the preview.
        foreach (var layer in project.ImageOverlays.Select((item, index) => (item, index))
            .GroupBy(entry => ImageLayerKey(entry.item))
            .OrderByDescending(group => group.First().item.ZIndex)
            .ThenByDescending(group => group.Max(entry => entry.index)))
        {
            int lane = lanes.Count;
            string name = assets[layer.First().item.AssetId].DisplayName ?? "Image";
            lanes.Add((name, imageColor));
            foreach (var entry in layer) imageLanes[entry.item.Id] = lane;
        }
        int videoLane = lanes.Count;
        lanes.Add(("Video", videoColor));

        var audioLanes = new Dictionary<Guid, int>();
        HashSet<Guid> usedAudioAssets = [.. project.AudioSegments.Select(item => item.AssetId)];
        foreach (VideoAsset asset in project.Assets.Where(item => usedAudioAssets.Contains(item.Id))
            .OrderBy(item => item.Kind switch
            {
                VideoAssetKind.ComputerAudio => 0,
                VideoAssetKind.MicrophoneAudio => 1,
                _ => 2,
            }))
        {
            string name = asset.Kind switch
            {
                VideoAssetKind.ComputerAudio => "Computer audio",
                VideoAssetKind.MicrophoneAudio => "Microphone",
                _ => asset.DisplayName ?? "Added audio",
            };
            audioLanes[asset.Id] = lanes.Count;
            lanes.Add((name, asset.Kind switch
            {
                VideoAssetKind.ComputerAudio => systemColor,
                VideoAssetKind.MicrophoneAudio => microphoneColor,
                _ => addedAudioColor,
            }));
        }

        double contentHeight = 22 + lanes.Count * 28;
        TimelinePanel.Height = Math.Min(280, 36 + contentHeight + 14);
        TimelineContent.Height = contentHeight;
        TimelineCanvas.Height = contentHeight;
        TimelineLaneHeaders.Children.Clear();
        TimelineLaneHeaders.Children.Add(new TextBlock { Height = 22 });
        foreach (var lane in lanes)
        {
            var header = new TextBlock
            {
                Text = lane.Label,
                Height = 28,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["ShnappToolbarInactiveIconBrush"],
            };
            ToolTipService.SetToolTip(header, lane.Label);
            TimelineLaneHeaders.Children.Add(header);
        }

        _lastTimelineViewportWidth = TimelineScroll.ActualWidth;
        double available = Math.Max(120, TimelineScroll.ActualWidth - 18);
        double fitScale = Math.Clamp(available / Math.Max(1, project.Duration.TotalSeconds), .02, 42);
        _pixelsPerSecond = Math.Clamp(fitScale * _timelineZoom, .02, 672);
        double width = Math.Max(available, project.Duration.TotalSeconds * _pixelsPerSecond + 24);
        TimelineContent.Width = width;
        TimelineCanvas.Width = width;
        TimelineCanvas.Children.Clear();
        _clipButtons.Clear();

        double viewport = Math.Max(120, TimelineScroll.ActualWidth);
        double offset = TimelineScroll.HorizontalOffset;
        _renderedWindowStartX = Math.Max(0, offset - viewport);
        _renderedWindowEndX = Math.Min(width, offset + viewport * 2);
        double renderedWidth = _renderedWindowEndX - _renderedWindowStartX;

        for (int lane = 0; lane < lanes.Count; lane++)
        {
            var background = new Rectangle
            {
                Width = renderedWidth,
                Height = 27,
                Fill = (Brush)Application.Current.Resources["ShnappFloatingEdgeBrush"],
                Opacity = lane % 2 == 0 ? .12 : .07,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(background, _renderedWindowStartX);
            Canvas.SetTop(background, 22 + lane * 28);
            TimelineCanvas.Children.Add(background);
        }

        if (_markIn.HasValue || _markOut.HasValue)
        {
            double first = (_markIn ?? _markOut!.Value).TotalSeconds * _pixelsPerSecond;
            double last = (_markOut ?? _markIn!.Value).TotalSeconds * _pixelsPerSecond;
            var markedRange = new Rectangle
            {
                Width = Math.Max(2, Math.Abs(last - first)),
                Height = contentHeight,
                Fill = (Brush)Application.Current.Resources["ShnappBlueBrush"],
                Opacity = _markIn.HasValue && _markOut.HasValue ? .18 : .7,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(markedRange, Math.Min(first, last));
            TimelineCanvas.Children.Add(markedRange);
        }

        double tickSeconds = TimelineTickIntervals.FirstOrDefault(seconds =>
            seconds * _pixelsPerSecond >= 68);
        if (tickSeconds <= 0) tickSeconds = TimelineTickIntervals[^1];
        double firstTick = Math.Ceiling(_renderedWindowStartX /
            (_pixelsPerSecond * tickSeconds)) * tickSeconds;
        for (double seconds = firstTick;
            seconds <= project.Duration.TotalSeconds &&
            seconds * _pixelsPerSecond <= _renderedWindowEndX;
            seconds += tickSeconds)
        {
            double x = seconds * _pixelsPerSecond;
            var tick = new Line
            {
                X1 = 0, X2 = 0, Y1 = 17, Y2 = contentHeight,
                Stroke = (Brush)Application.Current.Resources["ShnappToolbarInactiveIconBrush"],
                Opacity = .38,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(tick, x);
            TimelineCanvas.Children.Add(tick);
            var label = new TextBlock
            {
                Text = FormatTime(TimeSpan.FromSeconds(seconds)),
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["ShnappToolbarInactiveIconBrush"],
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, x + 3);
            TimelineCanvas.Children.Add(label);
        }

        foreach (VideoSegment segment in project.VideoSegments)
        {
            VideoAsset asset = assets[segment.AssetId];
            AddTimelineClip(segment.Id, TimelineKind.Video, videoLane, segment.Start,
                segment.Duration, asset.DisplayName ?? "Recording", videoColor);
        }
        foreach (AudioSegment segment in project.AudioSegments)
        {
            VideoAsset asset = assets[segment.AssetId];
            int lane = audioLanes[asset.Id];
            string title = lanes[lane].Label;
            AddTimelineClip(segment.Id, TimelineKind.Audio, lane,
                segment.Start, segment.Duration,
                segment.Muted ? $"{title} · muted" : title, lanes[lane].Fill);
        }
        foreach (ImageOverlay overlay in project.ImageOverlays)
        {
            int lane = imageLanes[overlay.Id];
            AddTimelineClip(overlay.Id, TimelineKind.Image, lane,
                overlay.Start, overlay.Duration, lanes[lane].Label, imageColor);
        }

        _playheadLine = new Line
        {
            X1 = 0, X2 = 0, Y1 = 0, Y2 = contentHeight,
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        TimelineCanvas.Children.Add(_playheadLine);
        DrawPlayhead();
    }

    private void AddTimelineClip(Guid id, TimelineKind kind, int lane,
        TimeSpan start, TimeSpan duration, string title, Color fill)
    {
        double left = start.TotalSeconds * _pixelsPerSecond;
        double width = Math.Max(3, duration.TotalSeconds * _pixelsPerSecond - 2);
        if (left + width < _renderedWindowStartX || left > _renderedWindowEndX)
            return;
        double visibleLeft = Math.Max(left, _renderedWindowStartX);
        double visibleRight = Math.Min(left + width, _renderedWindowEndX);
        bool selected = _selectedId == id && _selectedKind == kind;
        var surface = new Button
        {
            Width = Math.Max(3, visibleRight - visibleLeft),
            Height = 24,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(fill),
            BorderThickness = new Thickness(selected ? 2 : 0),
            BorderBrush = (Brush)Application.Current.Resources["ShnappToolbarActiveIconBrush"],
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Tag = (kind, id),
            AllowDrop = true,
            Content = new TextBlock
            {
                Text = title,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(7, 4, 7, 0),
            },
        };
        surface.PointerPressed += Clip_PointerPressed;
        surface.PointerMoved += Clip_PointerMoved;
        surface.PointerReleased += Clip_PointerReleased;
        surface.PointerCaptureLost += Clip_PointerCaptureLost;
        surface.DragOver += Timeline_DragOver;
        surface.Drop += Timeline_Drop;
        surface.Click += Clip_Click;
        AutomationProperties.SetName(surface,
            $"{title}, {FormatPreciseTime(start)} to {FormatPreciseTime(start + duration)}. Select or drag to move clip.");
        ToolTipService.SetToolTip(surface,
            $"{title} · {FormatPreciseTime(start)}–{FormatPreciseTime(start + duration)} · Drag to move");
        Canvas.SetLeft(surface, visibleLeft);
        Canvas.SetTop(surface, 24 + lane * 28);
        TimelineCanvas.Children.Add(surface);
        _clipButtons[id] = surface;
    }

    private void Clip_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Button { Tag: ValueTuple<TimelineKind, Guid> tag } button ||
            _editor is null) return;
        SelectClip(tag.Item1, tag.Item2);
        if (_selectedId == tag.Item2 && _selectedKind == tag.Item1 &&
            _clipButtons.TryGetValue(tag.Item2, out Button? current) &&
            ReferenceEquals(current, button) &&
            _selectedRange is { } range && button.CapturePointer(args.Pointer))
        {
            _clipDrag = new ClipDrag(button, args.Pointer.PointerId, tag.Item1, tag.Item2,
                range.Start, range.End - range.Start,
                args.GetCurrentPoint(TimelineContent).Position.X, Canvas.GetLeft(button),
                SelectionHint.Text, Math.Max(1, _editor.Current.FrameRate),
                CollectSnapTargets(tag.Item2));
        }
        args.Handled = true;
    }

    private void Clip_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_clipDrag is not { } drag || !ReferenceEquals(sender, drag.Surface) ||
            drag.PointerId != args.Pointer.PointerId || _editor is null) return;
        TimeSpan start = DraggedClipStart(drag, args.GetCurrentPoint(TimelineContent).Position.X);
        double delta = (start - drag.Start).TotalSeconds * _pixelsPerSecond;
        if (Math.Abs(delta) < 3)
        {
            if (drag.Surface.Opacity < 1)
            {
                Canvas.SetLeft(drag.Surface, drag.CanvasLeft);
                drag.Surface.Opacity = 1;
                SelectionHint.Text = drag.Hint;
            }
            return;
        }
        Canvas.SetLeft(drag.Surface, drag.CanvasLeft + delta);
        drag.Surface.Opacity = .78;
        SelectionHint.Text = $"Move to {FormatPreciseTime(start)}";
        args.Handled = true;
    }

    private void Clip_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_clipDrag is not { } drag || !ReferenceEquals(sender, drag.Surface) ||
            drag.PointerId != args.Pointer.PointerId || _editor is null) return;
        TimeSpan requested = DraggedClipStart(drag,
            args.GetCurrentPoint(TimelineContent).Position.X);
        bool moved = requested != drag.Start &&
            Math.Abs((requested - drag.Start).TotalSeconds * _pixelsPerSecond) >= 3;
        _clipDrag = null;
        drag.Surface.ReleasePointerCapture(args.Pointer);
        Canvas.SetLeft(drag.Surface, drag.CanvasLeft);
        drag.Surface.Opacity = 1;
        if (!moved) return;
        if (!ApplyPendingInspectorChanges())
        {
            SelectionHint.Text = drag.Hint;
            return;
        }

        TimeSpan delta = requested - drag.Start;
        bool committed = false;
        bool valid = TryEdit(() =>
        {
            if (drag.Kind == TimelineKind.Video)
            {
                VideoSegment clip = _editor.Current.VideoSegments.First(item => item.Id == drag.Id);
                committed = _editor.MoveVideoSegment(drag.Id, clip.Start + delta);
            }
            else if (drag.Kind == TimelineKind.Audio)
            {
                AudioSegment clip = _editor.Current.AudioSegments.First(item => item.Id == drag.Id);
                committed = _editor.MoveAudioSegment(drag.Id, clip.Start + delta);
            }
            else if (drag.Kind == TimelineKind.Image)
            {
                ImageOverlay clip = _editor.Current.ImageOverlays.First(item => item.Id == drag.Id);
                committed = _editor.UpdateImageOverlay(clip with { Start = clip.Start + delta });
            }
        });
        if (!valid || !committed) SelectionHint.Text = drag.Hint;
        args.Handled = true;
    }

    private void Clip_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_clipDrag is not { } drag || !ReferenceEquals(sender, drag.Surface) ||
            drag.PointerId != args.Pointer.PointerId) return;
        _clipDrag = null;
        Canvas.SetLeft(drag.Surface, drag.CanvasLeft);
        drag.Surface.Opacity = 1;
        SelectionHint.Text = drag.Hint;
    }

    private TimeSpan DraggedClipStart(ClipDrag drag, double pointerX)
    {
        if (_editor is null) return drag.Start;
        double pointerDelta = pointerX - drag.PressX;
        if (Math.Abs(pointerDelta) < 3) return drag.Start;
        double seconds = Math.Max(0,
            drag.Start.TotalSeconds + pointerDelta / _pixelsPerSecond);
        seconds = Math.Round(seconds * drag.FrameRate) / drag.FrameRate;
        if (_timelineSnapEnabled && drag.SnapTargets.Length > 0)
        {
            double startOffset = NearestSnapTarget(drag.SnapTargets, seconds) - seconds;
            double end = seconds + drag.Duration.TotalSeconds;
            double endOffset = NearestSnapTarget(drag.SnapTargets, end) - end;
            double offset = Math.Abs(startOffset) <= Math.Abs(endOffset) ?
                startOffset : endOffset;
            if (Math.Abs(offset) * _pixelsPerSecond <= 8)
                seconds = Math.Max(0, seconds + offset);
        }
        if (drag.Kind != TimelineKind.Video)
            seconds = Math.Min(seconds, Math.Max(0,
                _editor.Current.Duration.TotalSeconds - drag.Duration.TotalSeconds));
        return TimeSpan.FromSeconds(seconds);
    }

    private double[] CollectSnapTargets(Guid movingId)
    {
        if (_editor is null) return [];
        VideoProject project = _editor.Current;
        var targets = new List<double> { 0, _playhead.TotalSeconds };
        foreach (VideoSegment item in project.VideoSegments.Where(item => item.Id != movingId))
        {
            targets.Add(item.Start.TotalSeconds);
            targets.Add(item.End.TotalSeconds);
        }
        foreach (AudioSegment item in project.AudioSegments.Where(item => item.Id != movingId))
        {
            targets.Add(item.Start.TotalSeconds);
            targets.Add(item.End.TotalSeconds);
        }
        foreach (ImageOverlay item in project.ImageOverlays.Where(item => item.Id != movingId))
        {
            targets.Add(item.Start.TotalSeconds);
            targets.Add(item.End.TotalSeconds);
        }
        targets.Sort();
        return [.. targets.Distinct()];
    }

    private static double NearestSnapTarget(double[] targets, double seconds)
    {
        int index = Array.BinarySearch(targets, seconds);
        if (index >= 0) return targets[index];
        index = ~index;
        if (index == 0) return targets[0];
        if (index == targets.Length) return targets[^1];
        return seconds - targets[index - 1] <= targets[index] - seconds ?
            targets[index - 1] : targets[index];
    }

    private bool TimelineSnapEnabled => _timelineSnapEnabled;

    private void ToggleTimelineSnap()
    {
        _timelineSnapEnabled = !_timelineSnapEnabled;
        StatusText.Text = _timelineSnapEnabled ? "Timeline snapping on" : "Timeline snapping off";
    }

    private void Clip_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: ValueTuple<TimelineKind, Guid> tag }) return;
        SelectClip(tag.Item1, tag.Item2);
    }

    private void SelectClip(TimelineKind kind, Guid id)
    {
        if (_selectedId == id && _selectedKind == kind)
        {
            FocusSelectedClip();
            return;
        }
        if (!ApplyPendingInspectorChanges())
        {
            FocusSelectedClip();
            return;
        }
        if (_clipButtons.TryGetValue(_selectedId, out Button? previous))
            previous.BorderThickness = new Thickness(0);
        _selectedKind = kind;
        _selectedId = id;
        if (_clipButtons.TryGetValue(id, out Button? selected))
            selected.BorderThickness = new Thickness(2);
        RefreshInspector();
        FocusSelectedClip();
        UpdateEditActionState();
    }

    private void FocusSelectedClip()
    {
        Guid selectedId = _selectedId;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || selectedId != _selectedId) return;
            if (_clipButtons.TryGetValue(selectedId, out Button? clip) &&
                !clip.Focus(FocusState.Programmatic))
                PlayPauseButton.Focus(FocusState.Programmatic);
        });
    }

    private void Timeline_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        Seek(TimeSpan.FromSeconds(args.GetCurrentPoint(TimelineContent).Position.X /
            _pixelsPerSecond));
        BeginTimelineScrub(args);
    }

    private void Timeline_DragOver(object sender, DragEventArgs args)
    {
        if (args.DataView.Contains("Shnapp.GalleryVideoId") ||
            args.DataView.Contains("Shnapp.GalleryImageId"))
        {
            args.AcceptedOperation = DataPackageOperation.Copy;
            args.Handled = true;
        }
    }

    private async void Timeline_Drop(object sender, DragEventArgs args)
    {
        if (_editor is null) return;
        bool isVideo = args.DataView.Contains("Shnapp.GalleryVideoId");
        string format = isVideo ? "Shnapp.GalleryVideoId" : "Shnapp.GalleryImageId";
        if (!args.DataView.Contains(format)) return;
        args.Handled = true;
        args.AcceptedOperation = DataPackageOperation.Copy;
        var deferral = args.GetDeferral();
        try
        {
            double seconds = args.GetPosition(TimelineContent).X / _pixelsPerSecond;
            TimeSpan at = TimeSpan.FromSeconds(Math.Clamp(seconds, 0,
                _editor.Current.Duration.TotalSeconds));
            object value = await args.DataView.GetDataAsync(format);
            if (value is not string idText || !Guid.TryParse(idText, out Guid id))
                throw new InvalidOperationException("This gallery item could not be added.");
            await ImportGalleryMediaAsync(id, isVideo, at);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not add media: {exception.Message}";
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void BeginTimelineScrub(PointerRoutedEventArgs args)
    {
        if (TimelineContent.CapturePointer(args.Pointer))
            _scrubbingPointerId = args.Pointer.PointerId;
    }

    private void Timeline_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_scrubbingPointerId != args.Pointer.PointerId) return;
        Seek(TimeSpan.FromSeconds(args.GetCurrentPoint(TimelineContent).Position.X /
            _pixelsPerSecond));
        args.Handled = true;
    }

    private void Timeline_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_scrubbingPointerId != args.Pointer.PointerId) return;
        Seek(TimeSpan.FromSeconds(args.GetCurrentPoint(TimelineContent).Position.X /
            _pixelsPerSecond));
        _scrubbingPointerId = null;
        TimelineContent.ReleasePointerCapture(args.Pointer);
        args.Handled = true;
    }

    private void Timeline_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_scrubbingPointerId == args.Pointer.PointerId)
            _scrubbingPointerId = null;
    }

    private void TimelineScroll_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (Math.Abs(args.NewSize.Width - _lastTimelineViewportWidth) >= 1)
            RefreshTimeline();
    }

    private void TimelineScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        double left = TimelineScroll.HorizontalOffset;
        double right = left + TimelineScroll.ActualWidth;
        if (left < _renderedWindowStartX || right > _renderedWindowEndX)
            RefreshTimeline();
    }

    private void ChangeTimelineZoom(int direction)
    {
        if (_editor is null) return;
        double viewportWidth = Math.Max(1, TimelineScroll.ActualWidth);
        double available = Math.Max(120, viewportWidth - 18);
        double fitScale = Math.Clamp(available /
            Math.Max(1, _editor.Current.Duration.TotalSeconds), .02, 42);
        double maxZoom = Math.Min(32, 672 / fitScale);
        double next = direction == 0 ? 1 :
            Math.Clamp(_timelineZoom * (direction > 0 ? 1.5 : 1 / 1.5), 1, maxZoom);
        if (Math.Abs(next - _timelineZoom) < .001) return;

        double oldOffset = TimelineScroll.HorizontalOffset;
        double playheadX = _playhead.TotalSeconds * _pixelsPerSecond - oldOffset;
        bool playheadVisible = playheadX >= 0 && playheadX <= viewportWidth;
        double anchorX = playheadVisible ? playheadX : viewportWidth / 2;
        double anchorTime = playheadVisible ? _playhead.TotalSeconds :
            (oldOffset + anchorX) / _pixelsPerSecond;

        _timelineZoom = next;
        RefreshTimeline();
        double maxOffset = Math.Max(0, TimelineContent.Width - viewportWidth);
        double targetOffset = Math.Clamp(anchorTime * _pixelsPerSecond - anchorX, 0, maxOffset);
        TimelineScroll.ChangeView(targetOffset, null, null, disableAnimation: true);
        StatusText.Text = $"Timeline zoom {next * 100:0}%";
    }

    private void DrawPlayhead()
    {
        if (_playheadLine is not null)
            Canvas.SetLeft(_playheadLine, _playhead.TotalSeconds * _pixelsPerSecond);
    }

    private void RefreshInspector()
    {
        if (_editor is null) return;
        VideoProject project = _editor.Current;
        VideoSegment? video = _selectedKind == TimelineKind.Video
            ? project.VideoSegments.FirstOrDefault(item => item.Id == _selectedId) : null;
        AudioSegment? audio = _selectedKind == TimelineKind.Audio
            ? project.AudioSegments.FirstOrDefault(item => item.Id == _selectedId) : null;
        ImageOverlay? image = _selectedKind == TimelineKind.Image
            ? project.ImageOverlays.FirstOrDefault(item => item.Id == _selectedId) : null;

        if (video is null && audio is null && image is null)
        {
            _selectedKind = TimelineKind.None;
            _selectedId = Guid.Empty;
            _selectedRange = null;
            InspectorPanel.Visibility = Visibility.Collapsed;
            SetEnabled(ApplyPropertiesButton, false);
            PendingChangesText.Visibility = Visibility.Collapsed;
            SelectionHint.Text = "Select a clip to edit";
            UpdateEditActionState();
            return;
        }

        if (ActualWidth < 640) GalleryPanel.Visibility = Visibility.Collapsed;
        InspectorPanel.Visibility = Visibility.Visible;
        SourceFields.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        AudioFields.Visibility = audio is null ? Visibility.Collapsed : Visibility.Visible;
        OverlayFields.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        VideoAsset? selectedAsset = project.Assets.FirstOrDefault(asset =>
            asset.Id == (video?.AssetId ?? audio?.AssetId ?? image!.AssetId));
        SelectedItemTitle.Text = video is not null
            ? selectedAsset?.DisplayName ?? "Recording"
            : image is not null
                ? selectedAsset?.DisplayName ?? "Image"
                : selectedAsset?.DisplayName ?? "Audio";
        TimeSpan start = video?.Start ?? audio?.Start ?? image!.Start;
        TimeSpan duration = video?.Duration ?? audio?.Duration ?? image!.Duration;
        _selectedRange = (start, start + duration);
        SelectedItemDetail.Text = $"{FormatPreciseTime(start)} — {FormatPreciseTime(start + duration)}";
        SelectionHint.Text = $"{SelectedItemTitle.Text} · {FormatPreciseTime(duration)}";
        _loadingInspector = true;
        ClipStartSeconds.Value = start.TotalSeconds;
        if (video is not null || audio is not null)
        {
            ClipSourceInSeconds.Value = (video?.SourceIn ?? audio!.SourceIn).TotalSeconds;
            ClipSourceOutSeconds.Value = (video?.SourceOut ?? audio!.SourceOut).TotalSeconds;
        }
        if (audio is not null)
        {
            AudioGainSlider.Value = audio.Gain;
            AudioMuteSwitch.IsOn = audio.Muted;
        }
        if (image is not null)
        {
            OverlayDurationSeconds.Value = image.Duration.TotalSeconds;
            OverlayX.Value = image.Bounds.X;
            OverlayY.Value = image.Bounds.Y;
            OverlayWidth.Value = image.Bounds.Width;
            OverlayHeight.Value = image.Bounds.Height;
            var layers = project.ImageOverlays.Select((item, index) => (item, index))
                .GroupBy(entry => ImageLayerKey(entry.item))
                .OrderBy(group => group.First().item.ZIndex)
                .ThenBy(group => group.Min(entry => entry.index))
                .ToArray();
            int position = Array.FindIndex(layers, layer =>
                layer.Key == ImageLayerKey(image));
            SetEnabled(MoveImageForwardButton, position < layers.Length - 1);
            SetEnabled(MoveImageBackButton, position > 0);
        }
        _loadingInspector = false;
        SetEnabled(ApplyPropertiesButton, false);
        PendingChangesText.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(ApplyPropertiesButton, "No clip changes to apply");
        UpdateEditActionState();
    }

    private void InspectorNumber_ValueChanged(NumberBox sender,
        NumberBoxValueChangedEventArgs args) => MarkInspectorDirty();

    private void InspectorSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args) =>
        MarkInspectorDirty();

    private void InspectorMute_Toggled(object sender, RoutedEventArgs args) =>
        MarkInspectorDirty();

    private void MarkInspectorDirty()
    {
        if (_loadingInspector || _editor is null || _selectedKind == TimelineKind.None ||
            ApplyPropertiesButton is null) return;
        SetEnabled(ApplyPropertiesButton, true);
        PendingChangesText.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(ApplyPropertiesButton, "Apply pending clip changes");
    }

    private void UpdateEditActionState()
    {
        if (_editor is null) return;
        bool selected = _selectedKind != TimelineKind.None;
        bool inside = _selectedRange is { } clip && _playhead > clip.Start && _playhead < clip.End;
        bool videoAtPlayhead = _selectedKind == TimelineKind.None &&
            _editor.Current.VideoSegments.Any(item =>
                _playhead > item.Start && _playhead < item.End);
        SetEnabled(SplitButton, inside || videoAtPlayhead);
        SetEnabled(TrimLeftButton, inside);
        SetEnabled(TrimRightButton, inside);
        SetEnabled(DeleteClipButton, selected && _selectedRange.HasValue);
        bool canCut = _markIn.HasValue && _markOut.HasValue &&
            _markIn.Value != _markOut.Value;
        if (canCut && _selectedKind is TimelineKind.Audio or TimelineKind.Image)
        {
            TimeSpan start = Min(_markIn!.Value, _markOut!.Value);
            TimeSpan end = _markIn.Value > _markOut.Value ? _markIn.Value : _markOut.Value;
            canCut = _selectedRange is { } selectedRange &&
                start < selectedRange.End && end > selectedRange.Start;
        }
        if (canCut && _selectedKind is TimelineKind.Video or TimelineKind.None)
        {
            TimeSpan start = Min(_markIn!.Value, _markOut!.Value);
            TimeSpan end = _markIn.Value > _markOut.Value ? _markIn.Value : _markOut.Value;
            canCut = start > TimeSpan.Zero || end < _editor.Current.Duration;
        }
        SetEnabled(CutRangeButton, canCut);
    }

    private static void SetEnabled(Button button, bool enabled)
    {
        if (button.IsEnabled != enabled) button.IsEnabled = enabled;
    }

    private void ApplyProperties_Click(object sender, RoutedEventArgs args) =>
        ApplyPendingInspectorChanges();

    private bool ApplyPendingInspectorChanges()
    {
        if (_editor is null || ApplyPropertiesButton is null ||
            !ApplyPropertiesButton.IsEnabled) return true;
        if (TryEdit(() =>
        {
            TimeSpan start = Seconds(ClipStartSeconds.Value);
            if (_selectedKind == TimelineKind.Video)
            {
                VideoSegment clip = _editor.Current.VideoSegments.First(item => item.Id == _selectedId);
                _editor.UpdateVideoSegment(clip with
                {
                    Start = start,
                    SourceIn = Seconds(ClipSourceInSeconds.Value),
                    SourceOut = Seconds(ClipSourceOutSeconds.Value),
                });
            }
            else if (_selectedKind == TimelineKind.Audio)
            {
                AudioSegment clip = _editor.Current.AudioSegments.First(item => item.Id == _selectedId);
                _editor.UpdateAudioSegment(clip with
                {
                    Start = start,
                    SourceIn = Seconds(ClipSourceInSeconds.Value),
                    SourceOut = Seconds(ClipSourceOutSeconds.Value),
                    Gain = AudioGainSlider.Value,
                    Muted = AudioMuteSwitch.IsOn,
                });
            }
            else if (_selectedKind == TimelineKind.Image)
            {
                ImageOverlay overlay = _editor.Current.ImageOverlays.First(item => item.Id == _selectedId);
                _editor.UpdateImageOverlay(overlay with
                {
                    Start = start,
                    Duration = Seconds(OverlayDurationSeconds.Value),
                    Bounds = new VideoRect(OverlayX.Value, OverlayY.Value,
                        OverlayWidth.Value, OverlayHeight.Value),
                });
            }
        }))
        {
            SetEnabled(ApplyPropertiesButton, false);
            PendingChangesText.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(ApplyPropertiesButton, "No clip changes to apply");
            return true;
        }
        return false;
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs args) =>
        DeleteSelected(ripple: false);

    private void RippleDeleteSelected()
    {
        if (_selectedKind != TimelineKind.Video)
        {
            StatusText.Text = "Select a video clip to ripple delete.";
            return;
        }
        DeleteSelected(ripple: true);
    }

    private void DeleteSelected(bool ripple)
    {
        if (_editor is null) return;
        TryEdit(() =>
        {
            if (_selectedKind == TimelineKind.Video)
                _editor.DeleteVideoSegment(_selectedId, ripple);
            else if (_selectedKind == TimelineKind.Audio)
                _editor.DeleteAudioSegment(_selectedId);
            else if (_selectedKind == TimelineKind.Image)
                _editor.DeleteImageOverlay(_selectedId);
        });
    }

    private void MoveImageForward_Click(object sender, RoutedEventArgs args) =>
        MoveSelectedImageLayer(1);

    private void MoveImageBack_Click(object sender, RoutedEventArgs args) =>
        MoveSelectedImageLayer(-1);

    private void MoveSelectedImageLayer(int direction)
    {
        if (_editor is null || _selectedKind != TimelineKind.Image) return;
        TryEdit(() => _editor.MoveImageLayer(_selectedId, direction));
    }

    private void Split_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null) return;
        TryEdit(() =>
        {
            if (_selectedKind == TimelineKind.None)
            {
                VideoSegment? videoAtPlayhead = _editor.Current.VideoSegments.FirstOrDefault(
                    item => _playhead > item.Start && _playhead < item.End);
                if (videoAtPlayhead is null) return;
                _selectedKind = TimelineKind.Video;
                _selectedId = videoAtPlayhead.Id;
            }
            if (_selectedKind == TimelineKind.Video)
                _selectedId = _editor.SplitVideoSegment(_selectedId, _playhead);
            else if (_selectedKind == TimelineKind.Audio)
                _selectedId = _editor.SplitAudioSegment(_selectedId, _playhead);
            else if (_selectedKind == TimelineKind.Image)
                _selectedId = _editor.SplitImageOverlay(_selectedId, _playhead);
            RefreshInspector();
            RefreshTimeline();
        });
    }

    private void TrimLeft_Click(object sender, RoutedEventArgs args) => TrimAtPlayhead(left: true);
    private void TrimRight_Click(object sender, RoutedEventArgs args) => TrimAtPlayhead(left: false);

    private void TrimAtPlayhead(bool left)
    {
        if (_editor is null) return;
        TryEdit(() =>
        {
            if (_selectedKind == TimelineKind.Video)
            {
                VideoSegment item = _editor.Current.VideoSegments.First(value => value.Id == _selectedId);
                RequireInside(item.Start, item.End);
                _editor.UpdateVideoSegment(left
                    ? item with { Start = _playhead, SourceIn = item.SourceIn + (_playhead - item.Start) }
                    : item with { SourceOut = item.SourceIn + (_playhead - item.Start) });
            }
            else if (_selectedKind == TimelineKind.Audio)
            {
                AudioSegment item = _editor.Current.AudioSegments.First(value => value.Id == _selectedId);
                RequireInside(item.Start, item.End);
                _editor.UpdateAudioSegment(left
                    ? item with { Start = _playhead, SourceIn = item.SourceIn + (_playhead - item.Start) }
                    : item with { SourceOut = item.SourceIn + (_playhead - item.Start) });
            }
            else if (_selectedKind == TimelineKind.Image)
            {
                ImageOverlay item = _editor.Current.ImageOverlays.First(value => value.Id == _selectedId);
                RequireInside(item.Start, item.End);
                _editor.UpdateImageOverlay(left
                    ? item with { Start = _playhead, Duration = item.End - _playhead }
                    : item with { Duration = _playhead - item.Start });
            }
        });
    }

    private void RequireInside(TimeSpan start, TimeSpan end)
    {
        if (_playhead <= start || _playhead >= end)
            throw new ArgumentOutOfRangeException(nameof(_playhead), "Seek inside the selected clip first.");
    }

    private void MarkIn_Click(object sender, RoutedEventArgs args)
    {
        _markIn = _playhead;
        UpdateMarkedRangeHint();
        RefreshTimeline();
        UpdateEditActionState();
    }

    private void MarkOut_Click(object sender, RoutedEventArgs args)
    {
        _markOut = _playhead;
        UpdateMarkedRangeHint();
        RefreshTimeline();
        UpdateEditActionState();
    }

    private void CutRange_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null) return;
        TryEdit(() =>
        {
            if (_markIn is null || _markOut is null)
                throw new InvalidOperationException("Mark both In and Out before cutting.");
            TimeSpan start = Min(_markIn.Value, _markOut.Value);
            TimeSpan end = _markIn.Value > _markOut.Value ? _markIn.Value : _markOut.Value;
            if (_selectedKind == TimelineKind.Audio)
                _editor.CutAudioRange(_selectedId, start, end);
            else if (_selectedKind == TimelineKind.Image)
                _selectedId = _editor.CutImageRange(_selectedId, start, end) ?? Guid.Empty;
            else
                _editor.CutRange(start, end);
            _markIn = _markOut = null;
            Seek(start);
            RefreshInspector();
            RefreshTimeline();
            UpdateEditActionState();
        });
    }

    private void UpdateMarkedRangeHint()
    {
        SelectionHint.Text = (_markIn, _markOut) switch
        {
            ({ } start, { } end) =>
                $"{FormatPreciseTime(Min(start, end))}–{FormatPreciseTime(start > end ? start : end)}",
            ({ } start, _) => $"In {FormatPreciseTime(start)}",
            (_, { } end) => $"Out {FormatPreciseTime(end)}",
            _ => "Select a clip to edit",
        };
    }

    private void CloseInspector_Click(object sender, RoutedEventArgs args)
    {
        if (!ApplyPendingInspectorChanges()) return;
        _selectedKind = TimelineKind.None;
        _selectedId = Guid.Empty;
        _selectedRange = null;
        InspectorPanel.Visibility = Visibility.Collapsed;
        SetEnabled(ApplyPropertiesButton, false);
        PendingChangesText.Visibility = Visibility.Collapsed;
        UpdateMarkedRangeHint();
        RefreshTimeline();
        UpdateEditActionState();
    }

    private bool ClearTimelineSelectionAndRange()
    {
        if (_selectedKind == TimelineKind.None && !_markIn.HasValue && !_markOut.HasValue)
            return false;
        if (!ApplyPendingInspectorChanges()) return true;
        _selectedKind = TimelineKind.None;
        _selectedId = Guid.Empty;
        _selectedRange = null;
        _markIn = _markOut = null;
        InspectorPanel.Visibility = Visibility.Collapsed;
        SetEnabled(ApplyPropertiesButton, false);
        PendingChangesText.Visibility = Visibility.Collapsed;
        SelectionHint.Text = "Select a clip to edit";
        RefreshTimeline();
        UpdateEditActionState();
        return true;
    }

    private bool TryEdit(Action action)
    {
        try
        {
            action();
            StatusText.Text = "";
            return true;
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            return false;
        }
    }

    private static TimeSpan Seconds(double value)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Enter a valid positive time.");
        return TimeSpan.FromSeconds(value);
    }

    private static Guid ImageLayerKey(ImageOverlay image) =>
        image.LayerId == Guid.Empty ? image.Id : image.LayerId;
}
