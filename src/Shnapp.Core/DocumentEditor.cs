using System.Collections.Immutable;

namespace Shnapp.Core;

/// <summary>
/// Edits immutable shnapp snapshots with up to 100 undoable operations.
/// Annotations stay in original-image coordinates; cropping changes only the viewport.
/// </summary>
/// <param name="document">The valid original editable document.</param>
/// <remarks>Use an editor on one thread, normally the application's UI thread.</remarks>
public sealed class DocumentEditor(ShnappDocument document)
{
    private const int HistoryLimit = 100;
    private readonly List<ShnappDocument> _undo = [];
    private readonly List<ShnappDocument> _redo = [];
    private ShnappDocument _current = CreateInitialSnapshot(document);

    /// <summary>Gets the current immutable document snapshot.</summary>
    public ShnappDocument Current => _current;

    /// <summary>Gets whether an earlier snapshot can be restored.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Gets whether an undone snapshot can be restored.</summary>
    public bool CanRedo => _redo.Count > 0;

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

        Commit(Current with { Annotations = RenumberSteps(Current.Annotations.Add(annotation)) });
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

        Commit(Current with { Annotations = RenumberSteps(Current.Annotations.SetItem(index, annotation)) });
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
            Commit(Current with { Crop = viewport });
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
        if (next.Crop == Current.Crop && next.Annotations.SequenceEqual(Current.Annotations))
        {
            return;
        }

        Remember(Current);
        _redo.Clear();
        _current = next;
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
}
