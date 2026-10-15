namespace Shnapp.Core;

/// <summary>Builds the same closed outline for a shape preview and its exported mark.</summary>
public static class BasicShapeGeometry
{
    /// <summary>Open paths keep their end points separate and cannot have a fill.</summary>
    public static bool IsClosed(BasicShapeKind kind) => kind != BasicShapeKind.RightBracket;

    public static ImagePoint[] Outline(BasicShapeKind kind, ImageRect bounds)
    {
        if (!Enum.IsDefined(kind) || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
            bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "A supported shape needs positive, finite bounds.");
        }

        return kind switch
        {
            BasicShapeKind.Circle => Ellipse(SquareInside(bounds), 96),
            BasicShapeKind.Ellipse => Ellipse(bounds, SegmentCount(bounds)),
            BasicShapeKind.Star => Star(bounds),
            BasicShapeKind.Semicircle => Semicircle(bounds),
            _ => Polygon(bounds, kind switch
            {
                BasicShapeKind.Rectangle => [(0, 0), (1, 0), (1, 1), (0, 1)],
                BasicShapeKind.RightBracket => [(0, 0), (1, 0), (1, 1), (0, 1)],
                BasicShapeKind.Diamond => [(0.5, 0), (1, 0.5), (0.5, 1), (0, 0.5)],
                BasicShapeKind.Triangle => [(0.5, 0), (1, 1), (0, 1)],
                BasicShapeKind.InvertedTriangle => [(0, 0), (1, 0), (0.5, 1)],
                BasicShapeKind.Pentagon => [(0.5, 0), (1, 0.38), (0.8, 1), (0.2, 1), (0, 0.38)],
                BasicShapeKind.Hexagon => [(0.25, 0), (0.75, 0), (1, 0.5), (0.75, 1), (0.25, 1), (0, 0.5)],
                BasicShapeKind.WideHexagon => [(0.15, 0), (0.85, 0), (1, 0.5), (0.85, 1), (0.15, 1), (0, 0.5)],
                BasicShapeKind.Octagon => [(0.28, 0), (0.72, 0), (1, 0.28), (1, 0.72),
                    (0.72, 1), (0.28, 1), (0, 0.72), (0, 0.28)],
                BasicShapeKind.Parallelogram => [(0.25, 0), (1, 0), (0.75, 1), (0, 1)],
                BasicShapeKind.Trapezoid => [(0.2, 0), (0.8, 0), (1, 1), (0, 1)],
                BasicShapeKind.Plus => [(0.36, 0), (0.64, 0), (0.64, 0.36), (1, 0.36),
                    (1, 0.64), (0.64, 0.64), (0.64, 1), (0.36, 1), (0.36, 0.64),
                    (0, 0.64), (0, 0.36), (0.36, 0.36)],
                BasicShapeKind.UpArrow => [(0.5, 0), (1, 0.42), (0.76, 0.42),
                    (0.76, 1), (0.24, 1), (0.24, 0.42), (0, 0.42)],
                BasicShapeKind.RightTriangle => [(0, 0), (1, 1), (0, 1)],
                BasicShapeKind.TopLeftCorner => [(0, 0), (1, 0), (1, 0.24),
                    (0.24, 0.24), (0.24, 1), (0, 1)],
                BasicShapeKind.TopRightCorner => [(0, 0), (1, 0), (1, 1),
                    (0.76, 1), (0.76, 0.24), (0, 0.24)],
                BasicShapeKind.BottomLeftCorner => [(0, 0), (0.24, 0),
                    (0.24, 0.76), (1, 0.76), (1, 1), (0, 1)],
                BasicShapeKind.BottomRightCorner => [(0.76, 0), (1, 0),
                    (1, 1), (0, 1), (0, 0.76), (0.76, 0.76)],
                BasicShapeKind.UShape => [(0, 0), (0.22, 0), (0.22, 0.72),
                    (0.78, 0.72), (0.78, 0), (1, 0), (1, 1), (0, 1)],
                BasicShapeKind.InvertedUShape => [(0, 0), (1, 0), (1, 1),
                    (0.78, 1), (0.78, 0.28), (0.22, 0.28), (0.22, 1), (0, 1)],
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            }),
        };
    }

    private static ImagePoint[] Polygon(ImageRect bounds, (double X, double Y)[] unit) =>
        unit.Select(point => new ImagePoint(bounds.X + point.X * bounds.Width,
            bounds.Y + point.Y * bounds.Height)).ToArray();

    private static ImageRect SquareInside(ImageRect bounds)
    {
        double side = Math.Min(bounds.Width, bounds.Height);
        return new ImageRect(bounds.X + (bounds.Width - side) / 2,
            bounds.Y + (bounds.Height - side) / 2, side, side);
    }

    private static int SegmentCount(ImageRect bounds) =>
        Math.Clamp((int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height) / 4), 64, 2048);

    private static ImagePoint[] Ellipse(ImageRect bounds, int segments)
    {
        var points = new ImagePoint[segments];
        double centerX = bounds.X + bounds.Width / 2;
        double centerY = bounds.Y + bounds.Height / 2;
        for (int index = 0; index < points.Length; index++)
        {
            double angle = index * Math.Tau / segments;
            points[index] = new ImagePoint(centerX + Math.Cos(angle) * bounds.Width / 2,
                centerY + Math.Sin(angle) * bounds.Height / 2);
        }

        return points;
    }

    private static ImagePoint[] Semicircle(ImageRect bounds)
    {
        int segments = Math.Clamp(SegmentCount(bounds) / 2, 32, 1024);
        var points = new ImagePoint[segments + 1];
        for (int index = 0; index <= segments; index++)
        {
            double angle = Math.PI - index * Math.PI / segments;
            points[index] = new ImagePoint(bounds.X + bounds.Width / 2 +
                Math.Cos(angle) * bounds.Width / 2,
                bounds.Bottom - Math.Sin(angle) * bounds.Height);
        }

        return points;
    }

    private static ImagePoint[] Star(ImageRect bounds)
    {
        var points = new ImagePoint[10];
        double centerX = bounds.X + bounds.Width / 2;
        double centerY = bounds.Y + bounds.Height / 2;
        for (int index = 0; index < points.Length; index++)
        {
            double radius = index % 2 == 0 ? 0.5 : 0.22;
            double angle = -Math.PI / 2 + index * Math.PI / 5;
            points[index] = new ImagePoint(centerX + Math.Cos(angle) * bounds.Width * radius,
                centerY + Math.Sin(angle) * bounds.Height * radius);
        }

        return points;
    }
}
