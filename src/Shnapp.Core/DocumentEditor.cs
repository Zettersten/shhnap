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

    /// <summary>Adds an annotation and assigns step numbers, restarting at marked steps.</summary>
    /// <param name="annotation">An annotation with a unique, nonempty identifier and valid source coordinates.</param>
    /// <exception cref="ArgumentNullException">The annotation is null.</exception>
    /// <exception cref="ArgumentException">The annotation is invalid or its identifier already exists.</exception>
    public void AddAnnotation(Annotation annotation)
    {
        DocumentValidation.ValidateAnnotation(annotation, Current);
        if (FindAnnotation(annotation.Id) >= 0)
        {
            throw new ArgumentException("An annotation with this identifier already exists.", nameof(annotation));
        }

        if (annotation.LayerOrder == 0 && Current.Annotations.Any(item => item.LayerOrder > 0))
        {
            int highest = Current.Annotations.Max(item => item.LayerOrder);
            annotation = annotation with { LayerOrder = highest == int.MaxValue ? highest : highest + 1 };
        }

        ShnappDocument next = ExpandCanvasForImage(Current, annotation);
        Commit(next with { Annotations = RenumberSteps(next.Annotations.Add(annotation)) });
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
        DocumentValidation.ValidateAnnotation(annotation, Current);
        int index = FindAnnotation(annotation.Id);
        if (index < 0)
        {
            return;
        }

        Annotation previous = Current.Annotations[index];
        annotation = CarryVisibilityWithGeometry(previous, annotation);
        if (annotation.LayerOrder == 0 && previous.LayerOrder > 0)
        {
            annotation = annotation with { LayerOrder = previous.LayerOrder };
        }
        DocumentValidation.ValidateAnnotation(annotation, Current);
        ShnappDocument next = ExpandCanvasForImage(Current, annotation, annotation.Id);
        Commit(next with { Annotations = RenumberSteps(next.Annotations.SetItem(index, annotation)) });
    }

    /// <summary>Removes an annotation and renumbers remaining steps; an unknown identifier is a no-op.</summary>
    /// <param name="id">The nonempty identifier of the annotation to remove.</param>
    /// <exception cref="ArgumentException">The identifier is empty.</exception>
    public void RemoveAnnotation(Guid id)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        int index = FindAnnotation(id);
        if (index >= 0)
        {
            Commit(Current with { Annotations = RenumberSteps(Current.Annotations.RemoveAt(index)) });
        }
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
        if (source.HiddenByCrop)
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
            next = ExpandCanvasForImage(Current, clone);
        }
        catch (ArgumentException) when (source.Kind == AnnotationKind.Image && (dx != 0 || dy != 0))
        {
            // The existing image fits even when the canvas has reached its export limit.
            clone = ShiftClone(source, clone.Id, 0, 0);
            next = ExpandCanvasForImage(Current, clone);
        }
        DocumentValidation.ValidateAnnotation(clone, next);
        Commit(next with { Annotations = RenumberSteps(next.Annotations.Insert(index + 1, clone)) });
        return Current.Annotations[index + 1];
    }

    /// <summary>Moves an annotation above every other mark. Missing and topmost IDs are no-ops.</summary>
    public void MoveAnnotationToFront(Guid id)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        List<Annotation> layers = Current.OrderedAnnotations.ToList();
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
        List<Annotation> layers = Current.OrderedAnnotations.ToList();
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

    private static ShnappDocument ExpandCanvasForImage(ShnappDocument document, Annotation annotation,
        Guid? updatedId = null)
    {
        if (annotation.Kind != AnnotationKind.Image)
        {
            return document;
        }

        ImageRect canvas = UnionPixelBounds(document.CanvasBounds, annotation.Bounds);
        ImageRect? crop = document.Crop;
        ImageRect? baseImageCrop = document.BaseImageCrop;
        ImmutableArray<Annotation> annotations = document.Annotations;
        if (crop is ImageRect visible && !Contains(visible, annotation.Bounds))
        {
            if (!document.HideOriginalImage)
            {
                baseImageCrop ??= visible;
            }
            // The older layers were visible only inside the current crop. Keep that
            // mask when this image widens the viewport; the new or moved image stays visible.
            annotations = ClipExistingAnnotations(annotations, visible, updatedId);
            crop = UnionPixelBounds(visible, annotation.Bounds);
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
        if (next.Crop == Current.Crop && next.BaseImageCrop == Current.BaseImageCrop &&
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
