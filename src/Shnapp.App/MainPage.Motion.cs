using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Shnapp.App.Editor;
using Windows.UI.ViewManagement;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private readonly UISettings _surfaceMotionSettings = new();
    private Storyboard? _inspectorStoryboard;
    private Storyboard? _inspectorContentStoryboard;
    private string? _inspectorRenderedIdentity;
    private bool _inspectorContentTransitioning;
    private bool _inspectorContentEntering;

    private string InspectorContentIdentity()
    {
        if (HasMultipleSelectedAnnotations)
        {
            return $"group:{string.Join(",", _selectedIds.OrderBy(id => id))}";
        }

        if (InspectorAnnotation() is { } selected)
        {
            return $"annotation:{selected.Id}:{selected.Kind}:{_textBox is not null}";
        }

        return _tool == EditorTool.Shape ? $"tool:{_tool}:{_activeShape}" : $"tool:{_tool}";
    }

    private bool CanAnimateInspectorContent() =>
        IsLoaded && _surfaceMotionSettings.AnimationsEnabled && !_accessibility.HighContrast &&
        InspectorPanel.Visibility == Visibility.Visible && InspectorPanel.Opacity >= 0.99 &&
        InspectorToggle.IsChecked == true && _inspectorStoryboard is null;

    private void UpdateInspectorContent()
    {
        if (InspectorContent is null)
        {
            return;
        }

        string requested = InspectorContentIdentity();
        if (_inspectorRenderedIdentity is null || !CanAnimateInspectorContent())
        {
            ResetInspectorContentMotion();
            RenderInspectorOptions();
            _inspectorRenderedIdentity = requested;
            return;
        }

        if (_inspectorContentTransitioning)
        {
            if (requested == _inspectorRenderedIdentity)
            {
                RenderInspectorOptions();
                return;
            }

            // An exit already renders the latest tool at its end. Interrupt an
            // entrance so a newer choice does not wait behind stale controls.
            if (!_inspectorContentEntering)
            {
                return;
            }
        }

        if (requested == _inspectorRenderedIdentity)
        {
            RenderInspectorOptions();
            return;
        }

        _inspectorContentTransitioning = true;
        _inspectorContentEntering = false;
        InspectorContent.IsHitTestVisible = false;
        AnimateInspectorContent(0, 10, 65, EasingMode.EaseIn, () =>
        {
            // Read the current selection here so rapid switches show the latest options.
            RenderInspectorOptions();
            _inspectorRenderedIdentity = InspectorContentIdentity();
            InspectorScroll.ChangeView(null, 0, null, true);

            InspectorContent.Opacity = 0;
            ((TranslateTransform)InspectorContent.RenderTransform).X = -10;
            InspectorContent.IsHitTestVisible = true;
            _inspectorContentEntering = true;
            AnimateInspectorContent(1, 0, 125, EasingMode.EaseOut, () =>
            {
                _inspectorContentTransitioning = false;
                _inspectorContentEntering = false;
                InspectorContent.IsHitTestVisible = true;
                if (InspectorContentIdentity() != _inspectorRenderedIdentity)
                {
                    UpdateInspectorContent();
                }
                else
                {
                    RenderInspectorOptions();
                }
            });
        });
    }

    private void AnimateInspectorContent(double opacity, double x, int milliseconds,
        EasingMode easingMode, Action completed)
    {
        var offset = (TranslateTransform)InspectorContent.RenderTransform;
        double startingOpacity = InspectorContent.Opacity;
        double startingX = offset.X;
        Storyboard? previous = _inspectorContentStoryboard;
        _inspectorContentStoryboard = null;
        previous?.Stop();
        InspectorContent.Opacity = startingOpacity;
        offset.X = startingX;

        var storyboard = new Storyboard();
        var easing = new CubicEase { EasingMode = easingMode };
        var fade = new DoubleAnimation
        {
            From = startingOpacity,
            To = opacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(fade, InspectorContent);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        var slide = new DoubleAnimation
        {
            From = startingX,
            To = x,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(slide, offset);
        Storyboard.SetTargetProperty(slide, "X");
        storyboard.Children.Add(slide);

        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_inspectorContentStoryboard, storyboard))
            {
                return;
            }

            _inspectorContentStoryboard = null;
            storyboard.Stop();
            InspectorContent.Opacity = opacity;
            offset.X = x;
            completed();
        };
        _inspectorContentStoryboard = storyboard;
        storyboard.Begin();
    }

    private void ResetInspectorContentMotion()
    {
        Storyboard? previous = _inspectorContentStoryboard;
        _inspectorContentStoryboard = null;
        previous?.Stop();
        _inspectorContentTransitioning = false;
        _inspectorContentEntering = false;
        InspectorContent.Opacity = 1;
        ((TranslateTransform)InspectorContent.RenderTransform).X = 0;
        InspectorContent.IsHitTestVisible = true;
    }

    private void SetInspectorVisibility(bool visible, bool animate)
    {
        if (!visible && _inspectorContentTransitioning)
        {
            ResetInspectorContentMotion();
            RenderInspectorOptions();
            _inspectorRenderedIdentity = InspectorContentIdentity();
        }

        Storyboard? previous = _inspectorStoryboard;
        _inspectorStoryboard = null;
        previous?.Stop();

        InspectorToggle.IsChecked = visible;
        InspectorPanel.IsHitTestVisible = visible;
        var offset = (TranslateTransform)InspectorPanel.RenderTransform;
        if (!animate || !_surfaceMotionSettings.AnimationsEnabled || _accessibility.HighContrast)
        {
            InspectorPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            InspectorPanel.Opacity = 1;
            offset.X = 0;
            offset.Y = 0;
            return;
        }

        if (visible && InspectorPanel.Visibility == Visibility.Visible && InspectorPanel.Opacity == 1 &&
            offset.X == 0 && offset.Y == 0)
        {
            return;
        }

        if (!visible && InspectorPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        if (visible)
        {
            InspectorPanel.Visibility = Visibility.Visible;
            InspectorPanel.Opacity = 0;
            offset.X = 24;
            offset.Y = 24;
        }

        int milliseconds = visible ? 170 : 130;
        var easing = new CubicEase { EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn };
        var storyboard = new Storyboard();
        AddMotion(InspectorPanel, "Opacity", InspectorPanel.Opacity, visible ? 1 : 0);
        AddMotion(offset, "X", offset.X, visible ? 0 : 24);
        AddMotion(offset, "Y", offset.Y, visible ? 0 : 24);
        storyboard.Completed += (_, _) =>
        {
            storyboard.Stop();
            if (!ReferenceEquals(_inspectorStoryboard, storyboard))
            {
                return;
            }

            InspectorPanel.Opacity = 1;
            offset.X = 0;
            offset.Y = 0;
            InspectorPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            _inspectorStoryboard = null;
        };
        _inspectorStoryboard = storyboard;
        storyboard.Begin();

        void AddMotion(DependencyObject target, string property, double from, double to)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = easing,
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
    }

    private void AnimateSurfaceEntry(FrameworkElement surface)
    {
        if (ReferenceEquals(surface, LibraryRoot))
        {
            ResetInspectorContentMotion();
            _inspectorRenderedIdentity = null;
        }

        if (!_surfaceMotionSettings.AnimationsEnabled)
        {
            surface.Opacity = 1;
            return;
        }

        surface.Opacity = 0;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (surface.Visibility != Visibility.Visible)
            {
                surface.Opacity = 1;
                return;
            }

            Storyboard storyboard = new();
            DoubleAnimation fade = new()
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(190)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, surface);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
            storyboard.Completed += (_, _) => surface.Opacity = 1;
            storyboard.Begin();
        });
    }
}
