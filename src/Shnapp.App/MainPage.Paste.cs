using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private const int MaximumPastedImageSide = 12_000;
    private const long MaximumPastedImagePixels = 40_000_000;
    private const ulong MaximumPastedPngBytes = 32 * 1024 * 1024;
    private bool _pasteInProgress;

    private void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Text inputs retain their native paste behavior, including the floating text editor.
        if (_editor is null || EditorInputHasFocus())
        {
            return;
        }

        args.Handled = true;
        if (!_pasteInProgress)
        {
            _ = PasteClipboardAsync();
        }
    }

    private async Task PasteClipboardAsync()
    {
        DocumentEditor? editor = _editor;
        CanvasBitmap? original = _original;
        if (editor is null || original is null)
        {
            return;
        }

        _pasteInProgress = true;
        Annotation? annotation = null;
        try
        {
            ImagePoint placement = PasteCenter(editor.Current);
            DataPackageView contents = Clipboard.GetContent();
            if (contents.Contains("PNG"))
            {
                // Prefer the original PNG over Windows' synthesized bitmap, which
                // flattens transparent pixels in classic desktop clipboard formats.
                object png = await contents.GetDataAsync("PNG");
                if (png is IRandomAccessStreamReference reference)
                {
                    using IRandomAccessStreamWithContentType input = await reference.OpenReadAsync();
                    annotation = await ReadClipboardImageAsync(input, placement, original);
                }
                else if (png is IRandomAccessStream pngStream)
                {
                    using (pngStream)
                    {
                        pngStream.Seek(0);
                        annotation = await ReadClipboardImageAsync(pngStream, placement, original);
                    }
                }
                else if (png is IBuffer buffer)
                {
                    using var input = new InMemoryRandomAccessStream();
                    await input.WriteAsync(buffer);
                    input.Seek(0);
                    annotation = await ReadClipboardImageAsync(input, placement, original);
                }
            }

            if (annotation is null && contents.Contains(StandardDataFormats.Bitmap))
            {
                using IRandomAccessStreamWithContentType input =
                    await (await contents.GetBitmapAsync()).OpenReadAsync();
                annotation = await ReadClipboardImageAsync(input, placement, original);
            }
            else if (annotation is null && contents.Contains(StandardDataFormats.StorageItems))
            {
                IReadOnlyList<IStorageItem> items = await contents.GetStorageItemsAsync();
                StorageFile? imageFile = items.OfType<StorageFile>().FirstOrDefault(file =>
                    file.FileType.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or
                        ".tif" or ".tiff" or ".webp" or ".heic" or ".heif");
                if (imageFile is not null)
                {
                    using IRandomAccessStreamWithContentType input = await imageFile.OpenReadAsync();
                    annotation = await ReadClipboardImageAsync(input, placement, original);
                }
            }

            if (annotation is null && contents.Contains(StandardDataFormats.Text))
            {
                string text = await contents.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                annotation = CreatePastedText(placement, text);
            }

            if (annotation is null)
            {
                ViewModel.Status = "Copy an image or text, then press Ctrl+V";
                return;
            }

            // Clipboard reads can outlive a document switch. Never paste into a different shnapp.
            if (!ReferenceEquals(_editor, editor))
            {
                return;
            }

            if (annotation.Kind == AnnotationKind.Image)
            {
                await _controller!.Renderer.PreloadImageAsync(annotation);
                if (!ReferenceEquals(_editor, editor))
                {
                    return;
                }
            }

            CommitText();
            SetTool(EditorTool.Select);
            editor.AddAnnotation(annotation);
            SelectOnlyAnnotation(annotation.Id);
            UpdateInspector();
            DrawingCanvas.Invalidate();
            DrawingCanvas.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            MessageBar.IsOpen = false;
            ViewModel.Status = annotation.Kind == AnnotationKind.Image
                ? "Image pasted · drag to move · drag a handle to resize"
                : "Text pasted · drag to move · double-click to edit";
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_editor, editor))
            {
                ShowMessage("Couldn't paste", exception is ArgumentException or InvalidDataException
                    ? exception.Message
                    : "Shnapp could not read this clipboard image or text.", InfoBarSeverity.Warning);
            }
        }
        finally
        {
            if (annotation?.Kind == AnnotationKind.Image &&
                !editor.Current.Annotations.Any(item => item.Id == annotation.Id))
            {
                _controller?.Renderer.DiscardPastedImage(annotation.Id);
            }

            _pasteInProgress = false;
        }
    }

    private ImagePoint PasteCenter(ShnappDocument document)
    {
        ImageRect viewport = document.Viewport;
        var fallback = new ImagePoint(viewport.X + viewport.Width / 2, viewport.Y + viewport.Height / 2);
        ImagePoint visibleCenter = DrawingCanvas.ActualWidth > 0 && DrawingCanvas.ActualHeight > 0
            ? ImagePosition(new Point(DrawingCanvas.ActualWidth / 2, DrawingCanvas.ActualHeight / 2), clamp: true)
                ?? fallback
            : fallback;
        return new ImagePoint(
            Math.Clamp(visibleCenter.X, viewport.X, Math.Max(viewport.X, viewport.Right - 1)),
            Math.Clamp(visibleCenter.Y, viewport.Y, Math.Max(viewport.Y, viewport.Bottom - 1)));
    }

    private Annotation CreatePastedText(ImagePoint origin, string text)
    {
        ToolStyle style = _toolStyles[EditorTool.Text];
        return MeasureTextAnnotation(new Annotation
        {
            Kind = AnnotationKind.Text,
            Start = origin,
            End = origin,
            Text = text,
            StrokeArgb = style.Primary,
            FontFamily = style.FontFamily,
            FontSize = style.FontSize,
            FontWeight = style.FontWeight,
            Italic = style.Italic,
            TextKerning = style.TextKerning,
            TextLetterSpacing = style.TextLetterSpacing,
            TextTransform = style.TextTransform,
            TextAlignment = style.TextAlignment,
            TextTruncation = style.TextTruncation,
            TextLineHeight = style.TextLineHeight,
        });
    }

    private static async Task<Annotation> ReadClipboardImageAsync(
        IRandomAccessStream input, ImagePoint placement, CanvasBitmap original)
    {
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
        int width = checked((int)decoder.PixelWidth);
        int height = checked((int)decoder.PixelHeight);
        if (width < 1 || height < 1 || width > MaximumPastedImageSide ||
            height > MaximumPastedImageSide || (long)width * height > MaximumPastedImagePixels)
        {
            throw new InvalidDataException("The clipboard image exceeds the 40-megapixel paste limit.");
        }

        input.Seek(0);
        using CanvasBitmap bitmap = await CanvasBitmap.LoadAsync(original.Device, input, 96);
        width = checked((int)bitmap.SizeInPixels.Width);
        height = checked((int)bitmap.SizeInPixels.Height);
        if (width < 1 || height < 1 || width > MaximumPastedImageSide ||
            height > MaximumPastedImageSide || (long)width * height > MaximumPastedImagePixels)
        {
            throw new InvalidDataException("The clipboard image exceeds the 40-megapixel paste limit.");
        }

        using var png = new InMemoryRandomAccessStream();
        await bitmap.SaveAsync(png, CanvasBitmapFileFormat.Png);
        if (png.Size > MaximumPastedPngBytes)
        {
            throw new InvalidDataException("The clipboard image exceeds the 32 MB paste limit.");
        }

        png.Seek(0);
        using var reader = new DataReader(png);
        await reader.LoadAsync(checked((uint)png.Size));
        byte[] bytes = new byte[checked((int)png.Size)];
        reader.ReadBytes(bytes);

        var start = new ImagePoint(
            Math.Round(placement.X - width / 2d),
            Math.Round(placement.Y - height / 2d));
        return new Annotation
        {
            Kind = AnnotationKind.Image,
            Start = start,
            End = new(start.X + width, start.Y + height),
            ImagePngBase64 = Convert.ToBase64String(bytes),
        };
    }
}
