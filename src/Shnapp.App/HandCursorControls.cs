using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Shnapp.App;

internal static class ActionHandCursor
{
    internal static InputCursor Create() => InputSystemCursor.Create(InputSystemCursorShape.Hand);
}

public sealed class HandCursorAppBarButton : AppBarButton
{
    private readonly InputCursor _hand = ActionHandCursor.Create();
    public bool HideFlyoutChevron { get; set; }

    public HandCursorAppBarButton()
    {
        ProtectedCursor = _hand;
        Loaded += (_, _) =>
        {
            RefreshCursor();
            UpdateFlyoutChevron();
        };
        IsEnabledChanged += (_, _) => RefreshCursor();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateFlyoutChevron();
    }

    private void RefreshCursor() => ProtectedCursor = IsEnabled ? _hand : null;

    private void UpdateFlyoutChevron()
    {
        if (!HideFlyoutChevron)
        {
            return;
        }

        foreach (string name in new[] { "SubItemChevron", "OverflowSubItemChevron" })
        {
            if (GetTemplateChild(name) is FontIcon chevron)
            {
                chevron.Glyph = string.Empty;
                chevron.Margin = new Thickness(0);
                chevron.Width = 0;
            }
        }
    }
}

internal static class ToolbarIconAppearance
{
    private static readonly AccessibilitySettings Accessibility = new();

    internal static void Apply(ContentControl button, bool active)
    {
        if (button.Content is IconElement icon)
        {
            string themeKey = Accessibility.HighContrast ? "HighContrast" :
                button.ActualTheme == ElementTheme.Light ? "Light" : "Default";
            var theme = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[themeKey];
            Brush brush = (Brush)theme["ShnappToolbarActiveIconBrush"];
            if (!ReferenceEquals(icon.Foreground, brush))
            {
                icon.Foreground = brush;
            }
            icon.Opacity = !button.IsEnabled ? 0.35 : active ? 1 : 0.58;
        }
    }
}

/// <summary>A regular Button for toolbar actions and the Shapes flyout.</summary>
public sealed class ToolbarActionButton : Button
{
    private readonly InputCursor _hand = ActionHandCursor.Create();
    private bool _hovered;
    private bool _selected;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            RefreshAppearance();
        }
    }

    public ToolbarActionButton()
    {
        Loaded += (_, _) => RefreshAppearance();
        ActualThemeChanged += (_, _) => RefreshAppearance();
        IsEnabledChanged += (_, _) => RefreshAppearance();
        PointerEntered += (_, _) => { _hovered = true; RefreshAppearance(); };
        PointerExited += (_, _) => { _hovered = false; RefreshAppearance(); };
    }

    private void RefreshAppearance()
    {
        InputCursor? cursor = IsEnabled ? _hand : null;
        if (!ReferenceEquals(ProtectedCursor, cursor))
        {
            ProtectedCursor = cursor;
        }
        if (IsLoaded)
        {
            ToolbarIconAppearance.Apply(this, _selected || _hovered);
        }
    }
}

public sealed class HandCursorToggleButton : ToggleButton
{
    private readonly InputCursor _hand = ActionHandCursor.Create();

    public HandCursorToggleButton()
    {
        ProtectedCursor = _hand;
        Loaded += (_, _) => ProtectedCursor = IsEnabled ? _hand : null;
        IsEnabledChanged += (_, _) => ProtectedCursor = IsEnabled ? _hand : null;
    }
}

public sealed class HandCursorSplitButton : SplitButton
{
    private readonly InputCursor _hand = ActionHandCursor.Create();

    public HandCursorSplitButton()
    {
        ProtectedCursor = _hand;
        Loaded += (_, _) => ProtectedCursor = IsEnabled ? _hand : null;
        IsEnabledChanged += (_, _) => ProtectedCursor = IsEnabled ? _hand : null;
    }
}

public sealed class HandCursorRegion : Grid
{
    private readonly InputCursor _hand = ActionHandCursor.Create();

    public HandCursorRegion()
    {
        ProtectedCursor = _hand;
        Loaded += (_, _) => ProtectedCursor = _hand;
    }
}
