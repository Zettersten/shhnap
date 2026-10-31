using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Shnapp.Core;
using Windows.UI;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private enum TimelineKind { None, Video, Audio, Image }

    private TimelineKind _selectedKind;
    private Guid _selectedId;
    private (TimeSpan Start, TimeSpan End)? _selectedRange;
    private double _pixelsPerSecond = 28;
    private double _lastTimelineViewportWidth = double.NaN;
    private double _renderedWindowStartX;
    private double _renderedWindowEndX;
    private uint? _scrubbingPointerId;
    private Line? _playheadLine;
    private readonly Dictionary<Guid, Button> _clipButtons = new();
    private static readonly double[] TimelineTickIntervals =
        [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600,
         7200, 14400, 28800, 43200, 86400];

    private void RefreshTimeline()
    {
        if (_editor is null || TimelineCanvas is null) return;
        VideoProject project = _editor.Current;
        _lastTimelineViewportWidth = TimelineScroll.ActualWidth;
        double available = Math.Max(400, TimelineScroll.ActualWidth - 18);
        _pixelsPerSecond = Math.Clamp(available / Math.Max(1, project.Duration.TotalSeconds), .02, 42);
        double width = Math.Max(available, project.Duration.TotalSeconds * _pixelsPerSecond + 24);
        TimelineContent.Width = width;
        TimelineCanvas.Width = width;
        TimelineCanvas.Children.Clear();
        _clipButtons.Clear();

        double viewport = Math.Max(400, TimelineScroll.ActualWidth);
        double offset = TimelineScroll.HorizontalOffset;
        _renderedWindowStartX = Math.Max(0, offset - viewport);
        _renderedWindowEndX = Math.Min(width, offset + viewport * 2);
        double renderedWidth = _renderedWindowEndX - _renderedWindowStartX;

        var laneColors = new[]
        {
            Color.FromArgb(255, 52, 112, 162),
            Color.FromArgb(255, 66, 126, 112),
            Color.FromArgb(255, 95, 118, 93),
            Color.FromArgb(255, 125, 95, 157),
            Color.FromArgb(255, 119, 101, 70),
        };
        for (int lane = 0; lane < 5; lane++)
        {
            var background = new Rectangle
            {
                Width = renderedWidth,
                Height = 27,
                Fill = (Brush)Application.Current.Resources["ShnappFloatingEdgeBrush"],
                Opacity = lane % 2 == 0 ? .12 : .07,
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
                Height = 162,
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
                X1 = 0, X2 = 0, Y1 = 17, Y2 = 162,
                Stroke = (Brush)Application.Current.Resources["ShnappToolbarInactiveIconBrush"],
                Opacity = .38,
                StrokeThickness = 1,
            };
            Canvas.SetLeft(tick, x);
            TimelineCanvas.Children.Add(tick);
            var label = new TextBlock
            {
                Text = FormatTime(TimeSpan.FromSeconds(seconds)),
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["ShnappToolbarInactiveIconBrush"],
            };
            Canvas.SetLeft(label, x + 3);
            TimelineCanvas.Children.Add(label);
        }

        foreach (VideoSegment segment in project.VideoSegments)
            AddTimelineClip(segment.Id, TimelineKind.Video, 0, segment.Start,
                segment.Duration, "Recording", laneColors[0]);
        Dictionary<Guid, VideoAsset> assets = project.Assets.ToDictionary(item => item.Id);
        foreach (AudioSegment segment in project.AudioSegments)
        {
            VideoAsset asset = assets[segment.AssetId];
            int lane = asset.Kind switch
            {
                VideoAssetKind.ComputerAudio => 1,
                VideoAssetKind.MicrophoneAudio => 2,
                _ => 4,
            };
            string title = asset.Kind switch
            {
                VideoAssetKind.ComputerAudio => "Computer sound",
                VideoAssetKind.MicrophoneAudio => "Microphone",
                _ => asset.DisplayName ?? "Added audio",
            };
            AddTimelineClip(segment.Id, TimelineKind.Audio, lane,
                segment.Start, segment.Duration,
                segment.Muted ? $"{title} · muted" : title, laneColors[lane]);
        }
        foreach (ImageOverlay overlay in project.ImageOverlays)
            AddTimelineClip(overlay.Id, TimelineKind.Image, 3,
                overlay.Start, overlay.Duration, "Image overlay", laneColors[3]);

        _playheadLine = new Line
        {
            X1 = 0, X2 = 0, Y1 = 0, Y2 = 162,
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
        bool selected = _selectedId == id && _selectedKind == kind;
        var surface = new Button
        {
            Width = width,
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
        surface.Click += Clip_Click;
        AutomationProperties.SetName(surface,
            $"{title}, {FormatPreciseTime(start)} to {FormatPreciseTime(start + duration)}. Select clip.");
        ToolTipService.SetToolTip(surface,
            $"{title} · {FormatPreciseTime(start)}–{FormatPreciseTime(start + duration)}");
        Canvas.SetLeft(surface, left);
        Canvas.SetTop(surface, 24 + lane * 28);
        TimelineCanvas.Children.Add(surface);
        _clipButtons[id] = surface;
    }

    private void Clip_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Button { Tag: ValueTuple<TimelineKind, Guid> tag }) return;
        SelectClip(tag.Item1, tag.Item2);
        Seek(TimeSpan.FromSeconds(args.GetCurrentPoint(TimelineContent).Position.X /
            _pixelsPerSecond));
        BeginTimelineScrub(args);
        args.Handled = true;
    }

    private void Clip_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: ValueTuple<TimelineKind, Guid> tag }) return;
        SelectClip(tag.Item1, tag.Item2);
    }

    private void SelectClip(TimelineKind kind, Guid id)
    {
        if (_selectedId == id && _selectedKind == kind) return;
        if (_clipButtons.TryGetValue(_selectedId, out Button? previous))
            previous.BorderThickness = new Thickness(0);
        _selectedKind = kind;
        _selectedId = id;
        if (_clipButtons.TryGetValue(id, out Button? selected))
            selected.BorderThickness = new Thickness(2);
        RefreshInspector();
        UpdateEditActionState();
    }

    private void Timeline_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        Seek(TimeSpan.FromSeconds(args.GetCurrentPoint(TimelineContent).Position.X /
            _pixelsPerSecond));
        BeginTimelineScrub(args);
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
            SelectionHint.Text = "Select a clip to edit";
            UpdateEditActionState();
            return;
        }

        if (ActualWidth < 640) GalleryPanel.Visibility = Visibility.Collapsed;
        InspectorPanel.Visibility = Visibility.Visible;
        SourceFields.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        AudioFields.Visibility = audio is null ? Visibility.Collapsed : Visibility.Visible;
        OverlayFields.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        SelectedItemTitle.Text = video is not null ? "Video clip" :
            image is not null ? "Image overlay" : "Audio clip";
        TimeSpan start = video?.Start ?? audio?.Start ?? image!.Start;
        TimeSpan duration = video?.Duration ?? audio?.Duration ?? image!.Duration;
        _selectedRange = (start, start + duration);
        SelectedItemDetail.Text = $"{FormatPreciseTime(start)} — {FormatPreciseTime(start + duration)}";
        SelectionHint.Text = $"{SelectedItemTitle.Text} · {FormatPreciseTime(duration)}";
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
        }
        UpdateEditActionState();
    }

    private void UpdateEditActionState()
    {
        if (_editor is null) return;
        bool selected = _selectedKind != TimelineKind.None;
        bool inside = _selectedRange is { } clip && _playhead > clip.Start && _playhead < clip.End;
        SetEnabled(SplitButton, inside);
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

    private void ApplyProperties_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null) return;
        TryEdit(() =>
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
        });
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null) return;
        TryEdit(() =>
        {
            if (_selectedKind == TimelineKind.Video)
                _editor.DeleteVideoSegment(_selectedId, ripple: true);
            else if (_selectedKind == TimelineKind.Audio)
                _editor.DeleteAudioSegment(_selectedId);
            else if (_selectedKind == TimelineKind.Image)
                _editor.DeleteImageOverlay(_selectedId);
        });
    }

    private void Split_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null) return;
        TryEdit(() =>
        {
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
        _selectedKind = TimelineKind.None;
        _selectedId = Guid.Empty;
        _selectedRange = null;
        InspectorPanel.Visibility = Visibility.Collapsed;
        SelectionHint.Text = "Select a clip to edit";
        RefreshTimeline();
        UpdateEditActionState();
    }

    private void TryEdit(Action action)
    {
        try
        {
            action();
            StatusText.Text = "";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private static TimeSpan Seconds(double value)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Enter a valid positive time.");
        return TimeSpan.FromSeconds(value);
    }
}
