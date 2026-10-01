using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Shnapp.App;

/// <summary>Applies an editor cursor to the canvas and its overlay descendants.</summary>
public sealed class EditorCursorHost : Grid
{
    private const uint PanOpenCursorResourceId = 101;
    private const uint PanClosedCursorResourceId = 102;
    private readonly InputCursor _crosshair = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    private readonly InputCursor _move = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private readonly InputCursor _panOpen = CreatePanCursor(PanOpenCursorResourceId, InputSystemCursorShape.Hand);
    private readonly InputCursor _panClosed = CreatePanCursor(PanClosedCursorResourceId, InputSystemCursorShape.SizeAll);
    private readonly nint _panOpenHandle = LoadCursorW(GetModuleHandleW(null), (nint)PanOpenCursorResourceId);
    private readonly nint _panClosedHandle = LoadCursorW(GetModuleHandleW(null), (nint)PanClosedCursorResourceId);
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

    /// <summary>Reasserts the hand after Win2D handles a captured pointer move.</summary>
    public void ReinforcePanCursor()
    {
        if (!_panActive)
        {
            return;
        }

        nint handle = _panDragging ? _panClosedHandle : _panOpenHandle;
        if (handle != 0)
        {
            SetNativeCursor(handle);
        }
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
            : _cropActive ? _canMoveCrop ? _move : _crosshair
            : _selectionShape is InputSystemCursorShape shape ? GetSizingCursor(shape) : null;
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

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW", ExactSpelling = true)]
    private static extern nint LoadCursorW(nint module, nint resourceId);

    [DllImport("user32.dll", EntryPoint = "SetCursor", ExactSpelling = true)]
    private static extern nint SetNativeCursor(nint cursor);
}
