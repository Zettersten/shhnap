using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Shnapp.App;

internal static class ActionHandCursor
{
    internal static readonly InputCursor Value = InputSystemCursor.Create(InputSystemCursorShape.Hand);
}

public sealed class HandCursorAppBarButton : AppBarButton
{
    public HandCursorAppBarButton()
    {
        ProtectedCursor = ActionHandCursor.Value;
        Loaded += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        IsEnabledChanged += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        PointerEntered += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        AddHandler(PointerMovedEvent, new PointerEventHandler((_, _) =>
            ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null), true);
    }
}

public sealed class HandCursorAppBarToggleButton : AppBarToggleButton
{
    public HandCursorAppBarToggleButton()
    {
        ProtectedCursor = ActionHandCursor.Value;
        Loaded += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        IsEnabledChanged += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        PointerEntered += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        AddHandler(PointerMovedEvent, new PointerEventHandler((_, _) =>
            ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null), true);
    }
}

public sealed class HandCursorToggleButton : ToggleButton
{
    public HandCursorToggleButton()
    {
        ProtectedCursor = ActionHandCursor.Value;
        Loaded += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        IsEnabledChanged += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        PointerEntered += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        AddHandler(PointerMovedEvent, new PointerEventHandler((_, _) =>
            ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null), true);
    }
}

public sealed class HandCursorSplitButton : SplitButton
{
    public HandCursorSplitButton()
    {
        ProtectedCursor = ActionHandCursor.Value;
        Loaded += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        IsEnabledChanged += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        PointerEntered += (_, _) => ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null;
        AddHandler(PointerMovedEvent, new PointerEventHandler((_, _) =>
            ProtectedCursor = IsEnabled ? ActionHandCursor.Value : null), true);
    }
}

public sealed class HandCursorRegion : Grid
{
    public HandCursorRegion()
    {
        ProtectedCursor = ActionHandCursor.Value;
        Loaded += (_, _) => ProtectedCursor = ActionHandCursor.Value;
        PointerEntered += (_, _) => ProtectedCursor = ActionHandCursor.Value;
        AddHandler(PointerMovedEvent, new PointerEventHandler((_, _) =>
            ProtectedCursor = ActionHandCursor.Value), true);
    }
}
