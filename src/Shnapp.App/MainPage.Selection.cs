using System.Collections.Immutable;
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
        CurveMidpoint,
        Rotate,
    }

    private readonly record struct SelectionHandle(ResizeHandle Kind, ImagePoint Position);

    private Rect SelectionBounds(Annotation annotation) => annotation.Kind switch
    {
        AnnotationKind.Text => ShnappRenderer.TextBounds(annotation),
        AnnotationKind.Step => new Rect(annotation.Start.X - annotation.StepDiameter / 2,
            annotation.Start.Y - annotation.StepDiameter / 2, annotation.StepDiameter, annotation.StepDiameter),
        AnnotationKind.Callout => new Rect(annotation.End.X, annotation.End.Y,
            annotation.TextBoxWidth, annotation.TextBoxHeight),
        _ => ShnappRenderer.ToRect(annotation.Bounds),
    };

    private bool HitAnnotation(Annotation annotation, ImagePoint position)
    {
        if (annotation.IsFlattened || annotation.HiddenByCrop ||
            !ImageVisibility.Contains(annotation, position))
        {
            return false;
        }

        position = AnnotationRotation.UntransformContent(annotation, position);

        if (annotation.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            if (LineGeometry.DistanceToPath(annotation, position) <=
                Math.Max(7 / _scale, annotation.StrokeWidth / 2 + 3 / _scale))
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

        if (annotation.Kind == AnnotationKind.Callout)
        {
            double dx = position.X - annotation.Start.X;
            double dy = position.Y - annotation.Start.Y;
            return dx * dx + dy * dy <= Math.Pow(14 / _scale, 2) ||
                HitBounds(annotation).Contains(new Point(position.X, position.Y));
        }

        return HitBounds(annotation).Contains(new Point(position.X, position.Y));
    }

    private Annotation? HitTopAnnotation(ImagePoint position) =>
        _editor?.Current.OrderedAnnotations.Reverse()
            .FirstOrDefault(annotation => HitAnnotation(annotation, position));

    private SelectionHandle[] HandlesFor(Annotation annotation)
    {
        if (annotation.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            return annotation.AllowCurves && annotation.LinePoints.IsDefaultOrEmpty
                ? [new(ResizeHandle.Start, annotation.Start), new(ResizeHandle.End, annotation.End),
                    new(ResizeHandle.CurveMidpoint, LineGeometry.Midpoint(annotation))]
                : [new(ResizeHandle.Start, annotation.Start), new(ResizeHandle.End, annotation.End)];
        }

        Rect bounds = SelectionBounds(annotation);
        if (annotation.Kind == AnnotationKind.Step)
        {
            return [new(ResizeHandle.Right, new(bounds.Right, bounds.Y + bounds.Height / 2))];
        }

        if (annotation.Kind == AnnotationKind.Callout)
        {
            return [new(ResizeHandle.Start, annotation.Start)];
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

        if (annotation.Kind is AnnotationKind.Image or AnnotationKind.Magnify)
        {
            ImageRect? selectedBounds = annotation.Kind == AnnotationKind.Image
                ? VisibleImageBounds(annotation) : annotation.Bounds;
            if (selectedBounds is not ImageRect visible)
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
        if (annotation.RotationDegrees != 0)
        {
            // The displayed footprint is rotated, while resize handles stay in
            // the photo's local frame and are rotated when drawn or hit-tested.
            ImageRect displayed = AnnotationRotation.RotatedBounds(annotation, bounds);
            double visibleLeft = Math.Max(displayed.X, viewport.X);
            double visibleTop = Math.Max(displayed.Y, viewport.Y);
            double visibleRight = Math.Min(displayed.Right, viewport.Right);
            double visibleBottom = Math.Min(displayed.Bottom, viewport.Bottom);
            if (annotation.VisibilityClip is ImageRect visibleClip)
            {
                visibleLeft = Math.Max(visibleLeft, visibleClip.X);
                visibleTop = Math.Max(visibleTop, visibleClip.Y);
                visibleRight = Math.Min(visibleRight, visibleClip.Right);
                visibleBottom = Math.Min(visibleBottom, visibleClip.Bottom);
            }

            return visibleRight > visibleLeft && visibleBottom > visibleTop ? bounds : null;
        }

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

    private Rect SelectionFrame(Annotation annotation)
    {
        Rect bounds = annotation.Kind == AnnotationKind.Image &&
            VisibleImageBounds(annotation) is ImageRect visible
            ? ShnappRenderer.ToRect(visible)
            : SelectionBounds(annotation);
        if (annotation.Kind is not AnnotationKind.Line and not AnnotationKind.Arrow)
        {
            return bounds;
        }

        // A line needs a visible top edge for the rotation stem. Keep the frame
        // clear of the stroke and its endpoint handles at every zoom level.
        double padding = 5 / Math.Max(0.01, _scale);
        return new Rect(bounds.X - padding, bounds.Y - padding,
            Math.Max(bounds.Width, 1 / _scale) + padding * 2,
            Math.Max(bounds.Height, 1 / _scale) + padding * 2);
    }

    private ImagePoint RotationStemStart(Annotation annotation)
    {
        Rect frame = SelectionFrame(annotation);
        return new ImagePoint(frame.X + frame.Width / 2, frame.Y);
    }

    private ImagePoint RotationHandlePosition(Annotation annotation)
    {
        ImagePoint top = RotationStemStart(annotation);
        return AnnotationRotation.Rotate(annotation,
            new ImagePoint(top.X, top.Y - 24 / Math.Max(0.01, _scale)));
    }

    private static Annotation RotateAnnotationFromPointer(Annotation original,
        ImagePoint dragStart, ImagePoint pointer, bool snap)
    {
        ImagePoint pivot = AnnotationRotation.Pivot(original);
        double start = Math.Atan2(dragStart.Y - pivot.Y, dragStart.X - pivot.X);
        double current = Math.Atan2(pointer.Y - pivot.Y, pointer.X - pivot.X);
        double degrees = AnnotationRotation.NormalizeDegrees(original.RotationDegrees +
            (current - start) * 180 / Math.PI);
        if (snap)
        {
            degrees = AnnotationRotation.NormalizeDegrees(Math.Round(degrees / 15) * 15);
        }

        return original with { RotationDegrees = degrees };
    }

    // Frame handles stay on the displayed rectangle. Endpoint and callout
    // handles follow the actual content when that content is mirrored.
    private static ImagePoint DisplayHandlePosition(Annotation annotation, SelectionHandle handle) =>
        annotation.Kind is AnnotationKind.Line or AnnotationKind.Arrow or AnnotationKind.Callout
            ? AnnotationRotation.TransformContent(annotation, handle.Position)
            : AnnotationRotation.Rotate(annotation, handle.Position);

    private static ImagePoint LocalResizePoint(Annotation annotation, ImagePoint point) =>
        annotation.Kind is AnnotationKind.Line or AnnotationKind.Arrow or AnnotationKind.Callout
            ? AnnotationRotation.UntransformContent(annotation, point)
            : AnnotationRotation.Unrotate(annotation, point);

    private Annotation PreserveResizeAnchor(Annotation original, Annotation resized,
        ResizeHandle handle)
    {
        if ((original.RotationDegrees == 0 && !original.FlipHorizontal && !original.FlipVertical) ||
            handle == ResizeHandle.CurveMidpoint)
        {
            return resized;
        }

        ImagePoint anchorBefore = ResizeAnchor(original, handle);
        ImagePoint anchorAfter = ResizeAnchor(resized, handle);
        bool followsContent = original.Kind is AnnotationKind.Line or AnnotationKind.Arrow or
            AnnotationKind.Callout;
        ImagePoint before = followsContent
            ? AnnotationRotation.TransformContent(original, anchorBefore)
            : AnnotationRotation.Rotate(original, anchorBefore);
        ImagePoint after = followsContent
            ? AnnotationRotation.TransformContent(resized, anchorAfter)
            : AnnotationRotation.Rotate(resized, anchorAfter);
        double dx = before.X - after.X;
        double dy = before.Y - after.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
        {
            return resized;
        }

        return resized.Kind is AnnotationKind.Line or AnnotationKind.Arrow
            ? LineGeometry.Translate(resized, dx, dy)
            : resized with
            {
                Start = new ImagePoint(resized.Start.X + dx, resized.Start.Y + dy),
                End = new ImagePoint(resized.End.X + dx, resized.End.Y + dy),
            };
    }

    private ImagePoint ResizeAnchor(Annotation annotation, ResizeHandle handle)
    {
        if (annotation.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            return handle == ResizeHandle.Start ? annotation.End : annotation.Start;
        }

        if (annotation.Kind is AnnotationKind.Step or AnnotationKind.Callout)
        {
            return AnnotationRotation.Pivot(annotation);
        }

        Rect bounds = annotation.Kind == AnnotationKind.Image
            ? ShnappRenderer.ToRect(annotation.Bounds)
            : SelectionBounds(annotation);
        double midX = bounds.X + bounds.Width / 2;
        double midY = bounds.Y + bounds.Height / 2;
        return handle switch
        {
            ResizeHandle.TopLeft => new ImagePoint(bounds.Right, bounds.Bottom),
            ResizeHandle.Top => new ImagePoint(midX, bounds.Bottom),
            ResizeHandle.TopRight => new ImagePoint(bounds.Left, bounds.Bottom),
            ResizeHandle.Right => new ImagePoint(bounds.Left, midY),
            ResizeHandle.BottomRight => new ImagePoint(bounds.Left, bounds.Top),
            ResizeHandle.Bottom => new ImagePoint(midX, bounds.Top),
            ResizeHandle.BottomLeft => new ImagePoint(bounds.Right, bounds.Top),
            ResizeHandle.Left => new ImagePoint(bounds.Right, midY),
            _ => AnnotationRotation.Pivot(annotation),
        };
    }

    private ResizeHandle HitResizeHandle(Annotation annotation, Point canvasPosition)
    {
        ResizeHandle nearest = ResizeHandle.None;
        double nearestDistanceSquared = 9 * 9;
        Matrix3x2 transform = ImageTransform();
        if (!annotation.HiddenByCrop &&
            (annotation.Kind != AnnotationKind.Image || VisibleImageBounds(annotation) is not null))
        {
            ImagePoint rotationPosition = RotationHandlePosition(annotation);
            Vector2 center = Vector2.Transform(new((float)rotationPosition.X,
                (float)rotationPosition.Y), transform);
            double dx = canvasPosition.X - center.X;
            double dy = canvasPosition.Y - center.Y;
            if (dx * dx + dy * dy <= nearestDistanceSquared)
            {
                return ResizeHandle.Rotate;
            }
        }

        foreach (SelectionHandle handle in HandlesFor(annotation))
        {
            ImagePoint position = DisplayHandlePosition(annotation, handle);
            if (annotation.HiddenByCrop ||
                !ImageVisibility.Contains(annotation, position))
            {
                continue;
            }

            Vector2 center = Vector2.Transform(new((float)position.X, (float)position.Y), transform);
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

        var blue = ShnappRenderer.FromArgb(0xFF0A84FF);
        float outer = (float)(3 / _scale);
        float inner = (float)(1 / _scale);
        using (var clipLayer = annotation.VisibilityClip is ImageRect clip
            ? drawing.CreateLayer(1, ShnappRenderer.ToRect(clip))
            : null)
        {
            Rect frame = SelectionFrame(annotation);
            ImagePoint[] corners =
            [
                AnnotationRotation.Rotate(annotation, new ImagePoint(frame.Left, frame.Top)),
                AnnotationRotation.Rotate(annotation, new ImagePoint(frame.Right, frame.Top)),
                AnnotationRotation.Rotate(annotation, new ImagePoint(frame.Right, frame.Bottom)),
                AnnotationRotation.Rotate(annotation, new ImagePoint(frame.Left, frame.Bottom)),
            ];
            for (int index = 0; index < corners.Length; index++)
            {
                ImagePoint start = corners[index];
                ImagePoint end = corners[(index + 1) % corners.Length];
                drawing.DrawLine((float)start.X, (float)start.Y, (float)end.X, (float)end.Y,
                    Colors.White, outer);
                drawing.DrawLine((float)start.X, (float)start.Y, (float)end.X, (float)end.Y,
                    blue, inner);
            }

            float radius = (float)(5 / _scale);
            foreach (SelectionHandle handle in HandlesFor(annotation))
            {
                ImagePoint position = DisplayHandlePosition(annotation, handle);
                if (!ImageVisibility.Contains(annotation, position))
                {
                    continue;
                }

                if (handle.Kind == ResizeHandle.CurveMidpoint)
                {
                    drawing.FillCircle(new Vector2((float)position.X, (float)position.Y),
                        radius, blue);
                    drawing.DrawCircle(new Vector2((float)position.X, (float)position.Y),
                        radius, Colors.White, (float)(2 / _scale));
                    continue;
                }
                Rect square = new(position.X - radius, position.Y - radius,
                    radius * 2, radius * 2);
                drawing.FillRectangle(square, Colors.White);
                drawing.DrawRectangle(square, blue, (float)(2 / _scale));
            }
        }

        // The control is editor chrome, so it remains visible above a crop mask.
        ImagePoint stem = AnnotationRotation.Rotate(annotation, RotationStemStart(annotation));
        ImagePoint knob = RotationHandlePosition(annotation);
        drawing.DrawLine((float)stem.X, (float)stem.Y, (float)knob.X, (float)knob.Y,
            Colors.White, outer);
        drawing.DrawLine((float)stem.X, (float)stem.Y, (float)knob.X, (float)knob.Y,
            blue, inner);
        float knobRadius = (float)(5 / _scale);
        drawing.FillCircle(new Vector2((float)knob.X, (float)knob.Y), knobRadius, Colors.White);
        drawing.DrawCircle(new Vector2((float)knob.X, (float)knob.Y), knobRadius,
            blue, (float)(2 / _scale));
    }

    private Annotation ResizeAnnotation(Annotation original, ResizeHandle handle, ImagePoint point,
        bool maintainProportions)
    {
        ImageRect viewport = _editor!.Current.Viewport;
        if (original.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            if (handle == ResizeHandle.CurveMidpoint)
            {
                return original with { CurveMidpoint = point };
            }
            if (maintainProportions)
            {
                return ResizeLineProportional(original, handle, point);
            }

            ImagePoint start = handle == ResizeHandle.Start ? point : original.Start;
            ImagePoint end = handle == ResizeHandle.End ? point : original.End;
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            return Math.Sqrt(dx * dx + dy * dy) < 2
                ? original
                : LineGeometry.Resize(original, start, end);
        }

        if (original.Kind == AnnotationKind.Step)
        {
            double diameter = Math.Clamp(2 * Math.Abs(point.X - original.Start.X), 16, 120);
            double fontSize = Math.Clamp(original.FontSize * diameter / original.StepDiameter, 8, 144);
            return original with { StepDiameter = diameter, FontSize = fontSize };
        }

        if (original.Kind == AnnotationKind.Callout)
        {
            return handle == ResizeHandle.Start ? original with { Start = point } : original;
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

        if (original.Kind is AnnotationKind.Image or AnnotationKind.Magnify)
        {
            if (original.Kind == AnnotationKind.Image && VisibleImageBounds(original) is ImageRect visible)
            {
                ImagePoint fullCorner = Corner(original.Bounds, handle);
                ImagePoint visibleCorner = Corner(visible, handle);
                point = new ImagePoint(point.X + fullCorner.X - visibleCorner.X,
                    point.Y + fullCorner.Y - visibleCorner.Y);
            }

            return ResizeImage(original, handle, point, maintainProportions,
                original.Kind == AnnotationKind.Magnify ? 48 : 2);
        }

        Rect shape = SelectionBounds(original);
        if (maintainProportions && shape.Width >= 2 && shape.Height >= 2)
        {
            return ResizeProportionalShape(original, shape, handle, point);
        }

        const double minWidth = 2;
        const double minHeight = 2;
        double left = shape.X;
        double top = shape.Y;
        double right = shape.Right;
        double bottom = shape.Bottom;
        if (handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft)
        {
            left = Math.Min(point.X, right - minWidth);
        }
        else if (handle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight)
        {
            right = Math.Max(point.X, left + minWidth);
        }

        if (handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight)
        {
            top = Math.Min(point.Y, bottom - minHeight);
        }
        else if (handle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight)
        {
            bottom = Math.Max(point.Y, top + minHeight);
        }

        return original with { Start = new(left, top), End = new(right, bottom) };
    }

    private static Annotation ResizeImage(Annotation original, ResizeHandle handle, ImagePoint point,
        bool maintainProportions, double minimumSize)
    {
        ImageRect bounds = original.Bounds;
        bool fromLeft = handle is ResizeHandle.TopLeft or ResizeHandle.BottomLeft;
        bool fromTop = handle is ResizeHandle.TopLeft or ResizeHandle.TopRight;
        double anchorX = fromLeft ? bounds.Right : bounds.X;
        double anchorY = fromTop ? bounds.Bottom : bounds.Y;
        double width = Math.Max(minimumSize, fromLeft ? anchorX - point.X : point.X - anchorX);
        double height = Math.Max(minimumSize, fromTop ? anchorY - point.Y : point.Y - anchorY);

        if (maintainProportions)
        {
            double ratio = bounds.Width / bounds.Height;
            if (Math.Abs(width / bounds.Width - 1) >= Math.Abs(height / bounds.Height - 1))
            {
                height = Math.Max(minimumSize, width / ratio);
            }
            else
            {
                width = Math.Max(minimumSize, height * ratio);
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
        ResizeHandle handle, ImagePoint point)
    {
        double ratio = shape.Width / shape.Height;
        double left = shape.X;
        double top = shape.Y;
        double right = shape.Right;
        double bottom = shape.Bottom;
        double centerX = (left + right) / 2;
        double centerY = (top + bottom) / 2;
        double desired;

        switch (handle)
        {
            case ResizeHandle.Left:
                desired = right - point.X;
                break;
            case ResizeHandle.Right:
                desired = point.X - left;
                break;
            case ResizeHandle.Top:
                desired = (bottom - point.Y) * ratio;
                break;
            case ResizeHandle.Bottom:
                desired = (point.Y - top) * ratio;
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
                double cornerWidth = Math.Max(desired, Math.Max(2, 2 * ratio));
                double cornerHeight = cornerWidth / ratio;
                left = leftHandle ? anchorX - cornerWidth : anchorX;
                right = leftHandle ? anchorX : anchorX + cornerWidth;
                top = topHandle ? anchorY - cornerHeight : anchorY;
                bottom = topHandle ? anchorY : anchorY + cornerHeight;
                return original with { Start = new(left, top), End = new(right, bottom) };
        }

        double width = Math.Max(desired, Math.Max(2, 2 * ratio));
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
        ImagePoint point)
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
        double minimum = 2 / Math.Sqrt(lengthSquared);
        factor = Math.Max(factor, minimum);
        ImagePoint end = new(anchor.X + directionX * factor, anchor.Y + directionY * factor);
        return handle == ResizeHandle.Start
            ? LineGeometry.Resize(original, end, original.End)
            : LineGeometry.Resize(original, original.Start, end);
    }

    private bool NudgeSelected(int dx, int dy)
    {
        if (_editor is null || SelectedAnnotations() is not { Length: > 0 } selection)
        {
            return false;
        }

        if (selection.Length > 1)
        {
            try
            {
                _editor.MoveAnnotations(selection.Select(annotation => annotation.Id).ToArray(), dx, dy);
            }
            catch (ArgumentException exception)
            {
                ShowAnnotationEditError(exception);
            }

            return true;
        }

        Annotation selected = selection[0];
        double offsetX = dx;
        double offsetY = dy;
        if (offsetX != 0 || offsetY != 0)
        {
            Annotation moved = selected.Kind is AnnotationKind.Line or AnnotationKind.Arrow
                ? LineGeometry.Translate(selected, offsetX, offsetY)
                : selected with
                {
                    Start = new(selected.Start.X + offsetX, selected.Start.Y + offsetY),
                    End = new(selected.End.X + offsetX, selected.End.Y + offsetY),
                };
            try
            {
                if (selected.Kind == AnnotationKind.Image)
                {
                    Rect bounds = SelectionBounds(selected);
                    Vector2 center = Vector2.Transform(new((float)(bounds.X + bounds.Width / 2),
                        (float)(bounds.Y + bounds.Height / 2)), ImageTransform());
                    _pendingContentAnchor = new Point(center.X, center.Y);
                }
                _editor.UpdateAnnotation(moved);
            }
            catch (ArgumentException exception)
            {
                ShowAnnotationEditError(exception);
            }
            finally
            {
                _pendingContentAnchor = null;
            }
        }

        return true;
    }

    private void EnsureDragBase()
    {
        if (_dragBase is not null || _moving is null || _editor is null || _original is null || _controller is null)
        {
            return;
        }

        HashSet<Guid> movingIds = _groupMoving.Length > 1
            ? _groupMoving.Select(annotation => annotation.Id).ToHashSet()
            : [_moving.Id];
        ShnappDocument withoutSelection = _editor.Current with
        {
            Annotations = _editor.Current.Annotations
                .Where(annotation => !movingIds.Contains(annotation.Id)).ToImmutableArray(),
        };
        _dragBase = _controller.Renderer.Flatten(_original, withoutSelection, _editor.Current.Viewport);
    }

    private void DisposeDragBase()
    {
        _dragBase?.Dispose();
        _dragBase = null;
    }
}
