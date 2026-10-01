namespace Shnapp.Core;

/// <summary>The adjustment needed to align one element edge or center to a guide.</summary>
/// <param name="Correction">The distance to add in image pixels.</param>
/// <param name="Guide">The matched guide coordinate, or null when no guide is close enough.</param>
public readonly record struct AxisSnap(double Correction, double? Guide);

/// <summary>Finds the closest vertical or horizontal alignment guide in image coordinates.</summary>
public static class SmartGuideSnapper
{
    /// <summary>Snaps the nearest of an element's leading edge, center, or trailing edge.</summary>
    /// <param name="leading">The leading edge in image pixels.</param>
    /// <param name="center">The center in image pixels.</param>
    /// <param name="trailing">The trailing edge in image pixels.</param>
    /// <param name="guides">Coordinates from other element edges and centers.</param>
    /// <param name="tolerance">Maximum screen-space distance converted to image pixels.</param>
    /// <returns>The closest adjustment and the guide it matched.</returns>
    public static AxisSnap FindClosest(double leading, double center, double trailing,
        IReadOnlyList<double> guides, double tolerance)
    {
        double closestDistance = Math.Max(0, tolerance);
        double correction = 0;
        double? matched = null;
        ReadOnlySpan<double> positions = stackalloc double[] { leading, center, trailing };
        for (int index = 0; index < guides.Count; index++)
        {
            double guide = guides[index];
            if (!double.IsFinite(guide))
            {
                continue;
            }

            foreach (double position in positions)
            {
                double difference = guide - position;
                double distance = Math.Abs(difference);
                if (distance <= closestDistance && (matched is null || distance < closestDistance))
                {
                    closestDistance = distance;
                    correction = difference;
                    matched = guide;
                }
            }
        }

        return new AxisSnap(correction, matched);
    }
}
