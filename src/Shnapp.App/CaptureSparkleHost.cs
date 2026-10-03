using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Shnapp.App;

/// <summary>Small, clipped brand sparkles for the primary side of a capture button.</summary>
public sealed class CaptureSparkleHost : Grid
{
    private readonly UISettings _motionSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly DispatcherQueueTimer _visibilityTimer;
    private readonly List<ContainerVisual> _sparkles = [];
    private AppWindow? _appWindow;
    private ContainerVisual? _layer;
    private int _generation;

    public CaptureSparkleHost()
    {
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
        IsHitTestVisible = false;
        _visibilityTimer = DispatcherQueue.CreateTimer();
        _visibilityTimer.Interval = TimeSpan.FromMilliseconds(500);
        _visibilityTimer.IsRepeating = true;
        _visibilityTimer.Tick += (_, _) => Refresh();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) =>
        {
            Stop();
            Refresh();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _appWindow = App.Window.AppWindow;
        _appWindow.Changed += OnAppWindowChanged;
        if (_appWindow.IsVisible)
        {
            _visibilityTimer.Start();
        }
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _visibilityTimer.Stop();
        if (_appWindow is not null)
        {
            _appWindow.Changed -= OnAppWindowChanged;
            _appWindow = null;
        }
        Stop();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidVisibilityChange)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded)
            {
                return;
            }

            if (sender.IsVisible) { _visibilityTimer.Start(); }
            else { _visibilityTimer.Stop(); }
            Refresh();
        });
    }

    /// <summary>Rechecks parent visibility after the library or editor switches.</summary>
    public void Refresh()
    {
        if (!IsLoaded || _appWindow?.IsVisible != true ||
            ActualWidth < 24 || ActualHeight < 24 ||
            !IsInVisibleTree() || _accessibility.HighContrast || !_motionSettings.AnimationsEnabled)
        {
            Stop();
            return;
        }

        if (_layer is not null)
        {
            return;
        }

        Compositor compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        ContainerVisual layer = compositor.CreateContainerVisual();
        layer.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        layer.Clip = compositor.CreateInsetClip();
        ElementCompositionPreview.SetElementChildVisual(this, layer);
        _layer = layer;
        int generation = ++_generation;
        int count = Math.Clamp((int)(ActualWidth / 22) + 2, 8, 12);
        for (int index = 0; index < count; index++)
        {
            ContainerVisual sparkle = CreateSparkle(compositor);
            layer.Children.InsertAtTop(sparkle);
            _sparkles.Add(sparkle);
            AnimateSparkle(sparkle, generation,
                TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1050)));
        }
    }

    private bool IsInVisibleTree()
    {
        for (DependencyObject? current = this; current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element && element.Visibility != Visibility.Visible)
            {
                return false;
            }
        }

        return true;
    }

    private static ContainerVisual CreateSparkle(Compositor compositor)
    {
        ContainerVisual sparkle = compositor.CreateContainerVisual();
        sparkle.Size = new Vector2(6, 6);
        sparkle.CenterPoint = new Vector3(3, 3, 0);
        sparkle.Opacity = 0;

        var brush = compositor.CreateColorBrush(Colors.White);
        SpriteVisual vertical = compositor.CreateSpriteVisual();
        vertical.Brush = brush;
        vertical.Size = new Vector2(1.4f, 6);
        vertical.Offset = new Vector3(2.3f, 0, 0);
        sparkle.Children.InsertAtTop(vertical);

        SpriteVisual horizontal = compositor.CreateSpriteVisual();
        horizontal.Brush = brush;
        horizontal.Size = new Vector2(6, 1.4f);
        horizontal.Offset = new Vector3(0, 2.3f, 0);
        sparkle.Children.InsertAtTop(horizontal);
        return sparkle;
    }

    private void AnimateSparkle(ContainerVisual sparkle, int generation, TimeSpan delay)
    {
        if (generation != _generation || _layer is null)
        {
            return;
        }

        Compositor compositor = sparkle.Compositor;
        float width = _layer.Size.X;
        float height = _layer.Size.Y;
        float x = 7 + Random.Shared.NextSingle() * Math.Max(1, width - 14);
        float y = height * (0.64f + Random.Shared.NextSingle() * 0.2f);
        float rise = height * (0.32f + Random.Shared.NextSingle() * 0.2f);
        TimeSpan duration = TimeSpan.FromMilliseconds(Random.Shared.Next(1650, 2700));

        sparkle.Offset = new Vector3(x, y, 0);
        sparkle.Opacity = 0;
        Vector3KeyFrameAnimation drift = compositor.CreateVector3KeyFrameAnimation();
        drift.InsertKeyFrame(0, new Vector3(x, y, 0));
        drift.InsertKeyFrame(1, new Vector3(x, y - rise, 0));
        drift.Duration = duration;
        drift.DelayTime = delay;

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(0.2f, 0.88f);
        fade.InsertKeyFrame(0.62f, 0.75f);
        fade.InsertKeyFrame(1, 0);
        fade.Duration = duration;
        fade.DelayTime = delay;

        Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(0.72f, 0.72f, 1));
        scale.InsertKeyFrame(0.45f, new Vector3(1.16f, 1.16f, 1));
        scale.InsertKeyFrame(1, new Vector3(0.84f, 0.84f, 1));
        scale.Duration = duration;
        scale.DelayTime = delay;

        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        sparkle.StartAnimation("Offset", drift);
        sparkle.StartAnimation("Opacity", fade);
        sparkle.StartAnimation("Scale", scale);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            AnimateSparkle(sparkle, generation,
                TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250))));
    }

    private void Stop()
    {
        if (_layer is null)
        {
            return;
        }

        ++_generation;
        foreach (ContainerVisual sparkle in _sparkles)
        {
            sparkle.StopAnimation("Offset");
            sparkle.StopAnimation("Opacity");
            sparkle.StopAnimation("Scale");
        }
        _sparkles.Clear();
        ElementCompositionPreview.SetElementChildVisual(this, null!);
        _layer = null;
    }
}
