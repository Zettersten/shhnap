using Microsoft.UI;
using Microsoft.UI.Input;
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
    private enum TimelineKind { None, Video, Audio, Image, Text }
    private enum ClipDragMode { Move, TrimStart, TrimEnd }

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
    private readonly Dictionary<Guid, (bool Start, bool End)> _clipVisibleEdges = new();
    private readonly InputCursor _clipMoveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private readonly InputCursor _clipTrimCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private sealed class TimelineClipButton : Button
    {
        public void SetClipCursor(InputCursor cursor) => ProtectedCursor = cursor;
    }
    private sealed record ClipDrag(Button Surface, uint PointerId, TimelineKind Kind, Guid Id,
        ClipDragMode Mode, TimeSpan Start, TimeSpan Duration, TimeSpan MinStart,
        TimeSpan MaxEnd, double PressX, double CanvasLeft, double CanvasWidth, string Hint,
        int FrameRate, double[] SnapTargets)
    {
        public TimeSpan End => Start + Duration;
    }
    private static readonly double[] TimelineTickIntervals =
        [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600,
         7200, 14400, 28800, 43200, 86400];

    private void RefreshTimeline()
    {
        if (_editor is null || TimelineCanvas is null) return;
        VideoProject project = _editor.Current;
        Dictionary<Guid, VideoAsset> assets = project.Assets.ToDictionary(item => item.Id);
        var lanes = new List<(string Label, Color Fill)>();
        var textLanes = new Dictionary<Guid, int>();
        var imageLanes = new Dictionary<Guid, int>();
        Color textColor = Color.FromArgb(255, 73, 101, 164);
        Color imageColor = Color.FromArgb(255, 125, 95, 157);
        Color videoColor = Color.FromArgb(255, 52, 112, 162);
        Color systemColor = Color.FromArgb(255, 66, 126, 112);
        Color microphoneColor = Color.FromArgb(255, 95, 118, 93);
        Color addedAudioColor = Color.FromArgb(255, 119, 101, 70);

        foreach (TextOverlay text in project.TextOverlays.OrderByDescending(item => item.ZIndex))
        {
            textLanes[text.Id] = lanes.Count;
            string label = text.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            lanes.Add((label.Length > 24 ? label[..24] + "…" : label, textColor));
        }

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
        _clipVisibleEdges.Clear();

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
        foreach (TextOverlay overlay in project.TextOverlays)
        {
            int lane = textLanes[overlay.Id];
            AddTimelineClip(overlay.Id, TimelineKind.Text, lane,
                overlay.Start, overlay.Duration, lanes[lane].Label, textColor);
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
        bool startVisible = visibleLeft <= left;
        bool endVisible = visibleRight >= left + width;
        var content = new Grid { Height = 24, IsHitTestVisible = false };
        VideoSegment? video = kind == TimelineKind.Video
            ? _editor?.Current.VideoSegments.FirstOrDefault(item => item.Id == id) : null;
        if (video is not null)
        {
            AddFadeIndicator(video.FadeIn, true);
            AddFadeIndicator(video.FadeOut, false);
        }
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(9, 4, 9, 0),
        });
        if (visibleRight - visibleLeft >= 18)
        {
            var handleBrush = new SolidColorBrush(Colors.White);
            if (startVisible)
                content.Children.Add(new Border
                {
                    Width = 2, Height = 14, CornerRadius = new CornerRadius(1),
                    Background = handleBrush, Opacity = selected ? .9 : .6,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 0, 0, 0),
                });
            if (endVisible)
                content.Children.Add(new Border
                {
                    Width = 2, Height = 14, CornerRadius = new CornerRadius(1),
                    Background = handleBrush, Opacity = selected ? .9 : .6,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 3, 0),
                });
        }
        var surface = new TimelineClipButton
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
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = (kind, id),
            AllowDrop = true,
            Content = content,
        };
        // ButtonBase consumes pointer input before ordinary event handlers run.
        surface.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(Clip_PointerPressed), true);
        surface.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(Clip_PointerMoved), true);
        surface.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(Clip_PointerReleased), true);
        surface.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(Clip_PointerCaptureLost), true);
        surface.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler(Clip_KeyDown), true);
        surface.DragOver += Timeline_DragOver;
        surface.Drop += Timeline_Drop;
        surface.Click += Clip_Click;
        AutomationProperties.SetName(surface,
            $"{title}, {FormatPreciseTime(start)} to {FormatPreciseTime(start + duration)}" +
            $"{(video is null ? string.Empty : $", fade in {video.FadeIn.TotalSeconds:0.##} seconds, fade out {video.FadeOut.TotalSeconds:0.##} seconds")}. " +
            "Drag the middle to move, or either edge to trim.");
        ToolTipService.SetToolTip(surface,
            $"{title} · {FormatPreciseTime(start)}–{FormatPreciseTime(start + duration)} · Drag middle to move · Drag edge to trim");
        Canvas.SetLeft(surface, visibleLeft);
        Canvas.SetTop(surface, 24 + lane * 28);
        TimelineCanvas.Children.Add(surface);
        _clipButtons[id] = surface;
        _clipVisibleEdges[id] = (startVisible, endVisible);

        void AddFadeIndicator(TimeSpan fade, bool fadeIn)
        {
            if (fade <= TimeSpan.Zero) return;
            double fadeWidth = Math.Min(visibleRight - visibleLeft,
                Math.Max(4, fade.TotalSeconds * _pixelsPerSecond));
            var brush = new LinearGradientBrush
            {
                StartPoint = new global::Windows.Foundation.Point(0, 0),
                EndPoint = new global::Windows.Foundation.Point(1, 0),
            };
            brush.GradientStops.Add(new GradientStop
            {
                Color = fadeIn ? Color.FromArgb(190, 0, 0, 0) : Colors.Transparent,
                Offset = 0,
            });
            brush.GradientStops.Add(new GradientStop
            {
                Color = fadeIn ? Colors.Transparent : Color.FromArgb(190, 0, 0, 0),
                Offset = 1,
            });
            content.Children.Add(new Border
            {
                Width = fadeWidth,
                Height = 24,
                Background = brush,
                HorizontalAlignment = fadeIn
                    ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            });
        }
    }

    private void Clip_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Button { Tag: ValueTuple<TimelineKind, Guid> tag } button ||
            _editor is null) return;
        SelectClip(tag.Item1, tag.Item2);
        if (_selectedId == tag.Item2 && _selectedKind == tag.Item1 &&
            _clipButtons.TryGetValue(tag.Item2, out Button? current) &&
            ReferenceEquals(current, button) &&
            _selectedRange is { } range)
        {
            ClipDragMode mode = ClipModeAt(button, tag.Item2,
                args.GetCurrentPoint(button).Position.X);
            TimeSpan minStart = TimeSpan.Zero;
            TimeSpan maxEnd = TimeSpan.FromDays(7);
            VideoProject project = _editor.Current;
            if (tag.Item1 == TimelineKind.Video)
            {
                VideoSegment item = project.VideoSegments.First(clip => clip.Id == tag.Item2);
                VideoAsset asset = project.Assets.First(value => value.Id == item.AssetId);
                minStart = item.Start - item.SourceIn;
                maxEnd = Min(maxEnd, item.Start + asset.Duration - item.SourceIn);
                foreach (VideoSegment other in project.VideoSegments)
                {
                    if (other.Id == item.Id) continue;
                    if (other.End <= item.Start && other.End > minStart) minStart = other.End;
                    if (other.Start >= item.End && other.Start < maxEnd) maxEnd = other.Start;
                }
            }
            else if (tag.Item1 == TimelineKind.Audio)
            {
                AudioSegment item = project.AudioSegments.First(clip => clip.Id == tag.Item2);
                VideoAsset asset = project.Assets.First(value => value.Id == item.AssetId);
                minStart = item.Start - item.SourceIn;
                maxEnd = Min(maxEnd, item.Start + asset.Duration - item.SourceIn);
            }
            button.CapturePointer(args.Pointer);
            _clipDrag = new ClipDrag(button, args.Pointer.PointerId, tag.Item1, tag.Item2,
                mode, range.Start, range.End - range.Start, minStart, maxEnd,
                args.GetCurrentPoint(TimelineContent).Position.X, Canvas.GetLeft(button), button.Width,
                SelectionHint.Text, Math.Max(1, _editor.Current.FrameRate),
                CollectSnapTargets(tag.Item2));
        }
        args.Handled = true;
    }

    private void Clip_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_clipDrag is null && sender is TimelineClipButton
            { Tag: ValueTuple<TimelineKind, Guid> tag } hovered)
        {
            hovered.SetClipCursor(ClipModeAt(hovered, tag.Item2,
                args.GetCurrentPoint(hovered).Position.X) == ClipDragMode.Move
                ? _clipMoveCursor : _clipTrimCursor);
            return;
        }
        if (_clipDrag is not { } drag || !ReferenceEquals(sender, drag.Surface) ||
            drag.PointerId != args.Pointer.PointerId || _editor is null) return;
        (TimeSpan start, TimeSpan end) = DraggedClipRange(drag,
            args.GetCurrentPoint(TimelineContent).Position.X);
        double edgeDelta = (drag.Mode == ClipDragMode.TrimEnd
            ? end - drag.End : start - drag.Start).TotalSeconds * _pixelsPerSecond;
        if (Math.Abs(edgeDelta) < 3)
        {
            if (drag.Surface.Opacity < 1)
            {
                Canvas.SetLeft(drag.Surface, drag.CanvasLeft);
                drag.Surface.Width = drag.CanvasWidth;
                drag.Surface.Opacity = 1;
                SelectionHint.Text = drag.Hint;
            }
            return;
        }
        double left = drag.CanvasLeft + (start - drag.Start).TotalSeconds * _pixelsPerSecond;
        double width = drag.CanvasWidth +
            ((end - start) - drag.Duration).TotalSeconds * _pixelsPerSecond;
        Canvas.SetLeft(drag.Surface, left);
        drag.Surface.Width = Math.Max(3, width);
        drag.Surface.Opacity = .78;
        SelectionHint.Text = drag.Mode switch
        {
            ClipDragMode.Move => $"Move to {FormatPreciseTime(start)}",
            ClipDragMode.TrimStart => $"Trim in to {FormatPreciseTime(start)}",
            _ => $"Trim out to {FormatPreciseTime(end)}",
        };
        args.Handled = true;
    }

    private void Clip_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_clipDrag is not { } drag || !ReferenceEquals(sender, drag.Surface) ||
            drag.PointerId != args.Pointer.PointerId || _editor is null) return;
        (TimeSpan start, TimeSpan end) = DraggedClipRange(drag,
            args.GetCurrentPoint(TimelineContent).Position.X);
        double edgeDelta = (drag.Mode == ClipDragMode.TrimEnd
            ? end - drag.End : start - drag.Start).TotalSeconds * _pixelsPerSecond;
        bool moved = Math.Abs(edgeDelta) >= 3;
        _clipDrag = null;
        drag.Surface.ReleasePointerCapture(args.Pointer);
        Canvas.SetLeft(drag.Surface, drag.CanvasLeft);
        drag.Surface.Width = drag.CanvasWidth;
        drag.Surface.Opacity = 1;
        if (!moved)
        {
            FocusSelectedClip();
            return;
        }
        if (!ApplyPendingInspectorChanges())
        {
            SelectionHint.Text = drag.Hint;
            return;
        }

        bool committed = false;
        bool valid = TryEdit(() =>
        {
            if (drag.Kind == TimelineKind.Video)
            {
                VideoSegment clip = _editor.Current.VideoSegments.First(item => item.Id == drag.Id);
                committed = _editor.UpdateVideoSegment(drag.Mode switch
                {
                    ClipDragMode.Move => clip with { Start = start },
                    ClipDragMode.TrimStart => clip with
                    {
                        Start = start,
                        SourceIn = clip.SourceIn + (start - clip.Start),
                    },
                    _ => clip with { SourceOut = clip.SourceOut + (end - clip.End) },
                });
            }
            else if (drag.Kind == TimelineKind.Audio)
            {
                AudioSegment clip = _editor.Current.AudioSegments.First(item => item.Id == drag.Id);
                committed = _editor.UpdateAudioSegment(drag.Mode switch
                {
                    ClipDragMode.Move => clip with { Start = start },
                    ClipDragMode.TrimStart => clip with
                    {
                        Start = start,
                        SourceIn = clip.SourceIn + (start - clip.Start),
                    },
                    _ => clip with { SourceOut = clip.SourceOut + (end - clip.End) },
                });
            }
            else if (drag.Kind == TimelineKind.Image)
            {
                ImageOverlay clip = _editor.Current.ImageOverlays.First(item => item.Id == drag.Id);
                committed = _editor.UpdateImageOverlay(clip with
                {
                    Start = start,
                    Duration = end - start,
                });
            }
            else if (drag.Kind == TimelineKind.Text)
            {
                TextOverlay clip = _editor.Current.TextOverlays.First(item => item.Id == drag.Id);
                committed = _editor.UpdateTextOverlay(clip with
                {
                    Start = start,
                    Duration = end - start,
                });
            }
        });
        if (!valid || !committed) SelectionHint.Text = drag.Hint;
        FocusSelectedClip();
        args.Handled = true;
    }

    private void Clip_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_clipDrag is not { } drag || !ReferenceEquals(sender, drag.Surface) ||
            drag.PointerId != args.Pointer.PointerId) return;
        // ButtonBase may release its own capture just before PointerReleased.
        // Defer cleanup so that release can commit the drag first.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_clipDrag, drag)) return;
            _clipDrag = null;
            Canvas.SetLeft(drag.Surface, drag.CanvasLeft);
            drag.Surface.Width = drag.CanvasWidth;
            drag.Surface.Opacity = 1;
            SelectionHint.Text = drag.Hint;
        });
    }

    private void Clip_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != global::Windows.System.VirtualKey.Space || _closed || _editor is null) return;
        args.Handled = true;
        if (args.KeyStatus.WasKeyDown) return;
        TogglePlayback();
    }

    private ClipDragMode ClipModeAt(Button surface, Guid id, double pointerX)
    {
        if (surface.Width < 18 || !_clipVisibleEdges.TryGetValue(id, out var visible))
            return ClipDragMode.Move;
        double edgeWidth = Math.Min(9, surface.Width / 3);
        if (visible.Start && pointerX <= edgeWidth) return ClipDragMode.TrimStart;
        if (visible.End && pointerX >= surface.Width - edgeWidth) return ClipDragMode.TrimEnd;
        return ClipDragMode.Move;
    }

    private (TimeSpan Start, TimeSpan End) DraggedClipRange(ClipDrag drag, double pointerX)
    {
        if (drag.Mode == ClipDragMode.Move)
        {
            TimeSpan start = DraggedClipStart(drag, pointerX);
            return (start, start + drag.Duration);
        }

        double pointerDelta = pointerX - drag.PressX;
        if (Math.Abs(pointerDelta) < 3) return (drag.Start, drag.End);
        double seconds = (drag.Mode == ClipDragMode.TrimStart
            ? drag.Start : drag.End).TotalSeconds + pointerDelta / _pixelsPerSecond;
        seconds = Math.Round(seconds * drag.FrameRate) / drag.FrameRate;
        if (_timelineSnapEnabled && drag.SnapTargets.Length > 0)
        {
            double snapped = NearestSnapTarget(drag.SnapTargets, seconds);
            if (Math.Abs(snapped - seconds) * _pixelsPerSecond <= 8) seconds = snapped;
        }
        double minimumLength = Math.Min(drag.Duration.TotalSeconds, 1.0 / drag.FrameRate);
        if (drag.Mode == ClipDragMode.TrimStart)
        {
            seconds = Math.Clamp(seconds, drag.MinStart.TotalSeconds,
                drag.End.TotalSeconds - minimumLength);
            return (TimeSpan.FromSeconds(seconds), drag.End);
        }
        seconds = Math.Clamp(seconds, drag.Start.TotalSeconds + minimumLength,
            drag.MaxEnd.TotalSeconds);
        return (drag.Start, TimeSpan.FromSeconds(seconds));
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
        seconds = Math.Min(seconds, TimeSpan.FromDays(7).TotalSeconds -
            drag.Duration.TotalSeconds);
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
        foreach (TextOverlay item in project.TextOverlays.Where(item => item.Id != movingId))
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
        if (_clipButtons.TryGetValue(selectedId, out Button? current))
            current.Focus(FocusState.Programmatic);
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
        TextOverlay? text = _selectedKind == TimelineKind.Text
            ? project.TextOverlays.FirstOrDefault(item => item.Id == _selectedId) : null;

        if (video is null && audio is null && image is null && text is null)
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

        if (ActualWidth < 640) CloseGallery();
        InspectorPanel.Visibility = Visibility.Visible;
        SourceFields.Visibility = video is not null || audio is not null
            ? Visibility.Visible : Visibility.Collapsed;
        VideoFadeFields.Visibility = video is null ? Visibility.Collapsed : Visibility.Visible;
        AudioFields.Visibility = audio is null ? Visibility.Collapsed : Visibility.Visible;
        OverlayFields.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        TextFields.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        Guid? selectedAssetId = video?.AssetId ?? audio?.AssetId ?? image?.AssetId;
        VideoAsset? selectedAsset = selectedAssetId is { } assetId
            ? project.Assets.FirstOrDefault(asset => asset.Id == assetId) : null;
        SelectedItemTitle.Text = video is not null
            ? selectedAsset?.DisplayName ?? "Recording"
            : image is not null
                ? selectedAsset?.DisplayName ?? "Image"
                : text is not null ? "Text" : selectedAsset?.DisplayName ?? "Audio";
        TimeSpan start = video?.Start ?? audio?.Start ?? image?.Start ?? text!.Start;
        TimeSpan duration = video?.Duration ?? audio?.Duration ?? image?.Duration ?? text!.Duration;
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
        if (video is not null)
        {
            VideoFadeInSeconds.Value = video.FadeIn.TotalSeconds;
            VideoFadeOutSeconds.Value = video.FadeOut.TotalSeconds;
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
        if (text is not null)
        {
            if (TextContentBox.Text != text.Text) TextContentBox.Text = text.Text;
            TextDurationSeconds.Value = text.Duration.TotalSeconds;
            TextFontSize.Value = text.FontSize;
            TextAlignmentBox.SelectedIndex = (int)text.Alignment;
            TextX.Value = text.Bounds.X;
            TextY.Value = text.Bounds.Y;
            TextWidth.Value = text.Bounds.Width;
            TextHeight.Value = text.Bounds.Height;
            global::Windows.UI.Color color = ParseTextColor(text.Color);
            TextColorPicker.Color = color;
            TextColorSwatch.Background = new SolidColorBrush(color);
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
        if (canCut && _selectedKind is TimelineKind.Audio or TimelineKind.Image or TimelineKind.Text)
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
        SetEnabled(AddTransitionButton, FindDipCut() is not null);
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
                    FadeIn = Seconds(VideoFadeInSeconds.Value),
                    FadeOut = Seconds(VideoFadeOutSeconds.Value),
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
            else if (_selectedKind == TimelineKind.Text)
            {
                TextOverlay overlay = _editor.Current.TextOverlays.First(item => item.Id == _selectedId);
                _editor.UpdateTextOverlay(overlay with
                {
                    Start = start,
                    Duration = Seconds(TextDurationSeconds.Value),
                    Text = TextContentBox.Text.Trim(),
                    Bounds = new VideoRect(TextX.Value, TextY.Value,
                        TextWidth.Value, TextHeight.Value),
                    FontSize = TextFontSize.Value,
                    Color = FormatTextColor(TextColorPicker.Color),
                    Alignment = (TextOverlayAlignment)Math.Max(0, TextAlignmentBox.SelectedIndex),
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
            else if (_selectedKind == TimelineKind.Text)
                _editor.DeleteTextOverlay(_selectedId);
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
            else if (_selectedKind == TimelineKind.Text)
                _selectedId = _editor.SplitTextOverlay(_selectedId, _playhead);
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
            else if (_selectedKind == TimelineKind.Text)
            {
                TextOverlay item = _editor.Current.TextOverlays.First(value => value.Id == _selectedId);
                RequireInside(item.Start, item.End);
                _editor.UpdateTextOverlay(left
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
            else if (_selectedKind == TimelineKind.Text)
                _selectedId = _editor.CutTextRange(_selectedId, start, end) ?? Guid.Empty;
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
