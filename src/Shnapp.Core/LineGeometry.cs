using System.Collections.Immutable;

namespace Shnapp.Core;

/// <summary>Geometry shared by line editing, hit testing, rendering, and persistence.</summary>
public static class LineGeometry
{
    public static ImagePoint ConstrainAxis(ImagePoint start, ImagePoint end) =>
        Math.Abs(end.X - start.X) >= Math.Abs(end.Y - start.Y)
            ? new ImagePoint(end.X, start.Y)
            : new ImagePoint(start.X, end.Y);

    /// <summary>Rounds sharp pointer turns while preserving the exact first and last points.</summary>
    public static ImmutableArray<ImagePoint> Smooth(IReadOnlyList<ImagePoint> trace)
    {
        if (trace.Count < 2) return [];
        var points = trace.ToArray();
        for (int pass = 0; pass < 2 && points.Length > 2; pass++)
        {
            var rounded = new ImagePoint[points.Length * 2];
            rounded[0] = points[0];
            int count = 1;
            for (int index = 0; index < points.Length - 1; index++)
            {
                ImagePoint first = points[index];
                ImagePoint second = points[index + 1];
                rounded[count++] = Mix(first, second, 0.25);
                rounded[count++] = Mix(first, second, 0.75);
            }
            rounded[count++] = points[^1];
            points = rounded.AsSpan(0, count).ToArray();
        }
        return [.. points];
    }

    public static ImagePoint Midpoint(Annotation line) => line.CurveMidpoint ??
        Mix(line.Start, line.End, 0.5);

    /// <summary>Finds the visible halfway point when a freehand stroke becomes one editable curve.</summary>
    public static ImagePoint PathMidpoint(Annotation line)
    {
        if (line.LinePoints.IsDefaultOrEmpty) return Midpoint(line);
        double length = 0;
        for (int index = 1; index < line.LinePoints.Length; index++)
        {
            length += Distance(line.LinePoints[index - 1], line.LinePoints[index]);
        }
        double halfway = length / 2;
        for (int index = 1; index < line.LinePoints.Length; index++)
        {
            ImagePoint start = line.LinePoints[index - 1];
            ImagePoint end = line.LinePoints[index];
            double segment = Distance(start, end);
            if (halfway <= segment)
            {
                return segment < 0.0001 ? start : Mix(start, end, halfway / segment);
            }
            halfway -= segment;
        }
        return line.End;
    }

    /// <summary>Quadratic control point whose curve passes through the dragged midpoint.</summary>
    public static ImagePoint ControlPoint(Annotation line)
    {
        ImagePoint middle = Midpoint(line);
        return new ImagePoint(2 * middle.X - (line.Start.X + line.End.X) / 2,
            2 * middle.Y - (line.Start.Y + line.End.Y) / 2);
    }

    public static ImageRect Bounds(Annotation line)
    {
        double left = Math.Min(line.Start.X, line.End.X);
        double top = Math.Min(line.Start.Y, line.End.Y);
        double right = Math.Max(line.Start.X, line.End.X);
        double bottom = Math.Max(line.Start.Y, line.End.Y);
        if (!line.LinePoints.IsDefaultOrEmpty)
        {
            foreach (ImagePoint point in line.LinePoints)
            {
                left = Math.Min(left, point.X);
                top = Math.Min(top, point.Y);
                right = Math.Max(right, point.X);
                bottom = Math.Max(bottom, point.Y);
            }
        }
        else if (line.AllowCurves && line.CurveMidpoint is not null)
        {
            ImagePoint control = ControlPoint(line);
            IncludeQuadraticExtremum(line.Start.X, control.X, line.End.X, ref left, ref right);
            IncludeQuadraticExtremum(line.Start.Y, control.Y, line.End.Y, ref top, ref bottom);
        }
        return new ImageRect(left, top, right - left, bottom - top);
    }

    public static double DistanceToPath(Annotation line, ImagePoint point)
    {
        if (!line.LinePoints.IsDefaultOrEmpty)
        {
            double nearest = double.PositiveInfinity;
            for (int index = 1; index < line.LinePoints.Length; index++)
            {
                nearest = Math.Min(nearest, DistanceToSegment(point,
                    line.LinePoints[index - 1], line.LinePoints[index]));
            }
            return nearest;
        }
        if (line.AllowCurves && line.CurveMidpoint is not null)
        {
            ImagePoint control = ControlPoint(line);
            double nearest = double.PositiveInfinity;
            ImagePoint previous = line.Start;
            for (int index = 1; index <= 48; index++)
            {
                double t = index / 48.0;
                ImagePoint current = Quadratic(line.Start, control, line.End, t);
                nearest = Math.Min(nearest, DistanceToSegment(point, previous, current));
                previous = current;
            }
            return nearest;
        }
        return DistanceToSegment(point, line.Start, line.End);
    }

    public static Annotation Translate(Annotation line, double dx, double dy) => line with
    {
        Start = Offset(line.Start, dx, dy),
        End = Offset(line.End, dx, dy),
        CurveMidpoint = line.CurveMidpoint is ImagePoint middle ? Offset(middle, dx, dy) : null,
        LinePoints = line.LinePoints.IsDefaultOrEmpty ? line.LinePoints
            : [.. line.LinePoints.Select(point => Offset(point, dx, dy))],
    };

    /// <summary>Moves a line endpoint and carries the saved path or bend with the new chord.</summary>
    public static Annotation Resize(Annotation line, ImagePoint start, ImagePoint end)
    {
        double oldX = line.End.X - line.Start.X;
        double oldY = line.End.Y - line.Start.Y;
        double oldLengthSquared = oldX * oldX + oldY * oldY;
        if (oldLengthSquared < 0.0001)
        {
            ImmutableArray<ImagePoint> points = line.LinePoints;
            if (!points.IsDefaultOrEmpty)
            {
                var builder = points.ToBuilder();
                builder[0] = start;
                builder[^1] = end;
                points = builder.ToImmutable();
            }
            ImagePoint? fallbackMiddle = line.CurveMidpoint is ImagePoint oldMiddle
                ? Offset(oldMiddle,
                    (start.X - line.Start.X + end.X - line.End.X) / 2,
                    (start.Y - line.Start.Y + end.Y - line.End.Y) / 2)
                : null;
            return line with { Start = start, End = end, LinePoints = points, CurveMidpoint = fallbackMiddle };
        }

        ImagePoint Transform(ImagePoint point)
        {
            double x = point.X - line.Start.X;
            double y = point.Y - line.Start.Y;
            double along = (x * oldX + y * oldY) / oldLengthSquared;
            double across = (oldX * y - oldY * x) / oldLengthSquared;
            double newX = end.X - start.X;
            double newY = end.Y - start.Y;
            return new ImagePoint(start.X + along * newX - across * newY,
                start.Y + along * newY + across * newX);
        }

        ImmutableArray<ImagePoint> transformedPoints = line.LinePoints;
        if (!transformedPoints.IsDefaultOrEmpty)
        {
            var builder = ImmutableArray.CreateBuilder<ImagePoint>(transformedPoints.Length);
            foreach (ImagePoint point in transformedPoints)
            {
                builder.Add(Transform(point));
            }
            // The saved path and handles must share exact endpoints. Transforming
            // the endpoints through floating-point chord math can differ by a few ulps.
            builder[0] = start;
            builder[^1] = end;
            transformedPoints = builder.MoveToImmutable();
        }

        return line with
        {
            Start = start,
            End = end,
            CurveMidpoint = line.CurveMidpoint is ImagePoint middle ? Transform(middle) : null,
            LinePoints = transformedPoints,
        };
    }

    private static ImagePoint Mix(ImagePoint first, ImagePoint second, double fraction) =>
        new(first.X + (second.X - first.X) * fraction,
            first.Y + (second.Y - first.Y) * fraction);

    private static ImagePoint Offset(ImagePoint point, double dx, double dy) =>
        new(point.X + dx, point.Y + dy);

    private static double Distance(ImagePoint start, ImagePoint end) =>
        Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));

    private static ImagePoint Quadratic(ImagePoint start, ImagePoint control, ImagePoint end, double t)
    {
        double inverse = 1 - t;
        return new ImagePoint(inverse * inverse * start.X + 2 * inverse * t * control.X + t * t * end.X,
            inverse * inverse * start.Y + 2 * inverse * t * control.Y + t * t * end.Y);
    }

    private static void IncludeQuadraticExtremum(double start, double control, double end,
        ref double minimum, ref double maximum)
    {
        double denominator = start - 2 * control + end;
        if (Math.Abs(denominator) < 0.0001) return;
        double t = (start - control) / denominator;
        if (t is <= 0 or >= 1) return;
        double value = (1 - t) * (1 - t) * start + 2 * (1 - t) * t * control + t * t * end;
        minimum = Math.Min(minimum, value);
        maximum = Math.Max(maximum, value);
    }

    private static double DistanceToSegment(ImagePoint point, ImagePoint start, ImagePoint end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double squared = dx * dx + dy * dy;
        double fraction = squared < 0.0001 ? 0 : Math.Clamp(
            ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / squared, 0, 1);
        return Math.Sqrt(Math.Pow(point.X - start.X - fraction * dx, 2) +
            Math.Pow(point.Y - start.Y - fraction * dy, 2));
    }
}
