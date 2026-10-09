using System.Numerics;
using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Shnapp.Core;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Text;

namespace Shnapp.App.Editor;

internal sealed class ShnappRenderer(CanvasDevice device)
{
    internal const int ShadowPadding = 32;
    private const float MaximumUnboundedTextExtent = 16_384;
    private readonly CanvasDevice _device = device ?? throw new ArgumentNullException(nameof(device));
    private readonly Dictionary<Guid, List<(string Payload, CanvasBitmap Bitmap)>> _pastedImages = [];

    internal CanvasRenderTarget Flatten(CanvasBitmap original, ShnappDocument document,
        ImageRect? viewportOverride = null)
    {
        ImageRect viewport = viewportOverride ?? document.Viewport;
        var content = new CanvasRenderTarget(_device, (float)viewport.Width, (float)viewport.Height, 96);
        try
        {
            CanvasDrawingSession? drawing = null;
            try
            {
                drawing = content.CreateDrawingSession();
                drawing.Clear(Colors.Transparent);
                drawing.Transform = Matrix3x2.CreateTranslation(-(float)viewport.X, -(float)viewport.Y);
                ImageRect originalVisible = document.HideOriginalImage
                    ? new ImageRect(0, 0, 0, 0)
                    : document.BaseImageCrop is ImageRect baseCrop
                        ? Intersection(baseCrop, document.OriginalBounds)
                        : document.Crop is ImageRect crop
                            ? Intersection(crop, document.OriginalBounds)
                            : document.OriginalBounds;
                if (originalVisible.Width > 0 && originalVisible.Height > 0)
                {
                    drawing.DrawImage(original, ToRect(originalVisible), ToRect(originalVisible));
                }

                foreach (Annotation annotation in document.OrderedAnnotations)
                {
                    if (annotation.HiddenByCrop)
                    {
                        continue;
                    }

                    if (annotation.IsFlattened && annotation.Kind == AnnotationKind.Redaction &&
                        annotation.RedactionMode != RedactionMode.Solid)
                    {
                        // This PNG already includes the pixels beneath the privacy effect.
                        // Copying them keeps alpha exact on an expanded transparent canvas.
                        CanvasBlend previousBlend = drawing.Blend;
                        drawing.Blend = CanvasBlend.Copy;
                        using (var layer = annotation.VisibilityClip is ImageRect clip
                            ? drawing.CreateLayer(1, ToRect(clip)) : null)
                        {
                            DrawImageAnnotation(drawing, annotation);
                        }
                        drawing.Blend = previousBlend;
                        continue;
                    }

                    if (!annotation.IsFlattened && annotation.Kind == AnnotationKind.Redaction &&
                        annotation.RedactionMode != RedactionMode.Solid)
                    {
                        // An effect reads the layers beneath it. Finish writing that source
                        // before sampling it, then continue later layers on the new target.
                        drawing.Dispose();
                        drawing = null;
                        var effected = new CanvasRenderTarget(_device,
                            (float)viewport.Width, (float)viewport.Height, 96);
                        try
                        {
                            using CanvasDrawingSession effectDrawing = effected.CreateDrawingSession();
                            effectDrawing.Clear(Colors.Transparent);
                            effectDrawing.DrawImage(content);
                            effectDrawing.Transform = Matrix3x2.CreateTranslation(
                                -(float)viewport.X, -(float)viewport.Y);
                            using var layer = annotation.VisibilityClip is ImageRect clip
                                ? effectDrawing.CreateLayer(1, ToRect(clip)) : null;
                            DrawRedactionPreview(effectDrawing, content, annotation,
                                new ImagePoint(viewport.X, viewport.Y));
                        }
                        catch
                        {
                            effected.Dispose();
                            throw;
                        }

                        content.Dispose();
                        content = effected;
                        drawing = content.CreateDrawingSession();
                        drawing.Transform = Matrix3x2.CreateTranslation(
                            -(float)viewport.X, -(float)viewport.Y);
                        continue;
                    }

                    if (annotation.VisibilityClip is ImageRect visibility)
                    {
                        using var layer = drawing.CreateLayer(1, ToRect(visibility));
                        DrawLayer(annotation);
                    }
                    else
                    {
                        DrawLayer(annotation);
                    }

                    void DrawLayer(Annotation item)
                    {
                        if (item.IsFlattened || item.Kind == AnnotationKind.Image)
                        {
                            DrawImageAnnotation(drawing, item);
                        }
                        else
                        {
                            DrawAnnotation(drawing, item);
                        }
                    }
                }
            }
            finally
            {
                drawing?.Dispose();
            }

            if (!document.HasWindowShadow)
            {
                return content;
            }

            var output = new CanvasRenderTarget(_device, (float)viewport.Width + ShadowPadding * 2,
                (float)viewport.Height + ShadowPadding * 2 + 8, 96);
            try
            {
                using (CanvasDrawingSession shadowDrawing = output.CreateDrawingSession())
                {
                    shadowDrawing.Clear(Colors.Transparent);
                    using var shadow = new ShadowEffect
                    {
                        Source = content,
                        BlurAmount = 12,
                        ShadowColor = Color.FromArgb(86, 0, 0, 0),
                    };
                    shadowDrawing.DrawImage(shadow, ShadowPadding, ShadowPadding + 8);
                    shadowDrawing.DrawImage(content, ShadowPadding, ShadowPadding);
                }
                content.Dispose();
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
        catch
        {
            content.Dispose();
            throw;
        }
    }

    /// <summary>Downsamples a shnapp for a compact preview, preserving or cropping its aspect.</summary>
    internal CanvasRenderTarget Thumbnail(CanvasBitmap flattened, int width, int height,
        bool preserveEntireImage = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        double sourceWidth = flattened.Size.Width;
        double sourceHeight = flattened.Size.Height;
        double targetAspect = (double)width / height;
        double croppedWidth = Math.Min(sourceWidth, sourceHeight * targetAspect);
        double croppedHeight = Math.Min(sourceHeight, sourceWidth / targetAspect);
        var source = preserveEntireImage
            ? new Rect(0, 0, sourceWidth, sourceHeight)
            : new Rect((sourceWidth - croppedWidth) / 2,
                (sourceHeight - croppedHeight) / 2, croppedWidth, croppedHeight);
        double scale = Math.Min(width / sourceWidth, height / sourceHeight);
        var destination = preserveEntireImage
            ? new Rect((width - sourceWidth * scale) / 2, (height - sourceHeight * scale) / 2,
                sourceWidth * scale, sourceHeight * scale)
            : new Rect(0, 0, width, height);
        var preview = new CanvasRenderTarget(_device, width, height, 96);
        using CanvasDrawingSession drawing = preview.CreateDrawingSession();
        drawing.Clear(Colors.Transparent);
        drawing.DrawImage(flattened, destination, source);
        return preview;
    }

    /// <summary>Captures one element as pixels at its source coordinates for the frozen background.</summary>
    internal async Task<(string PngBase64, ImageRect Bounds)> RasterizeElementAsync(
        CanvasBitmap original, ShnappDocument document, Annotation annotation)
    {
        if (annotation.IsFlattened || annotation.HiddenByCrop)
        {
            throw new ArgumentException("The element is not editable.", nameof(annotation));
        }

        ImageRect bounds = ElementRasterBounds(annotation, document.Viewport);
        using var raster = new CanvasRenderTarget(_device, (float)bounds.Width, (float)bounds.Height, 96);
        bool samplesBackdrop = annotation.Kind == AnnotationKind.Redaction &&
            annotation.RedactionMode != RedactionMode.Solid;
        using CanvasRenderTarget? composited = samplesBackdrop
            ? Flatten(original, document with
            {
                // A flattened effect becomes part of the background. Sample only
                // pixels already frozen there; editable marks render above it.
                Annotations = [..document.OrderedAnnotations.Where(item => item.IsFlattened), annotation],
                HasWindowShadow = false,
            })
            : null;
        using (CanvasDrawingSession drawing = raster.CreateDrawingSession())
        {
            drawing.Clear(Colors.Transparent);
            drawing.Transform = Matrix3x2.CreateTranslation(-(float)bounds.X, -(float)bounds.Y);
            using var visible = !samplesBackdrop && annotation.VisibilityClip is ImageRect clip
                ? drawing.CreateLayer(1, ToRect(clip)) : null;
            if (composited is not null)
            {
                // Blur and pixelation depend on background pixels. Capture their
                // result so the frozen layer can replace those pixels exactly.
                ImageRect viewport = document.Viewport;
                drawing.DrawImage(composited, ToRect(bounds),
                    new Rect(bounds.X - viewport.X, bounds.Y - viewport.Y,
                        bounds.Width, bounds.Height));
            }
            else if (annotation.Kind == AnnotationKind.Image)
            {
                DrawImageAnnotation(drawing, annotation);
            }
            else
            {
                DrawAnnotation(drawing, annotation);
            }
        }

        using var stream = new InMemoryRandomAccessStream();
        await raster.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        if (stream.Size > 128L * 1024 * 1024)
        {
            throw new InvalidDataException("The flattened element exceeds the PNG size limit.");
        }

        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync(checked((uint)stream.Size));
        byte[] pixels = new byte[checked((int)stream.Size)];
        reader.ReadBytes(pixels);
        return (Convert.ToBase64String(pixels), bounds);
    }

    private ImageRect ElementRasterBounds(Annotation annotation, ImageRect viewport)
    {
        ImageRect area = annotation.Kind switch
        {
            AnnotationKind.Text => FromRect(MeasureTextBounds(annotation),
                Math.Max(4, annotation.FontSize * 0.3)),
            AnnotationKind.Step => new ImageRect(
                annotation.Start.X - annotation.StepDiameter / 2 - 3,
                annotation.Start.Y - annotation.StepDiameter / 2 - 3,
                annotation.StepDiameter + 6, annotation.StepDiameter + 6),
            AnnotationKind.Line or AnnotationKind.Arrow => FromRect(ToRect(annotation.Bounds),
                Math.Max(10, annotation.StrokeWidth * 3.5) +
                Math.Max(1, annotation.StrokeWidth) / 2 + 2),
            AnnotationKind.Rectangle or AnnotationKind.Ellipse when !annotation.HideOutline =>
                FromRect(ToRect(annotation.Bounds), annotation.StrokeWidth / 2 + 3),
            AnnotationKind.Redaction => WholePixelBounds(annotation.Bounds),
            _ => annotation.Bounds,
        };
        area = Intersection(area, viewport);
        if (annotation.VisibilityClip is ImageRect clip)
        {
            area = Intersection(area, clip);
        }

        double left = Math.Floor(area.X);
        double top = Math.Floor(area.Y);
        double right = Math.Ceiling(area.Right);
        double bottom = Math.Ceiling(area.Bottom);
        if (right <= left || bottom <= top)
        {
            throw new InvalidDataException("The selected element has no visible pixels to flatten.");
        }

        return new ImageRect(left, top, right - left, bottom - top);
    }

    private static ImageRect FromRect(Rect area, double padding) => new(
        area.X - padding, area.Y - padding,
        area.Width + padding * 2, area.Height + padding * 2);

    private static ImageRect WholePixelBounds(ImageRect area)
    {
        double left = Math.Floor(area.X);
        double top = Math.Floor(area.Y);
        return new ImageRect(left, top,
            Math.Ceiling(area.Right) - left, Math.Ceiling(area.Bottom) - top);
    }

    /// <summary>Decodes a pasted PNG before a synchronous canvas draw needs it.</summary>
    internal async Task PreloadImageAsync(Annotation annotation)
    {
        string payload = ImagePayload(annotation);
        if (_pastedImages.TryGetValue(annotation.Id, out var existing) &&
            existing.Any(entry => ReferenceEquals(entry.Payload, payload)))
        {
            return;
        }

        CanvasBitmap bitmap = await DecodeImageAsync(payload);
        if (!_pastedImages.TryGetValue(annotation.Id, out var entries))
        {
            entries = [];
            _pastedImages.Add(annotation.Id, entries);
        }

        entries.Add((payload, bitmap));
    }

    /// <summary>Prepares another document without invalidating an editor that is still visible.</summary>
    internal async Task PreloadImagesAsync(ShnappDocument document)
    {
        var prepared = new Dictionary<Guid, (string Payload, CanvasBitmap Bitmap)>();
        try
        {
            foreach (Annotation annotation in document.Annotations.Where(a => a.IsFlattened || a.Kind == AnnotationKind.Image))
            {
                string payload = ImagePayload(annotation);
                if (_pastedImages.TryGetValue(annotation.Id, out var existing) &&
                    existing.Any(entry => ReferenceEquals(entry.Payload, payload)))
                {
                    continue;
                }

                prepared.Add(annotation.Id, (payload, await DecodeImageAsync(payload)));
            }

            // Keep the old document's bitmaps until AppController switches pages and prunes the cache.
            foreach ((Guid id, var entry) in prepared)
            {
                if (!_pastedImages.TryGetValue(id, out var entries))
                {
                    entries = [];
                    _pastedImages.Add(id, entries);
                }

                entries.Add(entry);
            }
        }
        catch
        {
            foreach (var entry in prepared.Values)
            {
                entry.Bitmap.Dispose();
            }

            throw;
        }
    }

    private static string ImagePayload(Annotation annotation)
    {
        if (annotation.Kind != AnnotationKind.Image && !annotation.IsFlattened)
        {
            throw new ArgumentException("Only a pixel-backed element can be preloaded.", nameof(annotation));
        }

        string payload = annotation.ImagePngBase64
            ?? throw new InvalidDataException("This pasted image has no PNG data.");
        int maximumBytes = annotation.IsFlattened ? 128 * 1024 * 1024 : 32 * 1024 * 1024;
        if (payload.Length > ((maximumBytes + 2) / 3) * 4)
        {
            throw new InvalidDataException("This element exceeds the PNG size limit.");
        }

        return payload;
    }

    private async Task<CanvasBitmap> DecodeImageAsync(string payload)
    {
        // PNG size and dimensions are checked by DocumentValidation before a stored document opens.
        byte[] png = Convert.FromBase64String(payload);
        using var memory = new MemoryStream(png, writable: false);
        using IRandomAccessStream stream = memory.AsRandomAccessStream();
        return await CanvasBitmap.LoadAsync(_device, stream, 96);
    }

    /// <summary>Releases decoded images no longer used by the visible document or undo history.</summary>
    internal void RetainPastedImages(ShnappDocument document,
        IEnumerable<Annotation>? historicalElements = null)
    {
        var retained = document.Annotations.Concat(historicalElements ?? [])
            .Where(a => a.IsFlattened || a.Kind == AnnotationKind.Image)
            .ToLookup(a => a.Id);
        foreach ((Guid id, var entries) in _pastedImages.ToArray())
        {
            for (int index = entries.Count - 1; index >= 0; index--)
            {
                if (!retained[id].Any(annotation =>
                    ReferenceEquals(annotation.ImagePngBase64, entries[index].Payload)))
                {
                    entries[index].Bitmap.Dispose();
                    entries.RemoveAt(index);
                }
            }

            if (entries.Count == 0)
            {
                _pastedImages.Remove(id);
            }
        }
    }

    internal void ClearPastedImages()
    {
        foreach (var entries in _pastedImages.Values)
        {
            foreach (var entry in entries)
            {
                entry.Bitmap.Dispose();
            }
        }

        _pastedImages.Clear();
    }

    internal void DiscardPastedImage(Guid id)
    {
        if (_pastedImages.Remove(id, out var entries))
        {
            foreach (var entry in entries)
            {
                entry.Bitmap.Dispose();
            }
        }
    }

    internal void DiscardPastedImagePayload(Guid id, string payload)
    {
        if (!_pastedImages.TryGetValue(id, out var entries))
        {
            return;
        }

        for (int index = entries.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(entries[index].Payload, payload))
            {
                entries[index].Bitmap.Dispose();
                entries.RemoveAt(index);
            }
        }

        if (entries.Count == 0)
        {
            _pastedImages.Remove(id);
        }
    }

    internal void DrawImageAnnotation(CanvasDrawingSession drawing, Annotation annotation)
    {
        if (!_pastedImages.TryGetValue(annotation.Id, out var entries))
        {
            throw new InvalidDataException("This pasted image could not be loaded for rendering.");
        }

        foreach (var entry in entries)
        {
            if (ReferenceEquals(entry.Payload, annotation.ImagePngBase64))
            {
                drawing.DrawImage(entry.Bitmap, ToRect(annotation.IsFlattened
                    ? annotation.RasterizedBounds!.Value : annotation.Bounds));
                return;
            }
        }

        throw new InvalidDataException("This pasted image could not be loaded for rendering.");
    }

    internal static void DrawAnnotation(CanvasDrawingSession drawing, Annotation annotation)
    {
        Vector2 start = new((float)annotation.Start.X, (float)annotation.Start.Y);
        Vector2 end = new((float)annotation.End.X, (float)annotation.End.Y);
        Color stroke = FromArgb(annotation.StrokeArgb);
        Color fill = FromArgb(annotation.FillArgb);
        float width = (float)annotation.StrokeWidth;
        Rect bounds = ToRect(annotation.Bounds);
        switch (annotation.Kind)
        {
            case AnnotationKind.Text:
                if (fill.A != 0)
                {
                    drawing.FillRectangle(TextBounds(annotation), fill);
                }

                using (CanvasTextFormat format = TextFormat(annotation))
                {
                    ConfigureTextFormat(format, annotation);
                    string text = DisplayText(annotation);
                    using var layout = new CanvasTextLayout(drawing, text, format,
                        (float)(annotation.TextBoxWidth > 0 ? annotation.TextBoxWidth : MaximumUnboundedTextExtent),
                        (float)(annotation.TextBoxHeight > 0 ? annotation.TextBoxHeight : MaximumUnboundedTextExtent));
                    using CanvasTypography? typography = DisableKerningIfRequested(layout, annotation, text.Length);
                    if (text.Length > 0 && annotation.TextLetterSpacing != 0)
                    {
                        layout.SetCharacterSpacing(0, text.Length, 0,
                            (float)annotation.TextLetterSpacing, 0);
                    }

                    if (annotation.TextBoxHeight > 0)
                    {
                        // DirectWrite's layout may paint glyph overhang just outside its
                        // requested extent. The layer enforces the user's exact drag box.
                        using var clipped = drawing.CreateLayer(1, TextBounds(annotation));
                        drawing.DrawTextLayout(layout, start.X, start.Y, stroke);
                    }
                    else
                    {
                        drawing.DrawTextLayout(layout, start.X, start.Y, stroke);
                    }
                }

                break;
            case AnnotationKind.Step:
                float radius = (float)annotation.StepDiameter / 2;
                drawing.FillCircle(start, radius, stroke);
                drawing.DrawCircle(start, radius, Colors.White, 2);
                using (CanvasTextFormat format = TextFormat(annotation))
                {
                    string label = StepLabels.Format(annotation.StepNumber, annotation.StepLabelFormat);
                    format.FontSize = Math.Min(format.FontSize,
                        (float)Math.Max(5, (annotation.StepDiameter - 6) / Math.Max(1, label.Length * 0.62)));
                    format.HorizontalAlignment = CanvasHorizontalAlignment.Center;
                    format.VerticalAlignment = CanvasVerticalAlignment.Center;
                    drawing.DrawText(label,
                        new Rect(start.X - radius, start.Y - radius, radius * 2, radius * 2),
                        annotation.StepTextArgb == 0 ? Colors.White : FromArgb(annotation.StepTextArgb), format);
                }

                break;
            case AnnotationKind.Line:
            case AnnotationKind.Arrow:
                if (annotation.LinePattern == LinePattern.Dotted)
                {
                    DrawDottedLine(drawing, start, end, stroke, width);
                }
                else
                {
                    using var style = new CanvasStrokeStyle
                    {
                        StartCap = CanvasCapStyle.Flat,
                        EndCap = CanvasCapStyle.Flat,
                        DashStyle = annotation.LinePattern == LinePattern.Dashed
                            ? CanvasDashStyle.Dash : CanvasDashStyle.Solid,
                    };
                    drawing.DrawLine(start, end, stroke, width, style);
                }

                if (Vector2.Distance(start, end) > 1)
                {
                    Vector2 direction = Vector2.Normalize(end - start);
                    DrawLineEndCap(drawing, start, -direction, width, stroke, annotation.EffectiveStartCap);
                    DrawLineEndCap(drawing, end, direction, width, stroke, annotation.EffectiveEndCap);
                }

                break;
            case AnnotationKind.Rectangle:
                if (fill.A != 0)
                {
                    drawing.FillRectangle(bounds, fill);
                }

                if (!annotation.HideOutline)
                {
                    drawing.DrawRectangle(bounds, stroke, width);
                }
                break;
            case AnnotationKind.Ellipse:
                Vector2 center = new((float)(bounds.X + bounds.Width / 2), (float)(bounds.Y + bounds.Height / 2));
                if (fill.A != 0)
                {
                    drawing.FillEllipse(center, (float)bounds.Width / 2, (float)bounds.Height / 2, fill);
                }

                if (!annotation.HideOutline)
                {
                    drawing.DrawEllipse(center, (float)bounds.Width / 2, (float)bounds.Height / 2, stroke, width);
                }
                break;
            case AnnotationKind.Redaction:
                CanvasAntialiasing previous = drawing.Antialiasing;
                drawing.Antialiasing = CanvasAntialiasing.Aliased;
                double left = Math.Floor(bounds.X);
                double top = Math.Floor(bounds.Y);
                drawing.FillRectangle(new Rect(left, top, Math.Ceiling(bounds.Right) - left,
                    Math.Ceiling(bounds.Bottom) - top), Color.FromArgb(255, 17, 20, 24));
                drawing.Antialiasing = previous;
                break;
        }
    }

    /// <summary>Draws a redaction in source-image coordinates over a separate source bitmap.</summary>
    /// <remarks>The source must not be the target of the active drawing session.</remarks>
    internal void DrawRedactionPreview(CanvasDrawingSession drawing, CanvasBitmap source,
        Annotation annotation, ImagePoint sourceOrigin = default)
    {
        if (annotation.Kind != AnnotationKind.Redaction || annotation.RedactionMode == RedactionMode.Solid)
        {
            DrawAnnotation(drawing, annotation);
            return;
        }

        ImageRect bounds = annotation.Bounds;
        double left = Math.Max(sourceOrigin.X, Math.Floor(bounds.X));
        double top = Math.Max(sourceOrigin.Y, Math.Floor(bounds.Y));
        double right = Math.Min(sourceOrigin.X + source.Size.Width, Math.Ceiling(bounds.Right));
        double bottom = Math.Min(sourceOrigin.Y + source.Size.Height, Math.Ceiling(bounds.Bottom));
        if (right <= left || bottom <= top)
        {
            return;
        }

        var destination = new Rect(left, top, right - left, bottom - top);
        var sourceRect = new Rect(left - sourceOrigin.X, top - sourceOrigin.Y,
            right - left, bottom - top);
        if (annotation.RedactionMode == RedactionMode.Blur)
        {
            using var blur = new GaussianBlurEffect
            {
                Source = source,
                BlurAmount = 12,
                BorderMode = EffectBorderMode.Hard,
            };
            drawing.DrawImage(blur, destination, sourceRect, 1,
                CanvasImageInterpolation.Linear);
            return;
        }

        const double blockSize = 12;
        int reducedWidth = Math.Max(1, (int)Math.Ceiling(destination.Width / blockSize));
        int reducedHeight = Math.Max(1, (int)Math.Ceiling(destination.Height / blockSize));
        using var reduced = new CanvasRenderTarget(_device, reducedWidth, reducedHeight, 96);
        using (CanvasDrawingSession sample = reduced.CreateDrawingSession())
        {
            sample.Clear(Colors.Transparent);
            sample.DrawImage(source, new Rect(0, 0, reducedWidth, reducedHeight), sourceRect,
                1, CanvasImageInterpolation.Linear);
        }

        drawing.DrawImage(reduced, destination, new Rect(0, 0, reducedWidth, reducedHeight),
            1, CanvasImageInterpolation.NearestNeighbor);
    }

    private static void DrawDottedLine(CanvasDrawingSession drawing, Vector2 start, Vector2 end,
        Color color, float width)
    {
        Vector2 delta = end - start;
        float length = delta.Length();
        if (length <= 0)
        {
            return;
        }

        int intervals = Math.Max(1, (int)Math.Floor(length / Math.Max(4, width * 2.6f)));
        float radius = Math.Max(0.75f, width / 2);
        for (int index = 0; index <= intervals; index++)
        {
            drawing.FillCircle(start + delta * (index / (float)intervals), radius, color);
        }
    }

    private static void DrawLineEndCap(CanvasDrawingSession drawing, Vector2 anchor, Vector2 outward,
        float strokeWidth, Color color, LineEndCap cap)
    {
        if (cap == LineEndCap.None)
        {
            return;
        }

        Vector2 perpendicular = new(-outward.Y, outward.X);
        float length = Math.Max(10, strokeWidth * 3.5f);
        float halfWidth = length * 0.45f;
        switch (cap)
        {
            case LineEndCap.Triangle:
                FillPolygon(drawing, color,
                    anchor + perpendicular * halfWidth,
                    anchor + outward * length,
                    anchor - perpendicular * halfWidth);
                break;
            case LineEndCap.OpenArrow:
                drawing.DrawLine(anchor + perpendicular * halfWidth,
                    anchor + outward * length, color, Math.Max(1, strokeWidth));
                drawing.DrawLine(anchor - perpendicular * halfWidth,
                    anchor + outward * length, color, Math.Max(1, strokeWidth));
                break;
            case LineEndCap.Circle:
                float radius = length * 0.38f;
                drawing.FillCircle(anchor + outward * radius, radius, color);
                break;
            case LineEndCap.Diamond:
                float depth = length * 0.48f;
                FillPolygon(drawing, color,
                    anchor,
                    anchor + outward * depth + perpendicular * depth,
                    anchor + outward * (depth * 2),
                    anchor + outward * depth - perpendicular * depth);
                break;
            case LineEndCap.Bar:
                drawing.DrawLine(anchor + perpendicular * halfWidth,
                    anchor - perpendicular * halfWidth, color, Math.Max(1, strokeWidth));
                break;
        }
    }

    private static void FillPolygon(CanvasDrawingSession drawing, Color color, params Vector2[] points)
    {
        using var path = new CanvasPathBuilder(drawing);
        path.BeginFigure(points[0]);
        foreach (Vector2 point in points.AsSpan(1))
        {
            path.AddLine(point);
        }

        path.EndFigure(CanvasFigureLoop.Closed);
        using CanvasGeometry polygon = CanvasGeometry.CreatePath(path);
        drawing.FillGeometry(polygon, color);
    }

    internal Rect MeasureTextBounds(Annotation annotation)
    {
        if (annotation.TextBoxHeight > 0)
        {
            return TextBounds(annotation);
        }

        using CanvasTextFormat format = TextFormat(annotation);
        ConfigureTextFormat(format, annotation);
        string text = DisplayText(annotation);
        using var layout = new CanvasTextLayout(_device,
            text.Length == 0 ? " " : text,
            format, (float)(annotation.TextBoxWidth > 0 ? annotation.TextBoxWidth : MaximumUnboundedTextExtent),
            MaximumUnboundedTextExtent);
        using CanvasTypography? typography = DisableKerningIfRequested(layout, annotation, text.Length);
        if (text.Length > 0 && annotation.TextLetterSpacing != 0)
        {
            layout.SetCharacterSpacing(0, text.Length, 0, (float)annotation.TextLetterSpacing, 0);
        }
        Rect aligned = layout.LayoutBoundsIncludingTrailingWhitespace;
        Rect drawn = layout.DrawBounds;
        double width = annotation.TextBoxWidth > 0
            ? annotation.TextBoxWidth
            : Math.Max(16, Math.Max(aligned.Right, drawn.Right));
        int explicitLines = text.Count(character => character == '\n') + 1;
        double height = Math.Max(annotation.FontSize * 1.5,
            Math.Max(Math.Max(aligned.Bottom, drawn.Bottom),
                explicitLines * annotation.FontSize * 1.25));
        return new Rect(annotation.Start.X, annotation.Start.Y, Math.Ceiling(width), Math.Ceiling(height));
    }

    internal static Rect TextBounds(Annotation annotation)
    {
        if (annotation.TextBoxHeight > 0)
        {
            return new Rect(annotation.Start.X, annotation.Start.Y,
                annotation.TextBoxWidth, annotation.TextBoxHeight);
        }

        if (annotation.End.X > annotation.Start.X && annotation.End.Y > annotation.Start.Y)
        {
            return new Rect(annotation.Start.X, annotation.Start.Y,
                annotation.End.X - annotation.Start.X, annotation.End.Y - annotation.Start.Y);
        }

        int lineCount = 1;
        int longestLine = 0;
        int currentLine = 0;
        foreach (char character in DisplayText(annotation))
        {
            if (character == '\n')
            {
                longestLine = Math.Max(longestLine, currentLine);
                currentLine = 0;
                lineCount++;
            }
            else if (character != '\r')
            {
                currentLine++;
            }
        }

        longestLine = Math.Max(longestLine, currentLine);
        return new Rect(annotation.Start.X, annotation.Start.Y,
            Math.Max(16, longestLine * annotation.FontSize * 0.6),
            lineCount * annotation.FontSize * 1.5);
    }

    internal static CanvasTextFormat TextFormat(Annotation annotation) => new()
    {
        FontFamily = annotation.FontFamily,
        FontSize = (float)annotation.FontSize,
        FontWeight = new FontWeight { Weight = (ushort)annotation.FontWeight },
        FontStyle = annotation.Italic ? FontStyle.Italic : FontStyle.Normal,
    };

    private static void ConfigureTextFormat(CanvasTextFormat format, Annotation annotation)
    {
        bool fixedBox = annotation.TextBoxWidth > 0 && annotation.TextBoxHeight > 0;
        format.WordWrapping = annotation.TextBoxWidth > 0
            ? CanvasWordWrapping.Wrap : CanvasWordWrapping.NoWrap;
        if (!fixedBox)
        {
            return;
        }

        format.HorizontalAlignment = annotation.TextAlignment switch
        {
            TextHorizontalAlignment.Center => CanvasHorizontalAlignment.Center,
            TextHorizontalAlignment.Right => CanvasHorizontalAlignment.Right,
            TextHorizontalAlignment.Justify => CanvasHorizontalAlignment.Justified,
            _ => CanvasHorizontalAlignment.Left,
        };
        if (annotation.TextLineHeight > 0)
        {
            format.LineSpacingMode = CanvasLineSpacingMode.Uniform;
            format.LineSpacing = (float)annotation.TextLineHeight;
            format.LineSpacingBaseline = (float)(annotation.TextLineHeight * 0.8);
        }

        if (annotation.TextTruncation == TextTruncation.EndEllipsis)
        {
            format.TrimmingGranularity = CanvasTextTrimmingGranularity.Character;
            format.TrimmingSign = CanvasTrimmingSign.Ellipsis;
        }
    }

    private static CanvasTypography? DisableKerningIfRequested(CanvasTextLayout layout,
        Annotation annotation, int length)
    {
        if (annotation.TextKerning || length == 0)
        {
            return null;
        }

        var typography = new CanvasTypography();
        typography.AddFeature(CanvasTypographyFeatureName.Kerning, 0);
        layout.SetTypography(0, length, typography);
        return typography;
    }

    internal static string DisplayText(Annotation annotation)
    {
        string text = annotation.Text;
        TextInfo textInfo = CultureInfo.CurrentCulture.TextInfo;
        return annotation.TextTransform switch
        {
            TextTransformMode.Uppercase => textInfo.ToUpper(text),
            TextTransformMode.Lowercase => textInfo.ToLower(text),
            TextTransformMode.TitleCase => textInfo.ToTitleCase(textInfo.ToLower(text)),
            _ => text,
        };
    }

    internal static Rect ToRect(ImageRect rectangle) => new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
    private static ImageRect Intersection(ImageRect first, ImageRect second)
    {
        double left = Math.Max(first.X, second.X);
        double top = Math.Max(first.Y, second.Y);
        double right = Math.Min(first.Right, second.Right);
        double bottom = Math.Min(first.Bottom, second.Bottom);
        return new ImageRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
    internal static Color FromArgb(uint argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    internal static async Task SavePngAtomicAsync(CanvasBitmap bitmap, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await bitmap.SaveAsync(temporary, CanvasBitmapFileFormat.Png).AsTask(cancellationToken);
            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (Exception exception) when (attempt < 5 &&
                    exception is IOException or UnauthorizedAccessException &&
                    (exception.HResult & 0xFFFF) is 5 or 32 or 33 &&
                    File.Exists(temporary) && !Directory.Exists(path))
                {
                    await Task.Delay(20 * (attempt + 1), cancellationToken);
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // Keep the original save failure; ignored temporary PNGs are not library items.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the original save failure if temporary-file cleanup is also denied.
            }
        }
    }
}
