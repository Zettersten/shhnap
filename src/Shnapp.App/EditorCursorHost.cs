using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Shnapp.App;

/// <summary>Owns pointer input and the cursor above the canvas renderer.</summary>
public sealed class EditorCursorHost : Grid
{
    private const uint PanOpenCursorResourceId = 101;
    private const uint PanClosedCursorResourceId = 102;
    private readonly InputCursor _crosshair = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    private readonly InputCursor _move = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private readonly InputCursor _panOpen = CreatePanCursor(PanOpenCursorResourceId, InputSystemCursorShape.Hand);
    private readonly InputCursor _panClosed = CreatePanCursor(PanClosedCursorResourceId, InputSystemCursorShape.SizeAll);
    private readonly Dictionary<InputSystemCursorShape, InputCursor> _sizingCursors = [];
    private bool _cropActive;
    private bool _canMoveCrop;
    private bool _panActive;
    private bool _panDragging;
    private InputSystemCursorShape? _selectionShape;

    private static InputCursor CreatePanCursor(uint resourceId, InputSystemCursorShape fallback)
    {
        // The native resources contain an open palm and a closed grabbing hand.
        // Keep a system cursor fallback for builds without the resource.
        try
        {
            return InputDesktopResourceCursor.CreateFromModule(Environment.ProcessPath!, resourceId);
        }
        catch (Exception)
        {
            return InputSystemCursor.Create(fallback);
        }
    }

    /// <summary>Shows a crosshair while drawing crops and a move cursor over a crop.</summary>
    public void SetCropCursor(bool cropActive, bool canMove)
    {
        _cropActive = cropActive;
        _canMoveCrop = canMove;
        UpdateCursor();
    }

    /// <summary>Shows an open hand while Space is held and a closed hand during a drag.</summary>
    public void SetPanCursor(bool active, bool dragging = false)
    {
        _panActive = active;
        _panDragging = dragging;
        UpdateCursor();
    }

    /// <summary>Shows the correct directional cursor over an annotation's resize handle.</summary>
    public void SetSelectionCursor(InputSystemCursorShape? shape)
    {
        if (_selectionShape == shape)
        {
            return;
        }

        _selectionShape = shape;
        UpdateCursor();
    }

    private void UpdateCursor()
    {
        InputCursor? cursor = _panActive ? _panDragging ? _panClosed : _panOpen
            : _selectionShape is InputSystemCursorShape shape ? GetSizingCursor(shape)
            : _cropActive ? _canMoveCrop ? _move : _crosshair : null;
        if (!ReferenceEquals(ProtectedCursor, cursor))
        {
            ProtectedCursor = cursor;
        }
    }

    private InputCursor GetSizingCursor(InputSystemCursorShape shape)
    {
        if (shape == InputSystemCursorShape.SizeAll)
        {
            return _move;
        }

        if (!_sizingCursors.TryGetValue(shape, out InputCursor? cursor))
        {
            cursor = InputSystemCursor.Create(shape);
            _sizingCursors.Add(shape, cursor);
        }

        return cursor;
    }
}
