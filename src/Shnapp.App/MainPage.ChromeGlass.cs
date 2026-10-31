using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private UISettings? _chromeUiSettings;
    private SpriteVisual? _titleChromeGlass;
    private SpriteVisual? _footerChromeGlass;

    private void InitializeChromeGlass()
    {
        if (_titleChromeGlass is null)
        {
            // The two narrow GPU visuals sample the live image without copying the
            // Win2D canvas or re-rendering its full frame when zoom and pan change.
            Visual titleHost = ElementCompositionPreview.GetElementVisual(TitleChromeGlassHost);
            Compositor compositor = titleHost.Compositor;
            using var blur = new GaussianBlurEffect
            {
                BlurAmount = 18,
                BorderMode = EffectBorderMode.Hard,
                Source = new CompositionEffectSourceParameter("backdrop")
            };
            CompositionEffectFactory factory = compositor.CreateEffectFactory(blur);
            _titleChromeGlass = CreateChromeGlass(TitleChromeGlassHost, factory);
            _footerChromeGlass = CreateChromeGlass(FooterChromeGlassHost, factory);
        }

        if (_chromeUiSettings is null)
        {
            _chromeUiSettings = new UISettings();
            _chromeUiSettings.AdvancedEffectsEnabledChanged += ChromeAdvancedEffectsChanged;
        }
        UpdateChromeGlassVisibility();
    }

    private static SpriteVisual CreateChromeGlass(UIElement host, CompositionEffectFactory factory)
    {
        Visual hostVisual = ElementCompositionPreview.GetElementVisual(host);
        Compositor compositor = hostVisual.Compositor;
        CompositionEffectBrush brush = factory.CreateBrush();
        brush.SetSourceParameter("backdrop", compositor.CreateBackdropBrush());

        SpriteVisual glass = compositor.CreateSpriteVisual();
        glass.Brush = brush;
        glass.IsHitTestVisible = false;
        ElementCompositionPreview.SetElementChildVisual(host, glass);

        ExpressionAnimation size = compositor.CreateExpressionAnimation("host.Size");
        size.SetReferenceParameter("host", hostVisual);
        glass.StartAnimation("Size", size);
        return glass;
    }

    private void ChromeAdvancedEffectsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateChromeGlassVisibility);

    private void UpdateChromeGlassVisibility()
    {
        bool enabled = !_accessibility.HighContrast &&
            _chromeUiSettings?.AdvancedEffectsEnabled == true;
        if (_titleChromeGlass is not null)
        {
            _titleChromeGlass.IsVisible = enabled;
        }
        if (_footerChromeGlass is not null)
        {
            _footerChromeGlass.IsVisible = enabled;
        }
    }

    private void DisconnectChromeGlassSettings()
    {
        if (_chromeUiSettings is null)
        {
            return;
        }

        _chromeUiSettings.AdvancedEffectsEnabledChanged -= ChromeAdvancedEffectsChanged;
        _chromeUiSettings = null;
    }
}
