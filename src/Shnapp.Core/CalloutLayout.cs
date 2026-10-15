using System.Collections.Immutable;

namespace Shnapp.Core;

/// <summary>Places numbered text outside the captured image without covering another element.</summary>
internal static class CalloutLayout
{
    private const double ImageGap = 36;
    private const double BoxGap = 14;

    internal static ImmutableArray<Annotation> Reflow(ShnappDocument document)
    {
        if (!document.Annotations.Any(item => item.Kind == AnnotationKind.Callout && !item.IsFlattened))
        {
            return document.Annotations;
        }

        ImageRect image = document.OriginalBounds;
        var occupied = document.Annotations
            .Where(item => item.Kind != AnnotationKind.Callout && !item.HiddenByCrop)
            .Select(ObstacleBounds).ToList();
        var builder = document.Annotations.ToBuilder();
        int number = 0;
        for (int index = 0; index < builder.Count; index++)
        {
            Annotation item = builder[index];
            if (item.Kind != AnnotationKind.Callout)
            {
                continue;
            }

            number++;
            if (item.IsFlattened || item.HiddenByCrop)
            {
                occupied.Add(item.CalloutBox());
                continue;
            }

            (double width, double height) = MeasureBox(item);
            (CalloutSide side, ImageRect box) = Place(item.Start, width, height, image, occupied);
            Annotation placed = item with
            {
                End = new ImagePoint(box.X, box.Y),
                TextBoxWidth = width,
                TextBoxHeight = height,
                CalloutSide = side,
                CalloutNumber = number,
            };
            builder[index] = placed;
            occupied.Add(box);
        }

        return builder.ToImmutable();
    }

    internal static ImageRect CalloutBox(this Annotation item) =>
        new(item.End.X, item.End.Y, item.TextBoxWidth, item.TextBoxHeight);

    private static (double Width, double Height) MeasureBox(Annotation item)
    {
        if (string.IsNullOrWhiteSpace(item.Text))
        {
            throw new ArgumentException("Enter annotation text before placing it.", nameof(item));
        }

        double width = Math.Clamp(Math.Ceiling(200 + item.FontSize * 6), 300, 620);
        double available = width - 36;
        double lineWidth = 0;
        int lines = 1;
        foreach (char character in item.Text)
        {
            if (character == '\n')
            {
                lines++;
                lineWidth = 0;
                continue;
            }

            // Favor extra vertical room over a clipped final line across installed fonts.
            double advance = item.FontSize * (char.IsWhiteSpace(character) ? 0.42
                : character >= 0x2E80 ? 1.1
                : char.IsUpper(character) ? 1.0 : 0.78) + item.TextLetterSpacing;
            if (lineWidth > 0 && lineWidth + advance > available)
            {
                lines++;
                lineWidth = 0;
            }

            lineWidth += advance;
        }

        double lineHeight = item.TextLineHeight > 0 ? item.TextLineHeight : item.FontSize * 1.55;
        double height = Math.Ceiling(56 + lines * lineHeight);
        if (height > 12_000)
        {
            throw new ArgumentException("The annotation text is too tall at this font size.", nameof(item));
        }

        return (width, Math.Max(76, height));
    }

    private static (CalloutSide Side, ImageRect Box) Place(ImagePoint anchor, double width,
        double height, ImageRect image, IReadOnlyList<ImageRect> occupied)
    {
        CalloutSide[] sides = [CalloutSide.Right, CalloutSide.Left,
            CalloutSide.Bottom, CalloutSide.Top];
        double bestScore = double.PositiveInfinity;
        CalloutSide bestSide = CalloutSide.Right;
        ImageRect best = default;
        foreach (CalloutSide side in sides)
        {
            ImageRect initial = side switch
            {
                CalloutSide.Right => new(image.Right + ImageGap,
                    Math.Round(anchor.Y - height / 2), width, height),
                CalloutSide.Left => new(image.X - ImageGap - width,
                    Math.Round(anchor.Y - height / 2), width, height),
                CalloutSide.Bottom => new(Math.Round(anchor.X - width / 2),
                    image.Bottom + ImageGap, width, height),
                _ => new(Math.Round(anchor.X - width / 2),
                    image.Y - ImageGap - height, width, height),
            };
            double edgeDistance = side switch
            {
                CalloutSide.Right => image.Right - anchor.X,
                CalloutSide.Left => anchor.X - image.X,
                CalloutSide.Bottom => image.Bottom - anchor.Y,
                _ => anchor.Y - image.Y,
            };
            for (int attempt = 0; attempt < 128; attempt++)
            {
                int slot = attempt == 0 ? 0 : (attempt + 1) / 2 * (attempt % 2 == 1 ? 1 : -1);
                ImageRect candidate = side is CalloutSide.Left or CalloutSide.Right
                    ? initial with { Y = initial.Y + slot * (height + BoxGap) }
                    : initial with { X = initial.X + slot * (width + BoxGap) };
                if (occupied.Any(obstacle => Overlaps(candidate, Inflate(obstacle, BoxGap / 2))))
                {
                    continue;
                }

                ImagePoint edge = side switch
                {
                    CalloutSide.Right => new(image.Right, anchor.Y),
                    CalloutSide.Left => new(image.X, anchor.Y),
                    CalloutSide.Bottom => new(anchor.X, image.Bottom),
                    _ => new(anchor.X, image.Y),
                };
                ImagePoint entry = side switch
                {
                    CalloutSide.Right => new(candidate.X,
                        Math.Clamp(anchor.Y, candidate.Y + 18, candidate.Bottom - 18)),
                    CalloutSide.Left => new(candidate.Right,
                        Math.Clamp(anchor.Y, candidate.Y + 18, candidate.Bottom - 18)),
                    CalloutSide.Bottom => new(
                        Math.Clamp(anchor.X, candidate.X + 18, candidate.Right - 18), candidate.Y),
                    _ => new(Math.Clamp(anchor.X, candidate.X + 18, candidate.Right - 18),
                        candidate.Bottom),
                };
                if (occupied.Any(obstacle => SegmentCrossesRectangle(edge, entry,
                    Inflate(obstacle, 3))))
                {
                    continue;
                }

                double shift = Math.Abs(slot) * (side is CalloutSide.Left or CalloutSide.Right
                    ? height + BoxGap : width + BoxGap);
                double score = Math.Max(0, edgeDistance) + shift * 0.45;
                score += occupied.Count(obstacle => SegmentCrossesRectangle(anchor, edge,
                    Inflate(obstacle, 8))) * 500;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestSide = side;
                    best = candidate;
                }

                break;
            }
        }

        if (!double.IsFinite(bestScore))
        {
            throw new ArgumentException("No clear margin remains for this annotation.");
        }

        return (bestSide, best);
    }

    private static ImageRect ObstacleBounds(Annotation item)
    {
        ImageRect bounds = item.IsFlattened ? item.RasterizedBounds ?? item.Bounds : item.Bounds;
        return item.Kind == AnnotationKind.Step
            ? new ImageRect(item.Start.X - item.StepDiameter / 2,
                item.Start.Y - item.StepDiameter / 2, item.StepDiameter, item.StepDiameter)
            : bounds;
    }

    private static ImageRect Inflate(ImageRect rect, double amount) =>
        new(rect.X - amount, rect.Y - amount,
            rect.Width + amount * 2, rect.Height + amount * 2);

    private static bool Overlaps(ImageRect first, ImageRect second) =>
        first.X < second.Right && first.Right > second.X &&
        first.Y < second.Bottom && first.Bottom > second.Y;

    private static bool SegmentCrossesRectangle(ImagePoint start, ImagePoint end, ImageRect rect)
    {
        if (rect.Contains(start) || rect.Contains(end))
        {
            return true;
        }

        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double near = 0;
        double far = 1;
        return Clip(-dx, start.X - rect.X) && Clip(dx, rect.Right - start.X) &&
            Clip(-dy, start.Y - rect.Y) && Clip(dy, rect.Bottom - start.Y);

        bool Clip(double direction, double distance)
        {
            if (Math.Abs(direction) < 1e-9)
            {
                return distance >= 0;
            }

            double t = distance / direction;
            if (direction < 0)
            {
                near = Math.Max(near, t);
            }
            else
            {
                far = Math.Min(far, t);
            }

            return near <= far;
        }
    }
}
