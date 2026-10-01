using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private const string LibraryDragFormat = "Shnapp.LibraryImage";
    private bool _libraryImportInProgress;

    private void LibraryPreview_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        LibraryEntry? entry = (sender as FrameworkElement)?.Tag as LibraryEntry ??
            (sender as FrameworkElement)?.DataContext as LibraryEntry;
        if (entry is null)
        {
            args.Cancel = true;
            return;
        }

        args.Data.SetData(LibraryDragFormat, entry.Id.ToString("D"));
        args.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void Canvas_DragOver(object sender, DragEventArgs args)
    {
        args.AcceptedOperation = _editor is not null && !_libraryImportInProgress &&
            args.DataView.Contains(LibraryDragFormat)
                ? DataPackageOperation.Copy : DataPackageOperation.None;
        args.Handled = true;
    }

    private async void Canvas_Drop(object sender, DragEventArgs args)
    {
        DocumentEditor? editor = _editor;
        CanvasBitmap? original = _original;
        if (editor is null || original is null || _controller is null || _libraryImportInProgress ||
            !args.DataView.Contains(LibraryDragFormat))
        {
            args.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var deferral = args.GetDeferral();
        _libraryImportInProgress = true;
        Annotation? annotation = null;
        try
        {
            Point dropPosition = args.GetPosition(DrawingCanvas);
            ImagePoint placement = ImagePosition(dropPosition, allowOutside: true) ?? PasteCenter(editor.Current);
            object value = await args.DataView.GetDataAsync(LibraryDragFormat);
            if (value is not string idText || !Guid.TryParse(idText, out Guid id))
            {
                throw new InvalidDataException("This gallery item could not be identified.");
            }

            if (id == editor.Current.Id || !_galleryDocuments.Any(document => document.Id == id))
            {
                throw new InvalidDataException("This shnapp is not available in the current gallery.");
            }

            StorageFile file = await _controller.OpenSavedImageForLayerAsync(id);
            using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
            annotation = await ReadClipboardImageAsync(stream, placement, original);
            annotation = FitGalleryImageToViewport(annotation, placement, editor.Current.Viewport);
            if (!ReferenceEquals(_editor, editor))
            {
                args.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            await _controller.Renderer.PreloadImageAsync(annotation);
            if (!ReferenceEquals(_editor, editor))
            {
                args.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            CommitText();
            SetTool(EditorTool.Select);
            try
            {
                _pendingContentAnchor = dropPosition;
                editor.AddAnnotation(annotation);
            }
            finally
            {
                _pendingContentAnchor = null;
            }

            _selectedId = annotation.Id;
            UpdateInspector();
            DrawingCanvas.Invalidate();
            DrawingCanvas.Focus(FocusState.Programmatic);
            MessageBar.IsOpen = false;
            ViewModel.Status = "Shnapp added as an image · drag to move · drag a handle to resize";
            args.AcceptedOperation = DataPackageOperation.Copy;
        }
        catch (Exception exception)
        {
            args.AcceptedOperation = DataPackageOperation.None;
            if (ReferenceEquals(_editor, editor))
            {
                ShowMessage("Couldn't add this shnapp", exception is ArgumentException or InvalidDataException or IOException
                    ? exception.Message
                    : "Shnapp could not read this saved image. Try opening it in the Library.",
                    InfoBarSeverity.Warning);
            }
        }
        finally
        {
            if (annotation is not null && !editor.Current.Annotations.Any(item => item.Id == annotation.Id))
            {
                _controller?.Renderer.DiscardPastedImage(annotation.Id);
            }

            _libraryImportInProgress = false;
            deferral.Complete();
        }
    }

    private static Annotation FitGalleryImageToViewport(Annotation image, ImagePoint placement, ImageRect viewport)
    {
        ImageRect bounds = image.Bounds;
        double maximumWidth = Math.Max(160, viewport.Width * 0.8);
        double maximumHeight = Math.Max(160, viewport.Height * 0.8);
        double scale = Math.Min(1, Math.Min(maximumWidth / bounds.Width, maximumHeight / bounds.Height));
        double width = bounds.Width * scale;
        double height = bounds.Height * scale;
        var start = new ImagePoint(placement.X - width / 2, placement.Y - height / 2);
        return image with { Start = start, End = new(start.X + width, start.Y + height) };
    }
}
