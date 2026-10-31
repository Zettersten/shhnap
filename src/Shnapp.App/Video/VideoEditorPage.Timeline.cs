using Microsoft.UI;
using Microsoft.UI.Xaml;
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
    private double _pixelsPerSecond = 28;
    private Line? _playheadLine;

    private void RefreshTimeline()
    {
        if (_editor is null || TimelineCanvas is null) return;
        VideoProject project = _editor.Current;
        double available = Math.Max(400, TimelineScroll.ActualWidth - 18);
        _pixelsPerSecond = Math.Clamp(available / Math.Max(1, project.Duration.TotalSeconds), 7, 42);
        double width = Math.Max(available, project.Duration.TotalSeconds * _pixelsPerSecond + 24);
        TimelineContent.Width = width;
        TimelineCanvas.Width = width;
        TimelineCanvas.Children.Clear();

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
                Width = width,
                Height = 27,
                Fill = new SolidColorBrush(Color.FromArgb(
                    lane % 2 == 0 ? (byte)22 : (byte)12, 255, 255, 255)),
            };
            Canvas.SetTop(background, 22 + lane * 28);
            TimelineCanvas.Children.Add(background);
        }

        double tickSeconds = project.Duration.TotalMinutes > 15 ? 60 :
            project.Duration.TotalMinutes > 4 ? 30 :
            project.Duration.TotalSeconds > 90 ? 10 : 5;
        for (double seconds = 0; seconds <= project.Duration.TotalSeconds; seconds += tickSeconds)
        {
            double x = seconds * _pixelsPerSecond;
            var tick = new Line
            {
                X1 = 0, X2 = 0, Y1 = 17, Y2 = 162,
                Stroke = new SolidColorBrush(Color.FromArgb(46, 220, 234, 255)),
                StrokeThickness = 1,
            };
            Canvas.SetLeft(tick, x);
            TimelineCanvas.Children.Add(tick);
            var label = new TextBlock
            {
                Text = FormatTime(TimeSpan.FromSeconds(seconds)),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(180, 225, 235, 247)),
            };
            Canvas.SetLeft(label, x + 3);
            TimelineCanvas.Children.Add(label);
        }

        foreach (VideoSegment segment in project.VideoSegments)
            AddTimelineClip(segment.Id, TimelineKind.Video, 0, segment.Start,
                segment.Duration, "Recording", laneColors[0]);
        foreach (AudioSegment segment in project.AudioSegments)
        {
            VideoAsset asset = project.Assets.First(item => item.Id == segment.AssetId);
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
                _ => System.IO.Path.GetFileName(asset.RelativePath),
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
        double width = Math.Max(3, duration.TotalSeconds * _pixelsPerSecond - 2);
        bool selected = _selectedId == id && _selectedKind == kind;
        var surface = new Border
        {
            Width = width,
            Height = 24,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(fill),
            BorderThickness = new Thickness(selected ? 2 : 0),
            BorderBrush = new SolidColorBrush(Colors.White),
            Tag = (kind, id),
            Child = new TextBlock
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
        Canvas.SetLeft(surface, start.TotalSeconds * _pixelsPerSecond);
        Canvas.SetTop(surface, 24 + lane * 28);
        TimelineCanvas.Children.Add(surface);
    }

    private void Clip_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Border { Tag: ValueTuple<TimelineKind, Guid> tag }) return;
        _selectedKind = tag.Item1;
        _selectedId = tag.Item2;
        Seek(TimeSpan.FromSeconds(args.GetCurrentPoint(TimelineContent).Position.X /
            _pixelsPerSecond));
        RefreshInspector();
        RefreshTimeline();
        args.Handled = true;
    }

    private void Timeline_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        Seek(TimeSpan.FromSeconds(args.GetCurrentPoint(TimelineContent).Position.X /
            _pixelsPerSecond));
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
            InspectorPanel.Visibility = Visibility.Collapsed;
            SelectionHint.Text = "Select a clip to edit";
            return;
        }

        InspectorPanel.Visibility = Visibility.Visible;
        SourceFields.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        AudioFields.Visibility = audio is null ? Visibility.Collapsed : Visibility.Visible;
        OverlayFields.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        SelectedItemTitle.Text = video is not null ? "Video clip" :
            image is not null ? "Image overlay" : "Audio clip";
        TimeSpan start = video?.Start ?? audio?.Start ?? image!.Start;
        TimeSpan duration = video?.Duration ?? audio?.Duration ?? image!.Duration;
        SelectedItemDetail.Text = $"{FormatTime(start)} — {FormatTime(start + duration)}";
        SelectionHint.Text = $"{SelectedItemTitle.Text} · {FormatTime(duration)}";
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
            {
                ImageOverlay image = _editor.Current.ImageOverlays.First(item => item.Id == _selectedId);
                if (_playhead <= image.Start || _playhead >= image.End)
                    throw new ArgumentOutOfRangeException(nameof(_playhead), "Seek inside the image to split it.");
                _editor.UpdateImageOverlay(image with { Duration = _playhead - image.Start });
                var right = image with
                {
                    Id = Guid.NewGuid(), Start = _playhead, Duration = image.End - _playhead,
                };
                _editor.AddImageOverlay(right);
                _selectedId = right.Id;
            }
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
        SelectionHint.Text = $"In {FormatTime(_playhead)}";
    }

    private void MarkOut_Click(object sender, RoutedEventArgs args)
    {
        _markOut = _playhead;
        SelectionHint.Text = $"Out {FormatTime(_playhead)}";
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
            else
                _editor.CutRange(start, end);
            _markIn = _markOut = null;
            Seek(start);
        });
    }

    private void CloseInspector_Click(object sender, RoutedEventArgs args)
    {
        _selectedKind = TimelineKind.None;
        _selectedId = Guid.Empty;
        InspectorPanel.Visibility = Visibility.Collapsed;
        SelectionHint.Text = "Select a clip to edit";
        RefreshTimeline();
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
