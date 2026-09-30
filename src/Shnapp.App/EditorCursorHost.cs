using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Shnapp.App;

/// <summary>Applies an editor cursor to the canvas and its overlay descendants.</summary>
public sealed class EditorCursorHost : Grid
{
    private readonly InputCursor _crosshair = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    private readonly InputCursor _move = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);

    /// <summary>Shows a crosshair while drawing crops and a move cursor over a crop.</summary>
    public void SetCropCursor(bool cropActive, bool canMove) =>
        ProtectedCursor = cropActive ? canMove ? _move : _crosshair : null;
}
