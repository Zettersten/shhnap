using System.Collections.Immutable;

namespace Shnapp.Core;

/// <summary>
/// Edits immutable shnapp snapshots with up to 100 undoable operations.
/// Annotations use canvas pixels anchored to the original image at (0, 0);
/// cropping changes only the visible viewport.
/// </summary>
/// <param name="document">The valid original editable document.</param>
/// <remarks>Use an editor on one thread, normally the application's UI thread.</remarks>
public sealed class DocumentEditor(ShnappDocument document)
{
    private const int HistoryLimit = 100;
    private const long ImageHistoryByteLimit = 256L * 1024 * 1024;
    private readonly List<ShnappDocument> _undo = [];
    private readonly List<ShnappDocument> _redo = [];
    private ShnappDocument _current = CreateInitialSnapshot(document);

    /// <summary>Gets the current immutable document snapshot.</summary>
    public ShnappDocument Current => _current;

    /// <summary>Gets whether an earlier snapshot can be restored.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Gets whether an undone snapshot can be restored.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Gets pixel-backed elements still reachable through undo or redo.</summary>
    public IEnumerable<Annotation> HistoricalPixelElements() =>
        _undo.Concat(_redo).SelectMany(snapshot => snapshot.Annotations)
            .Where(annotation => annotation.IsFlattened || annotation.Kind == AnnotationKind.Image);

    /// <summary>Applies an existing annotation's crop mask to a moving or resizing preview.</summary>
    /// <remarks>Does not change the document or its undo history.</remarks>
    public Annotation PreviewAnnotation(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        int index = FindAnnotation(annotation.Id);
        return index < 0 ? annotation : CarryVisibilityWithGeometry(Current.Annotations[index], annotation);
    }

    /// <summary>Occurs once after a real edit, undo, or redo, after the new state is available.</summary>
    public event EventHandler? Changed;

    /// <summary>Renames this shnapp as an undoable document edit.</summary>
    public void Rename(string title)
    {
        string normalized = ShnappTitles.Normalize(title);
        if (!string.Equals(Current.Title, normalized, StringComparison.Ordinal))
        {
            Commit(Current with { Title = normalized });
        }
    }

    /// <summary>Adds an annotation and assigns step numbers, restarting at marked steps.</summary>
    /// <param name="annotation">An annotation with a unique, nonempty identifier and finite source coordinates.</param>
    /// <exception cref="ArgumentNullException">The annotation is null.</exception>
    /// <exception cref="ArgumentException">The annotation is invalid or its identifier already exists.</exception>
    public void AddAnnotation(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (FindAnnotation(annotation.Id) >= 0)
        {
            throw new ArgumentException("An annotation with this identifier already exists.", nameof(annotation));
        }

        if (annotation.LayerOrder == 0 && Current.Annotations.Any(item => item.LayerOrder > 0))
        {
            int highest = Current.Annotations.Max(item => item.LayerOrder);
            annotation = annotation with { LayerOrder = highest == int.MaxValue ? highest : highest + 1 };
        }

        if (annotation.Kind == AnnotationKind.Step &&
            (!Contains(Current.Viewport, ContentBounds(annotation)) ||
             !Contains(Current.OriginalBounds, ContentBounds(annotation))))
        {
            annotation = annotation with { StepExpandsCanvas = true };
        }

        ShnappDocument next = ExpandCanvasForContent(Current, annotation);
        DocumentValidation.ValidateAnnotation(annotation, next);
        next = next with { Annotations = RenumberSteps(next.Annotations.Add(annotation)) };
        DocumentValidation.Validate(next);
        Commit(next);
    }

    /// <summary>
    /// Replaces an annotation with the same identifier, preserving document order.
    /// Missing identifiers and semantically unchanged replacements are no-ops.
    /// </summary>
    /// <param name="annotation">The complete replacement annotation in original-image coordinates.</param>
    /// <exception cref="ArgumentNullException">The annotation is null.</exception>
    /// <exception cref="ArgumentException">The replacement annotation is invalid.</exception>
    public void UpdateAnnotation(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        int index = FindAnnotation(annotation.Id);
        if (index < 0)
        {
            DocumentValidation.ValidateAnnotation(annotation, Current);
            return;
        }

        Annotation previous = Current.Annotations[index];
        if (previous.IsFlattened)
        {
            return;
        }
        annotation = CarryVisibilityWithGeometry(previous, annotation);
        if (annotation.Kind == AnnotationKind.Step &&
            (annotation.Start != previous.Start || annotation.StepDiameter != previous.StepDiameter) &&
            (!Contains(Current.Viewport, ContentBounds(annotation)) ||
             !Contains(Current.OriginalBounds, ContentBounds(annotation))))
        {
            annotation = annotation with { StepExpandsCanvas = true };
        }
        else if (annotation.Kind != AnnotationKind.Step && annotation.StepExpandsCanvas)
        {
            annotation = annotation with { StepExpandsCanvas = false };
        }
        if (annotation.LayerOrder == 0 && previous.LayerOrder > 0)
        {
            annotation = annotation with { LayerOrder = previous.LayerOrder };
        }
        if (annotation == previous)
        {
            return;
        }
        ShnappDocument next = ExpandCanvasForContent(Current, annotation, annotation.Id);
        DocumentValidation.ValidateAnnotation(annotation, next);
        next = next with { Annotations = RenumberSteps(next.Annotations.SetItem(index, annotation)) };
        if (next.Crop == Current.Crop && next.BaseImageCrop == Current.BaseImageCrop &&
            next.HideOriginalImage == Current.HideOriginalImage &&
            next.ExpandedCanvasBounds == Current.ExpandedCanvasBounds &&
            next.Annotations.SequenceEqual(Current.Annotations))
        {
            return;
        }

        bool boundsChanged = annotation.Kind != previous.Kind || annotation.Start != previous.Start ||
            annotation.End != previous.End || annotation.StepDiameter != previous.StepDiameter ||
            annotation.StepExpandsCanvas != previous.StepExpandsCanvas ||
            annotation.TextBoxWidth != previous.TextBoxWidth ||
            annotation.TextBoxHeight != previous.TextBoxHeight ||
            annotation.VisibilityClip != previous.VisibilityClip ||
            annotation.HiddenByCrop != previous.HiddenByCrop;
        next = boundsChanged ? TrimCanvasToContent(next) : next;
        DocumentValidation.Validate(next);
        Commit(next);
    }

    /// <summary>Removes an annotation and renumbers remaining steps; an unknown identifier is a no-op.</summary>
    /// <param name="id">The nonempty identifier of the annotation to remove.</param>
    /// <exception cref="ArgumentException">The identifier is empty.</exception>
    public void RemoveAnnotation(Guid id)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        int index = FindAnnotation(id);
        if (index >= 0 && !Current.Annotations[index].IsFlattened)
        {
            ShnappDocument next = Current with
            {
                Annotations = RenumberSteps(Current.Annotations.RemoveAt(index)),
            };
            Commit(TrimCanvasToContent(next));
        }
    }

    /// <summary>Replaces one editable element's drawing with fixed source-pixel PNG data.</summary>
    /// <returns>Whether the element was flattened.</returns>
    public bool FlattenAnnotation(Guid id, string pngBase64, ImageRect rasterizedBounds)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentNullException.ThrowIfNull(pngBase64);
        int index = FindAnnotation(id);
        if (index < 0 || Current.Annotations[index].IsFlattened)
        {
            return false;
        }

        int latestFlattenOrder = Current.Annotations.Where(annotation => annotation.IsFlattened)
            .Select(annotation => annotation.FlattenOrder).DefaultIfEmpty().Max();
        if (latestFlattenOrder == int.MaxValue)
        {
            throw new ArgumentException("The flattened background has reached its layer limit.", nameof(id));
        }

        Annotation flattened = Current.Annotations[index] with
        {
            IsFlattened = true,
            RasterizedBounds = rasterizedBounds,
            ImagePngBase64 = pngBase64,
            FlattenOrder = latestFlattenOrder + 1,
        };
        DocumentValidation.ValidateAnnotation(flattened, Current);
        ShnappDocument next = Current with
        {
            Annotations = Current.Annotations.SetItem(index, flattened),
        };
        DocumentValidation.Validate(next);
        Commit(next);
        return true;
    }

    /// <summary>Duplicates an annotation just above its source in drawing order.</summary>
    /// <remarks>The copy keeps its styles, image data, and crop visibility. It is offset by up to
    /// 12 canvas pixels where possible, and can be moved independently afterward.</remarks>
    /// <returns>The new annotation, or null if the identifier is unknown.</returns>
    public Annotation? CloneAnnotation(Guid id, Guid? cloneId = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        if (cloneId is Guid proposed && (proposed == Guid.Empty || FindAnnotation(proposed) >= 0))
        {
            throw new ArgumentException("The clone must have a unique, nonempty identifier.", nameof(cloneId));
        }

        int index = FindAnnotation(id);
        if (index < 0)
        {
            return null;
        }

        Annotation source = Current.Annotations[index];
        if (source.HiddenByCrop || source.IsFlattened)
        {
            return null;
        }

        ImageRect bounds = source.Bounds;
        ImageRect visible = Current.Viewport;
        double dx = source.Kind == AnnotationKind.Image
            ? 12 : CloneOffset(bounds.X, bounds.Right, visible.X, visible.Right);
        double dy = source.Kind == AnnotationKind.Image
            ? 12 : CloneOffset(bounds.Y, bounds.Bottom, visible.Y, visible.Bottom);
        Guid nextId = cloneId ?? Guid.NewGuid();
        while (cloneId is null && FindAnnotation(nextId) >= 0)
        {
            nextId = Guid.NewGuid();
        }

        Annotation clone = ShiftClone(source, nextId, dx, dy);
        ShnappDocument next;
        try
        {
            next = ExpandCanvasForContent(Current, clone);
        }
        catch (ArgumentException) when (source.Kind == AnnotationKind.Image && (dx != 0 || dy != 0))
        {
            // The existing image fits even when the canvas has reached its export limit.
            clone = ShiftClone(source, clone.Id, 0, 0);
            next = ExpandCanvasForContent(Current, clone);
        }
        DocumentValidation.ValidateAnnotation(clone, next);
        Commit(next with { Annotations = RenumberSteps(next.Annotations.Insert(index + 1, clone)) });
        return Current.Annotations[index + 1];
    }

    /// <summary>Moves an annotation above every other mark. Missing and topmost IDs are no-ops.</summary>
    public void MoveAnnotationToFront(Guid id)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        List<Annotation> layers = Current.OrderedAnnotations.Where(annotation => !annotation.IsFlattened).ToList();
        int index = layers.FindIndex(annotation => annotation.Id == id);
        if (index < 0 || index == layers.Count - 1)
        {
            return;
        }

        Annotation moved = layers[index];
        layers.RemoveAt(index);
        layers.Add(moved);
        Commit(Current with { Annotations = WithLayerRanks(Current.Annotations, layers) });
    }

    /// <summary>Moves an annotation below every other mark. Missing and backmost IDs are no-ops.</summary>
    public void MoveAnnotationToBack(Guid id)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        List<Annotation> layers = Current.OrderedAnnotations.Where(annotation => !annotation.IsFlattened).ToList();
        int index = layers.FindIndex(annotation => annotation.Id == id);
        if (index <= 0)
        {
            return;
        }

        Annotation moved = layers[index];
        layers.RemoveAt(index);
        layers.Insert(0, moved);
        Commit(Current with { Annotations = WithLayerRanks(Current.Annotations, layers) });
    }

    /// <summary>
    /// Intersects a source-coordinate crop with the current viewport, rounding its left/top down
    /// and right/bottom up to exact pixel edges. A positive intersection always retains at least one pixel.
    /// </summary>
    /// <param name="crop">A finite, positive source-image rectangle; it may extend outside the current viewport.</param>
    /// <exception cref="ArgumentException">The rectangle is nonfinite, empty, or does not overlap the viewport.</exception>
    /// <remarks>The original image dimensions and annotation coordinates are never changed.</remarks>
    public void ApplyCrop(ImageRect crop)
    {
        ImageRect viewport = DocumentValidation.IntersectPixelCrop(crop, Current.Viewport);
        if (viewport != Current.Viewport)
        {
            ImageRect? baseImageCrop = Current.BaseImageCrop;
            bool hideOriginalImage = Current.HideOriginalImage;
            if (baseImageCrop is ImageRect previousMask)
            {
                baseImageCrop = Intersection(previousMask, viewport);
                hideOriginalImage |= baseImageCrop is null;
            }
            hideOriginalImage |= Intersection(viewport, Current.OriginalBounds) is null;

            Commit(Current with
            {
                Crop = viewport,
                BaseImageCrop = baseImageCrop,
                HideOriginalImage = hideOriginalImage,
            });
        }
    }

    /// <summary>Restores the previous immutable snapshot, including its original step numbering.</summary>
    /// <returns>True if a snapshot was restored; false when history is empty.</returns>
    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Add(Current);
        _current = Pop(_undo);
        TrimImageHistory();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Restores the next previously undone snapshot.</summary>
    /// <returns>True if a snapshot was restored; false when redo history is empty.</returns>
    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        Remember(Current);
        _current = Pop(_redo);
        TrimImageHistory();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static ShnappDocument CreateInitialSnapshot(ShnappDocument document)
    {
        DocumentValidation.Validate(document, validateStepNumbers: false);
        ImmutableArray<Annotation> annotations = RenumberSteps(document.Annotations);
        return annotations == document.Annotations ? document : document with { Annotations = annotations };
    }

    private static ImmutableArray<Annotation> RenumberSteps(ImmutableArray<Annotation> annotations)
    {
        ImmutableArray<Annotation>.Builder? builder = null;
        int number = 0;
        for (int index = 0; index < annotations.Length; index++)
        {
            Annotation annotation = annotations[index];
            if (annotation.Kind != AnnotationKind.Step)
            {
                continue;
            }

            if (annotation.StepReset)
            {
                number = 0;
            }

            if (annotation.IsFlattened)
            {
                number = annotation.StepNumber;
                continue;
            }

            if (annotation.StepNumber != ++number)
            {
                builder ??= annotations.ToBuilder();
                builder[index] = annotation with { StepNumber = number };
            }
        }

        return builder?.ToImmutable() ?? annotations;
    }

    private static ImmutableArray<Annotation> WithLayerRanks(
        ImmutableArray<Annotation> annotations, IReadOnlyList<Annotation> drawingOrder)
    {
        var rankById = new Dictionary<Guid, int>(drawingOrder.Count);
        for (int index = 0; index < drawingOrder.Count; index++)
        {
            rankById.Add(drawingOrder[index].Id, index + 1);
        }

        var builder = annotations.ToBuilder();
        for (int index = 0; index < builder.Count; index++)
        {
            Annotation annotation = builder[index];
            if (annotation.IsFlattened)
            {
                continue;
            }
            int rank = rankById[annotation.Id];
            if (annotation.LayerOrder != rank)
            {
                builder[index] = annotation with { LayerOrder = rank };
            }
        }

        return builder.ToImmutable();
    }

    private static Annotation ShiftClone(Annotation source, Guid id, double dx, double dy)
    {
        ImageRect? clip = source.VisibilityClip is ImageRect area
            ? area with { X = area.X + dx, Y = area.Y + dy }
            : null;
        return source with
        {
            Id = id,
            Start = new ImagePoint(source.Start.X + dx, source.Start.Y + dy),
            End = new ImagePoint(source.End.X + dx, source.End.Y + dy),
            VisibilityClip = clip,
        };
    }

    private static double CloneOffset(double near, double far, double visibleNear, double visibleFar)
    {
        double forward = Math.Max(0, visibleFar - far);
        double backward = Math.Max(0, near - visibleNear);
        if (forward >= 12 || forward >= backward)
        {
            return Math.Min(12, forward);
        }

        return -Math.Min(12, backward);
    }

    private static ShnappDocument ExpandCanvasForContent(ShnappDocument document, Annotation annotation,
        Guid? updatedId = null)
    {
        ImageRect content = annotation.Kind == AnnotationKind.Step && !annotation.StepExpandsCanvas &&
            !annotation.IsFlattened
            ? annotation.Bounds
            : ContentBounds(annotation);
        ImageRect canvas = UnionPixelBounds(document.CanvasBounds, content);
        if (annotation.VisibilityClip is ImageRect clip)
        {
            canvas = UnionPixelBounds(canvas, clip);
        }
        ImageRect? crop = document.Crop;
        ImageRect? baseImageCrop = document.BaseImageCrop;
        ImmutableArray<Annotation> annotations = document.Annotations;
        if (crop is ImageRect visible && !Contains(visible, content))
        {
            if (!document.HideOriginalImage)
            {
                baseImageCrop ??= visible;
            }
            // The older layers were visible only inside the current crop. Keep that
            // mask when new content widens the viewport; the new or moved mark stays visible.
            annotations = ClipExistingAnnotations(annotations, visible, updatedId);
            crop = UnionPixelBounds(visible, content);
        }

        if (canvas == document.CanvasBounds && crop == document.Crop &&
            annotations == document.Annotations)
        {
            return document;
        }

        ShnappDocument expanded = document with
        {
            ExpandedCanvasBounds = canvas,
            Crop = crop,
            BaseImageCrop = baseImageCrop,
            Annotations = annotations,
        };
        DocumentValidation.ValidateCanvasSize(expanded);
        return expanded;
    }

    private static ShnappDocument TrimCanvasToContent(ShnappDocument document)
    {
        ImageRect original = document.OriginalBounds;
        ImageRect? crop = document.Crop;
        ImageRect? originalVisible = null;
        bool hideOriginal = document.HideOriginalImage;
        if (crop is ImageRect currentCrop && !hideOriginal)
        {
            originalVisible = Intersection(document.BaseImageCrop ?? currentCrop, original);
        }

        ImageRect? occupied = originalVisible;
        ImageRect canvas = original;
        ImmutableArray<Annotation>.Builder? clippedAnnotations = null;
        for (int index = 0; index < document.Annotations.Length; index++)
        {
            Annotation annotation = document.Annotations[index];
            if (annotation.HiddenByCrop)
            {
                continue;
            }

            ImageRect bounds = ContentBounds(annotation);
            ImageRect? visible = bounds;
            if (visible is ImageRect unclipped && annotation.VisibilityClip is ImageRect clip)
            {
                visible = Intersection(unclipped, clip);
            }

            if (visible is ImageRect clipped && crop is ImageRect activeCrop)
            {
                visible = Intersection(clipped, activeCrop);
            }

            if (visible is not ImageRect remainder)
            {
                if (crop is not null)
                {
                    clippedAnnotations ??= document.Annotations.ToBuilder();
                    clippedAnnotations[index] = annotation with { VisibilityClip = null, HiddenByCrop = true };
                }

                continue;
            }

            if (annotation.VisibilityClip != remainder &&
                (annotation.VisibilityClip is not null || crop is not null && remainder != bounds))
            {
                clippedAnnotations ??= document.Annotations.ToBuilder();
                clippedAnnotations[index] = annotation with { VisibilityClip = remainder };
            }

            ImageRect canvasContent = remainder;
            if (crop is null && annotation.Kind == AnnotationKind.Step && !annotation.IsFlattened &&
                !annotation.StepExpandsCanvas && original.Contains(annotation.Start))
            {
                canvasContent = Intersection(remainder, original) ??
                    new ImageRect(annotation.Start.X, annotation.Start.Y, 1, 1);
            }

            canvas = UnionPixelBounds(canvas, WholePixelBounds(canvasContent));
            if (crop is not null)
            {
                ImageRect pixels = WholePixelBounds(remainder);
                occupied = occupied is ImageRect existing
                    ? UnionPixelBounds(existing, pixels)
                    : pixels;
            }
        }

        if (crop is not null)
        {
            // Keep a blank result transparent when the user intentionally cropped
            // the screenshot out and then removed its last visible layer.
            crop = occupied is ImageRect content
                ? WholePixelBounds(content)
                : new ImageRect(original.X, original.Y, 1, 1);
        }

        if (crop is ImageRect visibleCrop)
        {
            canvas = UnionPixelBounds(canvas, visibleCrop);
        }

        ImageRect? baseImageCrop = originalVisible is ImageRect originalArea &&
            crop is ImageRect visibleArea && originalArea != visibleArea
                ? originalArea : null;
        if (!hideOriginal && crop == canvas && baseImageCrop is null)
        {
            crop = null;
        }

        ShnappDocument trimmed = document with
        {
            ExpandedCanvasBounds = canvas == original ? null : canvas,
            Crop = crop,
            BaseImageCrop = baseImageCrop,
            HideOriginalImage = hideOriginal,
            Annotations = clippedAnnotations?.ToImmutable() ?? document.Annotations,
        };
        DocumentValidation.ValidateCanvasSize(trimmed);
        return trimmed;
    }

    private static ImageRect ContentBounds(Annotation annotation)
    {
        if (annotation.IsFlattened)
        {
            return annotation.RasterizedBounds!.Value;
        }

        if (annotation.Kind == AnnotationKind.Step)
        {
            // The renderer draws a two-pixel white ring around the filled dot.
            double radius = annotation.StepDiameter / 2 + 2;
            return new ImageRect(annotation.Start.X - radius, annotation.Start.Y - radius,
                radius * 2, radius * 2);
        }

        ImageRect bounds = annotation.Bounds;
        if (annotation.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse && !annotation.HideOutline)
        {
            return Inflate(bounds, annotation.StrokeWidth / 2 + 1);
        }

        if (annotation.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            double shaftOutset = Math.Max(0.75, annotation.StrokeWidth / 2) + 1;
            ImageRect content = Inflate(bounds, shaftOutset);
            double dx = annotation.End.X - annotation.Start.X;
            double dy = annotation.End.Y - annotation.Start.Y;
            if (dx * dx + dy * dy > 1)
            {
                double capOutset = Math.Max(10, annotation.StrokeWidth * 3.5) +
                    Math.Max(1, annotation.StrokeWidth) / 2 + 1;
                if (annotation.EffectiveStartCap != LineEndCap.None)
                {
                    content = IncludeRadius(content, annotation.Start, capOutset);
                }
                if (annotation.EffectiveEndCap != LineEndCap.None)
                {
                    content = IncludeRadius(content, annotation.End, capOutset);
                }
            }

            return content;
        }

        return new ImageRect(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
    }

    private static ImageRect Inflate(ImageRect bounds, double amount) =>
        new(bounds.X - amount, bounds.Y - amount,
            Math.Max(1, bounds.Width) + amount * 2,
            Math.Max(1, bounds.Height) + amount * 2);

    private static ImageRect IncludeRadius(ImageRect bounds, ImagePoint center, double radius)
    {
        double left = Math.Min(bounds.X, center.X - radius);
        double top = Math.Min(bounds.Y, center.Y - radius);
        double right = Math.Max(bounds.Right, center.X + radius);
        double bottom = Math.Max(bounds.Bottom, center.Y + radius);
        return new ImageRect(left, top, right - left, bottom - top);
    }

    private static ImageRect WholePixelBounds(ImageRect area)
    {
        double x = Math.Floor(area.X);
        double y = Math.Floor(area.Y);
        return new ImageRect(x, y, Math.Ceiling(area.Right) - x, Math.Ceiling(area.Bottom) - y);
    }

    private static ImmutableArray<Annotation> ClipExistingAnnotations(
        ImmutableArray<Annotation> annotations, ImageRect visible, Guid? updatedId)
    {
        ImmutableArray<Annotation>.Builder? builder = null;
        for (int index = 0; index < annotations.Length; index++)
        {
            Annotation existing = annotations[index];
            if (existing.Id == updatedId || existing.HiddenByCrop)
            {
                continue;
            }

            ImageRect? clip = existing.VisibilityClip is ImageRect previous
                ? Intersection(previous, visible)
                : visible;
            Annotation masked = clip is ImageRect area
                ? existing with { VisibilityClip = area }
                : existing with { VisibilityClip = null, HiddenByCrop = true };
            if (masked != existing)
            {
                builder ??= annotations.ToBuilder();
                builder[index] = masked;
            }
        }

        return builder?.ToImmutable() ?? annotations;
    }

    private static Annotation CarryVisibilityWithGeometry(Annotation previous, Annotation replacement)
    {
        if (previous.HiddenByCrop)
        {
            return replacement with { VisibilityClip = null, HiddenByCrop = true };
        }

        if (previous.VisibilityClip is not ImageRect clip)
        {
            return replacement with { VisibilityClip = null, HiddenByCrop = false };
        }

        if (replacement.Start == previous.Start && replacement.End == previous.End)
        {
            return replacement with { VisibilityClip = clip, HiddenByCrop = false };
        }

        ImageRect before = previous.Bounds;
        ImageRect after = replacement.Bounds;
        double scaleX = before.Width > 0 ? after.Width / before.Width : 1;
        double scaleY = before.Height > 0 ? after.Height / before.Height : 1;
        if (scaleX <= 0 || scaleY <= 0)
        {
            return replacement with { VisibilityClip = clip, HiddenByCrop = false };
        }

        ImageRect movedClip = new(
            after.X + (clip.X - before.X) * scaleX,
            after.Y + (clip.Y - before.Y) * scaleY,
            clip.Width * scaleX, clip.Height * scaleY);
        return replacement with { VisibilityClip = movedClip, HiddenByCrop = false };
    }

    private static ImageRect UnionPixelBounds(ImageRect area, ImageRect addition)
    {
        double left = Math.Floor(Math.Min(area.X, addition.X));
        double top = Math.Floor(Math.Min(area.Y, addition.Y));
        double right = Math.Ceiling(Math.Max(area.Right, addition.Right));
        double bottom = Math.Ceiling(Math.Max(area.Bottom, addition.Bottom));
        return new ImageRect(left, top, right - left, bottom - top);
    }

    private static bool Contains(ImageRect outer, ImageRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y &&
        inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static ImageRect? Intersection(ImageRect first, ImageRect second)
    {
        double left = Math.Max(first.X, second.X);
        double top = Math.Max(first.Y, second.Y);
        double right = Math.Min(first.Right, second.Right);
        double bottom = Math.Min(first.Bottom, second.Bottom);
        return right > left && bottom > top
            ? new ImageRect(left, top, right - left, bottom - top)
            : null;
    }

    private int FindAnnotation(Guid id)
    {
        for (int index = 0; index < Current.Annotations.Length; index++)
        {
            if (Current.Annotations[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    private void Commit(ShnappDocument next)
    {
        if (next.Title == Current.Title && next.Crop == Current.Crop && next.BaseImageCrop == Current.BaseImageCrop &&
            next.HideOriginalImage == Current.HideOriginalImage &&
            next.ExpandedCanvasBounds == Current.ExpandedCanvasBounds &&
            next.Annotations.SequenceEqual(Current.Annotations))
        {
            return;
        }

        Remember(Current);
        _redo.Clear();
        _current = next;
        TrimImageHistory();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Remember(ShnappDocument snapshot)
    {
        if (_undo.Count == HistoryLimit)
        {
            _undo.RemoveAt(0);
        }

        _undo.Add(snapshot);
    }

    private static ShnappDocument Pop(List<ShnappDocument> history)
    {
        ShnappDocument snapshot = history[^1];
        history.RemoveAt(history.Count - 1);
        return snapshot;
    }

    private void TrimImageHistory()
    {
        // Snapshots share immutable PNG strings; count each distinct payload only once.
        while (ImageHistoryBytes() > ImageHistoryByteLimit)
        {
            if (_undo.Count > 1)
            {
                _undo.RemoveAt(0);
            }
            else if (_redo.Count > 0)
            {
                _redo.RemoveAt(0);
            }
            else
            {
                break;
            }
        }
    }

    private long ImageHistoryBytes()
    {
        var seen = new HashSet<string>(ReferenceEqualityComparer.Instance);
        long bytes = 0;
        Include(Current);
        foreach (ShnappDocument snapshot in _undo)
        {
            Include(snapshot);
        }

        foreach (ShnappDocument snapshot in _redo)
        {
            Include(snapshot);
        }

        return bytes;

        void Include(ShnappDocument snapshot)
        {
            foreach (Annotation annotation in snapshot.Annotations)
            {
                if (annotation.ImagePngBase64 is string payload && seen.Add(payload))
                {
                    bytes += (long)payload.Length * sizeof(char);
                }
            }
        }
    }
}
