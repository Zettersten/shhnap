using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Shnapp.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private const int MaximumTimelineThumbnails = 40;
    private Guid _timelineThumbnailProjectId;
    private readonly Dictionary<Guid, BitmapImage> _timelineThumbnailCache = [];
    private readonly Dictionary<Guid, List<Image>> _timelineThumbnailViews = [];
    private readonly HashSet<Guid> _timelineThumbnailLoads = [];
    private readonly HashSet<Guid> _timelineThumbnailFailures = [];
    private readonly Queue<Guid> _timelineThumbnailOrder = new();
    private readonly SemaphoreSlim _timelineThumbnailGate = new(3, 3);
    private CancellationTokenSource? _timelineThumbnailCancellation = new();

    private void ClearTimelineThumbnailViews()
    {
        _timelineThumbnailViews.Clear();
        Guid projectId = _editor?.Current.Id ?? Guid.Empty;
        if (projectId == _timelineThumbnailProjectId) return;
        _timelineThumbnailCancellation?.Cancel();
        _timelineThumbnailCancellation?.Dispose();
        _timelineThumbnailCancellation = new CancellationTokenSource();
        _timelineThumbnailProjectId = projectId;
        _timelineThumbnailCache.Clear();
        _timelineThumbnailLoads.Clear();
        _timelineThumbnailFailures.Clear();
        _timelineThumbnailOrder.Clear();
    }

    private void ReleaseTimelineThumbnails()
    {
        _timelineThumbnailCancellation?.Cancel();
        _timelineThumbnailCancellation?.Dispose();
        _timelineThumbnailCancellation = null;
        foreach (List<Image> views in _timelineThumbnailViews.Values)
            foreach (Image view in views) view.Source = null;
        _timelineThumbnailViews.Clear();
        _timelineThumbnailCache.Clear();
        _timelineThumbnailLoads.Clear();
        _timelineThumbnailFailures.Clear();
        _timelineThumbnailOrder.Clear();
    }

    private void RegisterTimelineThumbnail(Guid assetId, VideoAssetKind kind,
        Image view)
    {
        if (!_timelineThumbnailViews.TryGetValue(assetId, out List<Image>? views))
            _timelineThumbnailViews[assetId] = views = [];
        views.Add(view);
        if (_timelineThumbnailCache.TryGetValue(assetId, out BitmapImage? cached))
        {
            view.Source = cached;
            return;
        }
        if (_closed || _timelineThumbnailCancellation is not { } cancellation ||
            _editor is not { } editor || _store is not { } store ||
            _timelineThumbnailFailures.Contains(assetId) ||
            !_timelineThumbnailLoads.Add(assetId)) return;
        _ = LoadTimelineThumbnailAsync(editor.Current, store, assetId, kind,
            cancellation.Token);
    }

    private async Task LoadTimelineThumbnailAsync(VideoProject project,
        VideoProjectStore store, Guid assetId, VideoAssetKind kind,
        CancellationToken token)
    {
        bool entered = false;
        try
        {
            await _timelineThumbnailGate.WaitAsync(token);
            entered = true;
            token.ThrowIfCancellationRequested();
            if (_closed || _editor?.Current.Id != project.Id) return;
            VideoAsset? asset = project.Assets.FirstOrDefault(item => item.Id == assetId);
            if (asset is null) return;
            string path = store.ResolveAssetPath(project, asset);
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            token.ThrowIfCancellationRequested();
            using StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(
                kind == VideoAssetKind.Image ? ThumbnailMode.PicturesView : ThumbnailMode.VideosView,
                96, ThumbnailOptions.UseCurrentScale);
            token.ThrowIfCancellationRequested();
            if (thumbnail.Size == 0)
            {
                if (!_closed && _timelineThumbnailProjectId == project.Id)
                    _timelineThumbnailFailures.Add(assetId);
                return;
            }
            var bitmap = new BitmapImage { DecodePixelWidth = 96 };
            await bitmap.SetSourceAsync(thumbnail);
            token.ThrowIfCancellationRequested();
            if (_closed || _editor?.Current.Id != project.Id) return;
            _timelineThumbnailCache[assetId] = bitmap;
            _timelineThumbnailOrder.Enqueue(assetId);
            if (_timelineThumbnailViews.TryGetValue(assetId, out List<Image>? views))
                foreach (Image view in views) view.Source = bitmap;
            while (_timelineThumbnailCache.Count > MaximumTimelineThumbnails)
                _timelineThumbnailCache.Remove(_timelineThumbnailOrder.Dequeue());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            // A missing or unsupported shell thumbnail leaves the media glyph visible.
            if (!_closed && _timelineThumbnailProjectId == project.Id)
                _timelineThumbnailFailures.Add(assetId);
        }
        finally
        {
            if (entered) _timelineThumbnailGate.Release();
            if (_timelineThumbnailProjectId == project.Id)
                _timelineThumbnailLoads.Remove(assetId);
        }
    }
}
