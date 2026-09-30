namespace Shnapp.Core;

internal static class DocumentValidation
{
    internal const int SchemaVersion = 1;

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

        if (document.Crop is ImageRect crop)
        {
            ValidateRectangle(crop, nameof(document));
            Require(crop.X >= 0 && crop.Y >= 0 &&
                crop.Right <= document.PixelWidth && crop.Bottom <= document.PixelHeight,
                "The crop must be inside the original image.", nameof(document));
            Require(IsInteger(crop.X) && IsInteger(crop.Y) && IsInteger(crop.Width) && IsInteger(crop.Height),
                "The crop must use exact whole-pixel bounds.", nameof(document));
        }

        var identifiers = new HashSet<Guid>();
        int stepNumber = 0;
        foreach (Annotation annotation in document.Annotations)
        {
            ValidateAnnotation(annotation, document);
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
        Require(annotation.Kind == AnnotationKind.Step || !annotation.StepReset,
            "Only a step can restart numbering.", nameof(annotation));
        ValidatePoint(annotation.Start, document, nameof(annotation));
        ValidatePoint(annotation.End, document, nameof(annotation));
        Require(double.IsFinite(annotation.StrokeWidth) && annotation.StrokeWidth >= 0,
            "Stroke width must be finite and nonnegative.", nameof(annotation));
        Require(double.IsFinite(annotation.FontSize) && annotation.FontSize > 0,
            "Font size must be finite and positive.", nameof(annotation));
        Require(double.IsFinite(annotation.StepDiameter) && annotation.StepDiameter > 0,
            "Step diameter must be finite and positive.", nameof(annotation));
        Require(annotation.FontWeight is >= 100 and <= 900,
            "Font weight must be between 100 and 900.", nameof(annotation));
        Require(annotation.Text is not null, "Annotation text cannot be null.", nameof(annotation));
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

    private static void ValidatePoint(ImagePoint point, ShnappDocument document, string parameterName) =>
        Require(double.IsFinite(point.X) && double.IsFinite(point.Y) &&
            point.X >= 0 && point.X <= document.PixelWidth &&
            point.Y >= 0 && point.Y <= document.PixelHeight,
            "Annotation points must be finite and inside the original image.", parameterName);

    private static bool IsInteger(double value) => value == Math.Truncate(value);

    private static void Require(bool condition, string message, string parameterName)
    {
        if (!condition)
        {
            throw new ArgumentException(message, parameterName);
        }
    }
}
