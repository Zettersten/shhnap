using System.Collections.Immutable;

namespace Shnapp.Core;

/// <summary>Preserves the visible source pixels of a cropped image through canvas transforms.</summary>
public static class ImageVisibility
{
    /// <summary>Tests the exact visible region, including any transformed image mask.</summary>
    public static bool Contains(Annotation image, ImagePoint point)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.HiddenByCrop || image.VisibilityClip is ImageRect clip && !clip.Contains(point))
        {
            return false;
        }

        ImmutableArray<ImagePoint> polygon = image.VisibilityPolygon;
        if (polygon.IsDefaultOrEmpty)
        {
            return true;
        }

        // The retained region starts as a rectangle and remains convex after affine
        // transforms and intersection with a rectangular crop.
        int direction = 0;
        for (int index = 0; index < polygon.Length; index++)
        {
            ImagePoint start = polygon[index];
            ImagePoint end = polygon[(index + 1) % polygon.Length];
            double cross = (end.X - start.X) * (point.Y - start.Y) -
                (end.Y - start.Y) * (point.X - start.X);
            if (Math.Abs(cross) < 1e-8)
            {
                continue;
            }

            int edgeDirection = Math.Sign(cross);
            if (direction != 0 && edgeDirection != direction)
            {
                return false;
            }
            direction = edgeDirection;
        }

        return true;
    }

    internal static Annotation Carry(Annotation previous, Annotation replacement)
    {
        var (oldBounds, oldSource, fullSource) = PhotoCropGeometry.ReadGeometry(previous);
        var (newBounds, newSource, _) = PhotoCropGeometry.ReadGeometry(replacement);
        bool hasRetainedMask = !previous.VisibilityPolygon.IsDefaultOrEmpty;
        ImmutableArray<ImagePoint> prior = !hasRetainedMask
            ? Rectangle(previous.VisibilityClip!.Value)
            : previous.VisibilityPolygon;
        ImmutableArray<ImagePoint> retained = Intersect(prior, previous.VisibilityClip!.Value);
        if (retained.IsDefaultOrEmpty)
        {
            return replacement with
            {
                VisibilityClip = null,
                VisibilityPolygon = default,
                HiddenByCrop = true,
            };
        }

        var sourcePoints = ImmutableArray.CreateBuilder<ImagePoint>(retained.Length);
        foreach (ImagePoint point in retained)
        {
            ImagePoint local = AnnotationRotation.UntransformContent(previous, point);
            sourcePoints.Add(new ImagePoint(
                oldSource.X + (local.X - oldBounds.X) * oldSource.Width / oldBounds.Width,
                oldSource.Y + (local.Y - oldBounds.Y) * oldSource.Height / oldBounds.Height));
        }

        // A viewport rectangle can extend far beyond the image. On first capture,
        // retain only PNG pixels that were actually displayed. Existing masks may
        // extend beyond a later temporary source crop so Reset can recover them.
        ImmutableArray<ImagePoint> sourceMask = Intersect(sourcePoints.MoveToImmutable(),
            hasRetainedMask ? fullSource : oldSource);
        if (sourceMask.IsDefaultOrEmpty)
        {
            return replacement with
            {
                VisibilityClip = null,
                VisibilityPolygon = default,
                HiddenByCrop = true,
            };
        }

        var moved = ImmutableArray.CreateBuilder<ImagePoint>(sourceMask.Length);
        foreach (ImagePoint source in sourceMask)
        {
            ImagePoint newLocal = new(
                newBounds.X + (source.X - newSource.X) * newBounds.Width / newSource.Width,
                newBounds.Y + (source.Y - newSource.Y) * newBounds.Height / newSource.Height);
            moved.Add(AnnotationRotation.TransformContent(replacement, newLocal));
        }

        ImmutableArray<ImagePoint> polygon = moved.MoveToImmutable();
        return replacement with
        {
            VisibilityClip = Bounds(polygon),
            VisibilityPolygon = polygon,
            HiddenByCrop = false,
        };
    }

    internal static bool OverlapsSourceCrop(Annotation image)
    {
        if (image.VisibilityClip is not ImageRect clip)
        {
            return true;
        }

        ImmutableArray<ImagePoint> mask = image.VisibilityPolygon.IsDefaultOrEmpty
            ? Rectangle(clip) : Intersect(image.VisibilityPolygon, clip);
        if (mask.IsDefaultOrEmpty)
        {
            return false;
        }

        var (destination, source, _) = PhotoCropGeometry.ReadGeometry(image);
        var pixels = ImmutableArray.CreateBuilder<ImagePoint>(mask.Length);
        foreach (ImagePoint point in mask)
        {
            ImagePoint local = AnnotationRotation.UntransformContent(image, point);
            pixels.Add(new ImagePoint(
                source.X + (local.X - destination.X) * source.Width / destination.Width,
                source.Y + (local.Y - destination.Y) * source.Height / destination.Height));
        }
        return !Intersect(pixels.MoveToImmutable(), source).IsDefaultOrEmpty;
    }

    /// <summary>Moves an exact retained-pixel mask with its image in canvas coordinates.</summary>
    public static ImmutableArray<ImagePoint> Translate(
        ImmutableArray<ImagePoint> polygon, double dx, double dy) =>
        polygon.IsDefaultOrEmpty ? polygon :
            [.. polygon.Select(point => new ImagePoint(point.X + dx, point.Y + dy))];

    private static ImmutableArray<ImagePoint> Rectangle(ImageRect area) =>
    [
        new ImagePoint(area.X, area.Y),
        new ImagePoint(area.Right, area.Y),
        new ImagePoint(area.Right, area.Bottom),
        new ImagePoint(area.X, area.Bottom),
    ];

    private static ImageRect Bounds(ImmutableArray<ImagePoint> polygon)
    {
        double left = polygon.Min(point => point.X);
        double top = polygon.Min(point => point.Y);
        double right = polygon.Max(point => point.X);
        double bottom = polygon.Max(point => point.Y);
        return new ImageRect(left, top, right - left, bottom - top);
    }

    private static ImmutableArray<ImagePoint> Intersect(
        ImmutableArray<ImagePoint> polygon, ImageRect clip)
    {
        List<ImagePoint> points = [.. polygon];
        points = ClipEdge(points, point => point.X >= clip.X,
            (start, end) => AtX(start, end, clip.X));
        points = ClipEdge(points, point => point.X <= clip.Right,
            (start, end) => AtX(start, end, clip.Right));
        points = ClipEdge(points, point => point.Y >= clip.Y,
            (start, end) => AtY(start, end, clip.Y));
        points = ClipEdge(points, point => point.Y <= clip.Bottom,
            (start, end) => AtY(start, end, clip.Bottom));
        if (points.Count < 3)
        {
            return default;
        }

        double doubledArea = 0;
        for (int index = 0; index < points.Count; index++)
        {
            ImagePoint start = points[index];
            ImagePoint end = points[(index + 1) % points.Count];
            doubledArea += start.X * end.Y - end.X * start.Y;
        }

        return Math.Abs(doubledArea) < 1e-8 ? default : [.. points];
    }

    private static List<ImagePoint> ClipEdge(List<ImagePoint> points,
        Func<ImagePoint, bool> inside, Func<ImagePoint, ImagePoint, ImagePoint> crossing)
    {
        var result = new List<ImagePoint>(points.Count + 2);
        if (points.Count == 0)
        {
            return result;
        }

        ImagePoint previous = points[^1];
        bool previousInside = inside(previous);
        foreach (ImagePoint point in points)
        {
            bool pointInside = inside(point);
            if (pointInside != previousInside)
            {
                result.Add(crossing(previous, point));
            }
            if (pointInside)
            {
                result.Add(point);
            }
            previous = point;
            previousInside = pointInside;
        }
        return result;
    }

    private static ImagePoint AtX(ImagePoint start, ImagePoint end, double x)
    {
        double fraction = (x - start.X) / (end.X - start.X);
        return new ImagePoint(x, start.Y + fraction * (end.Y - start.Y));
    }

    private static ImagePoint AtY(ImagePoint start, ImagePoint end, double y)
    {
        double fraction = (y - start.Y) / (end.Y - start.Y);
        return new ImagePoint(start.X + fraction * (end.X - start.X), y);
    }
}
