using Microsoft.UI.Input;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.Foundation;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private void UpdateCanvasElementCursor(Point canvasPosition)
    {
        if (_editor is null || _panning || _textBox is not null || _tool == EditorTool.Crop)
        {
            CanvasHost.SetSelectionCursor(null);
            return;
        }

        if (_moving is Annotation moving)
        {
            CanvasHost.SetSelectionCursor(_resizeHandle == ResizeHandle.None
                ? InputSystemCursorShape.SizeAll
                : CursorForHandle(_resizeHandle, moving));
            return;
        }

        if (_tool != EditorTool.Select)
        {
            CanvasHost.SetSelectionCursor(null);
            return;
        }

        if (_dragStart is not null || _textDragStart is not null)
        {
            CanvasHost.SetSelectionCursor(null);
            return;
        }

        if (SelectedAnnotation() is Annotation selected)
        {
            ResizeHandle handle = HitResizeHandle(selected, canvasPosition);
            if (handle != ResizeHandle.None)
            {
                CanvasHost.SetSelectionCursor(CursorForHandle(handle, selected));
                return;
            }
        }

        if (ImagePosition(canvasPosition, allowOutside: _tool == EditorTool.Text) is ImagePoint point &&
            HitTopAnnotation(point) is not null)
        {
            CanvasHost.SetSelectionCursor(InputSystemCursorShape.SizeAll);
            return;
        }

        CanvasHost.SetSelectionCursor(null);
    }

    private static InputSystemCursorShape CursorForHandle(ResizeHandle handle, Annotation annotation) => handle switch
    {
        ResizeHandle.Top or ResizeHandle.Bottom => InputSystemCursorShape.SizeNorthSouth,
        ResizeHandle.Left or ResizeHandle.Right => InputSystemCursorShape.SizeWestEast,
        ResizeHandle.TopLeft or ResizeHandle.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
        ResizeHandle.TopRight or ResizeHandle.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
        ResizeHandle.Start or ResizeHandle.End => LineHandleCursor(annotation),
        _ => InputSystemCursorShape.SizeAll,
    };

    private static InputSystemCursorShape LineHandleCursor(Annotation annotation)
    {
        double dx = annotation.End.X - annotation.Start.X;
        double dy = annotation.End.Y - annotation.Start.Y;
        double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        int sector = (int)Math.Round(angle / 45);
        return (((sector % 4) + 4) % 4) switch
        {
            0 => InputSystemCursorShape.SizeWestEast,
            1 => InputSystemCursorShape.SizeNorthwestSoutheast,
            2 => InputSystemCursorShape.SizeNorthSouth,
            _ => InputSystemCursorShape.SizeNortheastSouthwest,
        };
    }
}
