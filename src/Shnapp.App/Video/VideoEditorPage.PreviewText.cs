using Microsoft.UI;
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
    private readonly Border _previewFade = new()
    {
        Background = new SolidColorBrush(Colors.Black),
        Opacity = 0,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Canvas _previewText = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = true,
    };
    private readonly Dictionary<Guid, Image> _previewTextViews = [];
    private readonly Dictionary<Guid, TextOverlay> _visiblePreviewText = [];
    private readonly Dictionary<Guid, BitmapImage> _previewTextSources = [];
    private readonly Queue<Guid> _previewTextCacheOrder = new();
    private readonly HashSet<Guid> _previewTextLoads = [];
    private long _previewTextGeneration;
    private TimeSpan[] _previewTextBoundaries = [];
    private TimeSpan _previewTextIntervalStart = TimeSpan.MaxValue;
    private TimeSpan _previewTextIntervalEnd = TimeSpan.MinValue;

    private void InitializePreviewText()
    {
        PreviewHost.Children.Insert(PreviewHost.Children.IndexOf(_previewImages), _previewFade);
        PreviewHost.Children.Insert(PreviewHost.Children.IndexOf(_previewImages) + 1, _previewText);
    }

    private void RefreshPreviewText()
    {
        if (_editor is null) return;
        ++_previewTextGeneration;
        _previewText.Children.Clear();
        _previewTextViews.Clear();
        _visiblePreviewText.Clear();
        _previewTextSources.Clear();
        _previewTextCacheOrder.Clear();
        _previewTextLoads.Clear();
        RebuildTextPreviewTransitions(_editor.Current);
        _previewTextBoundaries = _editor.Current.TextOverlays
            .SelectMany(item => new[] { item.Start, item.End })
            .Concat(_textPreviewTransitions.SelectMany(item =>
                new[] { item.Span.Start, item.Span.End })).ToArray();
        Array.Sort(_previewTextBoundaries);
        _previewTextIntervalStart = TimeSpan.MaxValue;
        _previewTextIntervalEnd = TimeSpan.MinValue;
        UpdateVisiblePreviewText(_playhead);
        RefreshCanvasTextEditor();
        UpdateFadeOpacity(_playhead);
        SetPreviewImageTick(_player.PlaybackSession.PlaybackState ==
            global::Windows.Media.Playback.MediaPlaybackState.Playing);
    }

    private void ReleasePreviewText()
    {
        ++_previewTextGeneration;
        _previewText.Children.Clear();
        _previewTextViews.Clear();
        _visiblePreviewText.Clear();
        _previewTextSources.Clear();
        _previewTextCacheOrder.Clear();
        _previewTextLoads.Clear();
        _textPreviewTransitions = [];
        _activeTextPreviewTransitions = [];
        _previewTextBoundaries = [];
        TextCanvas.Visibility = Visibility.Collapsed;
    }

    private void UpdatePreviewTextBounds(TimeSpan? position = null)
    {
        if (_editor is null || !double.IsFinite(VideoPlayer.Width) ||
            !double.IsFinite(VideoPlayer.Height) || VideoPlayer.Width <= 0 || VideoPlayer.Height <= 0)
            return;
        VideoProject project = _editor.Current;
        double scaleX = VideoPlayer.Width / project.Width;
        double scaleY = VideoPlayer.Height / project.Height;
        _previewFade.Width = VideoPlayer.Width;
        _previewFade.Height = VideoPlayer.Height;
        _previewText.Width = VideoPlayer.Width;
        _previewText.Height = VideoPlayer.Height;
        _previewText.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, VideoPlayer.Width, VideoPlayer.Height),
        };
        foreach (TextOverlay overlay in _visiblePreviewText.Values)
        {
            Image view = _previewTextViews[overlay.Id];
            Canvas.SetLeft(view, overlay.Bounds.X * scaleX);
            Canvas.SetTop(view, overlay.Bounds.Y * scaleY);
            view.Width = overlay.Bounds.Width * scaleX;
            view.Height = overlay.Bounds.Height * scaleY;
        }
        UpdateTextTransitionVisuals(position ?? _playhead);
        UpdateCanvasTextBounds();
    }

    private void UpdateVisiblePreviewText(TimeSpan position)
    {
        if (_closed || _editor is null)
            return;
        if (position >= _previewTextIntervalStart && position < _previewTextIntervalEnd)
        {
            UpdateTextTransitionVisuals(position);
            return;
        }
        int nextBoundary = Array.BinarySearch(_previewTextBoundaries, position);
        if (nextBoundary < 0) nextBoundary = ~nextBoundary;
        else while (nextBoundary < _previewTextBoundaries.Length &&
            _previewTextBoundaries[nextBoundary] <= position) nextBoundary++;
        _previewTextIntervalStart = nextBoundary > 0
            ? _previewTextBoundaries[nextBoundary - 1] : TimeSpan.MinValue;
        _previewTextIntervalEnd = nextBoundary < _previewTextBoundaries.Length
            ? _previewTextBoundaries[nextBoundary] : TimeSpan.MaxValue;

        HashSet<Guid> activeIds = _editor.Current.TextOverlays
            .Where(item => item.Start <= position && position < item.End &&
                CanvasLayerVisible(_editor.Current, item.Id, item.LayerId))
            .Select(item => item.Id).ToHashSet();
        IncludeTextTransitionIds(position, activeIds);
        TextOverlay[] active = _editor.Current.TextOverlays
            .Where(item => activeIds.Contains(item.Id)).ToArray();
        foreach (Guid id in _previewTextViews.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _previewText.Children.Remove(_previewTextViews[id]);
            _previewTextViews.Remove(id);
        }
        _visiblePreviewText.Clear();
        foreach (TextOverlay overlay in active)
        {
            if (!_previewTextViews.TryGetValue(overlay.Id, out Image? view))
            {
                view = new Image
                {
                    IsHitTestVisible = true,
                    Stretch = Stretch.Fill,
                    Tag = overlay.Id,
                };
                view.Tapped += PreviewText_Tapped;
                _previewTextViews.Add(overlay.Id, view);
                _previewText.Children.Add(view);
            }
            Canvas.SetZIndex(view, overlay.ZIndex);
            _visiblePreviewText.Add(overlay.Id, overlay);
            if (_previewTextSources.TryGetValue(overlay.Id, out BitmapImage? source))
                view.Source = source;
            else if (!(_canvasTextId == overlay.Id &&
                    TextCanvas.Visibility == Visibility.Visible) &&
                _previewTextLoads.Add(overlay.Id))
                _ = LoadPreviewTextAsync(overlay, _previewTextGeneration);
        }
        SetActiveTextTransitions(position);
        UpdatePreviewTextBounds(position);
        RefreshCanvasTextEditor();
    }

    private async Task LoadPreviewTextAsync(TextOverlay overlay, long generation)
    {
        try
        {
            VideoProject? project = _editor?.Current;
            VideoProjectStore? store = _store;
            if (project is null || store is null) return;
            string path = await VideoCompositionService.EnsureTextOverlayPngAsync(
                project, overlay, store);
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var source = new BitmapImage { DecodePixelWidth = 1920 };
            await source.SetSourceAsync(stream);
            if (_closed || generation != _previewTextGeneration) return;
            _previewTextSources[overlay.Id] = source;
            _previewTextCacheOrder.Enqueue(overlay.Id);
            if (_previewTextViews.TryGetValue(overlay.Id, out Image? view))
                view.Source = source;
            TrimPreviewTextCache();
        }
        catch (Exception exception)
        {
            if (!_closed && generation == _previewTextGeneration)
                StatusText.Text = $"Text preview unavailable: {exception.Message}";
        }
        finally
        {
            if (generation == _previewTextGeneration)
                _previewTextLoads.Remove(overlay.Id);
        }
    }

    private void TrimPreviewTextCache()
    {
        int retainedActive = 0;
        while (_previewTextSources.Count > 12 &&
            retainedActive < _previewTextCacheOrder.Count)
        {
            Guid oldest = _previewTextCacheOrder.Dequeue();
            if (_visiblePreviewText.ContainsKey(oldest))
            {
                _previewTextCacheOrder.Enqueue(oldest);
                retainedActive++;
                continue;
            }
            _previewTextSources.Remove(oldest);
            retainedActive = 0;
        }
    }

    private void UpdateFadeOpacity(TimeSpan position)
    {
        if (_closed || _editor is null) return;
        double opacity = VideoCompositionService.GetFadeOpacity(_editor.Current, position);
        if (Math.Abs(_previewFade.Opacity - opacity) >= .001)
            _previewFade.Opacity = opacity;
    }
}
