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
            ShnappDocument source = await Library.OpenAsync(id, _lifetime.Token)
                ?? throw new FileNotFoundException("This shnapp is no longer in your Library.");
            EnsureLibraryImagesSafe(id, create: false);
            if (!File.Exists(Library.GetOriginalPath(id)))
            {
                throw new FileNotFoundException("This shnapp's original image is missing.");
            }

            IReadOnlyList<ShnappSummary> existing = await Library.ListSummariesAsync(_lifetime.Token);
            string title = UniqueCopyTitle(source.Title, existing);
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
            }
            catch
            {
                try { await Library.DeleteAsync(clone.Id); }
                catch { /* Preserve the original copy failure. */ }
                throw;
            }

            await RefreshLibraryAfterActionAsync();
            _page.ViewModel.Status = $"Cloned as {title}";

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

            string title;
            try { title = ShnappTitles.Normalize(name.Text); }
            catch (ArgumentException exception)
            {
                _page.ShowMessage("Name this shnapp", exception.Message);
                return;
            }

            if (title == document.Title)
            {
                return;
            }

            await Library.SaveAsync(document with { Title = title }, _lifetime.Token);
            await RefreshLibraryAfterActionAsync();
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
            await _saveGate.WaitAsync(_lifetime.Token);
            try
            {
                foreach (Guid id in ids)
                {
                    await Library.DeleteAsync(id, _lifetime.Token);
                }

                ForgetNavigationDocuments(ids);
                _page.RemoveLibrarySelection(ids);
                await RefreshLibraryAfterActionAsync();
                _page.ViewModel.Status = ids.Count == 1 ? "Shnapp deleted" : $"{ids.Count} shnapps deleted";
            }
            finally
            {
                _saveGate.Release();
            }
        }
        finally
        {
            _dialogOpen = false;
            _captureGate.Release();
        }
    }

    private async Task CopyDocumentPathAsync(Guid id)
    {
        if (await Library.OpenAsync(id, _lifetime.Token) is null)
        {
            throw new FileNotFoundException("This shnapp is no longer in your Library.");
        }

        EnsureLibraryImagesSafe(id, create: false);
        string path = Library.GetExportPath(id);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("This shnapp's saved PNG is missing.", path);
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(path);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        _page.ViewModel.Status = "Full PNG path copied";
    }

    private async Task RefreshLibraryAfterActionAsync()
    {
        IReadOnlyList<ShnappSummary> documents = await Library.ListSummariesAsync(_lifetime.Token);
        _page.ShowLibrary(documents);
        RecordNavigation(null);
        _page.SetGalleryDocuments(documents);
    }
}
