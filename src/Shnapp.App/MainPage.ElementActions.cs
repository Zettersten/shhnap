using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.System;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private bool _cloneInProgress;

    private void Canvas_RightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (_editor is null ||
            ImagePosition(args.GetPosition(DrawingCanvas)) is not ImagePoint position)
        {
            return;
        }

        Annotation? hit = _editor.Current.OrderedAnnotations.Reverse()
            .FirstOrDefault(annotation => HitAnnotation(annotation, position));
        if (hit is null)
        {
            // The underlying screen grab is never an editable element.
            return;
        }

        SetTool(EditorTool.Select);
        _selectedId = hit.Id;
        OpenInspectorForSelection();
        UpdateInspector();
        DrawingCanvas.Invalidate();

        Annotation[] layers = _editor.Current.OrderedAnnotations.ToArray();
        int index = Array.FindIndex(layers, annotation => annotation.Id == hit.Id);
        if (index < 0)
        {
            return;
        }
        var menu = new MenuFlyout();
        MenuFlyoutItem clone = ElementMenuItem("Clone", "Ctrl+D", "CloneElement", "\uE8C8");
        clone.Click += async (_, _) => await CloneSelectedAnnotationAsync();
        menu.Items.Add(clone);

        MenuFlyoutItem delete = ElementMenuItem("Delete", "Del", "DeleteElement", "\uE74D");
        delete.Click += (_, _) => DeleteSelectedAnnotation();
        menu.Items.Add(delete);
        menu.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem front = ElementMenuItem("Move to front", "Ctrl+]", "MoveElementToFront", "\uE74A");
        front.IsEnabled = index < layers.Length - 1;
        front.Click += (_, _) => MoveSelectedAnnotationToFront();
        menu.Items.Add(front);

        MenuFlyoutItem back = ElementMenuItem("Move to back", "Ctrl+[", "MoveElementToBack", "\uE74B");
        back.IsEnabled = index > 0;
        back.Click += (_, _) => MoveSelectedAnnotationToBack();
        menu.Items.Add(back);

        menu.ShowAt(DrawingCanvas, new FlyoutShowOptions { Position = args.GetPosition(DrawingCanvas) });
        args.Handled = true;
    }

    private static MenuFlyoutItem ElementMenuItem(string title, string shortcut, string automationId, string glyph)
    {
        var item = new MenuFlyoutItem
        {
            Text = title,
            KeyboardAcceleratorTextOverride = shortcut,
            Icon = new FontIcon { Glyph = glyph, FontSize = 16 },
        };
        AutomationProperties.SetAutomationId(item, automationId);
        return item;
    }

    // Called from Page_KeyDown after its text-input guard. OEM 4/6 are the bracket keys.
    private bool HandleElementActionKeyDown(KeyRoutedEventArgs args)
    {
        if (_editor is null || _selectedId is null)
        {
            return false;
        }

        bool control = KeyHeld(VirtualKey.Control);
        bool alt = KeyHeld(VirtualKey.Menu);
        bool shift = KeyHeld(VirtualKey.Shift);
        if (args.Key == VirtualKey.Delete && !control && !alt)
        {
            DeleteSelectedAnnotation();
        }
        else if (control && !alt && !shift && args.Key == VirtualKey.D)
        {
            _ = CloneSelectedAnnotationAsync();
        }
        else if (control && !alt && !shift && args.Key == (VirtualKey)0xDD)
        {
            MoveSelectedAnnotationToFront();
        }
        else if (control && !alt && !shift && args.Key == (VirtualKey)0xDB)
        {
            MoveSelectedAnnotationToBack();
        }
        else
        {
            return false;
        }

        args.Handled = true;
        return true;
    }

    private static bool KeyHeld(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) &
            global::Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private async Task CloneSelectedAnnotationAsync()
    {
        if (_cloneInProgress || _editor is null || _selectedId is not Guid id)
        {
            return;
        }

        DocumentEditor editor = _editor;
        Annotation? source = editor.Current.Annotations.FirstOrDefault(annotation => annotation.Id == id);
        if (source is null)
        {
            return;
        }

        _cloneInProgress = true;
        Guid cloneId = Guid.NewGuid();
        try
        {
            if (source.Kind == AnnotationKind.Image)
            {
                await _controller!.Renderer.PreloadImageAsync(source with { Id = cloneId });
                if (!ReferenceEquals(_editor, editor) ||
                    !editor.Current.Annotations.Any(annotation => annotation.Id == id))
                {
                    return;
                }
            }

            CommitText();
            CancelInteraction();
            Annotation? clone = editor.CloneAnnotation(id, cloneId);
            if (clone is null)
            {
                return;
            }

            SetTool(EditorTool.Select);
            _selectedId = clone.Id;
            UpdateInspector();
            DrawingCanvas.Invalidate();
            DrawingCanvas.Focus(FocusState.Programmatic);
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_editor, editor))
            {
                ShowMessage("Couldn't clone element", exception is ArgumentException or InvalidDataException
                    ? exception.Message : "Shnapp could not duplicate this element.", InfoBarSeverity.Warning);
            }
        }
        finally
        {
            if (source.Kind == AnnotationKind.Image &&
                !editor.Current.Annotations.Any(annotation => annotation.Id == cloneId))
            {
                _controller?.Renderer.DiscardPastedImage(cloneId);
            }

            _cloneInProgress = false;
        }
    }

    private void DeleteSelectedAnnotation()
    {
        if (_editor is null || _selectedId is not Guid id)
        {
            return;
        }

        CommitText();
        CancelInteraction();
        _selectedId = null;
        _editor.RemoveAnnotation(id);
        UpdateInspector();
        DrawingCanvas.Invalidate();
        DrawingCanvas.Focus(FocusState.Programmatic);
    }

    private void MoveSelectedAnnotationToFront()
    {
        if (_editor is null || _selectedId is not Guid id)
        {
            return;
        }

        CommitText();
        CancelInteraction();
        _editor.MoveAnnotationToFront(id);
        DrawingCanvas.Focus(FocusState.Programmatic);
    }

    private void MoveSelectedAnnotationToBack()
    {
        if (_editor is null || _selectedId is not Guid id)
        {
            return;
        }

        CommitText();
        CancelInteraction();
        _editor.MoveAnnotationToBack(id);
        DrawingCanvas.Focus(FocusState.Programmatic);
    }
}
