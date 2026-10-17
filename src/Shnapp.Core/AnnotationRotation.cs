namespace Shnapp.Core;

/// <summary>Rotation geometry in canvas pixels, where positive angles turn clockwise.</summary>
public static class AnnotationRotation
{
    /// <summary>Gets the fixed center about which an annotation is rendered and edited.</summary>
    public static ImagePoint Pivot(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (annotation.Kind == AnnotationKind.Step)
        {
            return annotation.Start;
        }

        if (annotation.Kind == AnnotationKind.Callout)
        {
            return new ImagePoint(annotation.End.X + annotation.TextBoxWidth / 2,
                annotation.End.Y + annotation.TextBoxHeight / 2);
        }

        ImageRect bounds = annotation.Bounds;
        return new ImagePoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
    }

    /// <summary>Maps an unrotated canvas point into the annotation's displayed position.</summary>
    public static ImagePoint Rotate(Annotation annotation, ImagePoint point)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return Transform(Pivot(annotation), point, annotation.RotationDegrees);
    }

    /// <summary>Maps a displayed canvas point back into the annotation's unrotated geometry.</summary>
    public static ImagePoint Unrotate(Annotation annotation, ImagePoint point)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return Transform(Pivot(annotation), point, -annotation.RotationDegrees);
    }

    /// <summary>Finds the axis-aligned bounds of a rectangle after annotation rotation.</summary>
    public static ImageRect RotatedBounds(Annotation annotation, ImageRect bounds)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (NormalizeDegrees(annotation.RotationDegrees) == 0)
        {
            return bounds;
        }

        ImagePoint topLeft = Rotate(annotation, new ImagePoint(bounds.X, bounds.Y));
        ImagePoint topRight = Rotate(annotation, new ImagePoint(bounds.Right, bounds.Y));
        ImagePoint bottomRight = Rotate(annotation, new ImagePoint(bounds.Right, bounds.Bottom));
        ImagePoint bottomLeft = Rotate(annotation, new ImagePoint(bounds.X, bounds.Bottom));
        double left = Math.Min(Math.Min(topLeft.X, topRight.X), Math.Min(bottomRight.X, bottomLeft.X));
        double top = Math.Min(Math.Min(topLeft.Y, topRight.Y), Math.Min(bottomRight.Y, bottomLeft.Y));
        double right = Math.Max(Math.Max(topLeft.X, topRight.X), Math.Max(bottomRight.X, bottomLeft.X));
        double bottom = Math.Max(Math.Max(topLeft.Y, topRight.Y), Math.Max(bottomRight.Y, bottomLeft.Y));
        return new ImageRect(left, top, right - left, bottom - top);
    }

    /// <summary>Normalizes a finite angle into the range [-180, 180).</summary>
    public static double NormalizeDegrees(double degrees)
    {
        if (!double.IsFinite(degrees))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), "The rotation must be finite.");
        }

        double normalized = degrees % 360;
        if (normalized >= 180)
        {
            normalized -= 360;
        }
        else if (normalized < -180)
        {
            normalized += 360;
        }

        return normalized == 0 ? 0 : normalized;
    }

    private static ImagePoint Transform(ImagePoint pivot, ImagePoint point, double degrees)
    {
        double angle = NormalizeDegrees(degrees);
        if (angle == 0)
        {
            return point;
        }

        double radians = angle * Math.PI / 180;
        double cosine = SnapTrigonometric(Math.Cos(radians));
        double sine = SnapTrigonometric(Math.Sin(radians));
        double x = point.X - pivot.X;
        double y = point.Y - pivot.Y;
        return new ImagePoint(pivot.X + x * cosine - y * sine,
            pivot.Y + x * sine + y * cosine);
    }

    private static double SnapTrigonometric(double value) =>
        Math.Abs(value) < 1e-12 ? 0 :
        Math.Abs(value - 1) < 1e-12 ? 1 :
        Math.Abs(value + 1) < 1e-12 ? -1 : value;
}
