using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Shnapp.Core;
using Windows.Foundation;
using Windows.UI;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private readonly HashSet<Guid> _selectedIds = [];

    private bool HasMultipleSelectedAnnotations => _selectedIds.Count > 1;

    private bool IsAnnotationSelected(Guid id) => _selectedIds.Contains(id);

    private Annotation[] SelectedAnnotations() => _editor?.Current.OrderedAnnotations
        .Where(annotation => _selectedIds.Contains(annotation.Id) &&
            !annotation.IsFlattened && !annotation.HiddenByCrop)
        .ToArray() ?? [];

    private void SelectOnlyAnnotation(Guid? id)
    {
        _selectedIds.Clear();
        if (id is Guid selected && IsEditableAnnotation(selected))
        {
            _selectedIds.Add(selected);
        }

        SyncSingleSelection();
    }

    private void ToggleAnnotationSelection(Guid id)
    {
        if (!_selectedIds.Remove(id) && IsEditableAnnotation(id))
        {
            _selectedIds.Add(id);
        }

        SyncSingleSelection();
    }

    private void SetSelectedAnnotations(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        HashSet<Guid> requested = ids.ToHashSet();
        _selectedIds.Clear();
        if (_editor is not null)
        {
            foreach (Annotation annotation in _editor.Current.Annotations)
            {
                if (requested.Contains(annotation.Id) && !annotation.IsFlattened &&
                    !annotation.HiddenByCrop)
                {
                    _selectedIds.Add(annotation.Id);
                }
            }
        }

        SyncSingleSelection();
    }

    private void SelectAllAnnotations()
    {
        _selectedIds.Clear();
        if (_editor is not null)
        {
            ImageRect viewport = _editor.Current.Viewport;
            foreach (Annotation annotation in _editor.Current.Annotations)
            {
                if (!annotation.IsFlattened && !annotation.HiddenByCrop &&
                    VisibleSelectionBounds(annotation, viewport) is not null)
                {
                    _selectedIds.Add(annotation.Id);
                }
            }
        }

        SyncSingleSelection();
    }

    private void ClearAnnotationSelection()
    {
        _selectedIds.Clear();
        _selectedId = null;
    }

    private bool IsEditableAnnotation(Guid id) => _editor?.Current.Annotations.Any(annotation =>
        annotation.Id == id && !annotation.IsFlattened && !annotation.HiddenByCrop) == true;

    private void SyncSingleSelection() =>
        _selectedId = _selectedIds.Count == 1 ? _selectedIds.First() : null;

    private Rect? VisibleSelectionBounds(Annotation annotation, ImageRect? viewport = null)
    {
        if (annotation.HiddenByCrop)
        {
            return null;
        }

        Rect bounds = SelectionBounds(annotation);
        double left = bounds.Left;
        double top = bounds.Top;
        double right = bounds.Right;
        double bottom = bounds.Bottom;
        if (annotation.VisibilityClip is ImageRect clip)
        {
            left = Math.Max(left, clip.X);
            top = Math.Max(top, clip.Y);
            right = Math.Min(right, clip.Right);
            bottom = Math.Min(bottom, clip.Bottom);
        }

        if (viewport is ImageRect canvas)
        {
            left = Math.Max(left, canvas.X);
            top = Math.Max(top, canvas.Y);
            right = Math.Min(right, canvas.Right);
            bottom = Math.Min(bottom, canvas.Bottom);
        }

        return right >= left && bottom >= top
            ? new Rect(left, top, right - left, bottom - top)
            : null;
    }

    private void DrawGroupSelection(CanvasDrawingSession drawing, IReadOnlyList<Annotation> annotations,
        bool previewOutsideViewport)
    {
        Rect? union = null;
        foreach (Annotation annotation in annotations)
        {
            if (VisibleSelectionBounds(annotation,
                    previewOutsideViewport ? null : _editor?.Current.Viewport) is not Rect bounds)
            {
                continue;
            }

            union = union is Rect previous
                ? new Rect(Math.Min(previous.Left, bounds.Left), Math.Min(previous.Top, bounds.Top),
                    Math.Max(previous.Right, bounds.Right) - Math.Min(previous.Left, bounds.Left),
                    Math.Max(previous.Bottom, bounds.Bottom) - Math.Min(previous.Top, bounds.Top))
                : bounds;
        }

        if (union is not Rect content)
        {
            return;
        }

        double padding = 5 / Math.Max(0.01, _scale);
        Rect frame = new(content.X - padding, content.Y - padding,
            Math.Max(content.Width, 1 / _scale) + padding * 2,
            Math.Max(content.Height, 1 / _scale) + padding * 2);
        Color border = CanvasThemeColor("ShnappBlueBrush");
        Color backdrop = _accessibility.HighContrast
            ? CanvasThemeColor("ShnappOnBlueBrush") : Colors.White;
        if (!_accessibility.HighContrast)
        {
            drawing.FillRectangle(frame, Color.FromArgb(24, border.R, border.G, border.B));
        }

        drawing.DrawRectangle(frame, backdrop, (float)(4 / _scale));
        drawing.DrawRectangle(frame, border, (float)(2 / _scale));
    }

    private static Annotation ShiftAnnotation(Annotation annotation, double dx, double dy) =>
        annotation with
        {
            Start = new ImagePoint(annotation.Start.X + dx, annotation.Start.Y + dy),
            End = new ImagePoint(annotation.End.X + dx, annotation.End.Y + dy),
            VisibilityClip = annotation.VisibilityClip is ImageRect clip
                ? clip with { X = clip.X + dx, Y = clip.Y + dy }
                : null,
        };
}
