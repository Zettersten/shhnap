using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private void InitializeNavigationInput()
    {
        var back = new KeyboardAccelerator
        {
            Key = VirtualKey.Left,
            Modifiers = VirtualKeyModifiers.Menu,
        };
        back.Invoked += (_, args) =>
        {
            if (_controller?.CanNavigateBack == true)
            {
                _controller.NavigateBack();
                args.Handled = true;
            }
        };
        KeyboardAccelerators.Add(back);

        var forward = new KeyboardAccelerator
        {
            Key = VirtualKey.Right,
            Modifiers = VirtualKeyModifiers.Menu,
        };
        forward.Invoked += (_, args) =>
        {
            if (_controller?.CanNavigateForward == true)
            {
                _controller.NavigateForward();
                args.Handled = true;
            }
        };
        KeyboardAccelerators.Add(forward);

        AddHandler(PointerPressedEvent, new PointerEventHandler(Navigation_PointerPressed), true);
    }

    private void Navigation_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_controller is null)
        {
            return;
        }

        var properties = args.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed && _controller.CanNavigateBack)
        {
            _controller.NavigateBack();
            args.Handled = true;
        }
        else if (properties.IsXButton2Pressed && _controller.CanNavigateForward)
        {
            _controller.NavigateForward();
            args.Handled = true;
        }
    }

    internal void UpdateNavigationButtons(bool canBack, bool canForward)
    {
        BackButton.IsEnabled = canBack;
        ForwardButton.IsEnabled = canForward;
    }

    private void Back_Click(object sender, RoutedEventArgs args) => _controller?.NavigateBack();

    private void Forward_Click(object sender, RoutedEventArgs args) => _controller?.NavigateForward();
}
