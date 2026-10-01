namespace Shnapp.Core;

/// <summary>Validated display metadata for a saved shnapp, without its editable layers.</summary>
/// <param name="Id">The stable identifier used to open the full document.</param>
/// <param name="Title">The saved, user-facing title.</param>
/// <param name="CreatedAt">When the capture was created.</param>
/// <param name="CaptureKind">The type of screen capture.</param>
/// <param name="Viewport">The visible canvas bounds in source pixels.</param>
public sealed record ShnappSummary(Guid Id, string Title, DateTimeOffset CreatedAt,
    CaptureKind CaptureKind, ImageRect Viewport);
