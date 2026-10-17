using Microsoft.UI.Input;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.System;
using Windows.UI;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private readonly Dictionary<TextBox, NumberBox> _numberBoxInputs = [];
    private FontPreviewChoice[] _fontChoices = [];
    private string? _previewedFontFamily;
    private CanvasRenderTarget? _fontPreviewImage;

    private void InitializeFontFamilies()
    {
        IReadOnlyList<string> installed;
        try
        {
            installed = CanvasTextFormat.GetSystemFontFamilies();
        }
        catch
        {
            installed = [];
        }

        _updatingOptions = true;
        string[] preferred = ["Segoe UI Variable Text", "Segoe UI", "Arial", "Calibri", "Georgia", "Consolas"];
        string[] choices = preferred.Concat(installed.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith('@'))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _fontChoices = choices.Select(name => new FontPreviewChoice(name)).ToArray();
        FontFamilyChoice.ItemsSource = _fontChoices;
        FontFamilyChoice.Text = preferred[0];
        _updatingOptions = false;
    }

    private void FontFamilyChoice_GotFocus(object sender, RoutedEventArgs args)
    {
        FontFamilyChoice.ItemsSource = _fontChoices;
        FontFamilyNoResults.Visibility = Visibility.Collapsed;
        FontFamilyChoice.IsSuggestionListOpen = _fontChoices.Length > 0;
        FindFontSearchInput(FontFamilyChoice)?.SelectAll();
    }

    private void FontFamilyChoice_LostFocus(object sender, RoutedEventArgs args)
    {
        // Selecting a suggestion briefly moves focus into the popup. Restore an uncommitted
        // search only after the popup is dismissed, so the field always shows the applied font.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (FontFamilyChoice.IsSuggestionListOpen)
            {
                return;
            }

            ClearFontPreview();

            string applied = StyleFor(InspectorAnnotation(), InspectorTool()).FontFamily;
            if (!string.Equals(FontFamilyChoice.Text, applied, StringComparison.Ordinal))
            {
                FontFamilyChoice.Text = applied;
            }

            FontFamilyNoResults.Visibility = Visibility.Collapsed;
        });
    }

    private static TextBox? FindFontSearchInput(DependencyObject root)
    {
        if (root is TextBox textBox)
        {
            return textBox;
        }

        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindFontSearchInput(VisualTreeHelper.GetChild(root, index)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void FontFamilyChoice_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_updatingOptions || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        ClearFontPreview();

        string query = sender.Text.Trim();
        FontPreviewChoice[] matches = query.Length == 0
            ? _fontChoices
            : _fontChoices.Where(choice => choice.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
        sender.ItemsSource = matches;
        sender.IsSuggestionListOpen = matches.Length > 0;
        FontFamilyNoResults.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FontFamilyChoice_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        FontPreviewChoice? choice = args.ChosenSuggestion as FontPreviewChoice
            ?? _fontChoices.FirstOrDefault(item => string.Equals(item.Name, args.QueryText.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (choice is not null)
        {
            ApplyFontFamily(choice);
        }
        else
        {
            sender.IsSuggestionListOpen = sender.ItemsSource is FontPreviewChoice[] { Length: > 0 };
        }
    }

    private void FontFamilyChoice_SuggestionChosen(AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is FontPreviewChoice choice)
        {
            PreviewFontFamily(choice);
        }
    }

    private void FontPreviewItem_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: FontPreviewChoice choice })
        {
            PreviewFontFamily(choice);
        }
    }

    private void PreviewFontFamily(FontPreviewChoice choice)
    {
        if (_previewedFontFamily == choice.Name)
        {
            return;
        }

        _previewedFontFamily = choice.Name;
        if (_textBox is not null)
        {
            SetEditorFontFamilyPreservingStyles(choice.PreviewFont);
            return;
        }

        if (_editor is null || _controller is null || _original is null ||
            SelectedAnnotation() is not { Kind: AnnotationKind.Text or AnnotationKind.Step } selected)
        {
            return;
        }

        int index = _editor.Current.Annotations.IndexOf(selected);
        if (index < 0)
        {
            return;
        }

        try
        {
            ShnappDocument preview = _editor.Current with
            {
                Annotations = _editor.Current.Annotations.SetItem(index,
                    selected with { FontFamily = choice.Name }),
            };
            CanvasRenderTarget image = _controller.Renderer.Flatten(_original, preview);
            _fontPreviewImage?.Dispose();
            _fontPreviewImage = image;
            DrawingCanvas.Invalidate();
        }
        catch (ArgumentException)
        {
            // An installed font may disappear between enumeration and preview.
        }
    }

    private void ClearFontPreview()
    {
        _previewedFontFamily = null;
        _fontPreviewImage?.Dispose();
        _fontPreviewImage = null;
        if (_textBox is not null && _textDraft is not null)
        {
            SetEditorFontFamilyPreservingStyles(new FontFamily(_textDraft.FontFamily));
        }

        DrawingCanvas.Invalidate();
    }

    private void ApplyFontFamily(FontPreviewChoice choice)
    {
        if (_updatingOptions || InspectorTitle is null)
        {
            return;
        }

        ToolStyle style = _toolStyles[InspectorTool()];
        ClearFontPreview();
        style.FontFamily = choice.Name;
        _updatingOptions = true;
        FontFamilyChoice.Text = choice.Name;
        FontFamilyChoice.IsSuggestionListOpen = false;
        FontFamilyNoResults.Visibility = Visibility.Collapsed;
        _updatingOptions = false;
        UpdateSelected(annotation => annotation with { FontFamily = choice.Name });
    }

    private Annotation? SelectedAnnotation() =>
        _editor?.Current.Annotations.FirstOrDefault(annotation =>
            annotation.Id == _selectedId && !annotation.HiddenByCrop);

    private Annotation? InspectorAnnotation() =>
        _textBox is not null && _textDraft is { } draft ? draft : SelectedAnnotation();

    private static EditorTool ToolFor(Annotation annotation) => annotation.Kind switch
    {
        AnnotationKind.Text => EditorTool.Text,
        AnnotationKind.Step => EditorTool.Step,
        AnnotationKind.Callout => EditorTool.Callout,
        AnnotationKind.Magnify => EditorTool.Magnify,
        AnnotationKind.Arrow => EditorTool.Line,
        AnnotationKind.Line => EditorTool.Line,
        AnnotationKind.Rectangle => EditorTool.Rectangle,
        AnnotationKind.Ellipse => EditorTool.Ellipse,
        AnnotationKind.Shape => EditorTool.Shape,
        AnnotationKind.Redaction => EditorTool.Redaction,
        _ => EditorTool.Select,
    };

    private static EditorTool DisplayShapeTool(Annotation? selected, EditorTool tool)
    {
        if (selected is not { Kind: AnnotationKind.Rectangle or AnnotationKind.Ellipse })
        {
            return tool;
        }

        ImageRect bounds = selected.Bounds;
        if (bounds.Width <= 0 || Math.Abs(bounds.Width - bounds.Height) > 0.5)
        {
            return tool;
        }

        return selected.Kind == AnnotationKind.Rectangle ? EditorTool.Square : EditorTool.Circle;
    }

    private EditorTool InspectorTool() => InspectorAnnotation() is { } selected ? ToolFor(selected) : _tool;

    private ToolStyle StyleFor(Annotation? selected, EditorTool tool)
    {
        ToolStyle defaults = _toolStyles[tool];
        if (selected is null)
        {
            return defaults;
        }

        return new ToolStyle
        {
            Primary = selected.StrokeArgb,
            Secondary = selected.Kind switch
            {
                AnnotationKind.Step => selected.StepTextArgb == 0 ? 0xFFFFFFFF : selected.StepTextArgb,
                AnnotationKind.Callout => selected.CalloutTextArgb == 0 ? defaults.Secondary : selected.CalloutTextArgb,
                _ => selected.FillArgb == 0 ? defaults.Secondary : selected.FillArgb,
            },
            StrokeWidth = selected.StrokeWidth,
            OutlineShape = !selected.HideOutline,
            FontFamily = selected.FontFamily,
            FontWeight = selected.FontWeight,
            Italic = selected.Italic,
            FontSize = selected.FontSize,
            TextKerning = selected.TextKerning,
            TextLetterSpacing = selected.TextLetterSpacing,
            TextTransform = selected.TextTransform,
            TextAlignment = selected.TextAlignment,
            TextVerticalAlignment = selected.TextVerticalAlignment,
            TextTruncation = selected.TextTruncation,
            TextLineHeight = selected.TextLineHeight,
            StepDiameter = selected.StepDiameter,
            FillShape = selected.FillArgb >> 24 != 0,
            TextBackgroundEnabled = selected.FillArgb >> 24 != 0,
            TextBackgroundColor = selected.FillArgb == 0 ? defaults.TextBackgroundColor :
                selected.FillArgb,
            TextPadding = selected.TextPadding,
            TextBorderEnabled = selected.TextBorderArgb >> 24 != 0,
            TextBorderColor = selected.TextBorderArgb == 0 ? defaults.TextBorderColor :
                selected.TextBorderArgb,
            TextBorderWidth = selected.TextBorderWidth > 0 ? selected.TextBorderWidth : defaults.TextBorderWidth,
            StartArrow = selected.StartArrow,
            EndArrow = selected.EndArrow || selected.Kind == AnnotationKind.Arrow,
            StartCap = selected.EffectiveStartCap,
            EndCap = selected.EffectiveEndCap,
            LinePattern = selected.LinePattern,
            StepLabelFormat = selected.StepLabelFormat,
            RedactionMode = selected.RedactionMode,
            MagnifyZoom = selected.MagnifyZoom > 0 ? selected.MagnifyZoom : defaults.MagnifyZoom,
            MagnifyShape = selected.MagnifyShape,
        };
    }

    private void UpdateInspector()
    {
        if (InspectorTitle is null)
        {
            return;
        }

        UpdateInspectorContent();
    }

    private void RenderInspectorOptions()
    {
        if (InspectorTitle is null)
        {
            return;
        }

        Annotation? selected = InspectorAnnotation();
        bool groupSelected = HasMultipleSelectedAnnotations;
        EditorTool tool = groupSelected ? EditorTool.Select
            : selected is null ? _tool : ToolFor(selected);
        EditorTool displayedShape = DisplayShapeTool(selected, tool);
        ToolStyle style = StyleFor(selected, tool);
        bool text = tool == EditorTool.Text;
        bool step = tool == EditorTool.Step;
        bool line = tool is EditorTool.Line or EditorTool.Arrow;
        bool shape = tool is EditorTool.Rectangle or EditorTool.Square or EditorTool.Ellipse or EditorTool.Circle or EditorTool.Shape;
        BasicShapeKind paletteShape = selected?.Kind == AnnotationKind.Shape
            ? selected.ShapeKind : _activeShape;
        bool openShape = tool == EditorTool.Shape && !BasicShapeGeometry.IsClosed(paletteShape);
        bool redaction = tool == EditorTool.Redaction;
        bool callout = tool == EditorTool.Callout;
        bool magnify = tool == EditorTool.Magnify;
        bool select = tool == EditorTool.Select;
        bool photoSelected = !groupSelected && selected?.Kind == AnnotationKind.Image;
        bool styleable = text || step || line || shape || callout || magnify;
        bool fixedTextBox = selected is { Kind: AnnotationKind.Text, TextBoxWidth: > 0, TextBoxHeight: > 0 };
        bool editingText = _textBox is not null && _textDraft is not null;

        _updatingOptions = true;
        InspectorToolIcon.Visibility = line || shape ? Visibility.Collapsed : Visibility.Visible;
        InspectorLineIcon.Visibility = line ? Visibility.Visible : Visibility.Collapsed;
        InspectorRectangleIcon.Visibility = displayedShape == EditorTool.Rectangle ? Visibility.Visible : Visibility.Collapsed;
        InspectorSquareIcon.Visibility = displayedShape == EditorTool.Square ? Visibility.Visible : Visibility.Collapsed;
        InspectorEllipseIcon.Visibility = displayedShape == EditorTool.Ellipse ? Visibility.Visible : Visibility.Collapsed;
        InspectorCircleIcon.Visibility = displayedShape == EditorTool.Circle ? Visibility.Visible : Visibility.Collapsed;
        InspectorPaletteShapeIcon.Visibility = tool == EditorTool.Shape ? Visibility.Visible : Visibility.Collapsed;
        if (tool == EditorTool.Shape)
        {
            InspectorPaletteShapeIcon.Data = ShapePreviewGeometry(paletteShape);
        }
        InspectorToolIcon.Glyph = selected?.Kind == AnnotationKind.Image ? "\uE722" : tool switch
        {
            EditorTool.Text => "\uE8D2",
            EditorTool.Step => "\uE8FD",
            EditorTool.Callout => "\uE8F2",
            EditorTool.Magnify => "\uE71E",
            EditorTool.Redaction => "\uE72E",
            EditorTool.Crop => "\uE7A8",
            _ => "\uE8B0",
        };
        InspectorTitle.Text = groupSelected ? $"{_selectedIds.Count} elements selected"
            : photoSelected ? "Selected photo"
            : editingText ? "Editing text" : redaction
            ? selected is null ? RedactionModeLabel(style.RedactionMode) : $"Selected {RedactionModeLabel(style.RedactionMode).ToLowerInvariant()}"
            : callout ? selected is null ? "Annotations" : $"Annotation {selected.CalloutNumber}"
            : magnify ? selected is null ? "Magnify" : "Selected magnifier"
            : selected is null ? tool == EditorTool.Shape ? ShapeLabel(paletteShape) : tool.ToString()
                : line ? "Selected line" : tool == EditorTool.Shape
                ? $"Selected {ShapeLabel(paletteShape).ToLowerInvariant()}" : shape
                ? $"Selected {displayedShape.ToString().ToLowerInvariant()}"
                : $"Selected {selected.Kind.ToString().ToLowerInvariant()}";
        InspectorHelp.Text = photoSelected
            ? _photoCropMode ? "Drag over the photo to choose the area to keep. Press Esc to cancel."
                : "Drag to move, use corners to resize, or use the circle above to rotate. Crop below."
            : editingText
            ? fixedTextBox
                ? "Text stays within the drawn box. Select characters for bold, italic, or underline. Press Ctrl+Enter to finish."
                : "Select characters for bold, italic, or underline. Press Ctrl+Enter to finish."
            : redaction
            ? selected is null
                ? "Drag to apply. Cover fully hides pixels in shared images; blur and pixelate obscure them."
                : "Drag squares to resize or the circle to rotate. Use Cover for private details."
            : fixedTextBox
                ? "Text stays within this box. Squares resize; the circle rotates. Choose its style below."
            : callout && selected is not null
                ? "Edit the note below. Drag the anchor to point elsewhere or the circle to rotate."
            : magnify && selected is not null
                ? "Drag the lens or its corner handles. The circle rotates it. Minimum size: 48 × 48 pixels."
            : line && selected?.AllowCurves == true
                ? "Drag the center to bend the line, either end to reshape it, or the circle to rotate."
            : selected is null
                ? ToolHint()
                : "Drag to move, squares to resize, or the circle to rotate. Arrow keys nudge.";

        InspectorHelp.Visibility = Visible(!select || photoSelected);
        SelectGuideRow.Visibility = Visible(select && !photoSelected);
        InspectorSeparator.Visibility = Visible(!select || photoSelected);
        if (select)
        {
            bool imageSelected = selected?.Kind == AnnotationKind.Image;
            SelectGuideFirstLabel.Text = groupSelected || imageSelected ? "Move" : "Click";
            SelectGuideFirstText.Text = groupSelected ? "Drag any selected element"
                : imageSelected ? "Drag the image" : "Choose an element";
            SelectGuideSecondLabel.Text = groupSelected ? "Actions" : selected is null ? "Drag" : "Handles";
            SelectGuideSecondText.Text = groupSelected ? "Right-click for group actions"
                : selected is not null ? "Squares resize · circle rotates" : "Move it or resize with handles";
        }

        PhotoOptionsRow.Visibility = Visible(photoSelected);
        PhotoCropButton.Content = _photoCropMode ? "Cancel crop" : "Crop photo";
        ResetPhotoCropButton.IsEnabled = selected?.ImageSourceCrop is not null;
        RedactionModeRow.Visibility = Visible(redaction);
        CalloutEditRow.Visibility = Visible(callout && selected is not null);
        MagnifyOptionsRow.Visibility = Visible(magnify);
        PrimaryColorRow.Visibility = Visible(styleable);
        SecondaryColorRow.Visibility = Visible(step || shape && !openShape || callout);
        StrokeWidthRow.Visibility = Visible(line || shape || callout || magnify);
        FontFamilyRow.Visibility = Visible(text || step || callout);
        SetInlineTextStyleVisible(editingText);
        EditSelectedTextButton.Visibility = Visible(text && !editingText && selected is not null);
        InlineBold.IsEnabled = editingText;
        InlineItalic.IsEnabled = editingText;
        InlineUnderline.IsEnabled = editingText;
        FontSizeRow.Visibility = Visible(text || step || callout);
        TextBoxLayoutRow.Visibility = Visible(fixedTextBox || callout);
        StepSizeRow.Visibility = Visible(step);
        StepLabelRow.Visibility = Visible(step);
        OutlineToggleRow.Visibility = Visible(shape && !openShape);
        FillToggleRow.Visibility = Visible(shape && !openShape);
        TextAreaStyleRow.Visibility = Visible(text);
        LineCapRow.Visibility = Visible(line);
        LinePatternRow.Visibility = Visible(line);
        LineFreehandRow.Visibility = Visible(line);
        LineCurvesRow.Visibility = Visible(line && selected is not null);

        PrimaryColorLabel.Text = tool switch
        {
            EditorTool.Text => "Text color",
            EditorTool.Step => "Dot color",
            EditorTool.Line or EditorTool.Arrow => "Line color",
            EditorTool.Callout => "Leader and border color",
            EditorTool.Magnify => "Lens border color",
            _ => "Outline color",
        };
        SecondaryColorLabel.Text = callout ? "Text color" : step ? "Number color" : "Fill color";
        PrimaryColorPicker.Color = ShnappRenderer.FromArgb(style.Primary);
        SecondaryColorPicker.Color = ShnappRenderer.FromArgb(style.Secondary);
        PrimaryColorSwatch.Background = new SolidColorBrush(PrimaryColorPicker.Color);
        SecondaryColorSwatch.Background = new SolidColorBrush(SecondaryColorPicker.Color);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PrimaryColorButton, PrimaryColorLabel.Text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SecondaryColorButton, SecondaryColorLabel.Text);

        StrokeSize.Value = style.StrokeWidth;
        StrokeSize.IsEnabled = openShape || !(shape || magnify) || style.OutlineShape;
        PrimaryColorButton.IsEnabled = openShape || !(shape || magnify) || style.OutlineShape;
        MagnifyZoomChoice.Value = style.MagnifyZoom * 100;
        SelectComboValue(MagnifyShapeChoice, style.MagnifyShape.ToString());
        MagnifyOutlineChoice.IsChecked = style.OutlineShape;
        FontSizeChoice.Value = style.FontSize;
        TextLineHeightChoice.Value = style.TextLineHeight;
        StepSize.Value = style.StepDiameter;
        FillShape.IsChecked = style.FillShape;
        OutlineShape.IsChecked = style.OutlineShape;
        TextBackgroundEnabled.IsChecked = style.TextBackgroundEnabled;
        TextPaddingChoice.Value = style.TextPadding;
        TextPaddingValue.Text = $"{Math.Round(style.TextPadding)} px";
        TextBackgroundColorPicker.Color = ShnappRenderer.FromArgb(style.TextBackgroundColor);
        TextBackgroundSwatch.Background = new SolidColorBrush(TextBackgroundColorPicker.Color);
        TextBackgroundColorButton.IsEnabled = style.TextBackgroundEnabled;
        TextBorderEnabled.IsChecked = style.TextBorderEnabled;
        TextBorderColorPicker.Color = ShnappRenderer.FromArgb(style.TextBorderColor);
        TextBorderSwatch.Background = new SolidColorBrush(TextBorderColorPicker.Color);
        TextBorderWidthChoice.Value = style.TextBorderWidth;
        TextBorderColorButton.IsEnabled = style.TextBorderEnabled;
        TextBorderWidthChoice.IsEnabled = style.TextBorderEnabled;
        StepResetCount.IsChecked = selected?.StepReset == true;
        StepResetCount.IsEnabled = selected?.Kind == AnnotationKind.Step;
        SelectComboValue(StepLabelChoice, style.StepLabelFormat.ToString());
        SelectComboValue(StartCapChoice, style.StartCap.ToString());
        SelectComboValue(EndCapChoice, style.EndCap.ToString());
        SelectComboValue(LinePatternChoice, style.LinePattern.ToString());
        LineFreehandChoice.IsChecked = _toolStyles[EditorTool.Line].FreehandLine;
        LineCurvesChoice.IsChecked = selected?.AllowCurves == true;
        LineCurvesChoice.IsEnabled = selected is { Kind: AnnotationKind.Line or AnnotationKind.Arrow };
        SelectComboValue(RedactionModeChoice, style.RedactionMode.ToString());
        FontFamilyChoice.Text = style.FontFamily;
        SelectComboValue(TextAlignmentChoice, style.TextAlignment.ToString());
        SelectComboValue(TextVerticalAlignmentChoice, style.TextVerticalAlignment.ToString());
        SelectComboValue(TextTruncationChoice, style.TextTruncation.ToString());
        RefreshInlineTextStyleButtons();
        _updatingOptions = false;
    }

    private static Visibility Visible(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private static string RedactionModeLabel(RedactionMode mode) => mode switch
    {
        RedactionMode.Blur => "Blur",
        RedactionMode.Pixelate => "Pixelate",
        _ => "Cover",
    };

    private static void SelectComboValue(ComboBox combo, string value)
    {
        for (int index = 0; index < combo.Items.Count; index++)
        {
            if (combo.Items[index] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = index;
                return;
            }
        }

        combo.Items.Add(new ComboBoxItem { Content = value, Tag = value });
        combo.SelectedIndex = combo.Items.Count - 1;
    }

    private void HandleOptionChanged(object sender)
    {
        if (!IsLoaded || _updatingOptions || InspectorTitle is null)
        {
            return;
        }

        EditorTool tool = InspectorTool();
        ToolStyle style = _toolStyles[tool];
        ToolStyle current = StyleFor(InspectorAnnotation(), tool);
        if (ReferenceEquals(sender, MagnifyZoomChoice))
        {
            style.MagnifyZoom = Math.Clamp(FiniteValue(MagnifyZoomChoice.Value, 200) / 100, 1.25, 8);
            UpdateSelected(annotation => annotation with { MagnifyZoom = style.MagnifyZoom });
        }
        else if (ReferenceEquals(sender, MagnifyShapeChoice) &&
            MagnifyShapeChoice.SelectedItem is ComboBoxItem magnifyShapeChoice &&
            Enum.TryParse(magnifyShapeChoice.Tag?.ToString(), out MagnifyShape magnifyShape))
        {
            style.MagnifyShape = magnifyShape;
            UpdateSelected(annotation => annotation with { MagnifyShape = magnifyShape });
        }
        else if (ReferenceEquals(sender, MagnifyOutlineChoice))
        {
            style.OutlineShape = MagnifyOutlineChoice.IsChecked == true;
            StrokeSize.IsEnabled = style.OutlineShape;
            PrimaryColorButton.IsEnabled = style.OutlineShape;
            UpdateSelected(annotation => annotation with { HideOutline = !style.OutlineShape });
        }
        else if (ReferenceEquals(sender, StrokeSize))
        {
            style.StrokeWidth = Math.Clamp(FiniteValue(StrokeSize.Value, 3), 1, 24);
            UpdateSelected(annotation => annotation with { StrokeWidth = style.StrokeWidth });
        }
        else if (ReferenceEquals(sender, FontSizeChoice))
        {
            style.FontSize = Math.Clamp(FiniteValue(FontSizeChoice.Value, 18), 8, 144);
            UpdateSelected(annotation => annotation with { FontSize = style.FontSize });
        }
        else if (ReferenceEquals(sender, TextPaddingChoice))
        {
            style.TextPadding = Math.Clamp(Math.Round(FiniteValue(TextPaddingChoice.Value, 0)), 0, 64);
            UpdateSelected(annotation => annotation with { TextPadding = style.TextPadding });
        }
        else if (ReferenceEquals(sender, TextBackgroundEnabled))
        {
            style.TextBackgroundColor = current.TextBackgroundColor;
            style.TextBackgroundEnabled = TextBackgroundEnabled.IsChecked == true;
            if (style.TextBackgroundEnabled && style.TextBackgroundColor >> 24 == 0)
            {
                style.TextBackgroundColor = WithVisibleAlpha(style.TextBackgroundColor, 255);
                SetPickerColor(TextBackgroundColorPicker, TextBackgroundSwatch, style.TextBackgroundColor);
            }
            TextBackgroundColorButton.IsEnabled = style.TextBackgroundEnabled;
            uint background = style.TextBackgroundEnabled
                ? style.TextBackgroundColor : 0;
            UpdateSelected(annotation => annotation with { FillArgb = background });
        }
        else if (ReferenceEquals(sender, TextBorderEnabled) ||
            ReferenceEquals(sender, TextBorderWidthChoice))
        {
            style.TextBorderColor = current.TextBorderColor;
            style.TextBorderEnabled = TextBorderEnabled.IsChecked == true;
            style.TextBorderWidth = Math.Clamp(FiniteValue(TextBorderWidthChoice.Value, 2), 1, 24);
            if (style.TextBorderEnabled && style.TextBorderColor >> 24 == 0)
            {
                style.TextBorderColor = WithVisibleAlpha(style.TextBorderColor, 255);
                SetPickerColor(TextBorderColorPicker, TextBorderSwatch, style.TextBorderColor);
            }
            TextBorderColorButton.IsEnabled = style.TextBorderEnabled;
            TextBorderWidthChoice.IsEnabled = style.TextBorderEnabled;
            uint border = style.TextBorderEnabled
                ? style.TextBorderColor : 0;
            UpdateSelected(annotation => annotation with
            {
                TextBorderArgb = border,
                TextBorderWidth = style.TextBorderWidth,
            });
        }
        else if (ReferenceEquals(sender, TextAlignmentChoice) &&
            TextAlignmentChoice.SelectedItem is ComboBoxItem alignmentChoice &&
            Enum.TryParse(alignmentChoice.Tag?.ToString(), out TextHorizontalAlignment textAlignment))
        {
            style.TextAlignment = textAlignment;
            UpdateSelected(annotation => annotation with { TextAlignment = textAlignment });
        }
        else if (ReferenceEquals(sender, TextVerticalAlignmentChoice) &&
            TextVerticalAlignmentChoice.SelectedItem is ComboBoxItem verticalChoice &&
            Enum.TryParse(verticalChoice.Tag?.ToString(), out TextVerticalAlignment verticalAlignment))
        {
            style.TextVerticalAlignment = verticalAlignment;
            UpdateSelected(annotation => annotation with { TextVerticalAlignment = verticalAlignment });
        }
        else if (ReferenceEquals(sender, TextTruncationChoice) &&
            TextTruncationChoice.SelectedItem is ComboBoxItem truncationChoice &&
            Enum.TryParse(truncationChoice.Tag?.ToString(), out TextTruncation textTruncation))
        {
            style.TextTruncation = textTruncation;
            UpdateSelected(annotation => annotation with { TextTruncation = textTruncation });
        }
        else if (ReferenceEquals(sender, TextLineHeightChoice))
        {
            double requested = Math.Clamp(FiniteValue(TextLineHeightChoice.Value, 0), 0, 320);
            style.TextLineHeight = requested == 0 ? 0 : Math.Max(8, requested);
            if (TextLineHeightChoice.Value != style.TextLineHeight)
            {
                _updatingOptions = true;
                TextLineHeightChoice.Value = style.TextLineHeight;
                _updatingOptions = false;
            }
            UpdateSelected(annotation => annotation with { TextLineHeight = style.TextLineHeight });
        }
        else if (ReferenceEquals(sender, StepSize))
        {
            style.StepDiameter = Math.Clamp(FiniteValue(StepSize.Value, 28), 16, 120);
            UpdateSelected(annotation => annotation with { StepDiameter = style.StepDiameter });
        }
        else if (ReferenceEquals(sender, StepLabelChoice) &&
            StepLabelChoice.SelectedItem is ComboBoxItem labelChoice &&
            Enum.TryParse(labelChoice.Tag?.ToString(), out StepLabelFormat labelFormat))
        {
            style.StepLabelFormat = labelFormat;
            UpdateSelected(annotation => annotation with { StepLabelFormat = labelFormat });
        }
        else if (ReferenceEquals(sender, StepResetCount))
        {
            UpdateSelected(annotation => annotation with { StepReset = StepResetCount.IsChecked == true });
        }
        else if (ReferenceEquals(sender, OutlineShape) || ReferenceEquals(sender, FillShape))
        {
            style.OutlineShape = OutlineShape.IsChecked == true;
            style.FillShape = FillShape.IsChecked == true;
            if (!style.OutlineShape && !style.FillShape)
            {
                if (ReferenceEquals(sender, OutlineShape))
                {
                    style.FillShape = true;
                    _updatingOptions = true;
                    FillShape.IsChecked = true;
                    _updatingOptions = false;
                }
                else
                {
                    style.OutlineShape = true;
                    _updatingOptions = true;
                    OutlineShape.IsChecked = true;
                    _updatingOptions = false;
                }
            }
            style.Secondary = current.Secondary;
            if (style.FillShape && style.Secondary >> 24 == 0)
            {
                style.Secondary = WithVisibleAlpha(style.Secondary, 64);
                SetPickerColor(SecondaryColorPicker, SecondaryColorSwatch, style.Secondary);
            }
            StrokeSize.IsEnabled = style.OutlineShape;
            PrimaryColorButton.IsEnabled = style.OutlineShape;
            uint fill = style.FillShape ? style.Secondary : 0;
            UpdateSelected(annotation => annotation with { HideOutline = !style.OutlineShape, FillArgb = fill });
        }
        else if (ReferenceEquals(sender, StartCapChoice) || ReferenceEquals(sender, EndCapChoice))
        {
            if (StartCapChoice is not null && EndCapChoice is not null &&
                StartCapChoice.SelectedItem is ComboBoxItem startCapChoice &&
                EndCapChoice.SelectedItem is ComboBoxItem endCapChoice &&
                Enum.TryParse(startCapChoice.Tag?.ToString(), out LineEndCap startCap) &&
                Enum.TryParse(endCapChoice.Tag?.ToString(), out LineEndCap endCap))
            {
                style.StartCap = startCap;
                style.EndCap = endCap;
                UpdateSelected(annotation => annotation with
                {
                    Kind = AnnotationKind.Line,
                    StartArrow = false,
                    EndArrow = false,
                    StartCap = startCap,
                    EndCap = endCap,
                });
            }
        }
        else if (ReferenceEquals(sender, LinePatternChoice) &&
            LinePatternChoice.SelectedItem is ComboBoxItem patternChoice &&
            Enum.TryParse(patternChoice.Tag?.ToString(), out LinePattern pattern))
        {
            style.LinePattern = pattern;
            UpdateSelected(annotation => annotation with { LinePattern = pattern });
        }
        else if (ReferenceEquals(sender, LineFreehandChoice))
        {
            bool freehand = LineFreehandChoice.IsChecked == true;
            _toolStyles[EditorTool.Line].FreehandLine = freehand;
            _toolStyles[EditorTool.Arrow].FreehandLine = freehand;
        }
        else if (ReferenceEquals(sender, LineCurvesChoice) &&
            InspectorAnnotation() is { Kind: AnnotationKind.Line or AnnotationKind.Arrow })
        {
            bool allow = LineCurvesChoice.IsChecked == true;
            UpdateSelected(annotation => annotation with
            {
                AllowCurves = allow,
                CurveMidpoint = allow && !annotation.LinePoints.IsDefaultOrEmpty
                    ? Shnapp.Core.LineGeometry.PathMidpoint(annotation)
                    : allow ? annotation.CurveMidpoint : null,
                LinePoints = allow ? [] : annotation.LinePoints,
            });
        }
        else if (ReferenceEquals(sender, RedactionModeChoice) &&
            RedactionModeChoice.SelectedItem is ComboBoxItem modeChoice &&
            Enum.TryParse(modeChoice.Tag?.ToString(), out RedactionMode mode))
        {
            style.RedactionMode = mode;
            UpdateSelected(annotation => annotation with { RedactionMode = mode });
            if (_editor is not null)
            {
                UpdateInspector();
            }
        }
    }

    private void TextPaddingChoice_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (TextPaddingValue is not null)
        {
            TextPaddingValue.Text = $"{Math.Round(args.NewValue)} px";
        }
        HandleOptionChanged(sender);
    }

    private void NumberBox_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not NumberBox number)
        {
            return;
        }

        number.ApplyTemplate();
        if (FindNumberInput(number) is TextBox input && _numberBoxInputs.TryAdd(input, number))
        {
            input.AddHandler(UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(NumberInput_PointerWheelChanged), true);
        }
    }

    private static TextBox? FindNumberInput(DependencyObject root)
    {
        if (root is TextBox { Name: "InputBox" } input)
        {
            return input;
        }

        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindNumberInput(VisualTreeHelper.GetChild(root, index)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void NumberInput_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not TextBox input || input.FocusState == FocusState.Unfocused ||
            !_numberBoxInputs.TryGetValue(input, out NumberBox? number) || !number.IsEnabled)
        {
            return;
        }

        int delta = args.GetCurrentPoint(input).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) &
            global::Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        // Positions may be negative after a pasted image expands the transparent canvas.
        // Size fields declare their own nonnegative minimum in XAML.
        double minimum = double.IsFinite(number.Minimum) ? number.Minimum : 0;
        double maximum = double.IsFinite(number.Maximum) ? Math.Max(minimum, number.Maximum) : double.MaxValue;
        double current = double.IsFinite(number.Value) ? number.Value : minimum;
        int notches = Math.Max(1, (int)Math.Round(Math.Abs((double)delta) / 120,
            MidpointRounding.AwayFromZero));
        number.Value = Math.Clamp(current + Math.Sign(delta) * notches * (shift ? 10 : 1), minimum, maximum);
        args.Handled = true;
    }

    private void HandleColorChanged(ColorPicker sender, Color color, bool commitSelected)
    {
        if (!IsLoaded || _updatingOptions || InspectorTitle is null)
        {
            return;
        }

        EditorTool tool = InspectorTool();
        ToolStyle style = _toolStyles[tool];
        ToolStyle current = StyleFor(InspectorAnnotation(), tool);
        uint argb = ((uint)color.A << 24) | ((uint)color.R << 16) |
            ((uint)color.G << 8) | color.B;
        if (sender == PrimaryColorPicker)
        {
            style.Primary = argb;
            PrimaryColorSwatch.Background = new SolidColorBrush(color);
            if (commitSelected)
            {
                UpdateSelected(annotation => annotation with { StrokeArgb = argb });
            }
        }
        else if (sender == SecondaryColorPicker)
        {
            // Zero is the legacy fallback marker for step and callout text colors.
            if (commitSelected && tool is EditorTool.Step or EditorTool.Callout && argb >> 24 == 0)
            {
                argb = WithVisibleAlpha(argb, 1);
                SetPickerColor(SecondaryColorPicker, SecondaryColorSwatch, argb);
            }

            bool fillWasEnabled = current.FillShape;
            style.Secondary = argb;
            SecondaryColorSwatch.Background = new SolidColorBrush(ShnappRenderer.FromArgb(argb));
            if (commitSelected && tool == EditorTool.Step)
            {
                UpdateSelected(annotation => annotation with { StepTextArgb = argb });
            }
            else if (commitSelected && tool == EditorTool.Callout)
            {
                UpdateSelected(annotation => annotation with { CalloutTextArgb = argb });
            }
            else if (commitSelected && tool is EditorTool.Rectangle or EditorTool.Square or EditorTool.Ellipse or EditorTool.Circle or EditorTool.Shape)
            {
                if (fillWasEnabled && argb >> 24 == 0)
                {
                    // An invisible fill is an off fill; an outline keeps the shape valid.
                    style.FillShape = false;
                    style.OutlineShape = true;
                    _updatingOptions = true;
                    FillShape.IsChecked = false;
                    OutlineShape.IsChecked = true;
                    _updatingOptions = false;
                    PrimaryColorButton.IsEnabled = true;
                    StrokeSize.IsEnabled = true;
                }

                if (fillWasEnabled)
                {
                    UpdateSelected(annotation => annotation with
                    {
                        FillArgb = argb >> 24 == 0 ? 0 : argb,
                        HideOutline = argb >> 24 == 0 ? false : annotation.HideOutline,
                    });
                }
            }
        }
        else if (sender == TextBackgroundColorPicker)
        {
            bool backgroundWasEnabled = current.TextBackgroundEnabled;
            style.TextBackgroundColor = argb;
            TextBackgroundSwatch.Background = new SolidColorBrush(color);
            if (commitSelected && backgroundWasEnabled)
            {
                if (argb >> 24 == 0)
                {
                    style.TextBackgroundEnabled = false;
                    _updatingOptions = true;
                    TextBackgroundEnabled.IsChecked = false;
                    _updatingOptions = false;
                    TextBackgroundColorButton.IsEnabled = false;
                }

                UpdateSelected(annotation => annotation with
                {
                    FillArgb = argb >> 24 == 0 ? 0 : argb,
                });
            }
        }
        else if (sender == TextBorderColorPicker)
        {
            bool borderWasEnabled = current.TextBorderEnabled;
            style.TextBorderColor = argb;
            TextBorderSwatch.Background = new SolidColorBrush(color);
            if (commitSelected && borderWasEnabled)
            {
                if (argb >> 24 == 0)
                {
                    style.TextBorderEnabled = false;
                    _updatingOptions = true;
                    TextBorderEnabled.IsChecked = false;
                    _updatingOptions = false;
                    TextBorderColorButton.IsEnabled = false;
                    TextBorderWidthChoice.IsEnabled = false;
                }

                UpdateSelected(annotation => annotation with
                {
                    TextBorderArgb = argb >> 24 == 0 ? 0 : argb,
                    TextBorderWidth = style.TextBorderWidth,
                });
            }
        }
    }

    private static uint WithVisibleAlpha(uint color, byte fallbackAlpha) =>
        color >> 24 == 0 ? ((uint)fallbackAlpha << 24) | (color & 0x00FFFFFF) : color;

    private void SetPickerColor(ColorPicker picker, Border swatch, uint argb)
    {
        bool wasUpdating = _updatingOptions;
        _updatingOptions = true;
        try
        {
            picker.Color = ShnappRenderer.FromArgb(argb);
            swatch.Background = new SolidColorBrush(picker.Color);
        }
        finally
        {
            _updatingOptions = wasUpdating;
        }
    }

    private void UpdateSelected(Func<Annotation, Annotation> change)
    {
        if (_textBox is not null && _textDraft is { } draft)
        {
            Annotation currentDraft = SyncTextDraftFromEditor();
            Annotation updatedDraft = change(currentDraft);
            if (updatedDraft != draft)
            {
                _textDraft = updatedDraft;
                RefreshActiveTextEditorStyle();
                UpdateInspector();
            }

            return;
        }

        Annotation? selected = SelectedAnnotation();
        if (selected is not null)
        {
            Annotation updated = change(selected);
            if (updated != selected)
            {
                if (updated.Kind == AnnotationKind.Text)
                {
                    updated = MeasureTextAnnotation(updated);
                }

                try
                {
                    _editor!.UpdateAnnotation(updated);
                }
                catch (ArgumentException exception)
                {
                    ShowAnnotationEditError(exception);
                }
            }
        }
    }
}

public sealed class FontPreviewChoice(string name)
{
    public string Name { get; } = name;
    public FontFamily PreviewFont { get; } = new(name);
}
