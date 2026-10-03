using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Shnapp.App.Windows;
using Shnapp.Core;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Shnapp.App.Capture;

internal sealed record CaptureSelection(ImageRect? Region, WindowTarget? Window);

internal sealed class SelectionWindow : Window, IDisposable
{
    private readonly CanvasBitmap _desktop;
    private readonly NativeMethods.Rect _bounds;
    private readonly IReadOnlyList<WindowTarget>? _windows;
    private readonly bool _previewDisplay;
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private readonly CanvasControl _canvas;
    private readonly DispatcherTimer _entranceTimer;
    private readonly Stopwatch _entranceClock = new();
    private readonly TaskCompletionSource<CaptureSelection?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ImagePoint? _start;
    private ImagePoint _pointer;
    private ImageRect? _selection;
    private WindowTarget? _hovered;
    private bool _windowPressed;
    private bool _disposed;

    internal SelectionWindow(CanvasBitmap desktop, NativeMethods.Rect bounds, IReadOnlyList<WindowTarget>? windows,
        bool previewDisplay = false)
    {
        _desktop = desktop;
        _bounds = bounds;
        _windows = windows;
        _previewDisplay = previewDisplay;
        Title = "Shnapp — " + (previewDisplay ? "Full screen" : windows is null ? "Free form" : "Window");
        _canvas = new CanvasControl { IsTabStop = true };
        _entranceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _entranceTimer.Tick += (_, _) =>
        {
            _canvas.Invalidate();
            if (_entranceClock.ElapsedMilliseconds >= 200)
            {
                _entranceTimer.Stop();
            }
        };
        _canvas.Draw += Draw;
        _canvas.PointerPressed += PointerPressed;
        _canvas.PointerMoved += PointerMoved;
        _canvas.PointerReleased += PointerReleased;
        _canvas.PointerCanceled += (_, _) => Cancel();
        _canvas.PointerCaptureLost += (_, _) =>
        {
            if ((_start is null && !_windowPressed) || _completion.Task.IsCompleted)
            {
                return;
            }

            _start = null;
            _windowPressed = false;
            _selection = null;
            _canvas.Invalidate();
        };
        _canvas.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape)
            {
                Cancel();
                args.Handled = true;
            }
        };
        var root = new OverlayRoot { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Black) };
        root.Children.Add(_canvas);
        Content = root;
        ExtendsContentIntoTitleBar = true;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.MoveAndResize(new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
        NativeMethods.SetWindowDisplayAffinity(WinRT.Interop.WindowNative.GetWindowHandle(this), 0x11);
        Closed += (_, _) => _completion.TrySetResult(null);
    }

    internal async Task<CaptureSelection?> PickAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => DispatcherQueue.TryEnqueue(Cancel));
        UpdatePointer();
        if (_animationsEnabled)
        {
            _entranceClock.Start();
        }
        Activate();
        NativeMethods.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _canvas.Focus(FocusState.Programmatic);
        if (_animationsEnabled)
        {
            _entranceTimer.Start();
        }

        if (_previewDisplay)
        {
            await Task.WhenAny(_completion.Task, Task.Delay(360, cancellationToken));
            _completion.TrySetResult(cancellationToken.IsCancellationRequested ? null :
                new CaptureSelection(new ImageRect(0, 0, _bounds.Width, _bounds.Height), null));
        }

        return await _completion.Task;
    }

    private void UpdatePointer()
    {
        NativeMethods.GetCursorPos(out NativeMethods.Point point);
        _pointer = new(
            Math.Clamp(point.X - _bounds.Left, 0, _bounds.Width),
            Math.Clamp(point.Y - _bounds.Top, 0, _bounds.Height));
        if (_previewDisplay)
        {
            _selection = new ImageRect(0, 0, _bounds.Width, _bounds.Height);
        }
        else if (_windows is not null)
        {
            var screenPoint = new ImagePoint(point.X, point.Y);
            _hovered = _windows.FirstOrDefault(window => window.Bounds.ToImageRect().Contains(screenPoint));
            _selection = _hovered is null ? null : new(
                _hovered.Bounds.Left - _bounds.Left,
                _hovered.Bounds.Top - _bounds.Top,
                _hovered.Bounds.Width,
                _hovered.Bounds.Height);
        }
        else if (_start is ImagePoint start)
        {
            ImagePoint end = _pointer;
            if (IsShiftHeld())
            {
                double side = Math.Min(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
                end = new ImagePoint(start.X + Math.CopySign(side, end.X - start.X),
                    start.Y + Math.CopySign(side, end.Y - start.Y));
            }

            _selection = ImageRect.FromPoints(start, end);
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static bool IsShiftHeld() => (GetAsyncKeyState(0x10) & 0x8000) != 0;

    private void PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_previewDisplay)
        {
            return;
        }

        if (!args.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed)
        {
            return;
        }

        UpdatePointer();
        if (_windows is null)
        {
            _start = _pointer;
        }
        else
        {
            _windowPressed = true;
        }

        _canvas.CapturePointer(args.Pointer);

        args.Handled = true;
        _canvas.Invalidate();
    }

    private void PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        UpdatePointer();
        _canvas.Invalidate();
    }

    private void PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_previewDisplay)
        {
            return;
        }

        UpdatePointer();
        if (_windows is not null && _windowPressed && _hovered is not null)
        {
            _completion.TrySetResult(new(null, _hovered));
        }
        else if (_start is not null && _selection is ImageRect region && region.Width >= 2 && region.Height >= 2)
        {
            _completion.TrySetResult(new(region, null));
        }
        else
        {
            _start = null;
            _selection = null;
        }

        _windowPressed = false;
        _canvas.ReleasePointerCapture(args.Pointer);
        args.Handled = true;
        _canvas.Invalidate();
    }

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        CanvasDrawingSession drawing = args.DrawingSession;
        float scaleX = (float)(sender.ActualWidth / _bounds.Width);
        float scaleY = (float)(sender.ActualHeight / _bounds.Height);
        if (scaleX <= 0 || scaleY <= 0)
        {
            return;
        }

        drawing.Transform = Matrix3x2.CreateScale(scaleX, scaleY);
        drawing.DrawImage(_desktop);
        float entrance = _animationsEnabled
            ? Math.Clamp((float)(_entranceClock.Elapsed.TotalMilliseconds / 200), 0, 1)
            : 1;
        drawing.FillRectangle(0, 0, _bounds.Width, _bounds.Height,
            Color.FromArgb((byte)(107 * entrance), 0, 0, 0));
        if (_selection is ImageRect selection)
        {
            double left = Math.Max(0, selection.X);
            double top = Math.Max(0, selection.Y);
            double right = Math.Min(_bounds.Width, selection.Right);
            double bottom = Math.Min(_bounds.Height, selection.Bottom);
            var rectangle = new global::Windows.Foundation.Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
            if (rectangle.Width > 0 && rectangle.Height > 0)
            {
                drawing.DrawImage(_desktop, rectangle, rectangle);
                if (_windows is not null || _previewDisplay)
                {
                    DrawSelectionBorder(drawing, rectangle, scaleX, scaleY, entrance);
                }
                else
                {
                    drawing.DrawRectangle(rectangle,
                        Color.FromArgb((byte)(255 * entrance), 255, 255, 255), 2 / scaleX);
                }
            }
            if (_previewDisplay)
            {
                return;
            }

            string dimensions = $"X {_bounds.Left + (int)left}   Y {_bounds.Top + (int)top}\n" +
                $"W {(int)rectangle.Width}   H {(int)rectangle.Height} px";
            if (_start is not null)
            {
                DrawLabel(drawing, dimensions,
                    (float)(_pointer.X + 24 / scaleX), (float)(_pointer.Y + 28 / scaleY), scaleX, scaleY,
                    entrance);
            }
            else
            {
                DrawLabel(drawing, dimensions, (float)left, (float)(bottom + 8 / scaleY), scaleX, scaleY,
                    entrance);
            }
        }

        if (_start is not null && _selection is not null)
        {
            return;
        }

        string hint = _windows is null ? "Drag a region · Shift for square · Esc to cancel" :
            "Click a window · Esc to cancel";
        if (_hovered is not null)
        {
            hint += "\n" + _hovered.Title;
        }

        DrawLabel(drawing, hint,
            (float)(_pointer.X + 24 / scaleX),
            (float)(_pointer.Y + 28 / scaleY), scaleX, scaleY, entrance);
    }

    private static void DrawSelectionBorder(CanvasDrawingSession drawing,
        global::Windows.Foundation.Rect bounds, float scaleX, float scaleY, float opacity)
    {
        float unit = 1 / Math.Min(scaleX, scaleY);
        double inset = 2 * unit;
        var border = new global::Windows.Foundation.Rect(bounds.X + inset, bounds.Y + inset,
            Math.Max(0, bounds.Width - inset * 2), Math.Max(0, bounds.Height - inset * 2));
        if (border.Width <= 0 || border.Height <= 0)
        {
            return;
        }

        drawing.DrawRectangle(border, Color.FromArgb((byte)(245 * opacity), 74, 210, 255), 2.5f * unit);
    }

    private void DrawLabel(CanvasDrawingSession drawing, string text, float x, float y,
        float scaleX, float scaleY, float opacity)
    {
        using var format = new CanvasTextFormat { FontFamily = "Segoe UI Variable Text", FontSize = 14 / scaleX };
        using var layout = new CanvasTextLayout(drawing, text, format,
            Math.Max(1, Math.Min(400 / scaleX, _bounds.Width - 48 / scaleX)), 96 / scaleY);
        float width = (float)layout.LayoutBounds.Width + 20 / scaleX;
        float height = (float)layout.LayoutBounds.Height + 12 / scaleY;
        float marginX = 16 / scaleX;
        float marginY = 16 / scaleY;
        float labelX = Math.Clamp(x, marginX, Math.Max(marginX, _bounds.Width - width - marginX));
        float labelY = Math.Clamp(y, marginY, Math.Max(marginY, _bounds.Height - height - marginY));
        drawing.FillRoundedRectangle(labelX, labelY, width, height,
            6 / scaleX, 6 / scaleY, Color.FromArgb((byte)(235 * opacity), 17, 20, 24));
        drawing.DrawTextLayout(layout, labelX + 10 / scaleX, labelY + 6 / scaleY,
            Color.FromArgb((byte)(255 * opacity), 255, 255, 255));
    }

    private void Cancel() => _completion.TrySetResult(null);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _entranceTimer.Stop();
        _completion.TrySetResult(null);
        AppWindow.Hide();
        _canvas.RemoveFromVisualTree();
        Content = null;
        Close();
    }

    private sealed class OverlayRoot : Grid
    {
        internal OverlayRoot() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    }
}
