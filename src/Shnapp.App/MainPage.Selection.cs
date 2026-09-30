using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.Foundation;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private enum ResizeHandle
    {
        None,
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left,
        Start,
        End,
    }

    private readonly record struct SelectionHandle(ResizeHandle Kind, ImagePoint Position);

    private Rect SelectionBounds(Annotation annotation) => annotation.Kind switch
    {
        AnnotationKind.Text => ShnappRenderer.TextBounds(annotation),
        AnnotationKind.Step => new Rect(annotation.Start.X - annotation.StepDiameter / 2,
            annotation.Start.Y - annotation.StepDiameter / 2, annotation.StepDiameter, annotation.StepDiameter),
        _ => ShnappRenderer.ToRect(annotation.Bounds),
    };

    private bool HitAnnotation(Annotation annotation, ImagePoint position)
    {
        if (annotation.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            double dx = annotation.End.X - annotation.Start.X;
            double dy = annotation.End.Y - annotation.Start.Y;
            double lengthSquared = dx * dx + dy * dy;
            double fraction = lengthSquared < 1 ? 0 : Math.Clamp(
                ((position.X - annotation.Start.X) * dx + (position.Y - annotation.Start.Y) * dy) / lengthSquared, 0, 1);
            double distanceX = position.X - annotation.Start.X - fraction * dx;
            double distanceY = position.Y - annotation.Start.Y - fraction * dy;
            double distance = Math.Sqrt(distanceX * distanceX + distanceY * distanceY);
            return distance <= Math.Max(7 / _scale, annotation.StrokeWidth / 2 + 3 / _scale);
        }

        if (annotation.Kind == AnnotationKind.Step)
        {
            double distanceX = position.X - annotation.Start.X;
            double distanceY = position.Y - annotation.Start.Y;
            return Math.Sqrt(distanceX * distanceX + distanceY * distanceY)
                <= annotation.StepDiameter / 2 + 5 / _scale;
        }

        return HitBounds(annotation).Contains(new Point(position.X, position.Y));
    }

    private SelectionHandle[] HandlesFor(Annotation annotation)
    {
        if (annotation.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            return [new(ResizeHandle.Start, annotation.Start), new(ResizeHandle.End, annotation.End)];
        }

        Rect bounds = SelectionBounds(annotation);
        if (annotation.Kind == AnnotationKind.Step)
        {
            return [new(ResizeHandle.Right, new(bounds.Right, bounds.Y + bounds.Height / 2))];
        }

        if (annotation.Kind == AnnotationKind.Text)
        {
            return [new(ResizeHandle.BottomRight, new(bounds.Right, bounds.Bottom))];
        }

        double midX = bounds.X + bounds.Width / 2;
        double midY = bounds.Y + bounds.Height / 2;
        if (bounds.Width * _scale < 32 || bounds.Height * _scale < 32)
        {
            return [
                new(ResizeHandle.TopLeft, new(bounds.X, bounds.Y)),
                new(ResizeHandle.TopRight, new(bounds.Right, bounds.Y)),
                new(ResizeHandle.BottomRight, new(bounds.Right, bounds.Bottom)),
                new(ResizeHandle.BottomLeft, new(bounds.X, bounds.Bottom)),
            ];
        }

        return [
            new(ResizeHandle.TopLeft, new(bounds.X, bounds.Y)),
            new(ResizeHandle.Top, new(midX, bounds.Y)),
            new(ResizeHandle.TopRight, new(bounds.Right, bounds.Y)),
            new(ResizeHandle.Right, new(bounds.Right, midY)),
            new(ResizeHandle.BottomRight, new(bounds.Right, bounds.Bottom)),
            new(ResizeHandle.Bottom, new(midX, bounds.Bottom)),
            new(ResizeHandle.BottomLeft, new(bounds.X, bounds.Bottom)),
            new(ResizeHandle.Left, new(bounds.X, midY)),
        ];
    }

    private ResizeHandle HitResizeHandle(Annotation annotation, Point canvasPosition)
    {
        ResizeHandle nearest = ResizeHandle.None;
        double nearestDistanceSquared = 9 * 9;
        Matrix3x2 transform = ImageTransform();
        foreach (SelectionHandle handle in HandlesFor(annotation))
        {
            Vector2 center = Vector2.Transform(new((float)handle.Position.X, (float)handle.Position.Y), transform);
            double dx = canvasPosition.X - center.X;
            double dy = canvasPosition.Y - center.Y;
            double distanceSquared = dx * dx + dy * dy;
            if (distanceSquared <= nearestDistanceSquared)
            {
                nearest = handle.Kind;
                nearestDistanceSquared = distanceSquared;
            }
        }

        return nearest;
    }

    private void DrawSelection(CanvasDrawingSession drawing, Annotation annotation)
    {
        var blue = ShnappRenderer.FromArgb(0xFF0A84FF);
        float outer = (float)(3 / _scale);
        float inner = (float)(1 / _scale);
        if (annotation.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            Vector2 start = new((float)annotation.Start.X, (float)annotation.Start.Y);
            Vector2 end = new((float)annotation.End.X, (float)annotation.End.Y);
            drawing.DrawLine(start, end, Colors.White, outer);
            drawing.DrawLine(start, end, blue, inner);
        }
        else
        {
            Rect bounds = SelectionBounds(annotation);
            drawing.DrawRectangle(bounds, Colors.White, outer);
            drawing.DrawRectangle(bounds, blue, inner);
        }

        float radius = (float)(5 / _scale);
        foreach (SelectionHandle handle in HandlesFor(annotation))
        {
            Rect square = new(handle.Position.X - radius, handle.Position.Y - radius, radius * 2, radius * 2);
            drawing.FillRectangle(square, Colors.White);
            drawing.DrawRectangle(square, blue, (float)(2 / _scale));
        }
    }

    private Annotation ResizeAnnotation(Annotation original, ResizeHandle handle, ImagePoint point)
    {
        ImageRect viewport = _editor!.Current.Viewport;
        if (original.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            ImagePoint start = handle == ResizeHandle.Start ? point : original.Start;
            ImagePoint end = handle == ResizeHandle.End ? point : original.End;
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            return Math.Sqrt(dx * dx + dy * dy) < 2
                ? original
                : original with { Start = start, End = end };
        }

        if (original.Kind == AnnotationKind.Step)
        {
            double available = 2 * Math.Min(
                Math.Min(original.Start.X - viewport.X, viewport.Right - original.Start.X),
                Math.Min(original.Start.Y - viewport.Y, viewport.Bottom - original.Start.Y));
            double diameter = Math.Clamp(2 * Math.Abs(point.X - original.Start.X), 16, Math.Max(16, Math.Min(120, available)));
            double fontSize = Math.Clamp(original.FontSize * diameter / original.StepDiameter, 8, 144);
            return original with { StepDiameter = diameter, FontSize = fontSize };
        }

        if (original.Kind == AnnotationKind.Text)
        {
            Rect bounds = SelectionBounds(original);
            double scaleX = (point.X - bounds.X) / Math.Max(1, bounds.Width);
            double scaleY = (point.Y - bounds.Y) / Math.Max(1, bounds.Height);
            double scale = Math.Abs(scaleX - 1) >= Math.Abs(scaleY - 1) ? scaleX : scaleY;
            double widthPerFontPixel = Math.Max(1, original.Text.Length * 0.6);
            double maxFontSize = Math.Min(144, Math.Min(
                (viewport.Right - original.Start.X) / widthPerFontPixel,
                (viewport.Bottom - original.Start.Y) / 1.5));
            double fontSize = Math.Clamp(original.FontSize * scale, 8, Math.Max(8, maxFontSize));
            return original with { FontSize = fontSize };
        }

        Rect shape = SelectionBounds(original);
        if ((original.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse) &&
            shape.Width >= 2 && shape.Height >= 2 && Math.Abs(shape.Width - shape.Height) <= 0.5)
        {
            return ResizeProportionalShape(original, shape, handle, point, viewport);
        }

        double minWidth = Math.Min(2, viewport.Width);
        double minHeight = Math.Min(2, viewport.Height);
        double left = shape.X;
        double top = shape.Y;
        double right = shape.Right;
        double bottom = shape.Bottom;
        if (handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft)
        {
            left = Math.Clamp(point.X, viewport.X, Math.Max(viewport.X, right - minWidth));
        }
        else if (handle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight)
        {
            right = Math.Clamp(point.X, Math.Min(viewport.Right, left + minWidth), viewport.Right);
        }

        if (handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight)
        {
            top = Math.Clamp(point.Y, viewport.Y, Math.Max(viewport.Y, bottom - minHeight));
        }
        else if (handle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight)
        {
            bottom = Math.Clamp(point.Y, Math.Min(viewport.Bottom, top + minHeight), viewport.Bottom);
        }

        return original with { Start = new(left, top), End = new(right, bottom) };
    }

    private static Annotation ResizeProportionalShape(Annotation original, Rect shape,
        ResizeHandle handle, ImagePoint point, ImageRect viewport)
    {
        double left = shape.X;
        double top = shape.Y;
        double right = shape.Right;
        double bottom = shape.Bottom;
        double centerX = (left + right) / 2;
        double centerY = (top + bottom) / 2;
        double desired;
        double maximum;

        switch (handle)
        {
            case ResizeHandle.Left:
                desired = right - point.X;
                maximum = Math.Min(right - viewport.X,
                    2 * Math.Min(centerY - viewport.Y, viewport.Bottom - centerY));
                break;
            case ResizeHandle.Right:
                desired = point.X - left;
                maximum = Math.Min(viewport.Right - left,
                    2 * Math.Min(centerY - viewport.Y, viewport.Bottom - centerY));
                break;
            case ResizeHandle.Top:
                desired = bottom - point.Y;
                maximum = Math.Min(bottom - viewport.Y,
                    2 * Math.Min(centerX - viewport.X, viewport.Right - centerX));
                break;
            case ResizeHandle.Bottom:
                desired = point.Y - top;
                maximum = Math.Min(viewport.Bottom - top,
                    2 * Math.Min(centerX - viewport.X, viewport.Right - centerX));
                break;
            default:
                bool leftHandle = handle is ResizeHandle.TopLeft or ResizeHandle.BottomLeft;
                bool topHandle = handle is ResizeHandle.TopLeft or ResizeHandle.TopRight;
                double anchorX = leftHandle ? right : left;
                double anchorY = topHandle ? bottom : top;
                double directionX = leftHandle ? -1 : 1;
                double directionY = topHandle ? -1 : 1;
                double extentX = directionX * (point.X - anchorX);
                double extentY = directionY * (point.Y - anchorY);
                double originalSide = (shape.Width + shape.Height) / 2;
                desired = Math.Abs(extentX - originalSide) >= Math.Abs(extentY - originalSide)
                    ? extentX : extentY;
                maximum = Math.Min(leftHandle ? anchorX - viewport.X : viewport.Right - anchorX,
                    topHandle ? anchorY - viewport.Y : viewport.Bottom - anchorY);
                if (maximum < 2)
                {
                    return original;
                }

                double cornerSide = Math.Clamp(desired, 2, maximum);
                left = leftHandle ? anchorX - cornerSide : anchorX;
                right = leftHandle ? anchorX : anchorX + cornerSide;
                top = topHandle ? anchorY - cornerSide : anchorY;
                bottom = topHandle ? anchorY : anchorY + cornerSide;
                return original with { Start = new(left, top), End = new(right, bottom) };
        }

        if (maximum < 2)
        {
            return original;
        }

        double side = Math.Clamp(desired, 2, maximum);
        switch (handle)
        {
            case ResizeHandle.Left:
                left = right - side;
                top = centerY - side / 2;
                bottom = centerY + side / 2;
                break;
            case ResizeHandle.Right:
                right = left + side;
                top = centerY - side / 2;
                bottom = centerY + side / 2;
                break;
            case ResizeHandle.Top:
                top = bottom - side;
                left = centerX - side / 2;
                right = centerX + side / 2;
                break;
            case ResizeHandle.Bottom:
                bottom = top + side;
                left = centerX - side / 2;
                right = centerX + side / 2;
                break;
        }

        return original with { Start = new(left, top), End = new(right, bottom) };
    }

    private static double ClampMovement(double change, double minimum, double maximum) =>
        minimum > maximum ? 0 : Math.Clamp(change, minimum, maximum);

    private void EnsureDragBase()
    {
        if (_dragBase is not null || _moving is null || _editor is null || _original is null || _controller is null)
        {
            return;
        }

        ShnappDocument withoutSelection = _editor.Current with
        {
            Annotations = _editor.Current.Annotations.Remove(_moving),
        };
        _dragBase = _controller.Renderer.Flatten(_original, withoutSelection);
    }

    private void DisposeDragBase()
    {
        _dragBase?.Dispose();
        _dragBase = null;
    }
}
