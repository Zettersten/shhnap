using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private const int NewShnappSparkleCount = 5;

    private readonly UISettings _newShnappMotionSettings = new();
    private Border? _newShnappBackground;
    private ContainerVisual? _newShnappSparkleLayer;
    private readonly List<ContainerVisual> _newShnappSparkles = [];
    private long _newShnappOverflowCallback;
    private bool _newShnappMotionAttached;
    private Window? _newShnappEventWindow;
    private AppWindow? _newShnappEventAppWindow;
    private int _newShnappSparkleGeneration;

    private void NewShnappButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (FindNewShnappPart<TextBlock>(NewShnappButton, "TextLabel") is { } label)
        {
            label.RenderTransform = new TranslateTransform { X = -6 };
        }

        if (!_newShnappMotionAttached)
        {
            _newShnappEventWindow = App.Window;
            _newShnappEventAppWindow = App.Window.AppWindow;
            _newShnappEventAppWindow.Changed += NewShnappAppWindow_Changed;
            _newShnappEventWindow.Activated += NewShnappWindow_Activated;
            _newShnappOverflowCallback = NewShnappButton.RegisterPropertyChangedCallback(
                AppBarButton.IsInOverflowProperty, (_, _) => RefreshNewShnappSparkles());
            _newShnappMotionAttached = true;
        }

        RefreshNewShnappSparkles();
    }

    private void NewShnappButton_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_newShnappMotionAttached)
        {
            _newShnappMotionAttached = false;
            if (_newShnappEventAppWindow is { } appWindow)
            {
                appWindow.Changed -= NewShnappAppWindow_Changed;
            }
            if (_newShnappEventWindow is { } window)
            {
                window.Activated -= NewShnappWindow_Activated;
            }
            _newShnappEventAppWindow = null;
            _newShnappEventWindow = null;
            NewShnappButton.UnregisterPropertyChangedCallback(
                AppBarButton.IsInOverflowProperty, _newShnappOverflowCallback);
        }

        StopNewShnappSparkles();
        if (_newShnappBackground is not null)
        {
            _newShnappBackground.SizeChanged -= NewShnappBackground_SizeChanged;
            _newShnappBackground = null;
        }
    }

    private void NewShnappAppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange)
        {
            App.DispatcherQueue.TryEnqueue(RefreshNewShnappSparkles);
        }
    }

    private void NewShnappWindow_Activated(object sender, WindowActivatedEventArgs args) =>
        RefreshNewShnappSparkles();

    private void NewShnappBackground_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_newShnappSparkleLayer is not null)
        {
            _newShnappSparkleLayer.Size = new Vector2((float)args.NewSize.Width, (float)args.NewSize.Height);
        }
        else
        {
            RefreshNewShnappSparkles();
        }
    }

    private void RefreshNewShnappSparkles()
    {
        if (!NewShnappButton.IsLoaded || NewShnappButton.IsInOverflow ||
            !App.Window.AppWindow.IsVisible || _accessibility.HighContrast ||
            !_newShnappMotionSettings.AnimationsEnabled)
        {
            StopNewShnappSparkles();
            return;
        }

        Border? background = FindNewShnappPart<Border>(NewShnappButton, "AppBarButtonInnerBorder");
        if (background is null || background.ActualWidth < 24 || background.ActualHeight < 24)
        {
            return;
        }

        if (!ReferenceEquals(background, _newShnappBackground))
        {
            StopNewShnappSparkles();
            if (_newShnappBackground is not null)
            {
                _newShnappBackground.SizeChanged -= NewShnappBackground_SizeChanged;
            }
            _newShnappBackground = background;
            background.SizeChanged += NewShnappBackground_SizeChanged;
        }

        if (_newShnappSparkleLayer is not null)
        {
            return;
        }

        // Composition runs the tiny particle loops off the XAML layout thread.
        // Attaching to the native inner border leaves its icon, flyout, and focus visuals intact.
        Compositor compositor = ElementCompositionPreview.GetElementVisual(background).Compositor;
        ContainerVisual layer = compositor.CreateContainerVisual();
        layer.Size = new Vector2((float)background.ActualWidth, (float)background.ActualHeight);
        layer.Clip = compositor.CreateInsetClip();
        ElementCompositionPreview.SetElementChildVisual(background, layer);
        _newShnappSparkleLayer = layer;
        int generation = ++_newShnappSparkleGeneration;

        for (int i = 0; i < NewShnappSparkleCount; i++)
        {
            ContainerVisual sparkle = CreateNewShnappSparkle(compositor);
            layer.Children.InsertAtTop(sparkle);
            _newShnappSparkles.Add(sparkle);
            AnimateNewShnappSparkle(sparkle, generation, TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1400)));
        }
    }

    private static T? FindNewShnappPart<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        if (parent is T { } element && element.Name == name)
        {
            return element;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            T? child = FindNewShnappPart<T>(VisualTreeHelper.GetChild(parent, i), name);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static ContainerVisual CreateNewShnappSparkle(Compositor compositor)
    {
        ContainerVisual sparkle = compositor.CreateContainerVisual();
        sparkle.Size = new Vector2(5, 5);
        sparkle.Opacity = 0;

        var brush = compositor.CreateColorBrush(Colors.White);
        SpriteVisual vertical = compositor.CreateSpriteVisual();
        vertical.Brush = brush;
        vertical.Size = new Vector2(1, 5);
        vertical.Offset = new Vector3(2, 0, 0);
        sparkle.Children.InsertAtTop(vertical);

        SpriteVisual horizontal = compositor.CreateSpriteVisual();
        horizontal.Brush = brush;
        horizontal.Size = new Vector2(5, 1);
        horizontal.Offset = new Vector3(0, 2, 0);
        sparkle.Children.InsertAtTop(horizontal);
        return sparkle;
    }

    private void AnimateNewShnappSparkle(ContainerVisual sparkle, int generation, TimeSpan delay)
    {
        if (generation != _newShnappSparkleGeneration || _newShnappSparkleLayer is null)
        {
            return;
        }

        Compositor compositor = sparkle.Compositor;
        float width = _newShnappSparkleLayer.Size.X;
        float height = _newShnappSparkleLayer.Size.Y;
        float x = 6 + Random.Shared.NextSingle() * Math.Max(1, width - 12);
        float y = Math.Max(20, height - 18) + Random.Shared.NextSingle() * 11;
        float rise = 9 + Random.Shared.NextSingle() * 9;
        TimeSpan duration = TimeSpan.FromMilliseconds(Random.Shared.Next(1700, 3200));

        sparkle.Offset = new Vector3(x, y, 0);
        sparkle.Opacity = 0;
        Vector3KeyFrameAnimation drift = compositor.CreateVector3KeyFrameAnimation();
        drift.InsertKeyFrame(0, new Vector3(x, y, 0));
        drift.InsertKeyFrame(1, new Vector3(x, y - rise, 0));
        drift.Duration = duration;
        drift.DelayTime = delay;

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(0.18f, 0.66f);
        fade.InsertKeyFrame(0.55f, 0.52f);
        fade.InsertKeyFrame(1, 0);
        fade.Duration = duration;
        fade.DelayTime = delay;

        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        sparkle.StartAnimation("Offset", drift);
        sparkle.StartAnimation("Opacity", fade);
        batch.End();
        batch.Completed += (_, _) => App.DispatcherQueue.TryEnqueue(() =>
            AnimateNewShnappSparkle(sparkle, generation,
                TimeSpan.FromMilliseconds(Random.Shared.Next(0, 350))));
    }

    private void StopNewShnappSparkles()
    {
        ++_newShnappSparkleGeneration;
        foreach (ContainerVisual sparkle in _newShnappSparkles)
        {
            sparkle.StopAnimation("Offset");
            sparkle.StopAnimation("Opacity");
        }
        _newShnappSparkles.Clear();

        if (_newShnappBackground is not null && _newShnappSparkleLayer is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(_newShnappBackground, null!);
            _newShnappSparkleLayer = null;
        }
    }
}
