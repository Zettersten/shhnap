using Microsoft.Graphics.Canvas;
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
    private bool _flattenInProgress;
    internal IEnumerable<Annotation> HistoricalPixelElements =>
        _editor?.HistoricalPixelElements() ?? [];

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

        Guid hitBeforeToolSwitch = hit.Id;
        Guid[] selectedBeforeToolSwitch = SelectedAnnotations()
            .Select(annotation => annotation.Id).ToArray();
        if (_tool != EditorTool.Select)
        {
            SetTool(EditorTool.Select);
        }
        // Switching tools commits in-progress text and can remove an empty text mark.
        // Recheck the committed document before selecting the hit element.
        hit = _editor.Current.OrderedAnnotations.Reverse()
            .FirstOrDefault(annotation => HitAnnotation(annotation, position));
        if (hit is null)
        {
            ClearAnnotationSelection();
            UpdateInspector();
            ViewModel.Status = FooterToolHint();
            DrawingCanvas.Invalidate();
            return;
        }

        if (hit.Id == hitBeforeToolSwitch && selectedBeforeToolSwitch.Length > 1 &&
            selectedBeforeToolSwitch.Contains(hit.Id))
        {
            SetSelectedAnnotations(selectedBeforeToolSwitch);
        }
        else if (!IsAnnotationSelected(hit.Id))
        {
            SelectOnlyAnnotation(hit.Id);
        }
        OpenInspectorForSelection();
        UpdateInspector();
        ViewModel.Status = FooterToolHint();
        DrawingCanvas.Invalidate();

        DocumentEditor menuEditor = _editor;
        ShnappDocument menuDocument = menuEditor.Current;
        Annotation[] menuSelection = SelectedAnnotations();
        int selectedCount = menuSelection.Length;
        bool multiple = selectedCount > 1;
        Annotation[] layers = _editor.Current.OrderedAnnotations
            .Where(annotation => !annotation.IsFlattened).ToArray();
        int index = Array.FindIndex(layers, annotation => annotation.Id == hit.Id);
        if (index < 0)
        {
            return;
        }
        var menu = new MenuFlyout();
        MenuFlyoutItem clone = ElementMenuItem(multiple ? $"Clone {selectedCount} elements" : "Clone",
            "Ctrl+D", "CloneElement", "\uE8C8");
        clone.Click += async (_, _) => await CloneSelectedAnnotationAsync();
        menu.Items.Add(clone);

        var transform = new MenuFlyoutSubItem
        {
            Text = "Transform",
            Icon = new FontIcon { Glyph = "\uE7AD", FontSize = 16 },
        };
        AutomationProperties.SetAutomationId(transform, "TransformElement");
        var flipHorizontal = new MenuFlyoutItem { Text = "Flip Horizontally" };
        AutomationProperties.SetAutomationId(flipHorizontal, "FlipElementHorizontally");
        flipHorizontal.Click += (_, _) => TransformSelectedAnnotations(menuEditor, menuDocument,
            menuSelection, annotation => annotation with
            {
                FlipHorizontal = !annotation.FlipHorizontal,
            }, multiple ? $"Flipped {selectedCount} elements horizontally" : "Flipped element horizontally");
        transform.Items.Add(flipHorizontal);

        var flipVertical = new MenuFlyoutItem { Text = "Flip Vertically" };
        AutomationProperties.SetAutomationId(flipVertical, "FlipElementVertically");
        flipVertical.Click += (_, _) => TransformSelectedAnnotations(menuEditor, menuDocument,
            menuSelection, annotation => annotation with
            {
                FlipVertical = !annotation.FlipVertical,
            }, multiple ? $"Flipped {selectedCount} elements vertically" : "Flipped element vertically");
        transform.Items.Add(flipVertical);
        transform.Items.Add(new MenuFlyoutSeparator());

        AddRotateSubmenu(transform, clockwise: true, menuEditor, menuDocument, menuSelection);
        AddRotateSubmenu(transform, clockwise: false, menuEditor, menuDocument, menuSelection);
        menu.Items.Add(transform);
        if (!multiple)
        {
            MenuFlyoutItem front = ElementMenuItem("Move to front", "Ctrl+]", "MoveElementToFront", "\uE74A");
            front.IsEnabled = index < layers.Length - 1;
            front.Click += (_, _) => MoveSelectedAnnotationToFront();
            menu.Items.Add(front);

            MenuFlyoutItem back = ElementMenuItem("Move to back", "Ctrl+[", "MoveElementToBack", "\uE74B");
            back.IsEnabled = index > 0;
            back.Click += (_, _) => MoveSelectedAnnotationToBack();
            menu.Items.Add(back);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem flatten = ElementMenuItem(multiple ? $"Flatten {selectedCount} elements" : "Flatten",
            "", "FlattenElement", "\uE8B9");
        flatten.Click += async (_, _) => await FlattenSelectedAnnotationAsync();
        menu.Items.Add(flatten);
        MenuFlyoutItem delete = ElementMenuItem(multiple ? $"Delete {selectedCount} elements" : "Delete",
            "Del", "DeleteElement", "\uE74D");
        delete.Click += (_, _) => DeleteSelectedAnnotation();
        menu.Items.Add(delete);

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

    private void AddRotateSubmenu(MenuFlyoutSubItem transform, bool clockwise,
        DocumentEditor editor, ShnappDocument document, Annotation[] sources)
    {
        string direction = clockwise ? "Clockwise" : "Counterclockwise";
        var rotate = new MenuFlyoutSubItem { Text = $"Rotate {direction}" };
        AutomationProperties.SetAutomationId(rotate, $"RotateElement{direction}");
        foreach (int degrees in new[] { 45, 90, 135, 180 })
        {
            var item = new MenuFlyoutItem { Text = $"{degrees}\u00b0" };
            AutomationProperties.SetAutomationId(item, $"RotateElement{direction}{degrees}");
            item.Click += (_, _) => TransformSelectedAnnotations(editor, document, sources,
                annotation => annotation with
                {
                    RotationDegrees = AnnotationRotation.NormalizeDegrees(
                        annotation.RotationDegrees + (clockwise ? degrees : -degrees)),
                }, sources.Length > 1
                    ? $"Rotated {sources.Length} elements {direction.ToLowerInvariant()} {degrees}\u00b0"
                    : $"Rotated element {direction.ToLowerInvariant()} {degrees}\u00b0");
            rotate.Items.Add(item);
        }

        transform.Items.Add(rotate);
    }

    private void TransformSelectedAnnotations(DocumentEditor editor, ShnappDocument document,
        Annotation[] sources, Func<Annotation, Annotation> transform, string status)
    {
        if (!ReferenceEquals(_editor, editor))
        {
            return;
        }

        CommitText();
        CancelInteraction();
        if (!SelectionMatches(editor, document, sources))
        {
            return;
        }

        try
        {
            Annotation[] replacements = sources.Select(transform).ToArray();
            if (replacements.Length == 1)
            {
                editor.UpdateAnnotation(replacements[0]);
            }
            else
            {
                editor.UpdateAnnotations(replacements);
            }

            SetSelectedAnnotations(sources.Select(annotation => annotation.Id));
            UpdateInspector();
            ViewModel.Status = status;
            DrawingCanvas.Invalidate();
            DrawingCanvas.Focus(FocusState.Programmatic);
        }
        catch (ArgumentException exception)
        {
            ShowAnnotationEditError(exception);
        }
    }

    // Called from Page_KeyDown after its text-input guard. OEM 4/6 are the bracket keys.
    private bool HandleElementActionKeyDown(KeyRoutedEventArgs args)
    {
        if (_editor is null || SelectedAnnotations().Length == 0)
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
        else if (control && !alt && !shift && !HasMultipleSelectedAnnotations &&
            args.Key == (VirtualKey)0xDD)
        {
            MoveSelectedAnnotationToFront();
        }
        else if (control && !alt && !shift && !HasMultipleSelectedAnnotations &&
            args.Key == (VirtualKey)0xDB)
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

    private bool SelectionMatches(DocumentEditor editor, ShnappDocument document,
        IReadOnlyList<Annotation> sources) =>
        ReferenceEquals(_editor, editor) && ReferenceEquals(editor.Current, document) &&
        sources.Select(annotation => annotation.Id)
            .SequenceEqual(SelectedAnnotations().Select(annotation => annotation.Id));

    private async Task CloneSelectedAnnotationAsync()
    {
        if (_cloneInProgress || _editor is null || _controller is null)
        {
            return;
        }

        DocumentEditor editor = _editor;
        _cloneInProgress = true;
        var cloneIds = new Dictionary<Guid, Guid>();
        Annotation[] sources = [];
        try
        {
            CommitText();
            CancelInteraction();
            if (!ReferenceEquals(_editor, editor))
            {
                return;
            }

            sources = SelectedAnnotations();
            if (sources.Length == 0)
            {
                return;
            }

            ShnappDocument document = editor.Current;
            foreach (Annotation source in sources)
            {
                Guid cloneId = Guid.NewGuid();
                cloneIds.Add(source.Id, cloneId);
                if (source.Kind != AnnotationKind.Image)
                {
                    continue;
                }

                await _controller.Renderer.PreloadImageAsync(source with { Id = cloneId });
                if (!SelectionMatches(editor, document, sources))
                {
                    return;
                }
            }

            if (!SelectionMatches(editor, document, sources))
            {
                return;
            }

            IReadOnlyList<Annotation> clones;
            if (sources.Length == 1)
            {
                Annotation? clone = editor.CloneAnnotation(sources[0].Id, cloneIds[sources[0].Id]);
                if (clone is null)
                {
                    return;
                }

                clones = [clone];
            }
            else
            {
                clones = editor.CloneAnnotations(cloneIds);
            }

            if (clones.Count == 0)
            {
                return;
            }

            SetTool(EditorTool.Select);
            SetSelectedAnnotations(clones.Select(annotation => annotation.Id));
            UpdateInspector();
            ViewModel.Status = FooterToolHint();
            DrawingCanvas.Invalidate();
            DrawingCanvas.Focus(FocusState.Programmatic);
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_editor, editor))
            {
                bool multiple = sources.Length > 1;
                ShowMessage(multiple ? "Couldn't clone elements" : "Couldn't clone element",
                    exception is ArgumentException or InvalidDataException ? exception.Message
                        : multiple ? "Shnapp could not duplicate the selected elements."
                        : "Shnapp could not duplicate this element.", InfoBarSeverity.Warning);
            }
        }
        finally
        {
            foreach (Annotation source in sources.Where(annotation => annotation.Kind == AnnotationKind.Image))
            {
                if (cloneIds.TryGetValue(source.Id, out Guid cloneId) &&
                    !editor.Current.Annotations.Any(annotation => annotation.Id == cloneId))
                {
                    _controller?.Renderer.DiscardPastedImage(cloneId);
                }
            }

            _cloneInProgress = false;
        }
    }

    private void DeleteSelectedAnnotation()
    {
        if (_editor is null)
        {
            return;
        }

        CommitText();
        CancelInteraction();
        Annotation[] selected = SelectedAnnotations();
        if (selected.Length == 0)
        {
            return;
        }

        if (selected.Length == 1)
        {
            _editor.RemoveAnnotation(selected[0].Id);
        }
        else
        {
            _editor.RemoveAnnotations(selected.Select(annotation => annotation.Id).ToArray());
        }

        ClearAnnotationSelection();
        UpdateInspector();
        ViewModel.Status = FooterToolHint();
        DrawingCanvas.Invalidate();
        DrawingCanvas.Focus(FocusState.Programmatic);
    }

    private async Task FlattenSelectedAnnotationAsync()
    {
        if (_flattenInProgress || _editor is null || _original is null || _controller is null)
        {
            return;
        }

        DocumentEditor editor = _editor;
        CanvasBitmap original = _original;
        ShnappRenderer renderer = _controller.Renderer;
        _flattenInProgress = true;
        var payloads = new Dictionary<Guid, (string PngBase64, ImageRect RasterizedBounds)>();
        Annotation[] sources = [];
        try
        {
            CommitText();
            CancelInteraction();
            if (!ReferenceEquals(_editor, editor))
            {
                return;
            }

            sources = SelectedAnnotations();
            if (sources.Length == 0)
            {
                return;
            }

            ShnappDocument document = editor.Current;
            // Later blur and pixelate elements must sample earlier selected elements
            // after they are frozen, while the actual editor still commits once.
            ShnappDocument rasterDocument = document;
            int flattenOrder = document.Annotations.Where(annotation => annotation.IsFlattened)
                .Select(annotation => annotation.FlattenOrder).DefaultIfEmpty().Max();
            if (sources.Length > int.MaxValue - flattenOrder)
            {
                throw new ArgumentException("The flattened background has reached its layer limit.");
            }

            foreach (Annotation source in sources)
            {
                (string payload, ImageRect bounds) =
                    await renderer.RasterizeElementAsync(original, rasterDocument, source);
                payloads.Add(source.Id, (payload, bounds));
                if (!SelectionMatches(editor, document, sources))
                {
                    return;
                }

                Annotation frozen = source with
                {
                    IsFlattened = true,
                    RasterizedBounds = bounds,
                    ImagePngBase64 = payload,
                    FlattenOrder = ++flattenOrder,
                };
                await renderer.PreloadImageAsync(frozen);
                if (!SelectionMatches(editor, document, sources))
                {
                    return;
                }

                int index = document.Annotations.IndexOf(source);
                rasterDocument = rasterDocument with
                {
                    Annotations = rasterDocument.Annotations.SetItem(index, frozen),
                };
            }

            if (!SelectionMatches(editor, document, sources))
            {
                return;
            }

            bool flattened = sources.Length == 1
                ? editor.FlattenAnnotation(sources[0].Id,
                    payloads[sources[0].Id].PngBase64,
                    payloads[sources[0].Id].RasterizedBounds)
                : editor.FlattenAnnotations(payloads) > 0;
            if (flattened)
            {
                ClearAnnotationSelection();
                UpdateInspector();
                ViewModel.Status = FooterToolHint();
                DrawingCanvas.Invalidate();
                DrawingCanvas.Focus(FocusState.Programmatic);
            }
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_editor, editor))
            {
                bool multiple = sources.Length > 1;
                ShowMessage(multiple ? "Couldn't flatten elements" : "Couldn't flatten element",
                    exception is ArgumentException or InvalidDataException ? exception.Message
                        : multiple ? "Shnapp could not flatten the selected elements."
                        : "Shnapp could not flatten this element.", InfoBarSeverity.Warning);
            }
        }
        finally
        {
            foreach (var (id, payload) in payloads)
            {
                if (!editor.Current.Annotations.Any(annotation => annotation.Id == id &&
                    annotation.IsFlattened && ReferenceEquals(annotation.ImagePngBase64, payload.PngBase64)))
                {
                    renderer.DiscardPastedImagePayload(id, payload.PngBase64);
                }
            }

            _flattenInProgress = false;
        }
    }

    private void MoveSelectedAnnotationToFront()
    {
        Annotation[] selected = SelectedAnnotations();
        if (_editor is null || selected.Length != 1)
        {
            return;
        }

        CommitText();
        CancelInteraction();
        _editor.MoveAnnotationToFront(selected[0].Id);
        DrawingCanvas.Focus(FocusState.Programmatic);
    }

    private void MoveSelectedAnnotationToBack()
    {
        Annotation[] selected = SelectedAnnotations();
        if (_editor is null || selected.Length != 1)
        {
            return;
        }

        CommitText();
        CancelInteraction();
        _editor.MoveAnnotationToBack(selected[0].Id);
        DrawingCanvas.Focus(FocusState.Programmatic);
    }
}
