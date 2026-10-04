using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.Storage.Streams;

namespace Shnapp.App;

internal sealed partial class AppController
{
    private readonly DocumentSaveJournal _documentSaveJournal;
    private readonly DocumentExportSnapshots _exportSnapshots;
    private readonly Queue<Guid> _deferredRecovery = new();
    private readonly HashSet<Guid> _queuedRecovery = [];
    private readonly HashSet<Guid> _failedRecovery = [];
    private bool _recoveringLibrary;

    private async Task CleanupExpiredExportSnapshotsAsync()
    {
        try
        {
            await Task.Run(() => _exportSnapshots.CleanupExpiredAsync(
                TimeSpan.FromDays(7), 256, _lifetime.Token), _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not clean expired export snapshots: {exception}");
        }
    }

    // Every library/gallery view passes through here. Unverified images stay hidden
    // while recovery proceeds a few documents at a time after the view is shown.
    private async Task<IReadOnlyList<ShnappSummary>> ListReadySummariesAsync(bool saveGateHeld = false)
    {
        var needsRecovery = new List<Guid>();
        if (!saveGateHeld) { await _saveGate.WaitAsync(_lifetime.Token); }
        try
        {
            IReadOnlyList<ShnappSummary> summaries = await Library.ListSummariesAsync(_lifetime.Token);
            var ready = new List<ShnappSummary>(summaries.Count);
            int unavailable = 0;
            foreach (ShnappSummary summary in summaries)
            {
                try
                {
                    if (IsCachedRenderReady(summary.Id)) { ready.Add(summary); }
                    else { needsRecovery.Add(summary.Id); }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Debug.WriteLine($"Could not recover shnapp {summary.Id}: {exception}");
                    unavailable++;
                }
            }

            if (unavailable > 0)
            {
                _page.ShowMessage("Some shnapps need repair",
                    $"{unavailable} shnapp(s) have unsafe library paths and are hidden. " +
                    "Keep their library files and repair the paths before reopening Shnapp.",
                    InfoBarSeverity.Warning);
            }

            return ready;
        }
        finally
        {
            if (!saveGateHeld) { _saveGate.Release(); }
            QueueDeferredRecovery(needsRecovery);
        }
    }

    private bool IsCachedRenderReady(Guid id)
    {
        EnsureLibraryImagesSafe(id, create: false);
        return !_documentSaveJournal.IsPending(id) &&
            _documentSaveJournal.IsVerified(id) &&
            DerivedImagePaths(id).All(File.Exists);
    }

    private void QueueDeferredRecovery(IEnumerable<Guid> ids)
    {
        foreach (Guid id in ids)
        {
            if (!_failedRecovery.Contains(id) && _queuedRecovery.Add(id))
            {
                _deferredRecovery.Enqueue(id);
            }
        }
        if (_recoveringLibrary || _deferredRecovery.Count == 0 || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _recoveringLibrary = true;
        _page.SetLibraryRecoveryState(restoring: true);
        _ = RecoverDeferredLibraryAsync();
    }

    private async Task RecoverDeferredLibraryAsync()
    {
        await Task.Yield();
        int recovered = 0;
        int failed = 0;
        bool paused = false;
        try
        {
            if (_page.Document is null) { _page.ViewModel.Status = "Restoring saved shnapps…"; }
            while (_deferredRecovery.Count > 0 && !_lifetime.IsCancellationRequested)
            {
                Guid id = _deferredRecovery.Dequeue();
                try
                {
                    await RecoverPendingSaveAsync(id);
                    recovered++;
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Debug.WriteLine($"Could not recover shnapp {id}: {exception}");
                    _failedRecovery.Add(id);
                    failed++;
                }
                finally
                {
                    _queuedRecovery.Remove(id);
                }

                if (recovered % 4 == 0 || _deferredRecovery.Count == 0)
                {
                    IReadOnlyList<ShnappSummary> ready = await ListReadySummariesAsync();
                    _page.UpdateReadyLibrary(ready);
                    _page.SetGalleryDocuments(ready);
                }
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            paused = true;
            Debug.WriteLine($"Library recovery paused: {exception}");
            _page.ShowMessage("Library recovery paused",
                "Some saved images could not be checked. Open Library again to retry.",
                InfoBarSeverity.Warning);
        }
        finally
        {
            _recoveringLibrary = false;
            if (!_lifetime.IsCancellationRequested) { _page.SetLibraryRecoveryState(restoring: false); }
        }

        if (_page.Document is null && !_lifetime.IsCancellationRequested)
        {
            _page.ViewModel.Status = paused ? "Library recovery paused" :
                failed == 0 ? "Saved shnapps ready" : "Some shnapps need repair";
        }
        if (failed > 0)
        {
            _page.ShowMessage("Some shnapps need repair",
                $"{failed} shnapp(s) could not have their saved images safely regenerated and are hidden. " +
                "Keep their library files and open Library again to retry after fixing the problem.",
                InfoBarSeverity.Warning);
        }
    }

    // Cached export consumers call this before giving a file to Windows or another document.
    private async Task RecoverPendingSaveAsync(Guid id)
    {
        await _saveGate.WaitAsync(_lifetime.Token);
        try { await RecoverPendingSaveUnderGateAsync(id); }
        finally { _saveGate.Release(); }
    }

    private async Task RecoverPendingSaveUnderGateAsync(Guid id)
    {
        if (IsCachedRenderReady(id)) { return; }
        bool pending = _documentSaveJournal.IsPending(id);
        bool preserveUnverifiedExport = !_documentSaveJournal.IsVerified(id);

        ShnappDocument? committed = await Library.OpenAsync(id, _lifetime.Token);
        if (committed is null)
        {
            if (pending)
            {
                // A first save never committed metadata. Keep original.png for manual
                // recovery, but remove derived images and never mark this item verified.
                DeleteDerivedImages(id);
                _documentSaveJournal.Abandon(id);
            }
            return;
        }

        if ((long)committed.PixelWidth * committed.PixelHeight > 64_000_000)
        {
            throw new InvalidDataException("This shnapp exceeds the 64-megapixel image limit.");
        }

        // A legacy item may have its only usable PNG in the old cache. Keep it
        // until atomic replacement succeeds, but hide it from every app reader.
        // Saves created by this journal can clear canonical paths immediately.
        if (pending && !preserveUnverifiedExport) { DeleteDerivedImages(id); }

        using FileStream source = new(Library.GetOriginalPath(id), FileMode.Open, FileAccess.Read, FileShare.Read);
        using CanvasBitmap original = await CanvasBitmap.LoadAsync(_device, source.AsRandomAccessStream(), 96);
        if (original.SizeInPixels.Width != committed.PixelWidth ||
            original.SizeInPixels.Height != committed.PixelHeight)
        {
            throw new InvalidDataException("This shnapp's original PNG does not match its editable document.");
        }

        try
        {
            await Renderer.PreloadImagesAsync(committed);
            using CanvasRenderTarget flattened = Renderer.Flatten(original, committed);
            if (!pending)
            {
                // Legacy files remain available for manual recovery if decoding or
                // rendering fails. Atomic replacements leave old exports intact on
                // a write failure, while the marker keeps them hidden in Shnapp.
                await _documentSaveJournal.BeginAsync(id, _lifetime.Token);
            }
            await WriteDerivedImagesAsync(id, flattened, _lifetime.Token);
            _documentSaveJournal.Complete(id);
        }
        finally
        {
            if (_page.Document is { } visible) { Renderer.RetainPastedImages(visible); }
            else { Renderer.ClearPastedImages(); }
        }
    }

    private void DeleteDerivedImages(Guid id)
    {
        foreach (string path in DerivedImagePaths(id)) { File.Delete(path); }
    }

    private string[] DerivedImagePaths(Guid id) =>
    [
        Library.GetGalleryPreviewPath(id),
        Library.GetCompactPreviewPath(id),
        Library.GetPreviewPath(id),
        Library.GetFittedPreviewPath(id),
        Library.GetExportPath(id),
    ];

    private async Task WriteDerivedImagesAsync(Guid id, CanvasBitmap flattened,
        CancellationToken cancellationToken = default)
    {
        using CanvasRenderTarget gallery = Renderer.Thumbnail(flattened, 160, 160);
        await ShnappRenderer.SavePngAtomicAsync(gallery, Library.GetGalleryPreviewPath(id), cancellationToken);
        using CanvasRenderTarget compact = Renderer.Thumbnail(flattened, 192, 128);
        await ShnappRenderer.SavePngAtomicAsync(compact, Library.GetCompactPreviewPath(id), cancellationToken);
        using CanvasRenderTarget preview = Renderer.Thumbnail(flattened, 384, 256);
        await ShnappRenderer.SavePngAtomicAsync(preview, Library.GetPreviewPath(id), cancellationToken);
        using CanvasRenderTarget fittedPreview = Renderer.Thumbnail(flattened, 384, 256,
            preserveEntireImage: true);
        await ShnappRenderer.SavePngAtomicAsync(fittedPreview, Library.GetFittedPreviewPath(id), cancellationToken);
        await ShnappRenderer.SavePngAtomicAsync(flattened, Library.GetExportPath(id), cancellationToken);
    }
}
