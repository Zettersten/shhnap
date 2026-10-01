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
        if (annotation.HiddenByCrop ||
            annotation.VisibilityClip is ImageRect clip && !clip.Contains(position))
        {
            return false;
        }

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
            if (distance <= Math.Max(7 / _scale, annotation.StrokeWidth / 2 + 3 / _scale))
            {
                return true;
            }

            double capRadius = Math.Max(10, annotation.StrokeWidth * 3.5) + 5 / _scale;
            return (annotation.EffectiveStartCap != LineEndCap.None &&
                    Math.Sqrt(Math.Pow(position.X - annotation.Start.X, 2) +
                        Math.Pow(position.Y - annotation.Start.Y, 2)) <= capRadius) ||
                (annotation.EffectiveEndCap != LineEndCap.None &&
                    Math.Sqrt(Math.Pow(position.X - annotation.End.X, 2) +
                        Math.Pow(position.Y - annotation.End.Y, 2)) <= capRadius);
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
            if (annotation.TextBoxWidth > 0 && annotation.TextBoxHeight > 0)
            {
                return [
                    new(ResizeHandle.TopLeft, new(bounds.X, bounds.Y)),
                    new(ResizeHandle.TopRight, new(bounds.Right, bounds.Y)),
                    new(ResizeHandle.BottomRight, new(bounds.Right, bounds.Bottom)),
                    new(ResizeHandle.BottomLeft, new(bounds.X, bounds.Bottom)),
                ];
            }

            return [new(ResizeHandle.BottomRight, new(bounds.Right, bounds.Bottom))];
        }

        if (annotation.Kind == AnnotationKind.Image)
        {
            if (VisibleImageBounds(annotation) is not ImageRect visible)
            {
                return [];
            }

            return [
                new(ResizeHandle.TopLeft, new(visible.X, visible.Y)),
                new(ResizeHandle.TopRight, new(visible.Right, visible.Y)),
                new(ResizeHandle.BottomRight, new(visible.Right, visible.Bottom)),
                new(ResizeHandle.BottomLeft, new(visible.X, visible.Bottom)),
            ];
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

    private ImageRect? VisibleImageBounds(Annotation annotation)
    {
        ImageRect bounds = annotation.Bounds;
        ImageRect viewport = _editor!.Current.Viewport;
        double left = Math.Max(bounds.X, viewport.X);
        double top = Math.Max(bounds.Y, viewport.Y);
        double right = Math.Min(bounds.Right, viewport.Right);
        double bottom = Math.Min(bounds.Bottom, viewport.Bottom);
        if (annotation.VisibilityClip is ImageRect clip)
        {
            left = Math.Max(left, clip.X);
            top = Math.Max(top, clip.Y);
            right = Math.Min(right, clip.Right);
            bottom = Math.Min(bottom, clip.Bottom);
        }

        return right > left && bottom > top
            ? new ImageRect(left, top, right - left, bottom - top)
            : null;
    }

    private static ImagePoint Corner(ImageRect bounds, ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft => new(bounds.X, bounds.Y),
        ResizeHandle.TopRight => new(bounds.Right, bounds.Y),
        ResizeHandle.BottomRight => new(bounds.Right, bounds.Bottom),
        _ => new(bounds.X, bounds.Bottom),
    };

    private ResizeHandle HitResizeHandle(Annotation annotation, Point canvasPosition)
    {
        ResizeHandle nearest = ResizeHandle.None;
        double nearestDistanceSquared = 9 * 9;
        Matrix3x2 transform = ImageTransform();
        foreach (SelectionHandle handle in HandlesFor(annotation))
        {
            if (annotation.HiddenByCrop ||
                annotation.VisibilityClip is ImageRect clip && !clip.Contains(handle.Position))
            {
                continue;
            }

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
        if (annotation.HiddenByCrop)
        {
            return;
        }

        if (annotation.Kind == AnnotationKind.Image && VisibleImageBounds(annotation) is null)
        {
            return;
        }

        using var clipLayer = annotation.VisibilityClip is ImageRect clip
            ? drawing.CreateLayer(1, ShnappRenderer.ToRect(clip))
            : null;
        var blue = ShnappRenderer.FromArgb(0xFF0A84FF);
        float outer = (float)(3 / _scale);
        float inner = (float)(1 / _scale);
        // Endpoint handles show a selected line without hiding its chosen stroke or color.
        if (annotation.Kind is not AnnotationKind.Line and not AnnotationKind.Arrow)
        {
            Rect bounds = annotation.Kind == AnnotationKind.Image &&
                VisibleImageBounds(annotation) is ImageRect visible
                ? ShnappRenderer.ToRect(visible)
                : SelectionBounds(annotation);
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

    private Annotation ResizeAnnotation(Annotation original, ResizeHandle handle, ImagePoint point,
        bool maintainProportions)
    {
        ImageRect viewport = _editor!.Current.Viewport;
        if (original.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            if (maintainProportions)
            {
                return ResizeLineProportional(original, handle, point, viewport);
            }

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
            if (original.TextBoxWidth > 0 && original.TextBoxHeight > 0)
            {
                return ResizeFixedText(original, handle, point, maintainProportions);
            }

            Rect bounds = SelectionBounds(original);
            double scaleX = (point.X - bounds.X) / Math.Max(1, bounds.Width);
            double scaleY = (point.Y - bounds.Y) / Math.Max(1, bounds.Height);
            if (maintainProportions)
            {
                double scale = Math.Abs(scaleX - 1) >= Math.Abs(scaleY - 1) ? scaleX : scaleY;
                double fontSize = Math.Clamp(original.FontSize * scale, 8, 144);
                double width = original.TextBoxWidth > 0
                    ? Math.Clamp(original.TextBoxWidth * scale, 48, 12000)
                    : 0;
                return MeasureTextAnnotation(original with { FontSize = fontSize, TextBoxWidth = width });
            }

            double fixedWidth = Math.Clamp(
                (original.TextBoxWidth > 0 ? original.TextBoxWidth : bounds.Width) * scaleX, 48, 12000);
            double fixedHeight = Math.Clamp(bounds.Height * scaleY, 24, 12000);
            return MeasureTextAnnotation(original with
            {
                TextBoxWidth = fixedWidth,
                TextBoxHeight = fixedHeight,
            });
        }

        if (original.Kind == AnnotationKind.Image)
        {
            if (VisibleImageBounds(original) is ImageRect visible)
            {
                ImagePoint fullCorner = Corner(original.Bounds, handle);
                ImagePoint visibleCorner = Corner(visible, handle);
                point = new ImagePoint(point.X + fullCorner.X - visibleCorner.X,
                    point.Y + fullCorner.Y - visibleCorner.Y);
            }

            return ResizeImage(original, handle, point, maintainProportions);
        }

        Rect shape = SelectionBounds(original);
        if (maintainProportions && shape.Width >= 2 && shape.Height >= 2)
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

    private static Annotation ResizeImage(Annotation original, ResizeHandle handle, ImagePoint point,
        bool maintainProportions)
    {
        ImageRect bounds = original.Bounds;
        bool fromLeft = handle is ResizeHandle.TopLeft or ResizeHandle.BottomLeft;
        bool fromTop = handle is ResizeHandle.TopLeft or ResizeHandle.TopRight;
        double anchorX = fromLeft ? bounds.Right : bounds.X;
        double anchorY = fromTop ? bounds.Bottom : bounds.Y;
        double width = Math.Max(2, fromLeft ? anchorX - point.X : point.X - anchorX);
        double height = Math.Max(2, fromTop ? anchorY - point.Y : point.Y - anchorY);

        if (maintainProportions)
        {
            double ratio = bounds.Width / bounds.Height;
            if (Math.Abs(width / bounds.Width - 1) >= Math.Abs(height / bounds.Height - 1))
            {
                height = Math.Max(2, width / ratio);
            }
            else
            {
                width = Math.Max(2, height * ratio);
            }
        }

        double left = fromLeft ? anchorX - width : anchorX;
        double top = fromTop ? anchorY - height : anchorY;
        return original with { Start = new(left, top), End = new(left + width, top + height) };
    }

    private static Annotation ResizeFixedText(Annotation original, ResizeHandle handle, ImagePoint point,
        bool maintainProportions)
    {
        ImageRect bounds = original.Bounds;
        bool fromLeft = handle is ResizeHandle.TopLeft or ResizeHandle.BottomLeft;
        bool fromTop = handle is ResizeHandle.TopLeft or ResizeHandle.TopRight;
        double anchorX = fromLeft ? bounds.Right : bounds.X;
        double anchorY = fromTop ? bounds.Bottom : bounds.Y;
        double width = Math.Clamp(fromLeft ? anchorX - point.X : point.X - anchorX, 48, 12000);
        double height = Math.Clamp(fromTop ? anchorY - point.Y : point.Y - anchorY, 24, 12000);
        if (maintainProportions)
        {
            double scaleX = width / bounds.Width;
            double scaleY = height / bounds.Height;
            double scale = Math.Abs(scaleX - 1) >= Math.Abs(scaleY - 1) ? scaleX : scaleY;
            double minimum = Math.Max(48 / bounds.Width, 24 / bounds.Height);
            double maximum = Math.Min(12000 / bounds.Width, 12000 / bounds.Height);
            scale = Math.Clamp(scale, minimum, maximum);
            width = bounds.Width * scale;
            height = bounds.Height * scale;
        }

        double left = fromLeft ? anchorX - width : anchorX;
        double top = fromTop ? anchorY - height : anchorY;
        return original with
        {
            Start = new ImagePoint(left, top),
            End = new ImagePoint(left + width, top + height),
            TextBoxWidth = width,
            TextBoxHeight = height,
        };
    }

    private static Annotation ResizeProportionalShape(Annotation original, Rect shape,
        ResizeHandle handle, ImagePoint point, ImageRect viewport)
    {
        double ratio = shape.Width / shape.Height;
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
                    2 * Math.Min(centerY - viewport.Y, viewport.Bottom - centerY) * ratio);
                break;
            case ResizeHandle.Right:
                desired = point.X - left;
                maximum = Math.Min(viewport.Right - left,
                    2 * Math.Min(centerY - viewport.Y, viewport.Bottom - centerY) * ratio);
                break;
            case ResizeHandle.Top:
                desired = (bottom - point.Y) * ratio;
                maximum = Math.Min((bottom - viewport.Y) * ratio,
                    2 * Math.Min(centerX - viewport.X, viewport.Right - centerX));
                break;
            case ResizeHandle.Bottom:
                desired = (point.Y - top) * ratio;
                maximum = Math.Min((viewport.Bottom - top) * ratio,
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
                double extentYAsWidth = directionY * (point.Y - anchorY) * ratio;
                desired = Math.Abs(extentX - shape.Width) >= Math.Abs(extentYAsWidth - shape.Width)
                    ? extentX : extentYAsWidth;
                maximum = Math.Min(leftHandle ? anchorX - viewport.X : viewport.Right - anchorX,
                    (topHandle ? anchorY - viewport.Y : viewport.Bottom - anchorY) * ratio);
                if (maximum < Math.Max(2, 2 * ratio))
                {
                    return original;
                }

                double cornerWidth = Math.Clamp(desired, Math.Max(2, 2 * ratio), maximum);
                double cornerHeight = cornerWidth / ratio;
                left = leftHandle ? anchorX - cornerWidth : anchorX;
                right = leftHandle ? anchorX : anchorX + cornerWidth;
                top = topHandle ? anchorY - cornerHeight : anchorY;
                bottom = topHandle ? anchorY : anchorY + cornerHeight;
                return original with { Start = new(left, top), End = new(right, bottom) };
        }

        if (maximum < Math.Max(2, 2 * ratio))
        {
            return original;
        }

        double width = Math.Clamp(desired, Math.Max(2, 2 * ratio), maximum);
        double height = width / ratio;
        switch (handle)
        {
            case ResizeHandle.Left:
                left = right - width;
                top = centerY - height / 2;
                bottom = centerY + height / 2;
                break;
            case ResizeHandle.Right:
                right = left + width;
                top = centerY - height / 2;
                bottom = centerY + height / 2;
                break;
            case ResizeHandle.Top:
                top = bottom - height;
                left = centerX - width / 2;
                right = centerX + width / 2;
                break;
            case ResizeHandle.Bottom:
                bottom = top + height;
                left = centerX - width / 2;
                right = centerX + width / 2;
                break;
        }

        return original with { Start = new(left, top), End = new(right, bottom) };
    }

    private static Annotation ResizeLineProportional(Annotation original, ResizeHandle handle,
        ImagePoint point, ImageRect viewport)
    {
        ImagePoint anchor = handle == ResizeHandle.Start ? original.End : original.Start;
        ImagePoint oldEnd = handle == ResizeHandle.Start ? original.Start : original.End;
        double directionX = oldEnd.X - anchor.X;
        double directionY = oldEnd.Y - anchor.Y;
        double lengthSquared = directionX * directionX + directionY * directionY;
        if (lengthSquared < 4)
        {
            return original;
        }

        double factor = ((point.X - anchor.X) * directionX +
            (point.Y - anchor.Y) * directionY) / lengthSquared;
        double maximum = double.PositiveInfinity;
        if (directionX > 0) maximum = Math.Min(maximum, (viewport.Right - anchor.X) / directionX);
        else if (directionX < 0) maximum = Math.Min(maximum, (viewport.X - anchor.X) / directionX);
        if (directionY > 0) maximum = Math.Min(maximum, (viewport.Bottom - anchor.Y) / directionY);
        else if (directionY < 0) maximum = Math.Min(maximum, (viewport.Y - anchor.Y) / directionY);
        double minimum = 2 / Math.Sqrt(lengthSquared);
        if (maximum < minimum)
        {
            return original;
        }

        factor = Math.Clamp(factor, minimum, maximum);
        ImagePoint end = new(anchor.X + directionX * factor, anchor.Y + directionY * factor);
        return handle == ResizeHandle.Start
            ? original with { Start = end }
            : original with { End = end };
    }

    private static double ClampMovement(double change, double minimum, double maximum) =>
        minimum > maximum ? 0 : Math.Clamp(change, minimum, maximum);

    private bool NudgeSelected(int dx, int dy)
    {
        if (_editor is null || SelectedAnnotation() is not { } selected)
        {
            return false;
        }

        Rect bounds = SelectionBounds(selected);
        ImageRect viewport = _editor.Current.Viewport;
        double offsetX = selected.Kind == AnnotationKind.Image
            ? dx : ClampNudge(dx, viewport.X - bounds.X, viewport.Right - bounds.Right);
        double offsetY = selected.Kind == AnnotationKind.Image
            ? dy : ClampNudge(dy, viewport.Y - bounds.Y, viewport.Bottom - bounds.Bottom);
        if (offsetX != 0 || offsetY != 0)
        {
            Annotation moved = selected with
            {
                Start = new(selected.Start.X + offsetX, selected.Start.Y + offsetY),
                End = new(selected.End.X + offsetX, selected.End.Y + offsetY),
            };
            try
            {
                if (selected.Kind == AnnotationKind.Image)
                {
                    Vector2 center = Vector2.Transform(new((float)(bounds.X + bounds.Width / 2),
                        (float)(bounds.Y + bounds.Height / 2)), ImageTransform());
                    _pendingContentAnchor = new Point(center.X, center.Y);
                }
                _editor.UpdateAnnotation(moved);
            }
            catch (ArgumentException exception) when (selected.Kind == AnnotationKind.Image)
            {
                ShowMessage("Image cannot expand the canvas", exception.Message);
            }
            finally
            {
                _pendingContentAnchor = null;
            }
        }

        return true;
    }

    private static double ClampNudge(int change, double minimum, double maximum) => change switch
    {
        > 0 when maximum > 0 => Math.Min(change, maximum),
        < 0 when minimum < 0 => Math.Max(change, minimum),
        _ => 0,
    };

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
        _dragBase = _controller.Renderer.Flatten(_original, withoutSelection, _editor.Current.Viewport);
    }

    private void DisposeDragBase()
    {
        _dragBase?.Dispose();
        _dragBase = null;
    }
}
