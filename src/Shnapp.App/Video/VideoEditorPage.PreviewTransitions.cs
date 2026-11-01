using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shnapp.Core;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private sealed record ImagePreviewTransition(VideoTransition Transition,
        VideoTransitionSpan Span, ImageOverlay Outgoing, ImageOverlay Incoming,
        Border Backdrop);

    private sealed record TextPreviewTransition(VideoTransition Transition,
        VideoTransitionSpan Span, TextOverlay Outgoing, TextOverlay Incoming,
        Border Backdrop);

    private ImagePreviewTransition[] _imagePreviewTransitions = [];
    private TextPreviewTransition[] _textPreviewTransitions = [];
    private ImagePreviewTransition[] _activeImagePreviewTransitions = [];
    private TextPreviewTransition[] _activeTextPreviewTransitions = [];

    private void RebuildImagePreviewTransitions(VideoProject project)
    {
        foreach (ImagePreviewTransition old in _imagePreviewTransitions)
            _previewImages.Children.Remove(old.Backdrop);
        var entries = new List<ImagePreviewTransition>();
        foreach (VideoTransition transition in project.Transitions)
        {
            if (transition.Lane != VideoTransitionLane.Image ||
                !VideoTransitionGeometry.TryGetSpan(project, transition, out var span))
                continue;
            ImageOverlay? outgoing = project.ImageOverlays.FirstOrDefault(item =>
                item.Id == transition.OutgoingId);
            ImageOverlay? incoming = project.ImageOverlays.FirstOrDefault(item =>
                item.Id == transition.IncomingId);
            if (outgoing is null || incoming is null) continue;
            Border backdrop = CreateTransitionBackdrop(transition.Effect);
            Canvas.SetZIndex(backdrop, Math.Min(outgoing.ZIndex, incoming.ZIndex) - 1);
            _previewImages.Children.Add(backdrop);
            entries.Add(new ImagePreviewTransition(transition, span, outgoing,
                incoming, backdrop));
        }
        _imagePreviewTransitions = [.. entries];
        _activeImagePreviewTransitions = [];
    }

    private void RebuildTextPreviewTransitions(VideoProject project)
    {
        foreach (TextPreviewTransition old in _textPreviewTransitions)
            _previewText.Children.Remove(old.Backdrop);
        var entries = new List<TextPreviewTransition>();
        foreach (VideoTransition transition in project.Transitions)
        {
            if (transition.Lane != VideoTransitionLane.Text ||
                !VideoTransitionGeometry.TryGetSpan(project, transition, out var span))
                continue;
            TextOverlay? outgoing = project.TextOverlays.FirstOrDefault(item =>
                item.Id == transition.OutgoingId);
            TextOverlay? incoming = project.TextOverlays.FirstOrDefault(item =>
                item.Id == transition.IncomingId);
            if (outgoing is null || incoming is null) continue;
            Border backdrop = CreateTransitionBackdrop(transition.Effect);
            Canvas.SetZIndex(backdrop, Math.Min(outgoing.ZIndex, incoming.ZIndex) - 1);
            _previewText.Children.Add(backdrop);
            entries.Add(new TextPreviewTransition(transition, span, outgoing,
                incoming, backdrop));
        }
        _textPreviewTransitions = [.. entries];
        _activeTextPreviewTransitions = [];
    }

    private static Border CreateTransitionBackdrop(VideoTransitionEffect effect) => new()
    {
        Background = new SolidColorBrush(effect == VideoTransitionEffect.DipToWhite
            ? Colors.White : Colors.Black),
        Visibility = Visibility.Collapsed,
        Opacity = 0,
        IsHitTestVisible = false,
    };

    private void IncludeImageTransitionIds(TimeSpan position, HashSet<Guid> active)
    {
        foreach (ImagePreviewTransition item in _imagePreviewTransitions)
        {
            if (position < item.Span.Start || position >= item.Span.End) continue;
            if (_editor?.Current is not { } project ||
                !CanvasLayerVisible(project, item.Outgoing.Id, item.Outgoing.LayerId))
                continue;
            active.Add(item.Outgoing.Id);
            active.Add(item.Incoming.Id);
        }
    }

    private void IncludeTextTransitionIds(TimeSpan position, HashSet<Guid> active)
    {
        foreach (TextPreviewTransition item in _textPreviewTransitions)
        {
            if (position < item.Span.Start || position >= item.Span.End) continue;
            if (_editor?.Current is not { } project ||
                !CanvasLayerVisible(project, item.Outgoing.Id, item.Outgoing.LayerId))
                continue;
            active.Add(item.Outgoing.Id);
            active.Add(item.Incoming.Id);
        }
    }

    private void SetActiveImageTransitions(TimeSpan position)
    {
        foreach (ImagePreviewTransition item in _activeImagePreviewTransitions)
            item.Backdrop.Visibility = Visibility.Collapsed;
        foreach (Border frame in _previewImageFrames.Values) ResetTransitionVisual(frame);
        _activeImagePreviewTransitions = _imagePreviewTransitions
            .Where(item => position >= item.Span.Start && position < item.Span.End &&
                _editor?.Current is { } project &&
                CanvasLayerVisible(project, item.Outgoing.Id, item.Outgoing.LayerId))
            .ToArray();
    }

    private void SetActiveTextTransitions(TimeSpan position)
    {
        foreach (TextPreviewTransition item in _activeTextPreviewTransitions)
            item.Backdrop.Visibility = Visibility.Collapsed;
        foreach (Image view in _previewTextViews.Values) ResetTransitionVisual(view);
        _activeTextPreviewTransitions = _textPreviewTransitions
            .Where(item => position >= item.Span.Start && position < item.Span.End &&
                _editor?.Current is { } project &&
                CanvasLayerVisible(project, item.Outgoing.Id, item.Outgoing.LayerId))
            .ToArray();
    }

    private void UpdateImageTransitionVisuals(TimeSpan position)
    {
        if (_activeImagePreviewTransitions.Length == 0 || _editor is null) return;
        foreach (ImagePreviewTransition item in _activeImagePreviewTransitions)
        {
            VideoTransitionSample sample = SampleTransition(item.Transition,
                item.Span, position);
            if (_previewImageFrames.TryGetValue(item.Outgoing.Id, out Border? outgoing))
                ApplyTransitionVisual(outgoing, sample.OutgoingOpacity,
                    sample.OutgoingOffsetX, sample.OutgoingOffsetY,
                    sample.OutgoingScale);
            if (_previewImageFrames.TryGetValue(item.Incoming.Id, out Border? incoming))
                ApplyTransitionVisual(incoming, sample.IncomingOpacity,
                    sample.IncomingOffsetX, sample.IncomingOffsetY,
                    sample.IncomingScale);
            SetTransitionBackdrop(item.Backdrop, sample, item.Span, position,
                item.Outgoing.Bounds, item.Incoming.Bounds);
        }
    }

    private void UpdateTextTransitionVisuals(TimeSpan position)
    {
        if (_activeTextPreviewTransitions.Length == 0 || _editor is null) return;
        foreach (TextPreviewTransition item in _activeTextPreviewTransitions)
        {
            VideoTransitionSample sample = SampleTransition(item.Transition,
                item.Span, position);
            if (_previewTextViews.TryGetValue(item.Outgoing.Id, out Image? outgoing))
                ApplyTransitionVisual(outgoing, sample.OutgoingOpacity,
                    sample.OutgoingOffsetX, sample.OutgoingOffsetY,
                    sample.OutgoingScale);
            if (_previewTextViews.TryGetValue(item.Incoming.Id, out Image? incoming))
                ApplyTransitionVisual(incoming, sample.IncomingOpacity,
                    sample.IncomingOffsetX, sample.IncomingOffsetY,
                    sample.IncomingScale);
            SetTransitionBackdrop(item.Backdrop, sample, item.Span, position,
                item.Outgoing.Bounds, item.Incoming.Bounds);
        }
    }

    private static VideoTransitionSample SampleTransition(VideoTransition transition,
        VideoTransitionSpan span, TimeSpan position) =>
        VideoTransitionGeometry.Sample(transition.Effect,
            (position - span.Start).TotalSeconds / transition.Duration.TotalSeconds);

    private static void ResetTransitionVisual(FrameworkElement view)
    {
        if (view.Opacity != 1) view.Opacity = 1;
        if (view.RenderTransform is not CompositeTransform transform) return;
        if (transform.ScaleX != 1) transform.ScaleX = 1;
        if (transform.ScaleY != 1) transform.ScaleY = 1;
        if (transform.TranslateX != 0) transform.TranslateX = 0;
        if (transform.TranslateY != 0) transform.TranslateY = 0;
    }

    private static void ApplyTransitionVisual(FrameworkElement view, double opacity,
        double offsetX, double offsetY, double scale)
    {
        if (Math.Abs(view.Opacity - opacity) > .001) view.Opacity = opacity;
        if (view.RenderTransform is not CompositeTransform transform)
            view.RenderTransform = transform = new CompositeTransform();
        double centerX = view.Width / 2;
        double centerY = view.Height / 2;
        double translateX = offsetX * view.Width;
        double translateY = offsetY * view.Height;
        if (Math.Abs(transform.CenterX - centerX) > .001) transform.CenterX = centerX;
        if (Math.Abs(transform.CenterY - centerY) > .001) transform.CenterY = centerY;
        if (Math.Abs(transform.ScaleX - scale) > .001) transform.ScaleX = scale;
        if (Math.Abs(transform.ScaleY - scale) > .001) transform.ScaleY = scale;
        if (Math.Abs(transform.TranslateX - translateX) > .001)
            transform.TranslateX = translateX;
        if (Math.Abs(transform.TranslateY - translateY) > .001)
            transform.TranslateY = translateY;
    }

    private void SetTransitionBackdrop(Border backdrop,
        VideoTransitionSample sample, VideoTransitionSpan span, TimeSpan position,
        VideoRect outgoing, VideoRect incoming)
    {
        if (sample.BackdropColor is null || _editor is null ||
            !double.IsFinite(VideoPlayer.Width) ||
            !double.IsFinite(VideoPlayer.Height) ||
            VideoPlayer.Width <= 0 || VideoPlayer.Height <= 0)
        {
            backdrop.Visibility = Visibility.Collapsed;
            return;
        }
        double progress = (position - span.Start).TotalSeconds /
            (span.End - span.Start).TotalSeconds;
        backdrop.Opacity = Math.Clamp(1 - Math.Abs(2 * progress - 1), 0, 1);
        backdrop.Visibility = Visibility.Visible;
        double scaleX = VideoPlayer.Width / _editor.Current.Width;
        double scaleY = VideoPlayer.Height / _editor.Current.Height;
        double left = Math.Min(outgoing.X, incoming.X);
        double top = Math.Min(outgoing.Y, incoming.Y);
        double right = Math.Max(outgoing.X + outgoing.Width,
            incoming.X + incoming.Width);
        double bottom = Math.Max(outgoing.Y + outgoing.Height,
            incoming.Y + incoming.Height);
        Canvas.SetLeft(backdrop, left * scaleX);
        Canvas.SetTop(backdrop, top * scaleY);
        backdrop.Width = (right - left) * scaleX;
        backdrop.Height = (bottom - top) * scaleY;
    }
}
