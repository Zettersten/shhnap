using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shnapp.Core;
using Windows.UI;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private sealed record TimelineTransitionMarker(VideoTransitionLane Lane,
        Guid OutgoingId, Guid IncomingId, Guid? TransitionId);

    private void AddTimelineTransitionMarkers(VideoProject project, int videoLane,
        IReadOnlyDictionary<Guid, int> imageLanes,
        IReadOnlyDictionary<Guid, int> textLanes)
    {
        var transitions = project.Transitions.ToDictionary(
            item => (item.Lane, item.OutgoingId, item.IncomingId));

        VideoSegment[] video = [.. project.VideoSegments.OrderBy(item => item.Start)];
        for (int index = 0; index + 1 < video.Length; index++)
            if (video[index].End == video[index + 1].Start)
                AddMarker(VideoTransitionLane.Video, video[index].Id,
                    video[index + 1].Id, video[index].End, videoLane,
                    transitions);

        foreach (var group in project.ImageOverlays.GroupBy(ImageLayerKey))
        {
            ImageOverlay[] images = [.. group.OrderBy(item => item.Start)];
            for (int index = 0; index + 1 < images.Length; index++)
                if (images[index].End == images[index + 1].Start &&
                    imageLanes.TryGetValue(images[index].Id, out int lane))
                    AddMarker(VideoTransitionLane.Image, images[index].Id,
                        images[index + 1].Id, images[index].End, lane,
                        transitions);
        }

        foreach (var group in project.TextOverlays.GroupBy(item =>
            item.LayerId == Guid.Empty ? item.Id : item.LayerId))
        {
            TextOverlay[] texts = [.. group.OrderBy(item => item.Start)];
            for (int index = 0; index + 1 < texts.Length; index++)
                if (texts[index].End == texts[index + 1].Start &&
                    textLanes.TryGetValue(texts[index].Id, out int lane))
                    AddMarker(VideoTransitionLane.Text, texts[index].Id,
                        texts[index + 1].Id, texts[index].End, lane,
                        transitions);
        }
    }

    private void AddMarker(VideoTransitionLane lane, Guid outgoingId,
        Guid incomingId, TimeSpan at, int timelineLane,
        IReadOnlyDictionary<(VideoTransitionLane Lane, Guid OutgoingId,
            Guid IncomingId), VideoTransition> transitions)
    {
        double x = at.TotalSeconds * _pixelsPerSecond;
        if (x < _renderedWindowStartX || x > _renderedWindowEndX) return;
        transitions.TryGetValue((lane, outgoingId, incomingId),
            out VideoTransition? transition);
        bool existing = transition is not null;
        bool selected = existing && _selectedKind == TimelineKind.Transition &&
            _selectedId == transition!.Id;
        if (transition is not null)
        {
            double spanWidth = Math.Max(16,
                transition.Duration.TotalSeconds * _pixelsPerSecond);
            var span = new Border
            {
                Width = spanWidth,
                Height = 24,
                CornerRadius = new CornerRadius(3),
                Background = (Brush)Application.Current.Resources["ShnappBlueBrush"],
                Opacity = selected ? .28 : .17,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(span, x - spanWidth / 2);
            Canvas.SetTop(span, 24 + timelineLane * 28);
            TimelineCanvas.Children.Add(span);
        }
        var marker = new Button
        {
            Width = 16,
            Height = 16,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = (Brush)Application.Current.Resources[
                existing ? "ShnappBlueBrush" : "ShnappImagePanelBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ShnappToolbarActiveIconBrush"],
            BorderThickness = new Thickness(selected ? 2 : 1),
            Tag = new TimelineTransitionMarker(lane, outgoingId, incomingId,
                transition?.Id),
            Content = new FontIcon
            {
                Glyph = existing ? TransitionGlyph(transition!.Effect) : "\uE710",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 10,
                Width = 10,
                Height = 10,
                Foreground = existing
                    ? new SolidColorBrush(Colors.White)
                    : (Brush)Application.Current.Resources["ShnappToolbarActiveIconBrush"],
            },
        };
        string kind = lane switch
        {
            VideoTransitionLane.Image => "image",
            VideoTransitionLane.Text => "text",
            _ => "video",
        };
        string description = existing
            ? $"{TransitionName(transition!.Effect)} transition, {transition.Duration.TotalSeconds:0.##} seconds, at {FormatPreciseTime(at)} on {kind} lane"
            : $"Add transition at {FormatPreciseTime(at)} on {kind} lane";
        AutomationProperties.SetName(marker, description);
        ToolTipService.SetToolTip(marker, description);
        marker.Click += TransitionCut_Click;
        Canvas.SetLeft(marker, x - 8);
        Canvas.SetTop(marker, 20 + timelineLane * 28);
        TimelineCanvas.Children.Add(marker);
        if (existing) _clipButtons[transition!.Id] = marker;
    }

    private void TransitionCut_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: TimelineTransitionMarker marker } button)
            return;
        if (marker.TransitionId is { } id)
            SelectClip(TimelineKind.Transition, id);
        else
            ShowTransitionMenu(button, marker.Lane,
                marker.OutgoingId, marker.IncomingId);
    }

    private static string TransitionGlyph(VideoTransitionEffect effect) => effect switch
    {
        VideoTransitionEffect.SlideLeft => "\uE72B",
        VideoTransitionEffect.SlideRight => "\uE72A",
        VideoTransitionEffect.SlideUp => "\uE70E",
        VideoTransitionEffect.SlideDown => "\uE70D",
        VideoTransitionEffect.ZoomIn => "\uE8A3",
        VideoTransitionEffect.ZoomOut => "\uE71F",
        _ => "\uE7A1",
    };

    private static string TransitionName(VideoTransitionEffect effect) => effect switch
    {
        VideoTransitionEffect.DipToBlack => "Dip to black",
        VideoTransitionEffect.DipToWhite => "Dip to white",
        VideoTransitionEffect.SlideLeft => "Slide left",
        VideoTransitionEffect.SlideRight => "Slide right",
        VideoTransitionEffect.SlideUp => "Slide up",
        VideoTransitionEffect.SlideDown => "Slide down",
        VideoTransitionEffect.ZoomIn => "Zoom in",
        VideoTransitionEffect.ZoomOut => "Zoom out",
        _ => "Fade",
    };
}
