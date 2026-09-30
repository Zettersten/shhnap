using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Shnapp.Core;

/// <summary>Identifies the origin of a shnapp.</summary>
public enum CaptureKind
{
    /// <summary>A selected application window.</summary>
    Window,
    /// <summary>The monitor containing the pointer.</summary>
    FullScreen,
    /// <summary>A rectangular screen region.</summary>
    Region,
}

/// <summary>The tools supported by the initial editor.</summary>
public enum AnnotationKind
{
    /// <summary>Text placed directly on the image.</summary>
    Text,
    /// <summary>An automatically numbered circular marker.</summary>
    Step,
    /// <summary>A plain line.</summary>
    Line,
    /// <summary>A line with an arrowhead.</summary>
    Arrow,
    /// <summary>An outlined or filled rectangle.</summary>
    Rectangle,
    /// <summary>An outlined or filled ellipse.</summary>
    Ellipse,
    /// <summary>An opaque privacy rectangle.</summary>
    Redaction,
}

/// <summary>A position in original-image pixels, never window DIPs.</summary>
/// <param name="X">Horizontal coordinate.</param>
/// <param name="Y">Vertical coordinate.</param>
public readonly record struct ImagePoint(double X, double Y);

/// <summary>An axis-aligned area in original-image pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct ImageRect(double X, double Y, double Width, double Height)
{
    /// <summary>Gets the right edge.</summary>
    [JsonIgnore]
    public double Right => X + Width;

    /// <summary>Gets the bottom edge.</summary>
    [JsonIgnore]
    public double Bottom => Y + Height;

    /// <summary>Creates a normalized rectangle from either drag direction.</summary>
    /// <param name="start">First point.</param>
    /// <param name="end">Second point.</param>
    /// <returns>The normalized area.</returns>
    public static ImageRect FromPoints(ImagePoint start, ImagePoint end) =>
        new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));

    /// <summary>Tests whether a point is inside the area, including its edges.</summary>
    /// <param name="point">Point to test.</param>
    /// <returns>Whether the point is contained.</returns>
    public bool Contains(ImagePoint point) =>
        point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;
}

/// <summary>An immutable, serializable annotation in source-image coordinates.</summary>
public sealed record Annotation
{
    /// <summary>Gets the stable annotation identifier.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Gets the tool that created the annotation.</summary>
    public AnnotationKind Kind { get; init; }

    /// <summary>Gets the first point or text/step origin.</summary>
    public ImagePoint Start { get; init; }

    /// <summary>Gets the second point for lines and regions.</summary>
    public ImagePoint End { get; init; }

    /// <summary>Gets the foreground color as packed ARGB.</summary>
    public uint StrokeArgb { get; init; } = 0xFFE5484D;

    /// <summary>Gets the fill color as packed ARGB; zero means transparent.</summary>
    public uint FillArgb { get; init; }

    /// <summary>Gets the step number color as packed ARGB.</summary>
    /// <remarks>Zero uses a white step number, preserving documents that omitted this property.</remarks>
    public uint StepTextArgb { get; init; }

    /// <summary>Gets the line width in source pixels.</summary>
    public double StrokeWidth { get; init; } = 3;

    /// <summary>Gets whether a line or arrow has an arrowhead at its starting point.</summary>
    public bool StartArrow { get; init; }

    /// <summary>Gets whether a line has an arrowhead at its ending point.</summary>
    /// <remarks>Legacy <see cref="AnnotationKind.Arrow"/> annotations always have an ending arrowhead.</remarks>
    public bool EndArrow { get; init; }

    /// <summary>Gets the user-entered text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets the font family for text and step labels.</summary>
    public string FontFamily { get; init; } = "Segoe UI Variable Text";

    /// <summary>Gets the font size in source pixels.</summary>
    public double FontSize { get; init; } = 18;

    /// <summary>Gets the font weight on the OpenType 100–900 scale.</summary>
    public int FontWeight { get; init; } = 600;

    /// <summary>Gets whether text is italic.</summary>
    public bool Italic { get; init; }

    /// <summary>Gets the step diameter in source pixels.</summary>
    public double StepDiameter { get; init; } = 28;

    /// <summary>Gets the assigned step number.</summary>
    public int StepNumber { get; init; }

    /// <summary>Gets the normalized geometric bounds.</summary>
    [JsonIgnore]
    public ImageRect Bounds => ImageRect.FromPoints(Start, End);
}

/// <summary>The editable source of truth for a saved shnapp.</summary>
public sealed record ShnappDocument
{
    /// <summary>Gets the schema version used by local persistence.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Gets the stable, path-safe document identifier.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Gets the user-facing title.</summary>
    public string Title { get; init; } = "Untitled shnapp";

    /// <summary>Gets the capture time.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Gets the capture origin.</summary>
    public CaptureKind CaptureKind { get; init; }

    /// <summary>Gets the original image width.</summary>
    public int PixelWidth { get; init; }

    /// <summary>Gets the original image height.</summary>
    public int PixelHeight { get; init; }

    /// <summary>Gets the non-destructive crop, or null for the complete image.</summary>
    public ImageRect? Crop { get; init; }

    /// <summary>Gets whether the exported image includes a window shadow.</summary>
    public bool HasWindowShadow { get; init; }

    /// <summary>Gets the immutable ordered annotations.</summary>
    public ImmutableArray<Annotation> Annotations { get; init; } = [];

    /// <summary>Gets the currently visible source-image area.</summary>
    [JsonIgnore]
    public ImageRect Viewport => Crop ?? new(0, 0, PixelWidth, PixelHeight);
}

/// <summary>User-scoped preferences. Startup is deliberately opt-in.</summary>
public sealed record ShnappSettings
{
    /// <summary>Gets whether Shnapp starts at sign-in.</summary>
    public bool StartOnLogin { get; init; }

    /// <summary>Gets the selected interface theme: System, Light, or Dark.</summary>
    public string Theme { get; init; } = "System";

    /// <summary>Gets whether a new shnapp is copied automatically.</summary>
    public bool AutoCopy { get; init; }

    /// <summary>Gets whether window shnapps receive a shadow by default.</summary>
    public bool WindowShadow { get; init; } = true;
}
