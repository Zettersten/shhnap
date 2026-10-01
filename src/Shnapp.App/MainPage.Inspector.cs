using Microsoft.UI.Input;
using Microsoft.Graphics.Canvas.Text;
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

    private void InitializeFontFamilies()
    {
        IReadOnlyList<string> installed;
        try
        {
            installed = CanvasTextFormat.GetSystemFontFamilies();
        }
        catch
        {
            // The five built-in choices remain available if Windows cannot enumerate fonts.
            return;
        }

        _updatingOptions = true;
        string[] preferred = ["Segoe UI Variable Text", "Segoe UI", "Arial", "Calibri", "Georgia", "Consolas"];
        string[] choices = preferred.Concat(installed.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith('@'))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        FontFamilyChoice.Items.Clear();
        foreach (string family in choices)
        {
            FontFamilyChoice.Items.Add(new ComboBoxItem { Content = family, Tag = family });
        }

        SelectComboValue(FontFamilyChoice, preferred[0]);
        _updatingOptions = false;
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
        AnnotationKind.Arrow => EditorTool.Line,
        AnnotationKind.Line => EditorTool.Line,
        AnnotationKind.Rectangle => EditorTool.Rectangle,
        AnnotationKind.Ellipse => EditorTool.Ellipse,
        AnnotationKind.Redaction => EditorTool.Redaction,
        _ => EditorTool.Select,
    };

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
            Secondary = selected.Kind == AnnotationKind.Step
                ? selected.StepTextArgb == 0 ? 0xFFFFFFFF : selected.StepTextArgb
                : selected.FillArgb == 0 ? defaults.Secondary : 0xFF000000 | (selected.FillArgb & 0x00FFFFFF),
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
            TextTruncation = selected.TextTruncation,
            TextLineHeight = selected.TextLineHeight,
            StepDiameter = selected.StepDiameter,
            FillShape = selected.FillArgb != 0,
            FillOpacity = selected.FillArgb == 0 ? defaults.FillOpacity : Math.Round((selected.FillArgb >> 24) * 100.0 / 255),
            StartArrow = selected.StartArrow,
            EndArrow = selected.EndArrow || selected.Kind == AnnotationKind.Arrow,
            StartCap = selected.EffectiveStartCap,
            EndCap = selected.EffectiveEndCap,
            LinePattern = selected.LinePattern,
            StepLabelFormat = selected.StepLabelFormat,
            RedactionMode = selected.RedactionMode,
        };
    }

    private void UpdateInspector()
    {
        if (InspectorTitle is null)
        {
            return;
        }

        Annotation? selected = InspectorAnnotation();
        EditorTool tool = selected is null ? _tool : ToolFor(selected);
        ToolStyle style = StyleFor(selected, tool);
        bool text = tool == EditorTool.Text;
        bool step = tool == EditorTool.Step;
        bool line = tool is EditorTool.Line or EditorTool.Arrow;
        bool shape = tool is EditorTool.Rectangle or EditorTool.Square or EditorTool.Ellipse or EditorTool.Circle;
        bool redaction = tool == EditorTool.Redaction;
        bool styleable = text || step || line || shape;
        bool fixedTextBox = selected is { Kind: AnnotationKind.Text, TextBoxWidth: > 0, TextBoxHeight: > 0 };
        bool editingText = _textBox is not null && _textDraft is not null;

        _updatingOptions = true;
        InspectorTitle.Text = editingText ? "Editing text" : redaction
            ? selected is null ? RedactionModeLabel(style.RedactionMode) : $"Selected {RedactionModeLabel(style.RedactionMode).ToLowerInvariant()}"
            : selected is null ? tool.ToString() : line ? "Selected line" : $"Selected {selected.Kind.ToString().ToLowerInvariant()}";
        InspectorHelp.Text = editingText
            ? fixedTextBox
                ? "Text stays within the drawn box. Set alignment and overflow here. Press Ctrl+Enter to finish."
                : "Text grows as you type. Press Ctrl+Enter to finish."
            : redaction
            ? selected is null
                ? "Drag to apply. Cover fully hides pixels in shared images; blur and pixelate obscure them."
                : "Change the mode, drag handles to resize, or use arrow keys to nudge. Use Cover for private details."
            : fixedTextBox
                ? "Text stays within this box. Resize with the handles; choose alignment, overflow and line height below."
            : selected is null
                ? ToolHint()
                : "Change options here, drag to move, or use arrow keys to nudge (Shift: 10 px). Drag handles to resize.";

        RedactionModeRow.Visibility = Visible(redaction);
        PrimaryColorRow.Visibility = Visible(styleable);
        SecondaryColorRow.Visibility = Visible(step || shape);
        StrokeWidthRow.Visibility = Visible(line || shape);
        FontFamilyRow.Visibility = Visible(text || step);
        FontWeightRow.Visibility = Visible(text || step);
        ItalicRow.Visibility = Visible(text);
        FontSizeRow.Visibility = Visible(text || step);
        TextCharacterRow.Visibility = Visible(text);
        TextBoxLayoutRow.Visibility = Visible(fixedTextBox);
        StepSizeRow.Visibility = Visible(step);
        StepLabelRow.Visibility = Visible(step);
        OutlineToggleRow.Visibility = Visible(shape);
        FillToggleRow.Visibility = Visible(shape);
        FillOpacityRow.Visibility = Visible(shape);
        LineCapRow.Visibility = Visible(line);
        LinePatternRow.Visibility = Visible(line);

        PrimaryColorLabel.Text = tool switch
        {
            EditorTool.Text => "Text color",
            EditorTool.Step => "Dot color",
            EditorTool.Line or EditorTool.Arrow => "Line color",
            _ => "Outline color",
        };
        SecondaryColorLabel.Text = step ? "Number color" : "Fill color";
        PrimaryColorPicker.Color = ShnappRenderer.FromArgb(style.Primary | 0xFF000000);
        SecondaryColorPicker.Color = ShnappRenderer.FromArgb(style.Secondary | 0xFF000000);
        PrimaryColorSwatch.Background = new SolidColorBrush(PrimaryColorPicker.Color);
        SecondaryColorSwatch.Background = new SolidColorBrush(SecondaryColorPicker.Color);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PrimaryColorButton, PrimaryColorLabel.Text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SecondaryColorButton, SecondaryColorLabel.Text);

        StrokeSize.Value = style.StrokeWidth;
        StrokeSize.IsEnabled = !shape || style.OutlineShape;
        PrimaryColorButton.IsEnabled = !shape || style.OutlineShape;
        FontSizeChoice.Value = style.FontSize;
        TextKerningChoice.IsChecked = style.TextKerning;
        TextLetterSpacingChoice.Value = style.TextLetterSpacing;
        TextLineHeightChoice.Value = style.TextLineHeight;
        StepSize.Value = style.StepDiameter;
        FillOpacityChoice.Value = style.FillOpacity;
        FillShape.IsChecked = style.FillShape;
        OutlineShape.IsChecked = style.OutlineShape;
        FillOpacityChoice.IsEnabled = style.FillShape;
        ItalicText.IsChecked = style.Italic;
        StepResetCount.IsChecked = selected?.StepReset == true;
        StepResetCount.IsEnabled = selected?.Kind == AnnotationKind.Step;
        SelectComboValue(StepLabelChoice, style.StepLabelFormat.ToString());
        SelectComboValue(StartCapChoice, style.StartCap.ToString());
        SelectComboValue(EndCapChoice, style.EndCap.ToString());
        SelectComboValue(LinePatternChoice, style.LinePattern.ToString());
        SelectComboValue(RedactionModeChoice, style.RedactionMode.ToString());
        SelectComboValue(FontFamilyChoice, style.FontFamily);
        SelectComboValue(FontWeightChoice, style.FontWeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SelectComboValue(TextTransformChoice, style.TextTransform.ToString());
        SelectComboValue(TextAlignmentChoice, style.TextAlignment.ToString());
        SelectComboValue(TextTruncationChoice, style.TextTruncation.ToString());
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
        if (_updatingOptions || InspectorTitle is null)
        {
            return;
        }

        EditorTool tool = InspectorTool();
        ToolStyle style = _toolStyles[tool];
        ToolStyle current = StyleFor(InspectorAnnotation(), tool);
        if (ReferenceEquals(sender, StrokeSize))
        {
            style.StrokeWidth = Math.Clamp(FiniteValue(StrokeSize.Value, 3), 1, 24);
            UpdateSelected(annotation => annotation with { StrokeWidth = style.StrokeWidth });
        }
        else if (ReferenceEquals(sender, FontSizeChoice))
        {
            style.FontSize = Math.Clamp(FiniteValue(FontSizeChoice.Value, 18), 8, 144);
            UpdateSelected(annotation => annotation with { FontSize = style.FontSize });
        }
        else if (ReferenceEquals(sender, TextKerningChoice))
        {
            style.TextKerning = TextKerningChoice.IsChecked == true;
            UpdateSelected(annotation => annotation with { TextKerning = style.TextKerning });
        }
        else if (ReferenceEquals(sender, TextLetterSpacingChoice))
        {
            style.TextLetterSpacing = Math.Clamp(FiniteValue(TextLetterSpacingChoice.Value, 0), 0, 50);
            UpdateSelected(annotation => annotation with { TextLetterSpacing = style.TextLetterSpacing });
        }
        else if (ReferenceEquals(sender, TextTransformChoice) &&
            TextTransformChoice.SelectedItem is ComboBoxItem transformChoice &&
            Enum.TryParse(transformChoice.Tag?.ToString(), out TextTransformMode textTransform))
        {
            style.TextTransform = textTransform;
            UpdateSelected(annotation => annotation with { TextTransform = textTransform });
        }
        else if (ReferenceEquals(sender, TextAlignmentChoice) &&
            TextAlignmentChoice.SelectedItem is ComboBoxItem alignmentChoice &&
            Enum.TryParse(alignmentChoice.Tag?.ToString(), out TextHorizontalAlignment textAlignment))
        {
            style.TextAlignment = textAlignment;
            UpdateSelected(annotation => annotation with { TextAlignment = textAlignment });
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
        else if (ReferenceEquals(sender, FontFamilyChoice) && FontFamilyChoice.SelectedItem is ComboBoxItem family)
        {
            style.FontFamily = family.Tag?.ToString() ?? family.Content?.ToString() ?? style.FontFamily;
            UpdateSelected(annotation => annotation with { FontFamily = style.FontFamily });
        }
        else if (ReferenceEquals(sender, FontWeightChoice) && FontWeightChoice.SelectedItem is ComboBoxItem weight &&
            int.TryParse(weight.Tag?.ToString(), out int fontWeight))
        {
            style.FontWeight = fontWeight;
            UpdateSelected(annotation => annotation with { FontWeight = fontWeight });
        }
        else if (ReferenceEquals(sender, ItalicText))
        {
            style.Italic = ItalicText.IsChecked == true;
            UpdateSelected(annotation => annotation with { Italic = style.Italic });
        }
        else if (ReferenceEquals(sender, OutlineShape) || ReferenceEquals(sender, FillShape) ||
            ReferenceEquals(sender, FillOpacityChoice))
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
            style.FillOpacity = Math.Clamp(FiniteValue(FillOpacityChoice.Value, 25), 1, 100);
            StrokeSize.IsEnabled = style.OutlineShape;
            PrimaryColorButton.IsEnabled = style.OutlineShape;
            FillOpacityChoice.IsEnabled = style.FillShape;
            uint fill = style.FillShape ? WithOpacity(current.Secondary, style.FillOpacity) : 0;
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
        if (_updatingOptions || InspectorTitle is null)
        {
            return;
        }

        EditorTool tool = InspectorTool();
        ToolStyle style = _toolStyles[tool];
        ToolStyle current = StyleFor(InspectorAnnotation(), tool);
        uint argb = 0xFF000000 | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
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
            style.Secondary = argb;
            SecondaryColorSwatch.Background = new SolidColorBrush(color);
            if (commitSelected && tool == EditorTool.Step)
            {
                UpdateSelected(annotation => annotation with { StepTextArgb = argb });
            }
            else if (commitSelected && tool is EditorTool.Rectangle or EditorTool.Square or EditorTool.Ellipse or EditorTool.Circle)
            {
                uint fill = current.FillShape ? WithOpacity(argb, current.FillOpacity) : 0;
                UpdateSelected(annotation => annotation with { FillArgb = fill });
            }
        }
    }

    private void UpdateSelected(Func<Annotation, Annotation> change)
    {
        if (_textBox is not null && _textDraft is { } draft)
        {
            Annotation updatedDraft = change(draft);
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
                catch (ArgumentException exception) when (updated.Kind == AnnotationKind.Text)
                {
                    ShowMessage("Text exceeds canvas limits", exception.Message);
                }
            }
        }
    }
}
