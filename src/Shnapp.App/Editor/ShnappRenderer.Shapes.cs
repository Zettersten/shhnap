using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Shnapp.Core;

namespace Shnapp.App.Editor;

internal sealed partial class ShnappRenderer
{
    private static void DrawBasicShape(CanvasDrawingSession drawing, Annotation annotation)
    {
        if (annotation.Bounds.Width <= 0 || annotation.Bounds.Height <= 0)
        {
            return;
        }

        ImagePoint[] outline = BasicShapeGeometry.Outline(annotation.ShapeKind, annotation.Bounds);
        using var path = new CanvasPathBuilder(drawing);
        path.BeginFigure(new Vector2((float)outline[0].X, (float)outline[0].Y));
        foreach (ImagePoint point in outline.AsSpan(1))
        {
            path.AddLine(new Vector2((float)point.X, (float)point.Y));
        }

        bool closed = BasicShapeGeometry.IsClosed(annotation.ShapeKind);
        path.EndFigure(closed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
        using CanvasGeometry geometry = CanvasGeometry.CreatePath(path);
        if (closed && (annotation.FillArgb >> 24) != 0)
        {
            drawing.FillGeometry(geometry, FromArgb(annotation.FillArgb));
        }

        if (!annotation.HideOutline)
        {
            drawing.DrawGeometry(geometry, FromArgb(annotation.StrokeArgb),
                (float)annotation.StrokeWidth);
        }
    }
}
