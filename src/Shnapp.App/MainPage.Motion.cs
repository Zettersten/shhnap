using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private readonly UISettings _surfaceMotionSettings = new();

    private void AnimateSurfaceEntry(FrameworkElement surface)
    {
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
