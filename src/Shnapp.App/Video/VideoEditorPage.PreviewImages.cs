using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shnapp.Core;
using Windows.Foundation;
using Windows.Storage;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private readonly Canvas _previewImages = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = true,
    };
    private readonly Dictionary<Guid, Image> _previewImageViews = [];
    private readonly Dictionary<Guid, Border> _previewImageFrames = [];
    private readonly Dictionary<Guid, ImageOverlay> _visiblePreviewImageDefinitions = [];
    private readonly Dictionary<Guid, Guid> _previewImageViewAssets = [];
    private readonly Dictionary<Guid, BitmapImage> _previewImageSources = [];
    private readonly Queue<Guid> _previewImageCacheOrder = new();
    private readonly HashSet<Guid> _previewImageLoads = [];
    private readonly HashSet<Guid> _previewImageFailures = [];
    private long _previewImageGeneration;
    private bool _previewImageTickAttached;
    private TimeSpan[] _previewImageBoundaries = [];
    private TimeSpan _previewImageIntervalStart = TimeSpan.MaxValue;
    private TimeSpan _previewImageIntervalEnd = TimeSpan.MinValue;

    private void InitializePreviewImages()
    {
        PreviewHost.Children.Insert(PreviewHost.Children.IndexOf(VideoPlayer) + 1, _previewImages);
    }

    private void ResetPreviewImages()
    {
        ++_previewImageGeneration;
        SetPreviewImageTick(false);
        _previewImages.Children.Clear();
        _previewImageViews.Clear();
        _previewImageFrames.Clear();
        _visiblePreviewImageDefinitions.Clear();
        _previewImageViewAssets.Clear();
        _previewImageSources.Clear();
        _previewImageCacheOrder.Clear();
        _previewImageLoads.Clear();
        _previewImageFailures.Clear();
        _imagePreviewTransitions = [];
        _activeImagePreviewTransitions = [];
        RefreshPreviewImages();
    }

    private void ReleasePreviewImages()
    {
        ++_previewImageGeneration;
        SetPreviewImageTick(false);
        _previewImages.Children.Clear();
        _previewImageViews.Clear();
        _previewImageFrames.Clear();
        _visiblePreviewImageDefinitions.Clear();
        _previewImageViewAssets.Clear();
        _previewImageSources.Clear();
        _previewImageCacheOrder.Clear();
        _previewImageLoads.Clear();
        _previewImageFailures.Clear();
        _imagePreviewTransitions = [];
        _activeImagePreviewTransitions = [];
    }

    private void RefreshPreviewImages()
    {
        if (_editor is null) return;
        VideoProject project = _editor.Current;
        RebuildImagePreviewTransitions(project);
        _previewImageBoundaries = project.ImageOverlays
            .SelectMany(item => new[] { item.Start, item.End })
            .Concat(_imagePreviewTransitions.SelectMany(item =>
                new[] { item.Span.Start, item.Span.End })).ToArray();
        Array.Sort(_previewImageBoundaries);
        _previewImageIntervalStart = TimeSpan.MaxValue;
        _previewImageIntervalEnd = TimeSpan.MinValue;
        UpdateVisiblePreviewImages(_playhead);
        SetPreviewImageTick(_player.PlaybackSession.PlaybackState ==
            global::Windows.Media.Playback.MediaPlaybackState.Playing);
    }

    private void UpdatePreviewImageBounds(TimeSpan? position = null)
    {
        if (_editor is null || !double.IsFinite(VideoPlayer.Width) ||
            !double.IsFinite(VideoPlayer.Height) || VideoPlayer.Width <= 0 || VideoPlayer.Height <= 0)
            return;

        _previewImages.Width = VideoPlayer.Width;
        _previewImages.Height = VideoPlayer.Height;
        _previewImages.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, VideoPlayer.Width, VideoPlayer.Height),
        };
        foreach (ImageOverlay overlay in _visiblePreviewImageDefinitions.Values)
            LayoutPreviewImage(overlay, overlay.Bounds,
                overlay.SourceCrop ?? new VideoRect(0, 0, 1, 1));
        UpdateImageTransitionVisuals(position ?? _playhead);
        RefreshCanvasMediaSelection();
    }

    private void LayoutPreviewImage(ImageOverlay overlay, VideoRect bounds,
        VideoRect crop)
    {
        if (_editor is null || VideoPlayer.Width <= 0 || VideoPlayer.Height <= 0 ||
            !_previewImageFrames.TryGetValue(overlay.Id, out Border? frame) ||
            !_previewImageViews.TryGetValue(overlay.Id, out Image? view)) return;
        double scaleX = VideoPlayer.Width / _editor.Current.Width;
        double scaleY = VideoPlayer.Height / _editor.Current.Height;
        double width = bounds.Width * scaleX;
        double height = bounds.Height * scaleY;
        Canvas.SetLeft(frame, bounds.X * scaleX);
        Canvas.SetTop(frame, bounds.Y * scaleY);
        frame.Width = width;
        frame.Height = height;
        frame.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
        view.Width = width / crop.Width;
        view.Height = height / crop.Height;
        Canvas.SetLeft(view, -crop.X * view.Width);
        Canvas.SetTop(view, -crop.Y * view.Height);
    }

    private void UpdateVisiblePreviewImages(TimeSpan position)
    {
        if (_closed || _editor is null)
            return;
        if (position >= _previewImageIntervalStart && position < _previewImageIntervalEnd)
        {
            UpdateImageTransitionVisuals(position);
            return;
        }

        int firstAfter = 0;
        int upper = _previewImageBoundaries.Length;
        while (firstAfter < upper)
        {
            int middle = firstAfter + (upper - firstAfter) / 2;
            if (_previewImageBoundaries[middle] <= position) firstAfter = middle + 1;
            else upper = middle;
        }
        _previewImageIntervalStart = firstAfter > 0
            ? _previewImageBoundaries[firstAfter - 1] : TimeSpan.MinValue;
        _previewImageIntervalEnd = firstAfter < _previewImageBoundaries.Length
            ? _previewImageBoundaries[firstAfter] : TimeSpan.MaxValue;

        HashSet<Guid> activeIds = _editor.Current.ImageOverlays
            .Where(item => item.Start <= position && position < item.End &&
                CanvasLayerVisible(_editor.Current, item.Id, item.LayerId))
            .Select(item => item.Id).ToHashSet();
        IncludeImageTransitionIds(position, activeIds);
        ImageOverlay[] active = _editor.Current.ImageOverlays
            .Where(item => activeIds.Contains(item.Id)).ToArray();
        foreach (Guid removedId in _previewImageViews.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _previewImages.Children.Remove(_previewImageFrames[removedId]);
            _previewImageViews.Remove(removedId);
            _previewImageFrames.Remove(removedId);
            _previewImageViewAssets.Remove(removedId);
        }
        _visiblePreviewImageDefinitions.Clear();

        foreach (ImageOverlay overlay in active)
        {
            if (!_previewImageViews.TryGetValue(overlay.Id, out Image? view))
            {
                view = new Image
                {
                    IsHitTestVisible = false,
                    Stretch = Stretch.Fill,
                };
                var imageCanvas = new Canvas { IsHitTestVisible = false };
                imageCanvas.Children.Add(view);
                var frame = new Border
                {
                    Background = new SolidColorBrush(global::Microsoft.UI.Colors.Transparent),
                    Child = imageCanvas,
                    Tag = (TimelineKind.Image, overlay.Id),
                };
                frame.PointerPressed += MediaSurface_PointerPressed;
                frame.PointerMoved += MediaSurface_PointerMoved;
                frame.PointerReleased += MediaSurface_PointerReleased;
                frame.PointerCaptureLost += MediaSurface_PointerCaptureLost;
                _previewImageViews.Add(overlay.Id, view);
                _previewImageFrames.Add(overlay.Id, frame);
                _previewImages.Children.Add(frame);
            }
            if (!_previewImageViewAssets.TryGetValue(overlay.Id, out Guid previousAsset) ||
                previousAsset != overlay.AssetId)
            {
                view.Source = null;
                _previewImageViewAssets[overlay.Id] = overlay.AssetId;
            }
            Canvas.SetZIndex(_previewImageFrames[overlay.Id], overlay.ZIndex);
            _visiblePreviewImageDefinitions.Add(overlay.Id, overlay);
            if (_previewImageSources.TryGetValue(overlay.AssetId, out BitmapImage? source))
                view.Source = source;
            if (view.Source is null)
            {
                if (!_previewImageFailures.Contains(overlay.AssetId) &&
                    _previewImageLoads.Add(overlay.AssetId))
                    _ = LoadPreviewImageAsync(overlay.AssetId, _previewImageGeneration);
            }
        }
        SetActiveImageTransitions(position);
        UpdatePreviewImageBounds(position);
        TrimPreviewImageCache();
    }

    private void TrimPreviewImageCache()
    {
        const int maximumCachedImages = 12;
        int retainedActive = 0;
        while (_previewImageSources.Count > maximumCachedImages &&
            retainedActive < _previewImageCacheOrder.Count)
        {
            Guid oldest = _previewImageCacheOrder.Dequeue();
            if (_visiblePreviewImageDefinitions.Values.Any(item => item.AssetId == oldest))
            {
                _previewImageCacheOrder.Enqueue(oldest);
                retainedActive++;
                continue;
            }
            _previewImageSources.Remove(oldest);
            retainedActive = 0;
        }
    }

    private async Task LoadPreviewImageAsync(Guid assetId, long generation)
    {
        try
        {
            VideoProject? project = _editor?.Current;
            VideoProjectStore? store = _store;
            VideoAsset? asset = project?.Assets.FirstOrDefault(item => item.Id == assetId);
            if (project is null || store is null || asset is null) return;

            StorageFile file = await StorageFile.GetFileFromPathAsync(
                store.ResolveAssetPath(project, asset));
            using var stream = await file.OpenReadAsync();
            // Preview frames are capped at 1920 pixels; avoid decoding full-resolution photos.
            var source = new BitmapImage { DecodePixelWidth = 1920 };
            await source.SetSourceAsync(stream);
            if (_closed || generation != _previewImageGeneration || _editor is null) return;
            _previewImageSources[assetId] = source;
            _previewImageCacheOrder.Enqueue(assetId);
            foreach (ImageOverlay overlay in _visiblePreviewImageDefinitions.Values.Where(item => item.AssetId == assetId))
                if (_previewImageViews.TryGetValue(overlay.Id, out Image? view)) view.Source = source;
            TrimPreviewImageCache();
        }
        catch (Exception exception)
        {
            if (!_closed && generation == _previewImageGeneration)
            {
                _previewImageFailures.Add(assetId);
                StatusText.Text = $"Image preview unavailable: {exception.Message}";
            }
        }
        finally
        {
            if (generation == _previewImageGeneration) _previewImageLoads.Remove(assetId);
        }
    }

    private void SetPreviewImageTick(bool playing)
    {
        VideoProject? project = _editor?.Current;
        if (project is null ||
            !project.ImageOverlays.Any(item =>
                CanvasLayerVisible(project, item.Id, item.LayerId)) &&
            !project.TextOverlays.Any(item =>
                CanvasLayerVisible(project, item.Id, item.LayerId)) &&
            (project.HiddenLayerIds.Contains(Guid.Empty) ||
                !project.VideoSegments.Any(item =>
                    item.FadeIn > TimeSpan.Zero || item.FadeOut > TimeSpan.Zero)))
            playing = false;
        if (playing == _previewImageTickAttached) return;
        if (playing)
            CompositionTarget.Rendering += PreviewImages_Rendering;
        else
            CompositionTarget.Rendering -= PreviewImages_Rendering;
        _previewImageTickAttached = playing;
    }

    private void PreviewImages_Rendering(object sender, object args)
    {
        if (_closed || _editor is null) return;
        TimeSpan position = _player.PlaybackSession.Position;
        UpdateVisiblePreviewImages(position);
        UpdateVisiblePreviewText(position);
        UpdateFadeOpacity(position);
    }
}
