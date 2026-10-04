using System.Buffers.Text;

namespace Shnapp.Core;

internal static class DocumentValidation
{
    internal const int SchemaVersion = 1;
    private const int MaximumPastedPngBytes = 32 * 1024 * 1024;
    private const int MaximumPastedImageSide = 12_000;
    private const long MaximumPastedImagePixels = 40_000_000;

    internal static void Validate(ShnappDocument document, bool validateStepNumbers = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        Require(document.SchemaVersion == SchemaVersion, "Unsupported document schema version.", nameof(document));
        Require(document.Id != Guid.Empty, "A document identifier cannot be empty.", nameof(document));
        Require(document.Title is not null, "A document title cannot be null.", nameof(document));
        Require(Enum.IsDefined(document.CaptureKind), "The capture kind is not supported.", nameof(document));
        Require(document.PixelWidth > 0 && document.PixelHeight > 0,
            "Original image dimensions must be positive.", nameof(document));
        Require(!document.Annotations.IsDefault, "The annotation array must be initialized.", nameof(document));

        ImageRect canvas = document.CanvasBounds;
        ValidateRectangle(canvas, nameof(document));
        Require(IsInteger(canvas.X) && IsInteger(canvas.Y) && IsInteger(canvas.Width) && IsInteger(canvas.Height) &&
            canvas.X <= 0 && canvas.Y <= 0 &&
            canvas.Right >= document.PixelWidth && canvas.Bottom >= document.PixelHeight,
            "The canvas must use whole pixels and contain the original image.", nameof(document));
        ValidateCanvasSize(document);

        if (document.Crop is ImageRect crop)
        {
            ValidateRectangle(crop, nameof(document));
            Require(Contains(canvas, crop), "The crop must be inside the canvas.", nameof(document));
            Require(IsInteger(crop.X) && IsInteger(crop.Y) && IsInteger(crop.Width) && IsInteger(crop.Height),
                "The crop must use exact whole-pixel bounds.", nameof(document));
        }

        if (document.BaseImageCrop is ImageRect baseCrop)
        {
            ValidateRectangle(baseCrop, nameof(document));
            Require(!document.HideOriginalImage && document.Crop is not null && Contains(canvas, baseCrop) &&
                IsInteger(baseCrop.X) && IsInteger(baseCrop.Y) &&
                IsInteger(baseCrop.Width) && IsInteger(baseCrop.Height),
                "The original-image crop must use whole pixels inside the canvas.", nameof(document));
        }

        Require(!document.HideOriginalImage || document.Crop is not null,
            "Hiding the original image requires a crop.", nameof(document));

        var identifiers = new HashSet<Guid>();
        int stepNumber = 0;
        foreach (Annotation annotation in document.Annotations)
        {
            ValidateAnnotation(annotation, document);
            if (annotation.VisibilityClip is null && !annotation.HiddenByCrop)
            {
                ImageRect content = annotation.Kind == AnnotationKind.Step && annotation.StepExpandsCanvas
                    ? new ImageRect(annotation.Start.X - annotation.StepDiameter / 2,
                        annotation.Start.Y - annotation.StepDiameter / 2,
                        annotation.StepDiameter, annotation.StepDiameter)
                    : annotation.Bounds;
                Require(Contains(canvas, content),
                    "Visible annotations must fit inside the expanded canvas.", nameof(document));
            }
            Require(identifiers.Add(annotation.Id), "Annotation identifiers must be unique.", nameof(document));
            if (annotation.Kind == AnnotationKind.Step)
            {
                if (annotation.StepReset)
                {
                    stepNumber = 0;
                }

                stepNumber++;
                Require(!validateStepNumbers || annotation.StepNumber == stepNumber,
                    "Step annotations must be numbered consecutively in document order.", nameof(document));
            }
        }
    }

    internal static void ValidateAnnotation(Annotation annotation, ShnappDocument document)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        Require(annotation.Id != Guid.Empty, "An annotation identifier cannot be empty.", nameof(annotation));
        Require(Enum.IsDefined(annotation.Kind), "The annotation kind is not supported.", nameof(annotation));
        Require(Enum.IsDefined(annotation.RedactionMode), "The redaction mode is not supported.", nameof(annotation));
        Require(Enum.IsDefined(annotation.StartCap) && Enum.IsDefined(annotation.EndCap),
            "The line end cap is not supported.", nameof(annotation));
        Require(Enum.IsDefined(annotation.LinePattern), "The line pattern is not supported.", nameof(annotation));
        Require(Enum.IsDefined(annotation.StepLabelFormat), "The step label format is not supported.", nameof(annotation));
        Require(Enum.IsDefined(annotation.TextAlignment) && Enum.IsDefined(annotation.TextTruncation) &&
            Enum.IsDefined(annotation.TextTransform),
            "A text formatting option is not supported.", nameof(annotation));
        Require(annotation.Kind == AnnotationKind.Step || !annotation.StepReset,
            "Only a step can restart numbering.", nameof(annotation));
        Require(annotation.Kind == AnnotationKind.Step || !annotation.StepExpandsCanvas,
            "Only a step can expand its full circle beyond the capture.", nameof(annotation));
        Require(annotation.LayerOrder >= 0,
            "An annotation layer order cannot be negative.", nameof(annotation));
        // New marks may extend the canvas; document validation verifies the final bounds.
        // A trim can also leave cropped-out annotations outside the visible canvas.
        ValidatePoint(annotation.Start, nameof(annotation));
        ValidatePoint(annotation.End, nameof(annotation));
        if (annotation.Kind == AnnotationKind.Image)
        {
            ImageRect bounds = annotation.Bounds;
            Require(bounds.Width >= 1 && bounds.Height >= 1,
                "A pasted image must have positive visible dimensions.", nameof(annotation));
            ValidatePastedPng(annotation.ImagePngBase64, nameof(annotation));
        }
        else
        {
            Require(annotation.ImagePngBase64 is null,
                "Only a pasted image can contain PNG data.", nameof(annotation));
        }
        if (annotation.VisibilityClip is ImageRect visibilityClip)
        {
            ValidateRectangle(visibilityClip, nameof(annotation));
            Require(Contains(document.CanvasBounds, visibilityClip),
                "A visibility clip must fit inside the canvas.", nameof(annotation));
            Require(!annotation.HiddenByCrop,
                "A completely hidden annotation cannot also have a visibility clip.", nameof(annotation));
        }
        Require(double.IsFinite(annotation.StrokeWidth) && annotation.StrokeWidth >= 0,
            "Stroke width must be finite and nonnegative.", nameof(annotation));
        Require(annotation.Kind is not (AnnotationKind.Rectangle or AnnotationKind.Ellipse) ||
            !annotation.HideOutline || (annotation.FillArgb >> 24) != 0,
            "A shape needs an outline or fill.", nameof(annotation));
        Require(double.IsFinite(annotation.FontSize) && annotation.FontSize > 0,
            "Font size must be finite and positive.", nameof(annotation));
        Require(double.IsFinite(annotation.StepDiameter) && annotation.StepDiameter > 0,
            "Step diameter must be finite and positive.", nameof(annotation));
        Require(annotation.FontWeight is >= 100 and <= 900,
            "Font weight must be between 100 and 900.", nameof(annotation));
        Require(annotation.Text is not null, "Annotation text cannot be null.", nameof(annotation));
        Require(double.IsFinite(annotation.TextBoxWidth) &&
            (annotation.TextBoxWidth == 0 || annotation.TextBoxWidth is >= 48 and <= 12_000),
            "A text box width must be zero or between 48 and 12,000 pixels.", nameof(annotation));
        Require(double.IsFinite(annotation.TextBoxHeight) &&
            (annotation.TextBoxHeight == 0 || annotation.TextBoxHeight is >= 24 and <= 12_000) &&
            (annotation.TextBoxHeight == 0 || annotation.TextBoxWidth > 0),
            "A fixed text box height must be zero or between 24 and 12,000 pixels and requires a width.",
            nameof(annotation));
        if (annotation.Kind == AnnotationKind.Text && annotation.TextBoxHeight > 0)
        {
            Require(Math.Abs(annotation.End.X - (annotation.Start.X + annotation.TextBoxWidth)) < 0.01 &&
                Math.Abs(annotation.End.Y - (annotation.Start.Y + annotation.TextBoxHeight)) < 0.01,
                "A fixed text box's end point must match its width and height.", nameof(annotation));
        }
        Require(double.IsFinite(annotation.TextLineHeight) &&
            (annotation.TextLineHeight == 0 || annotation.TextLineHeight is >= 8 and <= 320),
            "A text line height must be zero or between 8 and 320 pixels.", nameof(annotation));
        Require(double.IsFinite(annotation.TextLetterSpacing) && annotation.TextLetterSpacing is >= 0 and <= 50,
            "Text letter spacing must be between 0 and 50 pixels.", nameof(annotation));
        Require(!string.IsNullOrWhiteSpace(annotation.FontFamily),
            "A font family is required.", nameof(annotation));
    }

    internal static void ValidateSettings(ShnappSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Require(settings.Theme is "System" or "Light" or "Dark",
            "Theme must be System, Light, or Dark.", nameof(settings));
    }

    internal static ImageRect IntersectPixelCrop(ImageRect crop, ImageRect viewport)
    {
        ValidateRectangle(crop, nameof(crop));
        double left = Math.Max(crop.X, viewport.X);
        double top = Math.Max(crop.Y, viewport.Y);
        double right = Math.Min(crop.Right, viewport.Right);
        double bottom = Math.Min(crop.Bottom, viewport.Bottom);
        Require(right > left && bottom > top, "The crop must overlap the current viewport.", nameof(crop));

        left = Math.Max(viewport.X, Math.Floor(left));
        top = Math.Max(viewport.Y, Math.Floor(top));
        right = Math.Min(viewport.Right, Math.Ceiling(right));
        bottom = Math.Min(viewport.Bottom, Math.Ceiling(bottom));
        return new ImageRect(left, top, right - left, bottom - top);
    }

    private static void ValidateRectangle(ImageRect rectangle, string parameterName)
    {
        Require(double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y) &&
            double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height) &&
            double.IsFinite(rectangle.Right) && double.IsFinite(rectangle.Bottom),
            "Rectangle coordinates and edges must be finite.", parameterName);
        Require(rectangle.Width > 0 && rectangle.Height > 0,
            "A rectangle must have positive width and height.", parameterName);
    }

    private static void ValidatePoint(ImagePoint point, string parameterName)
    {
        Require(double.IsFinite(point.X) && double.IsFinite(point.Y),
            "Annotation points must be finite.", parameterName);
    }

    internal static void ValidateCanvasSize(ShnappDocument document)
    {
        ImageRect canvas = document.CanvasBounds;
        double width = canvas.Width + (document.HasWindowShadow ? 64 : 0);
        double height = canvas.Height + (document.HasWindowShadow ? 72 : 0);
        Require(double.IsFinite(width) && double.IsFinite(height) &&
            width <= 16_384 && height <= 16_384 && width * height <= 64_000_000,
            "The expanded canvas cannot exceed 16,384 pixels per side or 64 megapixels.", nameof(document));
    }

    private static void ValidatePastedPng(string? encoded, string parameterName)
    {
        int maximumEncodedLength = ((MaximumPastedPngBytes + 2) / 3) * 4;
        Require(encoded is { Length: >= 44 } && encoded.Length <= maximumEncodedLength &&
            encoded.Length % 4 == 0 && encoded.StartsWith("iVBORw0KGgo", StringComparison.Ordinal),
            "A pasted image needs a PNG no larger than 32 MiB.", parameterName);
        Require(Base64.IsValid(encoded.AsSpan(), out int decodedLength) &&
            decodedLength <= MaximumPastedPngBytes,
            "A pasted image needs complete PNG base64 data no larger than 32 MiB.", parameterName);

        Span<byte> header = stackalloc byte[33];
        Require(Convert.TryFromBase64Chars(encoded.AsSpan(0, 44), header, out int bytesWritten) &&
            bytesWritten == header.Length && header[8] == 0 && header[9] == 0 &&
            header[10] == 0 && header[11] == 13 &&
            header[12] == (byte)'I' && header[13] == (byte)'H' &&
            header[14] == (byte)'D' && header[15] == (byte)'R',
            "A pasted image needs a valid PNG header.", parameterName);

        uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        Require(width is > 0 and <= MaximumPastedImageSide &&
            height is > 0 and <= MaximumPastedImageSide &&
            (long)width * height <= MaximumPastedImagePixels,
            "A pasted image exceeds the 40-megapixel limit.", parameterName);
    }

    private static bool Contains(ImageRect outer, ImageRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y &&
        inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static bool IsInteger(double value) => value == Math.Truncate(value);

    private static void Require(bool condition, string message, string parameterName)
    {
        if (!condition)
        {
            throw new ArgumentException(message, parameterName);
        }
    }
}
