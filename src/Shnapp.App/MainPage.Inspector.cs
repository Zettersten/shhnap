using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.UI;

namespace Shnapp.App;

public sealed partial class MainPage
{
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
        _editor?.Current.Annotations.FirstOrDefault(annotation => annotation.Id == _selectedId);

    private static EditorTool ToolFor(Annotation annotation) => annotation.Kind switch
    {
        AnnotationKind.Text => EditorTool.Text,
        AnnotationKind.Step => EditorTool.Step,
        AnnotationKind.Arrow => EditorTool.Arrow,
        AnnotationKind.Line when annotation.EndArrow => EditorTool.Arrow,
        AnnotationKind.Line => EditorTool.Line,
        AnnotationKind.Rectangle => EditorTool.Rectangle,
        AnnotationKind.Ellipse => EditorTool.Ellipse,
        AnnotationKind.Redaction => EditorTool.Redaction,
        _ => EditorTool.Select,
    };

    private EditorTool InspectorTool() => SelectedAnnotation() is { } selected ? ToolFor(selected) : _tool;

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
            FontFamily = selected.FontFamily,
            FontWeight = selected.FontWeight,
            Italic = selected.Italic,
            FontSize = selected.FontSize,
            StepDiameter = selected.StepDiameter,
            FillShape = selected.FillArgb != 0,
            FillOpacity = selected.FillArgb == 0 ? defaults.FillOpacity : Math.Round((selected.FillArgb >> 24) * 100.0 / 255),
            StartArrow = selected.StartArrow,
            EndArrow = selected.EndArrow || selected.Kind == AnnotationKind.Arrow,
        };
    }

    private void UpdateInspector()
    {
        if (InspectorTitle is null)
        {
            return;
        }

        Annotation? selected = SelectedAnnotation();
        EditorTool tool = selected is null ? _tool : ToolFor(selected);
        ToolStyle style = StyleFor(selected, tool);
        bool text = tool == EditorTool.Text;
        bool step = tool == EditorTool.Step;
        bool line = tool is EditorTool.Line or EditorTool.Arrow;
        bool shape = tool is EditorTool.Rectangle or EditorTool.Square or EditorTool.Ellipse or EditorTool.Circle;
        bool styleable = text || step || line || shape;

        _updatingOptions = true;
        InspectorTitle.Text = selected is null ? tool.ToString() : $"Selected {selected.Kind.ToString().ToLowerInvariant()}";
        InspectorHelp.Text = selected is null
            ? ToolHint()
            : "Change only what you need. Drag to move; Delete removes this mark.";

        PrimaryColorRow.Visibility = Visible(styleable);
        SecondaryColorRow.Visibility = Visible(step || shape);
        StrokeWidthRow.Visibility = Visible(line || shape);
        FontFamilyRow.Visibility = Visible(text || step);
        FontWeightRow.Visibility = Visible(text || step);
        ItalicRow.Visibility = Visible(text);
        FontSizeRow.Visibility = Visible(text || step);
        StepSizeRow.Visibility = Visible(step);
        FillToggleRow.Visibility = Visible(shape);
        FillOpacityRow.Visibility = Visible(shape);
        LineCapRow.Visibility = Visible(line);

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
        FontSizeChoice.Value = style.FontSize;
        StepSize.Value = style.StepDiameter;
        FillOpacityChoice.Value = style.FillOpacity;
        FillShape.IsChecked = style.FillShape;
        FillOpacityChoice.IsEnabled = style.FillShape;
        ItalicText.IsChecked = style.Italic;
        StartArrow.IsChecked = style.StartArrow;
        EndArrow.IsChecked = style.EndArrow;
        SelectComboValue(FontFamilyChoice, style.FontFamily);
        SelectComboValue(FontWeightChoice, style.FontWeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _updatingOptions = false;
    }

    private static Visibility Visible(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

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
        ToolStyle current = StyleFor(SelectedAnnotation(), tool);
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
        else if (ReferenceEquals(sender, StepSize))
        {
            style.StepDiameter = Math.Clamp(FiniteValue(StepSize.Value, 28), 16, 120);
            UpdateSelected(annotation => annotation with { StepDiameter = style.StepDiameter });
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
        else if (ReferenceEquals(sender, FillShape) || ReferenceEquals(sender, FillOpacityChoice))
        {
            style.FillShape = FillShape.IsChecked == true;
            style.FillOpacity = Math.Clamp(FiniteValue(FillOpacityChoice.Value, 25), 1, 100);
            FillOpacityChoice.IsEnabled = style.FillShape;
            uint fill = style.FillShape ? WithOpacity(current.Secondary, style.FillOpacity) : 0;
            UpdateSelected(annotation => annotation with { FillArgb = fill });
        }
        else if (ReferenceEquals(sender, StartArrow) || ReferenceEquals(sender, EndArrow))
        {
            style.StartArrow = StartArrow.IsChecked == true;
            style.EndArrow = EndArrow.IsChecked == true;
            UpdateSelected(annotation => annotation with
            {
                Kind = AnnotationKind.Line,
                StartArrow = style.StartArrow,
                EndArrow = style.EndArrow,
            });
        }
    }

    private void HandleColorChanged(ColorPicker sender, Color color, bool commitSelected)
    {
        if (_updatingOptions || InspectorTitle is null)
        {
            return;
        }

        EditorTool tool = InspectorTool();
        ToolStyle style = _toolStyles[tool];
        ToolStyle current = StyleFor(SelectedAnnotation(), tool);
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
        Annotation? selected = SelectedAnnotation();
        if (selected is not null)
        {
            Annotation updated = change(selected);
            if (updated != selected)
            {
                _editor!.UpdateAnnotation(updated);
            }
        }
    }
}
