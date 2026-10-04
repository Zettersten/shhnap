using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Shnapp.App;

internal static class ActionHandCursor
{
    internal static readonly InputCursor Value = InputSystemCursor.Create(InputSystemCursorShape.Hand);
}

public sealed class HandCursorAppBarButton : AppBarButton
{
    public bool HideFlyoutChevron { get; set; }

    public HandCursorAppBarButton()
    {
        ProtectedCursor = ActionHandCursor.Value;
        Loaded += (_, _) =>
        {
            ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
            UpdateFlyoutChevron();
        };
        IsEnabledChanged += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateFlyoutChevron();
    }

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

public sealed class HandCursorAppBarToggleButton : AppBarToggleButton
{
    public HandCursorAppBarToggleButton()
    {
        ProtectedCursor = ActionHandCursor.Value;
        Loaded += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        IsEnabledChanged += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
    }
}

public sealed class HandCursorToggleButton : ToggleButton
{
    public HandCursorToggleButton()
    {
        ProtectedCursor = ActionHandCursor.Value;
        Loaded += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        IsEnabledChanged += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
    }
}

public sealed class HandCursorSplitButton : SplitButton
{
    public HandCursorSplitButton()
    {
        ProtectedCursor = ActionHandCursor.Value;
        Loaded += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        IsEnabledChanged += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
    }
}

public sealed class HandCursorRegion : Grid
{
    public HandCursorRegion()
    {
        ProtectedCursor = ActionHandCursor.Value;
        Loaded += (_, _) => ProtectedCursor = ActionHandCursor.Value;
    }
}
