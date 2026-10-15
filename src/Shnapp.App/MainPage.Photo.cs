using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private bool _photoCropMode;
    private Guid? _photoCropTargetId;
    private ImagePoint? _photoCropStart;
    private ImagePoint? _photoCropEnd;

    internal void SetCameraAvailable(bool available)
    {
        LibraryEmptyCameraCaptureMenu.IsEnabled = available;
        QuickCameraCapture.IsEnabled = available;
        CameraPhotoTool.IsEnabled = available;
        PhotoAddButton.IsEnabled = available;
    }

    private void CameraCapture_Click(object sender, RoutedEventArgs args) =>
        _controller?.Capture(CaptureKind.Camera);

    private void CameraPhotoTool_Click(object sender, RoutedEventArgs args) =>
        _controller?.InsertCameraPhoto();

    internal async Task InsertCameraPhotoAsync(byte[] png)
    {
        DocumentEditor? editor = _editor;
        CanvasBitmap? original = _original;
        if (editor is null || original is null || _controller is null) return;

        Annotation? image = null;
        try
        {
            ImagePoint placement = PasteCenter(editor.Current);
            using var memory = new MemoryStream(png, writable: false);
            using IRandomAccessStream stream = memory.AsRandomAccessStream();
            image = await ReadClipboardImageAsync(stream, placement, original, "camera photo");
            image = FitGalleryImageToViewport(image, placement, editor.Current.Viewport);
            if (!ReferenceEquals(_editor, editor)) return;

            await _controller.Renderer.PreloadImageAsync(image);
            if (!ReferenceEquals(_editor, editor)) return;

            CommitText();
            SetTool(EditorTool.Select);
            editor.AddAnnotation(image);
            SelectOnlyAnnotation(image.Id);
            UpdateInspector();
            DrawingCanvas.Invalidate();
            DrawingCanvas.Focus(FocusState.Programmatic);
            MessageBar.IsOpen = false;
            ViewModel.Status = "Camera photo added · drag to move · crop it in Options";
        }
        finally
        {
            if (image is not null && !editor.Current.Annotations.Any(item => item.Id == image.Id))
            {
                _controller.Renderer.DiscardPastedImage(image.Id);
            }
        }
    }

    private void PhotoCrop_Click(object sender, RoutedEventArgs args)
    {
        if (_photoCropMode)
        {
            CancelPhotoCrop();
            return;
        }

        if (SelectedAnnotation() is not { Kind: AnnotationKind.Image } photo) return;
        CancelInteraction();
        _photoCropMode = true;
        _photoCropTargetId = photo.Id;
        CanvasHost.SetToolCursor(EditorTool.Crop);
        ViewModel.Status = "Drag over the part of the photo to keep · Esc cancels";
        UpdateInspector();
        DrawingCanvas.Invalidate();
        CanvasHost.Focus(FocusState.Programmatic);
    }

    private void ResetPhotoCrop_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null || SelectedAnnotation() is not { Kind: AnnotationKind.Image } photo ||
            photo.ImageSourceCrop is null) return;

        CancelPhotoCrop();
        try
        {
            _editor.UpdateAnnotation(PhotoCropGeometry.Reset(photo));
            UpdateInspector();
            ViewModel.Status = "Photo crop reset";
        }
        catch (ArgumentException exception)
        {
            ShowAnnotationEditError(exception);
        }
    }

    private void CancelPhotoCrop()
    {
        _photoCropMode = false;
        _photoCropTargetId = null;
        _photoCropStart = null;
        _photoCropEnd = null;
        CanvasHost.ReleasePointerCaptures();
        CanvasHost.SetToolCursor(_tool);
        UpdateInspector();
        DrawingCanvas.Invalidate();
    }

    private bool TryStartPhotoCrop(PointerRoutedEventArgs args, Point canvasPosition)
    {
        if (!_photoCropMode) return false;
        Annotation? photo = SelectedAnnotation();
        if (photo is not { Kind: AnnotationKind.Image } || photo.Id != _photoCropTargetId)
        {
            CancelPhotoCrop();
            return true;
        }

        ImagePoint? point = ImagePosition(canvasPosition, allowOutside: true);
        if (point is not ImagePoint position || !photo.Bounds.Contains(position))
        {
            ViewModel.Status = "Drag inside the selected photo to choose the part to keep";
            args.Handled = true;
            return true;
        }

        _photoCropStart = position;
        _photoCropEnd = position;
        CanvasHost.CapturePointer(args.Pointer);
        args.Handled = true;
        DrawingCanvas.Invalidate();
        return true;
    }

    private bool TryUpdatePhotoCrop(Point canvasPosition)
    {
        if (!_photoCropMode || _photoCropStart is null) return false;
        Annotation? photo = SelectedAnnotation();
        ImagePoint? point = ImagePosition(canvasPosition, allowOutside: true);
        if (photo is null || point is not ImagePoint position) return true;

        ImageRect bounds = photo.Bounds;
        _photoCropEnd = new ImagePoint(
            Math.Clamp(position.X, bounds.X, bounds.Right),
            Math.Clamp(position.Y, bounds.Y, bounds.Bottom));
        DrawingCanvas.Invalidate();
        return true;
    }

    private bool TryCompletePhotoCrop(PointerRoutedEventArgs args)
    {
        if (!_photoCropMode || _photoCropStart is not ImagePoint start) return false;
        TryUpdatePhotoCrop(args.GetCurrentPoint(DrawingCanvas).Position);
        ImagePoint end = _photoCropEnd ?? start;
        ImageRect selection = new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        Annotation? photo = SelectedAnnotation();
        _photoCropMode = false;
        CanvasHost.ReleasePointerCapture(args.Pointer);
        CancelPhotoCrop();
        args.Handled = true;
        if (photo is not { Kind: AnnotationKind.Image } || _editor is null ||
            Math.Min(selection.Width, selection.Height) * _scale < 4)
        {
            ViewModel.Status = "Photo crop canceled";
            return true;
        }

        try
        {
            Annotation cropped = PhotoCropGeometry.Crop(photo, selection);
            if (cropped != photo)
            {
                _editor.UpdateAnnotation(cropped);
                ViewModel.Status = "Photo cropped · use Reset photo crop to restore it";
            }
            UpdateInspector();
        }
        catch (ArgumentException exception)
        {
            ShowAnnotationEditError(exception);
        }

        return true;
    }

    private void DrawPhotoCropOverlay(CanvasDrawingSession drawing)
    {
        if (!_photoCropMode || _photoCropStart is not ImagePoint start ||
            _photoCropEnd is not ImagePoint end || SelectedAnnotation() is not { } photo) return;

        ImageRect bounds = photo.Bounds;
        ImageRect chosen = new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        Color shade = Color.FromArgb(124, 0, 0, 0);
        drawing.FillRectangle(new Rect(bounds.X, bounds.Y, bounds.Width,
            Math.Max(0, chosen.Y - bounds.Y)), shade);
        drawing.FillRectangle(new Rect(bounds.X, chosen.Bottom, bounds.Width,
            Math.Max(0, bounds.Bottom - chosen.Bottom)), shade);
        drawing.FillRectangle(new Rect(bounds.X, chosen.Y,
            Math.Max(0, chosen.X - bounds.X), chosen.Height), shade);
        drawing.FillRectangle(new Rect(chosen.Right, chosen.Y,
            Math.Max(0, bounds.Right - chosen.Right), chosen.Height), shade);
        if (chosen.Width > 0 && chosen.Height > 0)
        {
            drawing.DrawRectangle(new Rect(chosen.X, chosen.Y, chosen.Width, chosen.Height),
                Colors.White, (float)(2 / _scale));
        }
    }
}
