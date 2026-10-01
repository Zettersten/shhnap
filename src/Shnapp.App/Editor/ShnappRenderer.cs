using System.Numerics;
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
    private readonly CanvasDevice _device = device ?? throw new ArgumentNullException(nameof(device));
    private readonly Dictionary<Guid, List<(string Payload, CanvasBitmap Bitmap)>> _pastedImages = [];

    internal CanvasRenderTarget Flatten(CanvasBitmap original, ShnappDocument document,
        ImageRect? viewportOverride = null)
    {
        ImageRect viewport = viewportOverride ?? document.Viewport;
        var content = new CanvasRenderTarget(_device, (float)viewport.Width, (float)viewport.Height, 96);
        try
        {
            using (CanvasDrawingSession drawing = content.CreateDrawingSession())
            {
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

                foreach (Annotation annotation in document.Annotations.Where(a => a.Kind != AnnotationKind.Redaction))
                {
                    if (annotation.HiddenByCrop)
                    {
                        continue;
                    }

                    if (annotation.VisibilityClip is ImageRect clip)
                    {
                        using var layer = drawing.CreateLayer(1, ToRect(clip));
                        DrawLayer(annotation);
                    }
                    else
                    {
                        DrawLayer(annotation);
                    }

                    void DrawLayer(Annotation item)
                    {
                        if (item.Kind == AnnotationKind.Image)
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

            bool hasEffects = document.Annotations.Any(a =>
                a.Kind == AnnotationKind.Redaction && a.RedactionMode != RedactionMode.Solid);
            if (hasEffects)
            {
                var redacted = new CanvasRenderTarget(_device, (float)viewport.Width, (float)viewport.Height, 96);
                try
                {
                    using (CanvasDrawingSession drawing = redacted.CreateDrawingSession())
                    {
                        drawing.Clear(Colors.Transparent);
                        drawing.DrawImage(content);
                        drawing.Transform = Matrix3x2.CreateTranslation(-(float)viewport.X, -(float)viewport.Y);
                        foreach (Annotation annotation in document.Annotations.Where(a =>
                                     a.Kind == AnnotationKind.Redaction && a.RedactionMode != RedactionMode.Solid))
                        {
                            if (annotation.HiddenByCrop)
                            {
                                continue;
                            }

                            if (annotation.VisibilityClip is ImageRect clip)
                            {
                                using var layer = drawing.CreateLayer(1, ToRect(clip));
                                DrawRedactionPreview(drawing, content, annotation,
                                    new ImagePoint(viewport.X, viewport.Y));
                            }
                            else
                            {
                                DrawRedactionPreview(drawing, content, annotation,
                                    new ImagePoint(viewport.X, viewport.Y));
                            }
                        }

                        // Opaque masks are last so another effect can never reveal their source pixels.
                        foreach (Annotation annotation in document.Annotations.Where(a =>
                                     a.Kind == AnnotationKind.Redaction && a.RedactionMode == RedactionMode.Solid))
                        {
                            if (annotation.HiddenByCrop)
                            {
                                continue;
                            }

                            if (annotation.VisibilityClip is ImageRect clip)
                            {
                                using var layer = drawing.CreateLayer(1, ToRect(clip));
                                DrawAnnotation(drawing, annotation);
                            }
                            else
                            {
                                DrawAnnotation(drawing, annotation);
                            }
                        }
                    }

                    content.Dispose();
                    content = redacted;
                }
                catch
                {
                    redacted.Dispose();
                    throw;
                }
            }
            else if (document.Annotations.Any(a => a.Kind == AnnotationKind.Redaction))
            {
                using CanvasDrawingSession drawing = content.CreateDrawingSession();
                drawing.Transform = Matrix3x2.CreateTranslation(-(float)viewport.X, -(float)viewport.Y);
                foreach (Annotation annotation in document.Annotations.Where(a => a.Kind == AnnotationKind.Redaction))
                {
                    if (annotation.HiddenByCrop)
                    {
                        continue;
                    }

                    if (annotation.VisibilityClip is ImageRect clip)
                    {
                        using var layer = drawing.CreateLayer(1, ToRect(clip));
                        DrawAnnotation(drawing, annotation);
                    }
                    else
                    {
                        DrawAnnotation(drawing, annotation);
                    }
                }
            }

            if (!document.HasWindowShadow)
            {
                return content;
            }

            var output = new CanvasRenderTarget(_device, (float)viewport.Width + ShadowPadding * 2,
                (float)viewport.Height + ShadowPadding * 2 + 8, 96);
            try
            {
                using (CanvasDrawingSession drawing = output.CreateDrawingSession())
                {
                    drawing.Clear(Colors.Transparent);
                    using var shadow = new ShadowEffect
                    {
                        Source = content,
                        BlurAmount = 12,
                        ShadowColor = Color.FromArgb(86, 0, 0, 0),
                    };
                    drawing.DrawImage(shadow, ShadowPadding, ShadowPadding + 8);
                    drawing.DrawImage(content, ShadowPadding, ShadowPadding);
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

    internal CanvasRenderTarget Thumbnail(CanvasBitmap flattened)
    {
        double scale = Math.Min(1, Math.Min(384 / flattened.Size.Width, 256 / flattened.Size.Height));
        float width = (float)Math.Max(1, Math.Round(flattened.Size.Width * scale));
        float height = (float)Math.Max(1, Math.Round(flattened.Size.Height * scale));
        var preview = new CanvasRenderTarget(_device, width, height, 96);
        using CanvasDrawingSession drawing = preview.CreateDrawingSession();
        drawing.Clear(Colors.Transparent);
        drawing.DrawImage(flattened, new Rect(0, 0, width, height), new Rect(0, 0, flattened.Size.Width, flattened.Size.Height));
        return preview;
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
            foreach (Annotation annotation in document.Annotations.Where(a => a.Kind == AnnotationKind.Image))
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
        if (annotation.Kind != AnnotationKind.Image)
        {
            throw new ArgumentException("Only a pasted image can be preloaded.", nameof(annotation));
        }

        string payload = annotation.ImagePngBase64
            ?? throw new InvalidDataException("This pasted image has no PNG data.");
        if (payload.Length > ((32 * 1024 * 1024 + 2) / 3) * 4)
        {
            throw new InvalidDataException("This pasted image exceeds the 32 MiB PNG limit.");
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

    /// <summary>Releases decoded images that are no longer used after switching documents.</summary>
    internal void RetainPastedImages(ShnappDocument document)
    {
        var retained = document.Annotations.Where(a => a.Kind == AnnotationKind.Image)
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
                drawing.DrawImage(entry.Bitmap, ToRect(annotation.Bounds));
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
                    drawing.DrawText(annotation.Text, start, stroke, format);
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

    internal static Rect TextBounds(Annotation annotation)
    {
        int lineCount = 1;
        int longestLine = 0;
        int currentLine = 0;
        foreach (char character in annotation.Text)
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
