using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Shnapp.Core;
using Windows.Foundation;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private readonly Dictionary<VideoOverlayShape, ToggleMenuFlyoutItem>
        _videoShapeMenuItems = [];

    private void InitializeVideoShapeMenu()
    {
        var menu = new MenuFlyout();
        foreach ((VideoOverlayShape shape, string label) in new[]
        {
            (VideoOverlayShape.Rectangle, "Original rectangle"),
            (VideoOverlayShape.Square, "Square · 1:1"),
            (VideoOverlayShape.Circle, "Circle · 1:1"),
            (VideoOverlayShape.Squircle, "Squircle · 1:1"),
            (VideoOverlayShape.Oval, "Oval"),
            (VideoOverlayShape.RoundedRectangle, "Rounded rectangle"),
        })
        {
            var item = new ToggleMenuFlyoutItem { Text = label, Tag = shape };
            item.Click += VideoShapeMenu_Click;
            _videoShapeMenuItems.Add(shape, item);
            menu.Items.Add(item);
        }
        _canvasMediaShapeButton.Flyout = menu;
        ToolTipService.SetToolTip(_canvasMediaShapeButton, "Video crop shape");
        AutomationProperties.SetName(_canvasMediaShapeButton, "Video crop shape");
    }

    private void RefreshVideoShapeMenuSelection()
    {
        VideoOverlayShape selected = _selectedKind == TimelineKind.VideoOverlay &&
            _editor?.Current.VideoOverlays.FirstOrDefault(item =>
                item.Id == _selectedId) is { } overlay
            ? overlay.Shape : VideoOverlayShape.Rectangle;
        foreach ((VideoOverlayShape shape, ToggleMenuFlyoutItem item) in
            _videoShapeMenuItems)
            item.IsChecked = shape == selected;
    }

    private void VideoShapeMenu_Click(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: VideoOverlayShape shape })
            ApplyVideoShapePreset(shape);
    }

    private void VideoShapeBox_SelectionChanged(object sender,
        SelectionChangedEventArgs args)
    {
        if (_loadingInspector || VideoShapeBox.SelectedItem is not ComboBoxItem
            { Tag: string value } ||
            !Enum.TryParse(value, out VideoOverlayShape shape)) return;
        ApplyVideoShapePreset(shape);
    }

    private void ApplyVideoShapePreset(VideoOverlayShape shape)
    {
        if (_editor is null || _selectedKind != TimelineKind.VideoOverlay ||
            !ApplyPendingInspectorChanges()) return;
        VideoOverlay? overlay = _editor.Current.VideoOverlays.FirstOrDefault(item =>
            item.Id == _selectedId);
        if (overlay is null || overlay.Shape == shape) return;
        if (TryEdit(() => _editor.UpdateVideoOverlay(
            VideoOverlayCropPresets.Apply(overlay, shape))))
            StatusText.Text = $"Video shape: {shape switch
            {
                VideoOverlayShape.RoundedRectangle => "rounded rectangle",
                _ => shape.ToString().ToLowerInvariant(),
            }}";
    }

    private sealed class ShapeClipState
    {
        internal VideoOverlayShape Shape;
        internal double Width;
        internal double Height;
        internal CanvasGeometry? Path;
    }

    private readonly ConditionalWeakTable<UIElement, ShapeClipState>
        _shapeClipStates = new();

    private void ApplyVideoShapeClip(UIElement element, VideoOverlayShape shape,
        double width, double height)
    {
        element.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, width, height),
        };
        ShapeClipState state = _shapeClipStates.GetOrCreateValue(element);
        if (state.Shape == shape && state.Width == width &&
            state.Height == height) return;
        state.Path?.Dispose();
        state.Path = null;
        state.Shape = shape;
        state.Width = width;
        state.Height = height;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (shape is VideoOverlayShape.Rectangle or VideoOverlayShape.Square)
        {
            visual.Clip = null;
            return;
        }
        Compositor compositor = visual.Compositor;
        CompositionGeometry geometry;
        if (shape is VideoOverlayShape.Circle or VideoOverlayShape.Oval)
        {
            var ellipse = compositor.CreateEllipseGeometry();
            ellipse.Center = new Vector2((float)width / 2, (float)height / 2);
            ellipse.Radius = ellipse.Center;
            geometry = ellipse;
        }
        else if (shape == VideoOverlayShape.RoundedRectangle)
        {
            var rounded = compositor.CreateRoundedRectangleGeometry();
            rounded.Size = new Vector2((float)width, (float)height);
            float radius = (float)Math.Min(width, height) * .12f;
            rounded.CornerRadius = new Vector2(radius, radius);
            geometry = rounded;
        }
        else
        {
            using var path = new CanvasPathBuilder(CanvasDevice.GetSharedDevice());
            for (int index = 0; index <= 64; index++)
            {
                double angle = 2 * Math.PI * index / 64;
                double cosine = Math.Cos(angle);
                double sine = Math.Sin(angle);
                var point = new Vector2(
                    (float)(width / 2 + width / 2 * Math.Sign(cosine) *
                        Math.Sqrt(Math.Abs(cosine))),
                    (float)(height / 2 + height / 2 * Math.Sign(sine) *
                        Math.Sqrt(Math.Abs(sine))));
                if (index == 0) path.BeginFigure(point);
                else path.AddLine(point);
            }
            path.EndFigure(CanvasFigureLoop.Closed);
            state.Path = CanvasGeometry.CreatePath(path);
            var compositionPath = new CompositionPath(state.Path);
            var pathGeometry = compositor.CreatePathGeometry();
            pathGeometry.Path = compositionPath;
            geometry = pathGeometry;
        }
        visual.Clip = compositor.CreateGeometricClip(geometry);
    }
}
