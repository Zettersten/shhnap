using Microsoft.UI.Xaml.Input;
using Shnapp.Core;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private readonly List<ImagePoint> _lineTrace = [];

    private void UpdateLineDraftFromPointer(PointerRoutedEventArgs args, ImagePoint current,
        bool shiftHeld)
    {
        if (shiftHeld || !_toolStyles[_tool].FreehandLine)
        {
            UpdateLineDraft(current, shiftHeld);
            return;
        }

        // WinUI can coalesce pointer moves. Its history is in chronological order
        // and includes the current point, so preserve the path even on a busy frame.
        var history = args.GetIntermediatePoints(DrawingCanvas);
        if (history.Count == 0)
        {
            UpdateLineDraft(current, false);
            return;
        }
        foreach (var sample in history)
        {
            if (ImagePosition(sample.Position, allowOutside: true) is ImagePoint point)
            {
                UpdateLineDraft(point, false);
            }
        }
    }

    private void UpdateLineDraft(ImagePoint point, bool shiftHeld)
    {
        if (_draft is not { Kind: AnnotationKind.Line or AnnotationKind.Arrow } line)
        {
            return;
        }

        if (shiftHeld)
        {
            _lineTrace.Clear();
            _lineTrace.Add(line.Start);
            _draft = line with
            {
                End = LineGeometry.ConstrainAxis(line.Start, point),
                LinePoints = [],
            };
            return;
        }

        if (!_toolStyles[_tool].FreehandLine)
        {
            _draft = line with { End = point };
            return;
        }

        if (_lineTrace.Count == 0) _lineTrace.Add(line.Start);
        ImagePoint last = _lineTrace[^1];
        double dx = point.X - last.X;
        double dy = point.Y - last.Y;
        if (dx * dx + dy * dy >= Math.Pow(1.5 / Math.Max(0.2, _scale), 2))
        {
            if (_lineTrace.Count >= 1024)
            {
                for (int index = _lineTrace.Count - 2; index > 0; index -= 2)
                {
                    _lineTrace.RemoveAt(index);
                }
            }
            _lineTrace.Add(point);
        }
        else if (_lineTrace.Count > 1)
        {
            _lineTrace[^1] = point;
        }

        _draft = _lineTrace.Count < 2
            ? line with { End = point }
            : line with { End = _lineTrace[^1], LinePoints = LineGeometry.Smooth(_lineTrace) };
    }
}
