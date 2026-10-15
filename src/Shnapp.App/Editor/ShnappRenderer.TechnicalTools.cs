using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Shnapp.Core;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace Shnapp.App.Editor;

internal sealed partial class ShnappRenderer
{
    private static void DrawCalloutAnnotation(CanvasDrawingSession drawing, Annotation annotation)
    {
        ImageRect box = new(annotation.End.X, annotation.End.Y,
            annotation.TextBoxWidth, annotation.TextBoxHeight);
        if (box.Width <= 0 || box.Height <= 0)
        {
            return;
        }

        Color accent = FromArgb(annotation.StrokeArgb);
        Color textColor = FromArgb(annotation.CalloutTextArgb == 0
            ? 0xFF1F2937 : annotation.CalloutTextArgb);
        float stroke = (float)Math.Max(1, annotation.StrokeWidth);
        ImagePoint anchor = annotation.Start;
        ImagePoint imageEdge = annotation.CalloutSide switch
        {
            CalloutSide.Left => new(Math.Min(anchor.X, box.Right + 36), anchor.Y),
            CalloutSide.Right => new(Math.Max(anchor.X, box.X - 36), anchor.Y),
            CalloutSide.Top => new(anchor.X, Math.Min(anchor.Y, box.Bottom + 36)),
            _ => new(anchor.X, Math.Max(anchor.Y, box.Y - 36)),
        };
        ImagePoint entry = annotation.CalloutSide switch
        {
            CalloutSide.Left => new(box.Right, Math.Clamp(anchor.Y, box.Y + 18, box.Bottom - 18)),
            CalloutSide.Right => new(box.X, Math.Clamp(anchor.Y, box.Y + 18, box.Bottom - 18)),
            CalloutSide.Top => new(Math.Clamp(anchor.X, box.X + 18, box.Right - 18), box.Bottom),
            _ => new(Math.Clamp(anchor.X, box.X + 18, box.Right - 18), box.Y),
        };

        drawing.DrawLine((float)anchor.X, (float)anchor.Y,
            (float)imageEdge.X, (float)imageEdge.Y, accent, stroke);
        drawing.DrawLine((float)imageEdge.X, (float)imageEdge.Y,
            (float)entry.X, (float)entry.Y, accent, stroke);
        drawing.FillCircle((float)anchor.X, (float)anchor.Y, 11, accent);
        drawing.DrawCircle((float)anchor.X, (float)anchor.Y, 11, Color.FromArgb(255, 255, 255, 255), 1.5f);

        using var numberFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 12,
            FontWeight = new FontWeight { Weight = 700 },
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        string number = annotation.CalloutNumber > 0
            ? annotation.CalloutNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "?";
        numberFormat.FontSize = number.Length > 2 ? 8 : number.Length > 1 ? 10 : 12;
        drawing.DrawText(number, new Rect(anchor.X - 11, anchor.Y - 11, 22, 22),
            Color.FromArgb(255, 255, 255, 255), numberFormat);

        Rect frame = ToRect(box);
        drawing.FillRoundedRectangle(frame, 8, 8, Color.FromArgb(250, 255, 255, 255));
        drawing.DrawRoundedRectangle(frame, 8, 8, accent, stroke);
        using var headingFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 12,
            FontWeight = new FontWeight { Weight = 700 },
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        drawing.DrawText(number.PadLeft(2, '0'),
            new Rect(box.X + 18, box.Y + 9, box.Width - 36, 20), accent, headingFormat);
        drawing.DrawLine((float)(box.X + 18), (float)(box.Y + 35),
            (float)(box.Right - 18), (float)(box.Y + 35),
            Color.FromArgb(90, accent.R, accent.G, accent.B), 1);

        using CanvasTextFormat format = TextFormat(annotation);
        ConfigureTextFormat(format, annotation);
        string text = DisplayText(annotation);
        using var layout = new CanvasTextLayout(drawing, text, format,
            (float)Math.Max(1, box.Width - 36), (float)Math.Max(1, box.Height - 56));
        using CanvasTypography? typography = DisableKerningIfRequested(layout, annotation, text.Length);
        if (annotation.TextLetterSpacing > 0 && text.Length > 0)
        {
            layout.SetCharacterSpacing(0, text.Length, 0,
                (float)annotation.TextLetterSpacing, 0);
        }
        ApplyTextStyleRuns(layout, annotation, text.Length);

        using var clip = drawing.CreateLayer(1, new Rect(box.X + 18, box.Y + 43,
            Math.Max(1, box.Width - 36), Math.Max(1, box.Height - 54)));
        drawing.DrawTextLayout(layout, (float)(box.X + 18), (float)(box.Y + 43), textColor);
    }

    /// <summary>Draws a lens preview using the same privacy-safe source as exported pixels.</summary>
    internal void DrawMagnifyAnnotation(CanvasDrawingSession drawing, CanvasBitmap original,
        Annotation annotation, ShnappDocument document)
    {
        DrawMagnifyFromSource(drawing, GetMagnifierPreviewSource(original, document),
            annotation, document);
    }

    /// <summary>Draws sampled pixels without recursively magnifying the lens itself.</summary>
    private void DrawMagnifyFromSource(CanvasDrawingSession drawing, CanvasBitmap source,
        Annotation annotation, ShnappDocument document)
    {
        ImageRect bounds = annotation.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        Rect destination = ToRect(bounds);
        using CanvasGeometry lens = annotation.MagnifyShape == MagnifyShape.Rectangle
            ? CanvasGeometry.CreateRectangle(_device, destination)
            : CanvasGeometry.CreateEllipse(_device,
                new Vector2((float)(bounds.X + bounds.Width / 2),
                    (float)(bounds.Y + bounds.Height / 2)),
                (float)(bounds.Width / 2), (float)(bounds.Height / 2));
        double zoom = annotation.MagnifyZoom > 0 ? annotation.MagnifyZoom : 2;
        double sourceWidth = bounds.Width / zoom;
        double sourceHeight = bounds.Height / zoom;
        double sourceX = bounds.X + bounds.Width / 2 - sourceWidth / 2;
        double sourceY = bounds.Y + bounds.Height / 2 - sourceHeight / 2;
        ImageRect visible = document.HideOriginalImage
            ? new ImageRect(0, 0, 0, 0)
            : document.BaseImageCrop ?? document.Crop ?? document.OriginalBounds;
        double left = Math.Max(0, Math.Max(visible.X, sourceX));
        double top = Math.Max(0, Math.Max(visible.Y, sourceY));
        double right = Math.Min(source.Size.Width, Math.Min(visible.Right, sourceX + sourceWidth));
        double bottom = Math.Min(source.Size.Height, Math.Min(visible.Bottom, sourceY + sourceHeight));

        using (drawing.CreateLayer(1, lens))
        {
            drawing.FillGeometry(lens, Color.FromArgb(255, 255, 255, 255));
            if (right > left && bottom > top)
            {
                drawing.DrawImage(source,
                    new Rect(bounds.X + (left - sourceX) * zoom,
                        bounds.Y + (top - sourceY) * zoom,
                        (right - left) * zoom, (bottom - top) * zoom),
                    new Rect(left, top, right - left, bottom - top),
                    1, CanvasImageInterpolation.HighQualityCubic);
            }
        }

        if (!annotation.HideOutline)
        {
            drawing.DrawGeometry(lens, FromArgb(annotation.StrokeArgb),
                (float)Math.Max(1, annotation.StrokeWidth));
        }
    }
}
