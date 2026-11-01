using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private SpriteVisual? _inspectorGlass;
    private UISettings? _inspectorGlassSettings;

    private void InspectorGlassHost_Loaded(object sender, RoutedEventArgs args)
    {
        if (_inspectorGlass is null)
        {
            Visual host = ElementCompositionPreview.GetElementVisual(InspectorGlassHost);
            Compositor compositor = host.Compositor;
            using var blur = new GaussianBlurEffect
            {
                BlurAmount = 18,
                BorderMode = EffectBorderMode.Hard,
                Source = new CompositionEffectSourceParameter("backdrop"),
            };
            CompositionEffectBrush brush = compositor.CreateEffectFactory(blur).CreateBrush();
            brush.SetSourceParameter("backdrop", compositor.CreateBackdropBrush());

            _inspectorGlass = compositor.CreateSpriteVisual();
            _inspectorGlass.Brush = brush;
            _inspectorGlass.IsHitTestVisible = false;
            ElementCompositionPreview.SetElementChildVisual(InspectorGlassHost, _inspectorGlass);

            ExpressionAnimation size = compositor.CreateExpressionAnimation("host.Size");
            size.SetReferenceParameter("host", host);
            _inspectorGlass.StartAnimation("Size", size);
        }

        if (_inspectorGlassSettings is null)
        {
            _inspectorGlassSettings = new UISettings();
            _inspectorGlassSettings.AdvancedEffectsEnabledChanged += InspectorGlassSettings_Changed;
        }
        UpdateInspectorGlassVisibility();
    }

    private void InspectorGlassHost_Unloaded(object sender, RoutedEventArgs args)
    {
        if (_inspectorGlassSettings is not null)
        {
            _inspectorGlassSettings.AdvancedEffectsEnabledChanged -= InspectorGlassSettings_Changed;
            _inspectorGlassSettings = null;
        }
        if (_inspectorGlass is not null) _inspectorGlass.IsVisible = false;
    }

    private void InspectorGlassSettings_Changed(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateInspectorGlassVisibility);

    private void UpdateInspectorGlassVisibility()
    {
        if (_inspectorGlass is not null)
            _inspectorGlass.IsVisible = !_accessibility.HighContrast &&
                _inspectorGlassSettings?.AdvancedEffectsEnabled == true;
    }
}
