using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shnapp.Core;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    // A paused MediaStreamSource seek can stay black. Decode only the active
    // source frames here; MediaPlayer still owns playback and native export.
    private readonly Grid _pausedFrameSurface = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Colors.Black),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };
    private readonly Image _pausedVideoFrame = new()
    {
        Stretch = Stretch.Fill,
        IsHitTestVisible = false,
    };
    private readonly Dictionary<Guid, (Border Frame, Image Image)> _pausedOverlayViews = [];
    private readonly Dictionary<Guid, Border> _pausedBackdropViews = [];
    private PausedSource[] _pausedActiveSources = [];
    private PausedBackdrop[] _pausedActiveBackdrops = [];
    private readonly DispatcherTimer _pausedFrameSchedule = new()
    {
        Interval = TimeSpan.FromMilliseconds(16),
    };
    private readonly SemaphoreSlim _pausedFrameGate = new(1, 1);
    private bool _pausedDecodePending;
    private bool _pausedDecodeInProgress;
    // Twelve decoded 1080p frames can occupy about 95 MiB before native
    // decoder surfaces are counted. Keep the useful nearby frames, but cap
    // retained pixels to roughly six 1080p frames.
    private readonly ByteBudgetCache<(Guid AssetId, long Frame), BitmapImage>
        _pausedFrameCache = new(12, 48L * 1024 * 1024);
    private readonly Dictionary<Guid, MediaComposition> _pausedSourceCompositions = [];
    private readonly Queue<Guid> _pausedSourceOrder = new();
    private MediaComposition? _pausedPreviewComposition;
    private long _pausedCompositionRevision;
    private long _pausedRequestRevision;
    private long _pausedDisplayedFrame = long.MinValue;
    private TimeSpan _pausedRequestedPosition;

    private readonly record struct PausedSource(Guid ItemId, Guid AssetId,
        TimeSpan SourceAt, VideoRect Bounds, VideoRect Crop, int ZIndex,
        double Opacity = 1,
        VideoOverlayShape Shape = VideoOverlayShape.Rectangle);
    private readonly record struct PausedBackdrop(Guid Id, VideoRect Bounds,
        bool White, double Opacity, int ZIndex);

    private void InitializePausedFrame()
    {
        _pausedFrameSurface.Children.Add(_pausedVideoFrame);
        PreviewHost.Children.Insert(PreviewHost.Children.IndexOf(VideoPlayer) + 1,
            _pausedFrameSurface);
        _pausedFrameSchedule.Tick += PausedFrameSchedule_Tick;
    }

    private void ReleasePausedFrame()
    {
        InvalidatePausedFrame(clearPresentedFrame: true);
        _pausedFrameSchedule.Tick -= PausedFrameSchedule_Tick;
    }

    private void InvalidatePausedFrame(bool clearPresentedFrame = false)
    {
        ++_pausedRequestRevision;
        _pausedFrameSchedule.Stop();
        _pausedDecodePending = false;
        _pausedPreviewComposition = null;
        _pausedCompositionRevision = 0;
        _pausedDisplayedFrame = long.MinValue;
        if (clearPresentedFrame)
        {
            _pausedFrameCache.Clear();
            _pausedSourceCompositions.Clear();
            _pausedSourceOrder.Clear();
            _pausedVideoFrame.Source = null;
            foreach (var view in _pausedOverlayViews.Values)
                _previewImages.Children.Remove(view.Frame);
            foreach (Border backdrop in _pausedBackdropViews.Values)
                _previewImages.Children.Remove(backdrop);
            _pausedOverlayViews.Clear();
            _pausedBackdropViews.Clear();
            _pausedActiveSources = [];
            _pausedActiveBackdrops = [];
        }
        else if (_editor is { } editor)
        {
            // Keep decoded pixels visible while the native composition rebuilds.
            // Reconcile removed and reordered layers against the new model.
            _pausedActiveSources = ActivePausedOverlays(editor.Current, _playhead);
            _pausedActiveBackdrops = ActivePausedBackdrops(editor.Current, _playhead);
            SyncPausedOverlayViews(_pausedActiveSources);
            SyncPausedBackdrops(_pausedActiveBackdrops);
        }
        _pausedFrameSurface.Visibility = _closed ||
            _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetPausedPreviewComposition(MediaComposition composition,
        long revision)
    {
        if (_closed || revision != _previewRevision) return;
        _pausedPreviewComposition = composition;
        _pausedCompositionRevision = revision;
        ++_pausedRequestRevision;
        _pausedFrameSchedule.Stop();
        _pausedDecodePending = false;
        _pausedDisplayedFrame = long.MinValue;
        UpdatePausedFrameBounds();
        UpdatePausedFrame(_playhead);
    }

    private void UpdatePausedFrameBounds()
    {
        if (VideoPlayer.Width <= 0 || VideoPlayer.Height <= 0) return;
        _pausedFrameSurface.Width = VideoPlayer.Width;
        _pausedFrameSurface.Height = VideoPlayer.Height;
        _pausedVideoFrame.Width = VideoPlayer.Width;
        _pausedVideoFrame.Height = VideoPlayer.Height;
        _pausedFrameSurface.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, VideoPlayer.Width, VideoPlayer.Height),
        };
        foreach (PausedSource source in _pausedActiveSources)
            if (_pausedOverlayViews.TryGetValue(source.ItemId, out var view))
                LayoutPausedOverlay(source.Bounds, source.Crop,
                    view.Frame, view.Image, source.Shape);
        foreach (PausedBackdrop backdrop in _pausedActiveBackdrops)
            if (_pausedBackdropViews.TryGetValue(backdrop.Id, out Border? view))
                LayoutPausedBackdrop(backdrop, view);
    }

    private void UpdatePausedFrame(TimeSpan position)
    {
        if (_closed || _editor is null ||
            _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _pausedFrameSchedule.Stop();
            _pausedDecodePending = false;
            if (_pausedFrameSurface.Visibility != Visibility.Collapsed)
            {
                _pausedFrameSurface.Visibility = Visibility.Collapsed;
                _pausedDisplayedFrame = long.MinValue;
                ++_pausedRequestRevision;
            }
            return;
        }
        if (_pausedPreviewComposition is null)
        {
            // A native composition is rebuilding. Keep the stale player frame
            // covered until its new source and precise paused frames are ready.
            _pausedFrameSurface.Visibility = Visibility.Visible;
            return;
        }

        VideoProject project = _editor.Current;
        long frame = PausedFrameKey(position, project.FrameRate);
        if (_pausedFrameSurface.Visibility == Visibility.Visible &&
            _pausedDisplayedFrame == frame) return;
        _pausedDisplayedFrame = frame;
        _pausedRequestedPosition = position;
        _pausedFrameSurface.Visibility = Visibility.Visible;
        ++_pausedRequestRevision;
        bool needsDecode = false;
        if (PrimarySourceAt(project, position) is { } primary)
        {
            var key = (primary.AssetId, PausedFrameKey(primary.SourceAt,
                project.FrameRate));
            if (_pausedFrameCache.TryGetValue(key, out BitmapImage? bitmap))
                _pausedVideoFrame.Source = bitmap;
            else needsDecode = true;
        }
        else _pausedVideoFrame.Source = null;

        PausedSource[] overlays = ActivePausedOverlays(project, position);
        _pausedActiveSources = overlays;
        _pausedActiveBackdrops = ActivePausedBackdrops(project, position);
        SyncPausedOverlayViews(overlays);
        SyncPausedBackdrops(_pausedActiveBackdrops);
        foreach (PausedSource overlay in overlays)
        {
            var key = (overlay.AssetId, PausedFrameKey(overlay.SourceAt,
                project.FrameRate));
            if (_pausedFrameCache.TryGetValue(key, out BitmapImage? bitmap))
                _pausedOverlayViews[overlay.ItemId].Image.Source = bitmap;
            else needsDecode = true;
        }
        _pausedDecodePending = needsDecode;
        if (needsDecode)
        {
            // Start once per burst of pointer movement. Resetting the timer on
            // every seek starves frame decoding until the drag stops.
            if (!_pausedDecodeInProgress && !_pausedFrameSchedule.IsEnabled)
                _pausedFrameSchedule.Start();
        }
        else _pausedFrameSchedule.Stop();
    }

    private static PausedSource? PrimarySourceAt(VideoProject project,
        TimeSpan position)
    {
        if (project.HiddenLayerIds.Contains(Guid.Empty)) return null;
        if (TryGetActiveTransition(project, VideoTransitionLane.Video,
            position, out VideoTransition? transition, out _) &&
            transition is { Effect: not (VideoTransitionEffect.DipToBlack or
                VideoTransitionEffect.DipToWhite) })
            return null;
        VideoSegment? segment = project.VideoSegments.FirstOrDefault(item =>
            item.Start <= position && position < item.End);
        if (segment is null) return null;
        return new PausedSource(segment.Id, segment.AssetId,
            segment.SourceIn + position - segment.Start,
            new VideoRect(0, 0, project.Width, project.Height),
            new VideoRect(0, 0, 1, 1), 0);
    }

    private void SyncPausedOverlayViews(PausedSource[] overlays)
    {
        HashSet<Guid> active = overlays.Select(item => item.ItemId).ToHashSet();
        foreach (Guid id in _pausedOverlayViews.Keys.Where(item =>
            !active.Contains(item)).ToArray())
        {
            _previewImages.Children.Remove(_pausedOverlayViews[id].Frame);
            _pausedOverlayViews.Remove(id);
        }
        foreach (PausedSource overlay in overlays)
        {
            if (!_pausedOverlayViews.TryGetValue(overlay.ItemId, out var view))
            {
                var image = new Image
                {
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                };
                var imageCanvas = new Canvas { IsHitTestVisible = false };
                imageCanvas.Children.Add(image);
                var border = new Border
                {
                    Child = imageCanvas,
                    IsHitTestVisible = false,
                };
                view = (border, image);
                _pausedOverlayViews.Add(overlay.ItemId, view);
                _previewImages.Children.Add(border);
            }
            Canvas.SetZIndex(view.Frame, overlay.ZIndex);
            view.Frame.Opacity = overlay.Opacity;
            LayoutPausedOverlay(overlay.Bounds, overlay.Crop,
                view.Frame, view.Image, overlay.Shape);
        }
    }

    private void SyncPausedBackdrops(PausedBackdrop[] backdrops)
    {
        HashSet<Guid> active = backdrops.Select(item => item.Id).ToHashSet();
        foreach (Guid id in _pausedBackdropViews.Keys.Where(item =>
            !active.Contains(item)).ToArray())
        {
            _previewImages.Children.Remove(_pausedBackdropViews[id]);
            _pausedBackdropViews.Remove(id);
        }
        foreach (PausedBackdrop backdrop in backdrops)
        {
            if (!_pausedBackdropViews.TryGetValue(backdrop.Id, out Border? view))
            {
                view = new Border { IsHitTestVisible = false };
                _pausedBackdropViews.Add(backdrop.Id, view);
                _previewImages.Children.Add(view);
            }
            view.Background = new SolidColorBrush(backdrop.White
                ? Colors.White : Colors.Black);
            view.Opacity = backdrop.Opacity;
            Canvas.SetZIndex(view, backdrop.ZIndex);
            LayoutPausedBackdrop(backdrop, view);
        }
    }

    private void LayoutPausedBackdrop(PausedBackdrop backdrop, Border view)
    {
        if (_editor is null || VideoPlayer.Width <= 0 || VideoPlayer.Height <= 0)
            return;
        double sx = VideoPlayer.Width / _editor.Current.Width;
        double sy = VideoPlayer.Height / _editor.Current.Height;
        Canvas.SetLeft(view, backdrop.Bounds.X * sx);
        Canvas.SetTop(view, backdrop.Bounds.Y * sy);
        view.Width = backdrop.Bounds.Width * sx;
        view.Height = backdrop.Bounds.Height * sy;
    }

    private void LayoutPausedOverlay(VideoRect bounds, VideoRect crop,
        Border frame, Image image, VideoOverlayShape shape)
    {
        if (_editor is null || VideoPlayer.Width <= 0 || VideoPlayer.Height <= 0)
            return;
        double sx = VideoPlayer.Width / _editor.Current.Width;
        double sy = VideoPlayer.Height / _editor.Current.Height;
        double width = bounds.Width * sx;
        double height = bounds.Height * sy;
        Canvas.SetLeft(frame, bounds.X * sx);
        Canvas.SetTop(frame, bounds.Y * sy);
        frame.Width = width;
        frame.Height = height;
        ApplyVideoShapeClip(frame, shape, width, height);
        image.Width = width / crop.Width;
        image.Height = height / crop.Height;
        Canvas.SetLeft(image, -crop.X * image.Width);
        Canvas.SetTop(image, -crop.Y * image.Height);
    }

    private static long PausedFrameKey(TimeSpan position, int frameRate) =>
        Math.Max(0, (long)Math.Round(position.TotalSeconds *
            Math.Clamp(frameRate, 1, 240)));

    private async void PausedFrameSchedule_Tick(object? sender, object args)
    {
        _pausedFrameSchedule.Stop();
        if (!_pausedDecodePending || _pausedDecodeInProgress) return;
        _pausedDecodePending = false;
        _pausedDecodeInProgress = true;
        try
        {
            await RenderPausedFrameAsync(_pausedRequestedPosition,
                _pausedRequestRevision, _pausedCompositionRevision);
        }
        finally
        {
            _pausedDecodeInProgress = false;
            // One decode at a time. If a seek arrived during it, process only
            // the newest requested position after the current decode exits.
            if (_pausedDecodePending && !_closed &&
                _pausedPreviewComposition is not null &&
                _player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                _pausedFrameSchedule.Start();
        }
    }

    private async Task RenderPausedFrameAsync(TimeSpan position,
        long requestRevision, long compositionRevision)
    {
        if (_pausedPreviewComposition is null || _editor is null || _store is null)
            return;
        await _pausedFrameGate.WaitAsync();
        try
        {
            if (!PausedRequestCurrent(requestRevision, compositionRevision) ||
                _editor is not { } editor || _store is not { } store) return;
            VideoProject project = editor.Current;
            (int width, int height) = GetPreviewSize(project);
            PausedSource? primary = PrimarySourceAt(project, position);
            PausedSource[] overlays = ActivePausedOverlays(project, position);
            IEnumerable<PausedSource> sources = primary is { } baseSource
                ? new[] { baseSource }.Concat(overlays) : overlays;
            foreach (PausedSource source in sources)
            {
                if (!PausedRequestCurrent(requestRevision, compositionRevision)) return;
                var key = (source.AssetId, PausedFrameKey(source.SourceAt,
                    project.FrameRate));
                if (!_pausedFrameCache.TryGetValue(key, out BitmapImage? bitmap))
                {
                    VideoAsset? asset = project.Assets.FirstOrDefault(item =>
                        item.Id == source.AssetId);
                    if (asset is null) continue;
                    if (!_pausedSourceCompositions.TryGetValue(source.AssetId,
                        out MediaComposition? composition))
                    {
                        StorageFile file = await StorageFile.GetFileFromPathAsync(
                            store.ResolveAssetPath(project, asset));
                        MediaClip sourceClip = await MediaClip.CreateFromFileAsync(file);
                        composition = new MediaComposition();
                        composition.Clips.Add(sourceClip);
                        _pausedSourceCompositions[source.AssetId] = composition;
                        _pausedSourceOrder.Enqueue(source.AssetId);
                        while (_pausedSourceCompositions.Count > 8)
                            _pausedSourceCompositions.Remove(_pausedSourceOrder.Dequeue());
                    }
                    MediaClip clip = composition.Clips[0];
                    TimeSpan sourceAt = TimeSpan.FromTicks(Math.Clamp(
                        source.SourceAt.Ticks, 0,
                        Math.Max(0, clip.OriginalDuration.Ticks - 1)));
                    VideoEncodingProperties format =
                        clip.GetVideoEncodingProperties();
                    double sourceWidth = Math.Max(1, format.Width);
                    double sourceHeight = Math.Max(1, format.Height);
                    double scale = Math.Min(width / sourceWidth,
                        height / sourceHeight);
                    int frameWidth = Math.Max(2,
                        (int)Math.Floor(sourceWidth * scale / 2) * 2);
                    int frameHeight = Math.Max(2,
                        (int)Math.Floor(sourceHeight * scale / 2) * 2);
                    using var stream = await composition.GetThumbnailAsync(sourceAt,
                        frameWidth, frameHeight,
                        VideoFramePrecision.NearestFrame);
                    bitmap = new BitmapImage { DecodePixelWidth = frameWidth };
                    await bitmap.SetSourceAsync(stream);
                    if (!PausedRequestCurrent(requestRevision, compositionRevision))
                        return;
                    _pausedFrameCache.Add(key, bitmap,
                        (long)frameWidth * frameHeight * 4);
                }
                if (primary is { } selected && selected.ItemId == source.ItemId)
                    _pausedVideoFrame.Source = bitmap;
                else if (_pausedOverlayViews.TryGetValue(source.ItemId, out var view))
                    view.Image.Source = bitmap;
            }
        }
        catch (Exception exception)
        {
            if (!_closed && requestRevision == _pausedRequestRevision)
                StatusText.Text = $"Paused frame unavailable: {exception.Message}";
        }
        finally
        {
            _pausedFrameGate.Release();
        }
    }

    private bool PausedRequestCurrent(long requestRevision,
        long compositionRevision) => !_closed &&
        requestRevision == _pausedRequestRevision &&
        compositionRevision == _pausedCompositionRevision &&
        _player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing;
}
