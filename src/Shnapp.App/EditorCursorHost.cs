using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Shnapp.App.Editor;

namespace Shnapp.App;

/// <summary>Owns pointer input and the cursor above the canvas renderer.</summary>
public sealed class EditorCursorHost : Grid
{
    private const uint PanOpenCursorResourceId = 101;
    private const uint PanClosedCursorResourceId = 102;
    private const uint ShapeCursorResourceId = 103;
    private const uint LineCursorResourceId = 104;
    private const uint StepCursorResourceId = 105;
    private const uint PrivacyCursorResourceId = 106;
    private readonly InputCursor _crosshair = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    private readonly InputCursor _text = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    private readonly InputCursor _move = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private readonly InputCursor _panOpen = CreateResourceCursor(PanOpenCursorResourceId, InputSystemCursorShape.Hand);
    private readonly InputCursor _panClosed = CreateResourceCursor(PanClosedCursorResourceId, InputSystemCursorShape.SizeAll);
    private readonly InputCursor _shape = CreateResourceCursor(ShapeCursorResourceId, InputSystemCursorShape.Cross);
    private readonly InputCursor _line = CreateResourceCursor(LineCursorResourceId, InputSystemCursorShape.Cross);
    private readonly InputCursor _step = CreateResourceCursor(StepCursorResourceId, InputSystemCursorShape.Pin);
    private readonly InputCursor _privacy = CreateResourceCursor(PrivacyCursorResourceId, InputSystemCursorShape.Cross);
    private readonly Dictionary<InputSystemCursorShape, InputCursor> _sizingCursors = [];
    private EditorTool _tool;
    private bool _panActive;
    private bool _panDragging;
    private InputSystemCursorShape? _selectionShape;


    private static InputCursor CreateResourceCursor(uint resourceId, InputSystemCursorShape fallback)
    {
        // Use a system cursor when a native cursor resource is unavailable.
        try
        {
            return InputDesktopResourceCursor.CreateFromModule(Environment.ProcessPath!, resourceId);
        }
        catch (Exception)
        {
            return InputSystemCursor.Create(fallback);
        }
    }

    /// <summary>Shows a cursor that identifies the active placement tool.</summary>
    internal void SetToolCursor(EditorTool tool)
    {
        _tool = tool;
        _selectionShape = null;
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
            : _tool == EditorTool.Crop ? _crosshair
            : _selectionShape is InputSystemCursorShape shape ? GetSizingCursor(shape)
            : _tool switch
            {
                EditorTool.Text => _text,
                EditorTool.Step => _step,
                EditorTool.Line or EditorTool.Arrow => _line,
                EditorTool.Rectangle or EditorTool.Square or EditorTool.Ellipse or EditorTool.Circle => _shape,
                EditorTool.Redaction => _privacy,
                _ => null,
            };
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
