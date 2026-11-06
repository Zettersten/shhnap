namespace Shnapp.Core;

/// <summary>Applies camera and imported-video crop shapes without re-encoding the source.</summary>
public static class VideoOverlayCropPresets
{
    public static VideoOverlay Apply(VideoOverlay overlay, VideoOverlayShape shape)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (!Enum.IsDefined(shape)) throw new ArgumentOutOfRangeException(nameof(shape));
        if (overlay.Shape == shape) return overlay;
        if (!IsSquare(shape))
        {
            if (!IsSquare(overlay.Shape) && shape != VideoOverlayShape.Rectangle)
                return overlay with { Shape = shape };
            if (overlay.ShapeOriginalBounds is not { } original)
                return overlay with { Shape = shape };
            double baseSide = Math.Min(original.Width, original.Height);
            double scale = Math.Min(overlay.Bounds.Width,
                overlay.Bounds.Height) / baseSide;
            double width = original.Width * scale;
            double height = original.Height * scale;
            VideoRect restored = new(
                overlay.Bounds.X + overlay.Bounds.Width / 2 - width / 2,
                overlay.Bounds.Y + overlay.Bounds.Height / 2 - height / 2,
                width, height);
            return overlay with
            {
                Shape = shape,
                Bounds = restored,
                SourceCrop = overlay.ShapeOriginalCrop,
                ShapeOriginalBounds = shape == VideoOverlayShape.Rectangle
                    ? null : original,
                ShapeOriginalCrop = shape == VideoOverlayShape.Rectangle
                    ? null : overlay.ShapeOriginalCrop,
            };
        }
        if (IsSquare(overlay.Shape)) return overlay with { Shape = shape };

        VideoRect bounds = overlay.Bounds;
        double side = Math.Min(bounds.Width, bounds.Height);
        if (Math.Abs(bounds.Width - bounds.Height) < .00001)
            return overlay with { Shape = shape };
        VideoRect crop = overlay.SourceCrop ?? new VideoRect(0, 0, 1, 1);
        double cutX = (bounds.Width - side) / (2 * bounds.Width);
        double cutY = (bounds.Height - side) / (2 * bounds.Height);
        VideoRect squareCrop = new(
            crop.X + crop.Width * cutX,
            crop.Y + crop.Height * cutY,
            crop.Width * side / bounds.Width,
            crop.Height * side / bounds.Height);
        VideoRect squareBounds = new(bounds.X + (bounds.Width - side) / 2,
            bounds.Y + (bounds.Height - side) / 2, side, side);
        return overlay with
        {
            Shape = shape,
            Bounds = squareBounds,
            SourceCrop = squareCrop,
            ShapeOriginalBounds = overlay.ShapeOriginalBounds ?? overlay.Bounds,
            ShapeOriginalCrop = overlay.ShapeOriginalBounds is null
                ? overlay.SourceCrop : overlay.ShapeOriginalCrop,
        };
    }

    public static bool IsSquare(VideoOverlayShape shape) => shape is
        VideoOverlayShape.Square or VideoOverlayShape.Circle or
        VideoOverlayShape.Squircle;
}
