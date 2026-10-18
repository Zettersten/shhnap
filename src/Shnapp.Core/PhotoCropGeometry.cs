using System.Buffers.Binary;

namespace Shnapp.Core;

/// <summary>Maps an editable image's visible canvas rectangle to whole source pixels.</summary>
public static class PhotoCropGeometry
{
    /// <summary>
    /// Narrows an image to the intersection with <paramref name="selectedDestination"/>
    /// without changing the PNG or the scale of pixels that remain visible.
    /// </summary>
    /// <exception cref="ArgumentException">The selection cannot retain a visible source pixel.</exception>
    public static Annotation Crop(Annotation image, ImageRect selectedDestination)
    {
        var (destination, source, _) = ReadGeometry(image);
        ValidateRectangle(selectedDestination, nameof(selectedDestination));

        double left = Math.Max(destination.X, selectedDestination.X);
        double top = Math.Max(destination.Y, selectedDestination.Y);
        double right = Math.Min(destination.Right, selectedDestination.Right);
        double bottom = Math.Min(destination.Bottom, selectedDestination.Bottom);
        if (right <= left || bottom <= top)
        {
            throw new ArgumentException("The crop must overlap the image.", nameof(selectedDestination));
        }

        double pixelScaleX = destination.Width / source.Width;
        double pixelScaleY = destination.Height / source.Height;
        double sourceLeft = Math.Clamp(RoundSourceEdge(source.X + (left - destination.X) / pixelScaleX),
            source.X, source.Right);
        double sourceTop = Math.Clamp(RoundSourceEdge(source.Y + (top - destination.Y) / pixelScaleY),
            source.Y, source.Bottom);
        double sourceRight = Math.Clamp(RoundSourceEdge(source.X + (right - destination.X) / pixelScaleX),
            source.X, source.Right);
        double sourceBottom = Math.Clamp(RoundSourceEdge(source.Y + (bottom - destination.Y) / pixelScaleY),
            source.Y, source.Bottom);
        if (sourceRight <= sourceLeft || sourceBottom <= sourceTop ||
            (sourceRight - sourceLeft) * pixelScaleX < 1 ||
            (sourceBottom - sourceTop) * pixelScaleY < 1)
        {
            throw new ArgumentException("The crop is too small to keep a visible source pixel.",
                nameof(selectedDestination));
        }

        var croppedSource = new ImageRect(sourceLeft, sourceTop,
            sourceRight - sourceLeft, sourceBottom - sourceTop);
        if (croppedSource == source)
        {
            return image;
        }

        double destinationLeft = destination.X + (sourceLeft - source.X) * pixelScaleX;
        double destinationTop = destination.Y + (sourceTop - source.Y) * pixelScaleY;
        double destinationRight = destination.X + (sourceRight - source.X) * pixelScaleX;
        double destinationBottom = destination.Y + (sourceBottom - source.Y) * pixelScaleY;
        return image with
        {
            Start = new ImagePoint(destinationLeft, destinationTop),
            End = new ImagePoint(destinationRight, destinationBottom),
            ImageSourceCrop = croppedSource,
        };
    }

    /// <summary>Restores every source pixel around the current image at its existing pixel scale.</summary>
    public static Annotation Reset(Annotation image)
    {
        (ImageRect destination, ImageRect source, ImageRect full) = ReadGeometry(image);
        if (image.ImageSourceCrop is null)
        {
            return image;
        }

        double pixelScaleX = destination.Width / source.Width;
        double pixelScaleY = destination.Height / source.Height;
        double left = destination.X - source.X * pixelScaleX;
        double top = destination.Y - source.Y * pixelScaleY;
        double right = left + full.Width * pixelScaleX;
        double bottom = top + full.Height * pixelScaleY;
        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(right) || !double.IsFinite(bottom))
        {
            throw new ArgumentException("The restored image cannot fit in canvas coordinates.", nameof(image));
        }

        return image with
        {
            Start = new ImagePoint(left, top),
            End = new ImagePoint(right, bottom),
            ImageSourceCrop = null,
        };
    }

    /// <summary>Keeps surviving source pixels at their displayed positions after a photo crop.</summary>
    public static Annotation PreserveTransformedPlacement(Annotation original, Annotation changed)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(changed);
        if (changed == original ||
            original.RotationDegrees == 0 && !original.FlipHorizontal && !original.FlipVertical)
        {
            return changed;
        }

        ImageRect bounds = changed.Bounds;
        ImagePoint localCenter = new(bounds.X + bounds.Width / 2,
            bounds.Y + bounds.Height / 2);
        ImagePoint worldCenter = AnnotationRotation.TransformContent(original, localCenter);
        double dx = worldCenter.X - localCenter.X;
        double dy = worldCenter.Y - localCenter.Y;
        return changed with
        {
            Start = new(changed.Start.X + dx, changed.Start.Y + dy),
            End = new(changed.End.X + dx, changed.End.Y + dy),
        };
    }

    private static double RoundSourceEdge(double position) =>
        Math.Round(position, 0, MidpointRounding.AwayFromZero);

    internal static (ImageRect Destination, ImageRect Source, ImageRect Full) ReadGeometry(Annotation image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Kind != AnnotationKind.Image || image.IsFlattened)
        {
            throw new ArgumentException("Only an editable image can be cropped.", nameof(image));
        }

        ImageRect destination = image.Bounds;
        ValidateRectangle(destination, nameof(image));
        if (destination.Width < 1 || destination.Height < 1)
        {
            throw new ArgumentException("The image needs visible canvas dimensions.", nameof(image));
        }
        (uint width, uint height) = ReadPngSize(image.ImagePngBase64);
        var full = new ImageRect(0, 0, width, height);
        ImageRect source = image.ImageSourceCrop ?? full;
        ValidateRectangle(source, nameof(image));
        if (!IsInteger(source.X) || !IsInteger(source.Y) ||
            !IsInteger(source.Width) || !IsInteger(source.Height) ||
            source.X < 0 || source.Y < 0 || source.Right > width || source.Bottom > height)
        {
            throw new ArgumentException("The image crop must fit whole pixels inside its PNG.", nameof(image));
        }

        return (destination, source, full);
    }

    private static (uint Width, uint Height) ReadPngSize(string? encoded)
    {
        if (encoded is null || encoded.Length < 44)
        {
            throw new ArgumentException("The image needs PNG pixels.", nameof(encoded));
        }

        Span<byte> header = stackalloc byte[33];
        bool decoded = Convert.TryFromBase64Chars(encoded.AsSpan(0, 44), header,
            out int bytesWritten);
        if (!decoded || bytesWritten != 33 ||
            header[0] != 137 || header[1] != 80 || header[2] != 78 || header[3] != 71 ||
            header[4] != 13 || header[5] != 10 || header[6] != 26 || header[7] != 10 ||
            BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13 ||
            header[12] != (byte)'I' || header[13] != (byte)'H' ||
            header[14] != (byte)'D' || header[15] != (byte)'R')
        {
            throw new ArgumentException("The image needs a valid PNG header.", nameof(encoded));
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        if (width == 0 || height == 0 || width > 12_000 || height > 12_000 ||
            (long)width * height > 40_000_000)
        {
            throw new ArgumentException("The image source dimensions exceed the photo limit.", nameof(encoded));
        }

        return (width, height);
    }

    private static void ValidateRectangle(ImageRect rectangle, string parameterName)
    {
        if (!double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y) ||
            !double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height) ||
            rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            throw new ArgumentException("The crop rectangle must be finite and positive.", parameterName);
        }
    }

    private static bool IsInteger(double value) => value == Math.Truncate(value);
}
