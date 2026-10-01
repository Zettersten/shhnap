using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Shnapp.Core;
using Windows.Foundation;
using Windows.UI;

namespace Shnapp.App;

public sealed partial class MainPage
{
    // Guides are derived from current annotations at drag start; they are never stored in a shnapp.
    private readonly List<double> _smartGuideXs = [];
    private readonly List<double> _smartGuideYs = [];
    private double? _activeGuideX;
    private double? _activeGuideY;

    private void BeginSmartGuideDrag(Annotation moving)
    {
        ClearSmartGuides();
        if (_editor is null)
        {
            return;
        }

        ImageRect viewport = _editor.Current.Viewport;
        AddGuideCoordinates(new Rect(viewport.X, viewport.Y, viewport.Width, viewport.Height));
        foreach (Annotation annotation in _editor.Current.Annotations)
        {
            if (annotation.Id == moving.Id || annotation.HiddenByCrop ||
                VisibleGuideBounds(annotation, viewport) is not Rect bounds)
            {
                continue;
            }

            AddGuideCoordinates(bounds);
        }
    }

    private Rect? VisibleGuideBounds(Annotation annotation, ImageRect viewport)
    {
        Rect bounds = SelectionBounds(annotation);
        double left = Math.Max(bounds.X, viewport.X);
        double top = Math.Max(bounds.Y, viewport.Y);
        double right = Math.Min(bounds.Right, viewport.Right);
        double bottom = Math.Min(bounds.Bottom, viewport.Bottom);
        if (annotation.VisibilityClip is ImageRect clip)
        {
            left = Math.Max(left, clip.X);
            top = Math.Max(top, clip.Y);
            right = Math.Min(right, clip.Right);
            bottom = Math.Min(bottom, clip.Bottom);
        }

        return right >= left && bottom >= top
            ? new Rect(left, top, right - left, bottom - top)
            : null;
    }

    private void AddGuideCoordinates(Rect bounds)
    {
        _smartGuideXs.Add(bounds.X);
        _smartGuideXs.Add(bounds.X + bounds.Width / 2);
        _smartGuideXs.Add(bounds.Right);
        _smartGuideYs.Add(bounds.Y);
        _smartGuideYs.Add(bounds.Y + bounds.Height / 2);
        _smartGuideYs.Add(bounds.Bottom);
    }

    private (double X, double Y) SnapMovingAnnotation(Annotation moving, double dx, double dy,
        bool allowX, bool allowY)
    {
        _activeGuideX = null;
        _activeGuideY = null;
        Rect bounds = SelectionBounds(moving);
        ImageRect viewport = _editor!.Current.Viewport;
        bool bounded = moving.Kind is not (AnnotationKind.Image or AnnotationKind.Text);
        if (bounded)
        {
            dx = ClampMovement(dx, viewport.X - bounds.X, viewport.Right - bounds.Right);
            dy = ClampMovement(dy, viewport.Y - bounds.Y, viewport.Bottom - bounds.Bottom);
        }

        // A fixed distance in DIPs makes guides equally easy to catch at any zoom level.
        double tolerance = 7 / Math.Max(0.01, _scale);
        if (allowX && _smartGuideXs.Count > 0)
        {
            AxisSnap snap = SmartGuideSnapper.FindClosest(bounds.X + dx,
                bounds.X + bounds.Width / 2 + dx, bounds.Right + dx, _smartGuideXs, tolerance);
            if (snap.Guide is double guide)
            {
                double suggested = dx + snap.Correction;
                double adjusted = bounded
                    ? ClampMovement(suggested, viewport.X - bounds.X, viewport.Right - bounds.Right)
                    : suggested;
                if (Math.Abs(adjusted - suggested) < 0.001)
                {
                    dx = adjusted;
                    _activeGuideX = guide;
                }
            }
        }

        if (allowY && _smartGuideYs.Count > 0)
        {
            AxisSnap snap = SmartGuideSnapper.FindClosest(bounds.Y + dy,
                bounds.Y + bounds.Height / 2 + dy, bounds.Bottom + dy, _smartGuideYs, tolerance);
            if (snap.Guide is double guide)
            {
                double suggested = dy + snap.Correction;
                double adjusted = bounded
                    ? ClampMovement(suggested, viewport.Y - bounds.Y, viewport.Bottom - bounds.Bottom)
                    : suggested;
                if (Math.Abs(adjusted - suggested) < 0.001)
                {
                    dy = adjusted;
                    _activeGuideY = guide;
                }
            }
        }

        return (dx, dy);
    }

    private Annotation ResizeWithSmartGuides(Annotation moving, ResizeHandle handle,
        ImagePoint point, bool maintainProportions)
    {
        _activeGuideX = null;
        _activeGuideY = null;
        double? xGuide = null;
        double? yGuide = null;
        double tolerance = 7 / Math.Max(0.01, _scale);
        if (handle is not ResizeHandle.Top and not ResizeHandle.Bottom && _smartGuideXs.Count > 0)
        {
            AxisSnap snap = SmartGuideSnapper.FindClosest(point.X, point.X, point.X,
                _smartGuideXs, tolerance);
            point = point with { X = point.X + snap.Correction };
            xGuide = snap.Guide;
        }

        if (handle is not ResizeHandle.Left and not ResizeHandle.Right && _smartGuideYs.Count > 0)
        {
            AxisSnap snap = SmartGuideSnapper.FindClosest(point.Y, point.Y, point.Y,
                _smartGuideYs, tolerance);
            point = point with { Y = point.Y + snap.Correction };
            yGuide = snap.Guide;
        }

        Annotation resized = ResizeAnnotation(moving, handle, point, maintainProportions);
        Rect result = SelectionBounds(resized);
        if (xGuide is double x && TouchesGuide(result.X, result.X + result.Width / 2, result.Right, x))
        {
            _activeGuideX = x;
        }

        if (yGuide is double y && TouchesGuide(result.Y, result.Y + result.Height / 2, result.Bottom, y))
        {
            _activeGuideY = y;
        }

        return resized;
    }

    private static bool TouchesGuide(double leading, double center, double trailing, double guide) =>
        Math.Abs(leading - guide) < 0.5 || Math.Abs(center - guide) < 0.5 ||
        Math.Abs(trailing - guide) < 0.5;

    private void DrawSmartGuides(CanvasDrawingSession drawing)
    {
        if (_editor is null || (_activeGuideX is null && _activeGuideY is null))
        {
            return;
        }

        ImageRect viewport = _editor.Current.Viewport;
        Color guide = _accessibility.HighContrast ? Colors.Yellow : Color.FromArgb(220, 24, 156, 255);
        float stroke = (float)(1 / Math.Max(0.01, _scale));
        if (_activeGuideX is double x)
        {
            drawing.DrawLine((float)x, (float)viewport.Y, (float)x, (float)viewport.Bottom, guide, stroke);
        }

        if (_activeGuideY is double y)
        {
            drawing.DrawLine((float)viewport.X, (float)y, (float)viewport.Right, (float)y, guide, stroke);
        }
    }

    private void ClearSmartGuides()
    {
        _smartGuideXs.Clear();
        _smartGuideYs.Clear();
        _activeGuideX = null;
        _activeGuideY = null;
    }
}
