using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Shnapp.App;

/// <summary>Applies an editor cursor to the canvas and its overlay descendants.</summary>
public sealed class EditorCursorHost : Grid
{
    private readonly InputCursor _crosshair = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    private readonly InputCursor _move = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private readonly InputCursor _grab = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    private bool _cropActive;
    private bool _canMoveCrop;
    private bool _panActive;

    /// <summary>Shows a crosshair while drawing crops and a move cursor over a crop.</summary>
    public void SetCropCursor(bool cropActive, bool canMove)
    {
        _cropActive = cropActive;
        _canMoveCrop = canMove;
        UpdateCursor();
    }

    /// <summary>Shows a hand while Space is held or the canvas is being panned.</summary>
    public void SetPanCursor(bool active)
    {
        _panActive = active;
        UpdateCursor();
    }

    private void UpdateCursor() =>
        ProtectedCursor = _panActive ? _grab : _cropActive ? _canMoveCrop ? _move : _crosshair : null;
}
