using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Shnapp.App.Windows;
using Windows.Graphics;
using Windows.UI;

namespace Shnapp.App;

/// <summary>Small, capture-excluded recording controls shown while the editor is hidden.</summary>
internal sealed class RecordingHudWindow : Window
{
    private readonly TextBlock _elapsed;
    private readonly Button _stop;
    private readonly Button _cancel;
    private readonly Button _pause;
    private readonly Ellipse _indicator;
    private readonly Border? _cameraBadge;
    private readonly TextBlock? _cameraLabel;
    private bool _cameraUnavailable;

    internal event Action? StopRequested;
    internal event Action? CancelRequested;
    internal event Action? PauseRequested;

    internal bool IsExcludedFromCapture { get; }

    internal RecordingHudWindow(NativeMethods.Rect target, bool computerAudio,
        bool microphone, bool camera)
    {
        Title = "Shnapp recording";
        var background = new SolidColorBrush(Color.FromArgb(244, 16, 28, 54));
        var foreground = new SolidColorBrush(Colors.White);
        var muted = new SolidColorBrush(Color.FromArgb(255, 179, 198, 224));
        var blue = new SolidColorBrush(Color.FromArgb(255, 10, 132, 255));

        _indicator = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Color.FromArgb(255, 255, 85, 102)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _elapsed = new TextBlock
        {
            Text = "Starting…",
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 76,
        };
        _pause = MakeButton("Pause", foreground, new SolidColorBrush(Colors.Transparent));
        _pause.Click += (_, _) => PauseRequested?.Invoke();
        _pause.IsEnabled = false;
        _stop = MakeButton("Stop", foreground, blue);
        _stop.Click += (_, _) => StopRequested?.Invoke();
        _stop.IsEnabled = false;
        _cancel = MakeButton("Cancel", muted, new SolidColorBrush(Colors.Transparent));
        _cancel.Click += (_, _) => CancelRequested?.Invoke();

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
        };
        controls.Children.Add(_indicator);
        controls.Children.Add(_elapsed);
        if (computerAudio) { controls.Children.Add(Badge("PC audio", muted)); }
        if (microphone) { controls.Children.Add(Badge("Mic", muted)); }
        if (camera)
        {
            _cameraBadge = Badge("Camera…", muted);
            _cameraLabel = (TextBlock)_cameraBadge.Child;
            AutomationProperties.SetName(_cameraBadge, "Preparing camera recording");
            AutomationProperties.SetLiveSetting(_cameraLabel, AutomationLiveSetting.Assertive);
            controls.Children.Add(_cameraBadge);
        }
        controls.Children.Add(_pause);
        controls.Children.Add(_stop);
        controls.Children.Add(_cancel);
        Content = new Border
        {
            Background = background,
            BorderBrush = new SolidColorBrush(Color.FromArgb(128, 141, 173, 215)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = controls,
        };

        var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(handle) / 96.0);
        int width = (int)Math.Ceiling((300 + (computerAudio ? 60 : 0) +
            (microphone ? 40 : 0) + (camera ? 66 : 0)) * scale);
        int height = (int)Math.Ceiling(44 * scale);
        var targetCenter = new NativeMethods.Point
        {
            X = target.Left + target.Width / 2,
            Y = target.Top + target.Height / 2,
        };
        nint monitor = NativeMethods.MonitorFromPoint(targetCenter,
            NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            throw new InvalidOperationException("The recording display is no longer available.");
        }
        NativeMethods.Rect work = info.WorkArea;
        int left = Math.Clamp(target.Left + (target.Width - width) / 2,
            work.Left, Math.Max(work.Left, work.Right - width));
        int top = Math.Clamp(target.Top + (int)(12 * scale),
            work.Top, Math.Max(work.Top, work.Bottom - height));
        AppWindow.MoveAndResize(new RectInt32(left, top, width, height));
        IsExcludedFromCapture = NativeMethods.SetWindowDisplayAffinity(handle, 0x11);
    }

    private static Button MakeButton(string label, Brush foreground, Brush background) => new()
    {
        Content = label,
        MinWidth = 0,
        MinHeight = 0,
        Height = 27,
        Padding = new Thickness(10, 0, 10, 0),
        FontSize = 12,
        Foreground = foreground,
        Background = background,
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(6),
    };

    private static Border Badge(string label, Brush foreground) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 2, 5, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = label, FontSize = 10, Foreground = foreground },
    };

    internal void SetElapsed(TimeSpan elapsed)
    {
        _elapsed.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") :
            elapsed.ToString(@"mm\:ss");
    }

    internal void SetRecording()
    {
        _elapsed.Text = "00:00";
        _pause.IsEnabled = true;
        _stop.IsEnabled = true;
        if (_cameraBadge is not null && !_cameraUnavailable)
        {
            _cameraLabel!.Text = "Camera";
            AutomationProperties.SetName(_cameraBadge, "Camera recording active");
        }
    }

    internal void SetCameraUnavailable(string reason)
    {
        if (_cameraBadge is null || _cameraLabel is null) return;
        _cameraUnavailable = true;
        _cameraLabel.Text = "Camera off";
        _cameraLabel.Foreground = new SolidColorBrush(Colors.White);
        _cameraBadge.Background = new SolidColorBrush(Color.FromArgb(200, 142, 72, 17));
        string message = "Camera stopped. Screen recording continues. " + reason;
        AutomationProperties.SetName(_cameraBadge, message);
        AutomationProperties.SetName(_cameraLabel, message);
        ToolTipService.SetToolTip(_cameraBadge, message);
    }

    internal void SetPaused(bool paused)
    {
        _pause.Content = paused ? "Resume" : "Pause";
        _indicator.Opacity = paused ? 0.45 : 1;
    }

    internal void SetFinishing()
    {
        _pause.IsEnabled = false;
        _stop.IsEnabled = false;
        _cancel.IsEnabled = false;
        _elapsed.Text = "Saving…";
    }
}
