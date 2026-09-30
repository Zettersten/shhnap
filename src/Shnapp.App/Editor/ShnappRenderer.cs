using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Shnapp.Core;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace Shnapp.App.Editor;

internal sealed class ShnappRenderer(CanvasDevice device)
{
    internal const int ShadowPadding = 32;
    private readonly CanvasDevice _device = device ?? throw new ArgumentNullException(nameof(device));

    internal CanvasRenderTarget Flatten(CanvasBitmap original, ShnappDocument document)
    {
        ImageRect viewport = document.Viewport;
        var content = new CanvasRenderTarget(_device, (float)viewport.Width, (float)viewport.Height, 96);
        try
        {
            using (CanvasDrawingSession drawing = content.CreateDrawingSession())
            {
                drawing.Clear(Colors.Transparent);
                drawing.DrawImage(original, new Rect(0, 0, viewport.Width, viewport.Height), ToRect(viewport));
                drawing.Transform = Matrix3x2.CreateTranslation(-(float)viewport.X, -(float)viewport.Y);
                foreach (Annotation annotation in document.Annotations.Where(a => a.Kind != AnnotationKind.Redaction))
                {
                    DrawAnnotation(drawing, annotation);
                }

                foreach (Annotation annotation in document.Annotations.Where(a => a.Kind == AnnotationKind.Redaction))
                {
                    DrawAnnotation(drawing, annotation);
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
                    format.HorizontalAlignment = CanvasHorizontalAlignment.Center;
                    format.VerticalAlignment = CanvasVerticalAlignment.Center;
                    drawing.DrawText(annotation.StepNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        new Rect(start.X - radius, start.Y - radius, radius * 2, radius * 2), Colors.White, format);
                }

                break;
            case AnnotationKind.Line:
            case AnnotationKind.Arrow:
                using (var style = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round })
                {
                    drawing.DrawLine(start, end, stroke, width, style);
                }

                if (annotation.Kind == AnnotationKind.Arrow && Vector2.Distance(start, end) > 1)
                {
                    Vector2 direction = Vector2.Normalize(end - start);
                    Vector2 perpendicular = new(-direction.Y, direction.X);
                    float head = Math.Max(10, width * 3.5f);
                    using var path = new CanvasPathBuilder(drawing);
                    path.BeginFigure(end);
                    path.AddLine(end - direction * head + perpendicular * head * 0.45f);
                    path.AddLine(end - direction * head - perpendicular * head * 0.45f);
                    path.EndFigure(CanvasFigureLoop.Closed);
                    using CanvasGeometry triangle = CanvasGeometry.CreatePath(path);
                    drawing.FillGeometry(triangle, stroke);
                }

                break;
            case AnnotationKind.Rectangle:
                if (fill.A != 0)
                {
                    drawing.FillRectangle(bounds, fill);
                }

                drawing.DrawRectangle(bounds, stroke, width);
                break;
            case AnnotationKind.Ellipse:
                Vector2 center = new((float)(bounds.X + bounds.Width / 2), (float)(bounds.Y + bounds.Height / 2));
                if (fill.A != 0)
                {
                    drawing.FillEllipse(center, (float)bounds.Width / 2, (float)bounds.Height / 2, fill);
                }

                drawing.DrawEllipse(center, (float)bounds.Width / 2, (float)bounds.Height / 2, stroke, width);
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

    internal static Rect TextBounds(Annotation annotation) => new(annotation.Start.X, annotation.Start.Y,
        Math.Max(16, annotation.Text.Length * annotation.FontSize * 0.6), annotation.FontSize * 1.5);

    internal static CanvasTextFormat TextFormat(Annotation annotation) => new()
    {
        FontFamily = annotation.FontFamily,
        FontSize = (float)annotation.FontSize,
        FontWeight = new FontWeight { Weight = (ushort)annotation.FontWeight },
        FontStyle = annotation.Italic ? FontStyle.Italic : FontStyle.Normal,
    };

    internal static Rect ToRect(ImageRect rectangle) => new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
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
