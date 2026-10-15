using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.Foundation;
using Windows.UI;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private BasicShapeKind _activeShape = BasicShapeKind.Rectangle;

    private void ShapePalette_Opening(object sender, object args)
    {
        ShapePaletteGrid.Children.Clear();
        if (ShapePaletteGrid.ColumnDefinitions.Count == 0)
        {
            for (int column = 0; column < 4; column++)
            {
                ShapePaletteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            }

            for (int row = 0; row < 6; row++)
            {
                ShapePaletteGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            }
        }

        Color ink = ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(255, 216, 228, 246)
            : Color.FromArgb(255, 78, 94, 113);
        var stroke = new SolidColorBrush(ink);
        foreach (BasicShapeKind kind in Enum.GetValues<BasicShapeKind>())
        {
            int index = (int)kind;
            var glyph = new XamlPath
            {
                Data = ShapePreviewGeometry(kind),
                Stroke = stroke,
                StrokeThickness = 1.1,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.Uniform,
                Width = 20,
                Height = 20,
                IsHitTestVisible = false,
            };
            var button = new Button
            {
                Width = 38,
                Height = 32,
                MinWidth = 38,
                MinHeight = 32,
                Padding = new Thickness(0),
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = glyph,
                Tag = kind,
            };
            string label = ShapeLabel(kind);
            ToolTipService.SetToolTip(button, label);
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetAutomationId(button, $"Shape{kind}");
            button.Click += ShapePaletteItem_Click;
            Grid.SetColumn(button, index % 4);
            Grid.SetRow(button, index / 4);
            ShapePaletteGrid.Children.Add(button);
        }
    }

    private void ShapePaletteItem_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: BasicShapeKind kind })
        {
            return;
        }

        _activeShape = kind;
        if (!BasicShapeGeometry.IsClosed(kind))
        {
            ToolStyle style = _toolStyles[EditorTool.Shape];
            style.OutlineShape = true;
            style.FillShape = false;
        }
        SetTool(EditorTool.Shape, clearSelectionForUserAction: true);
        ShapesTool.Flyout?.Hide();
    }

    private static PathGeometry ShapePreviewGeometry(BasicShapeKind kind)
    {
        ImagePoint[] outline = BasicShapeGeometry.Outline(kind, new ImageRect(1, 1, 18, 18));
        var figure = new PathFigure
        {
            StartPoint = new Point(outline[0].X, outline[0].Y),
            IsClosed = BasicShapeGeometry.IsClosed(kind),
        };
        foreach (ImagePoint point in outline.AsSpan(1))
        {
            figure.Segments.Add(new LineSegment { Point = new Point(point.X, point.Y) });
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static string ShapeLabel(BasicShapeKind kind) => kind switch
    {
        BasicShapeKind.Ellipse => "Oval",
        BasicShapeKind.Plus => "Cross",
        BasicShapeKind.UShape => "U shape",
        BasicShapeKind.InvertedUShape => "Inverted U shape",
        _ => System.Text.RegularExpressions.Regex.Replace(kind.ToString(), "(?<!^)([A-Z])", " $1"),
    };
}
