using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Shnapp.App;

/// <summary>Shows a hand over actionable controls while leaving editor cursors intact.</summary>
public sealed class InteractiveCursorHost : Grid
{
    private readonly InputCursor _hand = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    public InteractiveCursorHost()
    {
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        AddHandler(PointerEnteredEvent, new PointerEventHandler(OnPointerMoved), true);
        PointerExited += (_, args) =>
        {
            Point pointer = args.GetCurrentPoint(this).Position;
            if (pointer.X < 0 || pointer.Y < 0 || pointer.X >= ActualWidth || pointer.Y >= ActualHeight)
            {
                ReleaseCursor();
            }
        };
        Unloaded += (_, _) => ReleaseCursor();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        bool actionable = false;
        CommandBar? commandBar = null;
        for (DependencyObject? element = args.OriginalSource as DependencyObject;
             element is not null && !ReferenceEquals(element, this);
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase button)
            {
                actionable |= button.IsEnabled;
            }

            if (element is SplitButton splitButton)
            {
                actionable |= splitButton.IsEnabled;
            }

            if (element is ToggleSwitch toggle)
            {
                actionable |= toggle.IsEnabled;
            }

            if (element is CommandBar bar)
            {
                commandBar = bar;
            }
        }

        if (!actionable && commandBar is not null)
        {
            // Split button dividers and some AppBar template edges are painted
            // but hit-test to the CommandBar. Cover the full button bounds.
            actionable = IsWithinEnabledAction(commandBar, args.GetCurrentPoint(this).Position);
        }

        ProtectedCursor = actionable ? _hand : null;
    }

    internal void ReleaseCursor()
    {
        ProtectedCursor = null;
    }

    private bool IsWithinEnabledAction(DependencyObject element, Point pointer)
    {
        if (element is FrameworkElement visual)
        {
            if (visual.Visibility != Visibility.Visible)
            {
                return false;
            }

            bool enabled = element switch
            {
                ButtonBase button => button.IsEnabled,
                SplitButton splitButton => splitButton.IsEnabled,
                ToggleSwitch toggle => toggle.IsEnabled,
                _ => false,
            };
            if (enabled && visual.ActualWidth > 0 && visual.ActualHeight > 0)
            {
                Point origin = visual.TransformToVisual(this).TransformPoint(new Point());
                if (pointer.X >= origin.X && pointer.X < origin.X + visual.ActualWidth &&
                    pointer.Y >= origin.Y && pointer.Y < origin.Y + visual.ActualHeight)
                {
                    return true;
                }
            }
        }

        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            if (IsWithinEnabledAction(VisualTreeHelper.GetChild(element, index), pointer))
            {
                return true;
            }
        }

        return false;
    }

}
