using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Shnapp.App;

internal sealed partial class AppController
{
    internal void CloneDocument(Guid id) => Run(() => CloneDocumentAsync(id));

    internal void RequestRenameDocument(Guid id) => Run(() => RenameDocumentAsync(id));

    internal void RequestDeleteDocuments(IReadOnlyList<Guid> ids) =>
        Run(() => DeleteDocumentsAsync(ids.Distinct().ToArray()));

    internal void CopyDocumentPath(Guid id) => Run(() => CopyDocumentPathAsync(id));

    internal void CopyCurrentDocumentPath() => Run(async () =>
    {
        if (_page.Document is not { } document)
        {
            return;
        }

        _page.CommitText();
        _saveTimer.Stop();
        await SaveCurrentAsync();
        if (_page.Document?.Id == document.Id)
        {
            await CopyDocumentPathAsync(document.Id);
        }
    });

    private async Task CloneDocumentAsync(Guid id)
    {
        if (!await _captureGate.WaitAsync(0, _lifetime.Token))
        {
            return;
        }

        try
        {
            string title;
            await _saveGate.WaitAsync(_lifetime.Token);
            try
            {
                await RecoverPendingSaveUnderGateAsync(id);
                ShnappDocument source = await Library.OpenAsync(id, _lifetime.Token)
                    ?? throw new FileNotFoundException("This shnapp is no longer in your Library.");
                EnsureLibraryImagesSafe(id, create: false);
                if (!File.Exists(Library.GetOriginalPath(id)))
                {
                    throw new FileNotFoundException("This shnapp's original image is missing.");
                }

                IReadOnlyList<ShnappSummary> existing = await Library.ListSummariesAsync(_lifetime.Token);
                title = UniqueCopyTitle(source.Title, existing);
                ShnappDocument clone = source with
                {
                    Id = Guid.NewGuid(),
                    Title = title,
                    CreatedAt = DateTimeOffset.UtcNow,
                };

                try
                {
                    EnsureLibraryImagesSafe(clone.Id, create: true);
                    Copy(Library.GetOriginalPath);
                    Copy(Library.GetExportPath);
                    Copy(Library.GetPreviewPath);
                    Copy(Library.GetFittedPreviewPath);
                    Copy(Library.GetCompactPreviewPath);
                    Copy(Library.GetGalleryPreviewPath);
                    await EnsureClonedImagesAsync(source, clone.Id);
                    await Library.SaveAsync(clone, _lifetime.Token);
                    _documentSaveJournal.Complete(clone.Id);
                }
                catch
                {
                    try { await Library.DeleteAsync(clone.Id); }
                    catch { /* Preserve the original copy failure. */ }
                    throw;
                }

                void Copy(Func<Guid, string> path)
                {
                    string from = path(id);
                    if (File.Exists(from))
                    {
                        File.Copy(from, path(clone.Id), overwrite: false);
                    }
                }
            }
            finally
            {
                _saveGate.Release();
            }

            await RefreshLibraryAfterActionAsync();
            _page.ViewModel.Status = $"Cloned as {title}";
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async Task EnsureClonedImagesAsync(ShnappDocument source, Guid cloneId)
    {
        string exportPath = Library.GetExportPath(cloneId);
        if (!File.Exists(exportPath))
        {
            await Renderer.PreloadImagesAsync(source);
            using FileStream originalFile = new(Library.GetOriginalPath(cloneId), FileMode.Open, FileAccess.Read, FileShare.Read);
            using CanvasBitmap original = await CanvasBitmap.LoadAsync(_device, originalFile.AsRandomAccessStream(), 96);
            using CanvasRenderTarget flattened = Renderer.Flatten(original, source);
            await ShnappRenderer.SavePngAtomicAsync(flattened, exportPath, _lifetime.Token);
        }

        if (File.Exists(Library.GetPreviewPath(cloneId)) &&
            File.Exists(Library.GetFittedPreviewPath(cloneId)) &&
            File.Exists(Library.GetCompactPreviewPath(cloneId)) &&
            File.Exists(Library.GetGalleryPreviewPath(cloneId)))
        {
            return;
        }

        using FileStream imageFile = new(exportPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using CanvasBitmap image = await CanvasBitmap.LoadAsync(_device, imageFile.AsRandomAccessStream(), 96);
        if (!File.Exists(Library.GetPreviewPath(cloneId)))
        {
            using CanvasRenderTarget preview = Renderer.Thumbnail(image, 384, 256);
            await ShnappRenderer.SavePngAtomicAsync(preview, Library.GetPreviewPath(cloneId), _lifetime.Token);
        }
        if (!File.Exists(Library.GetFittedPreviewPath(cloneId)))
        {
            using CanvasRenderTarget fittedPreview = Renderer.Thumbnail(image, 384, 256,
                preserveEntireImage: true);
            await ShnappRenderer.SavePngAtomicAsync(fittedPreview, Library.GetFittedPreviewPath(cloneId), _lifetime.Token);
        }
        if (!File.Exists(Library.GetCompactPreviewPath(cloneId)))
        {
            using CanvasRenderTarget compact = Renderer.Thumbnail(image, 192, 128);
            await ShnappRenderer.SavePngAtomicAsync(compact, Library.GetCompactPreviewPath(cloneId), _lifetime.Token);
        }
        if (!File.Exists(Library.GetGalleryPreviewPath(cloneId)))
        {
            using CanvasRenderTarget gallery = Renderer.Thumbnail(image, 160, 160);
            await ShnappRenderer.SavePngAtomicAsync(gallery, Library.GetGalleryPreviewPath(cloneId), _lifetime.Token);
        }
    }

    private static string UniqueCopyTitle(string original, IReadOnlyList<ShnappSummary> existing)
    {
        var names = existing.Select(document => document.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; ; index++)
        {
            string suffix = index == 1 ? " copy" : $" copy {index}";
            string stem = original.Trim();
            if (stem.Length > 120 - suffix.Length)
            {
                stem = stem[..(120 - suffix.Length)].TrimEnd();
            }

            string title = ShnappTitles.Normalize(stem + suffix);
            if (!names.Contains(title))
            {
                return title;
            }
        }
    }

    private async Task RenameDocumentAsync(Guid id)
    {
        if (_dialogOpen || !await _captureGate.WaitAsync(0, _lifetime.Token))
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            ShnappDocument document = await Library.OpenAsync(id, _lifetime.Token)
                ?? throw new FileNotFoundException("This shnapp is no longer in your Library.");
            var name = new TextBox
            {
                Text = document.Title,
                MaxLength = 120,
                PlaceholderText = "Shnapp name",
            };
            name.Loaded += (_, _) =>
            {
                name.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                name.SelectAll();
            };
            var dialog = new ContentDialog
            {
                XamlRoot = _page.XamlRoot,
                RequestedTheme = _page.RequestedTheme,
                Title = "Rename shnapp",
                Content = name,
                PrimaryButtonText = "Rename",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (_page.Document?.Id == id)
            {
                _page.CommitText();
                _page.CommitTitleRename();
            }

            string title;
            try { title = ShnappTitles.Normalize(name.Text); }
            catch (ArgumentException exception)
            {
                _page.ShowMessage("Name this shnapp", exception.Message);
                return;
            }

            bool editorIsOpen;
            await _saveGate.WaitAsync(_lifetime.Token);
            try
            {
                // The dialog can remain open while an autosave commits newer annotations.
                ShnappDocument latest = await Library.OpenAsync(id, _lifetime.Token)
                    ?? throw new FileNotFoundException("This shnapp is no longer in your Library.");
                if (latest.Title != title)
                {
                    await Library.SaveAsync(latest with { Title = title }, _lifetime.Token);
                }

                ShnappDocument? editorBefore = _page.Document;
                editorIsOpen = editorBefore?.Id == id;
                if (editorIsOpen)
                {
                    bool editorWasSaved = ReferenceEquals(editorBefore, _saved);
                    ShnappDocument editorAfter = _page.ApplySavedDocumentTitle(id, title)!;
                    if (editorWasSaved)
                    {
                        _saved = editorAfter;
                        _saveTimer.Stop();
                    }
                    else
                    {
                        // An unsaved edit still needs its annotations persisted with the new title.
                        _saveTimer.Stop();
                        _saveTimer.Start();
                    }
                }
            }
            finally
            {
                _saveGate.Release();
            }

            if (editorIsOpen)
            {
                _page.SetGalleryDocuments(await ListReadySummariesAsync());
            }
            else
            {
                await RefreshLibraryAfterActionAsync();
            }
            _page.ViewModel.Status = $"Renamed to {title}";
        }
        finally
        {
            _dialogOpen = false;
            _captureGate.Release();
        }
    }

    private async Task DeleteDocumentsAsync(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0 || _dialogOpen || !await _captureGate.WaitAsync(0, _lifetime.Token))
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            string item = ids.Count == 1 ? "this shnapp" : $"{ids.Count} shnapps";
            var dialog = new ContentDialog
            {
                XamlRoot = _page.XamlRoot,
                RequestedTheme = _page.RequestedTheme,
                Title = $"Delete {item}?",
                Content = "The editable capture and its saved PNG will be removed from this device. This cannot be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (_page.Document is { } current && ids.Contains(current.Id))
            {
                _saveTimer.Stop();
            }

            // A pending autosave must finish before the file is removed; holding
            // the gate until the editor closes prevents a queued save restoring it.
            var snapshotsNeedingRetry = new List<Guid>();
            await _saveGate.WaitAsync(_lifetime.Token);
            try
            {
                foreach (Guid id in ids)
                {
                    bool snapshotPurgeFailed = false;
                    try
                    {
                        _exportSnapshots.DeleteForDocument(id);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        System.Diagnostics.Debug.WriteLine($"Could not remove export snapshots for {id}: {exception}");
                        snapshotPurgeFailed = true;
                    }

                    await Library.DeleteAsync(id, _lifetime.Token);
                    if (snapshotPurgeFailed) { snapshotsNeedingRetry.Add(id); }
                }

                ForgetNavigationDocuments(ids);
                _page.RemoveLibrarySelection(ids);
                await RefreshLibraryAfterActionAsync(saveGateHeld: true);
                _page.ViewModel.Status = snapshotsNeedingRetry.Count == 0
                    ? (ids.Count == 1 ? "Shnapp deleted" : $"{ids.Count} shnapps deleted")
                    : $"{ids.Count} shnapp(s) deleted · saved PNG copy removal pending";
            }
            finally
            {
                _saveGate.Release();
                if (snapshotsNeedingRetry.Count > 0)
                {
                    _page.ShowMessage("Saved PNG copy may remain on this device",
                        "The shnapp was deleted, but a saved PNG copy could not be removed. " +
                        "Shnapp will retry shortly. If another app is using the copy, close it.", InfoBarSeverity.Warning);
                    _ = RetrySnapshotPurgeAsync(snapshotsNeedingRetry);
                }
            }
        }
        finally
        {
            _dialogOpen = false;
            _captureGate.Release();
        }
    }

    private async Task RetrySnapshotPurgeAsync(IReadOnlyList<Guid> ids)
    {
        var remaining = ids.ToHashSet();
        try
        {
            foreach (TimeSpan delay in new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10),
                TimeSpan.FromMinutes(1) })
            {
                await Task.Delay(delay, _lifetime.Token);
                foreach (Guid id in remaining.ToArray())
                {
                    try
                    {
                        _exportSnapshots.DeleteForDocument(id);
                        remaining.Remove(id);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        System.Diagnostics.Debug.WriteLine($"Could not retry export snapshot removal for {id}: {exception}");
                    }
                }

                if (remaining.Count == 0) { return; }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task CopyDocumentPathAsync(Guid id)
    {
        string snapshotPath;
        await _saveGate.WaitAsync(_lifetime.Token);
        try
        {
            await RecoverPendingSaveUnderGateAsync(id);
            if (await Library.OpenAsync(id, _lifetime.Token) is null)
            {
                throw new FileNotFoundException("This shnapp is no longer in your Library.");
            }

            EnsureLibraryImagesSafe(id, create: false);
            snapshotPath = await _exportSnapshots.CreateAsync(id,
                Library.GetExportPath(id), _lifetime.Token);
        }
        finally
        {
            _saveGate.Release();
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(snapshotPath);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        _page.ViewModel.Status = "PNG snapshot path copied · kept at least 7 days unless this shnapp is deleted";
    }

    private async Task RefreshLibraryAfterActionAsync(bool saveGateHeld = false)
    {
        IReadOnlyList<ShnappSummary> documents = await ListReadySummariesAsync(saveGateHeld);
        _page.ShowLibrary(documents);
        RecordNavigation(null);
        _page.SetGalleryDocuments(documents);
    }
}
