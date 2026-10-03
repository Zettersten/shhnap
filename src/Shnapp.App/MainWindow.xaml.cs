using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Shnapp.App;

/// <summary>The single native window; ordinary close returns it to the tray.</summary>
public sealed partial class MainWindow : Window
{
    private nint _windowIcon;
    internal MainPage Page { get; }
    internal SettingsPage? Settings { get; private set; }
    private UIElement? _focusBeforeSettings;
    private int _settingsTransitionVersion;

    internal void ShowSettings(SettingsPage page)
    {
        ++_settingsTransitionVersion;
        if (SettingsOverlay.Visibility != Visibility.Visible)
        {
            _focusBeforeSettings = Page.XamlRoot is { } root
                ? FocusManager.GetFocusedElement(root) as UIElement : null;
        }
        (Page.Content as InteractiveCursorHost)?.ReleaseCursor();
        Settings = page;
        RootFrame.IsEnabled = false;
        SettingsOverlay.Content = page;
        SettingsOverlay.IsHitTestVisible = true;
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    internal async void ShowMainPage(bool restoreFocus = false)
    {
        if (restoreFocus && Settings is null)
        {
            return;
        }

        int transitionVersion = ++_settingsTransitionVersion;
        SettingsPage? closingSettings = Settings;
        bool wasSettingsOpen = closingSettings is not null;
        Settings = null;
        if (restoreFocus && closingSettings is not null)
        {
            SettingsOverlay.IsHitTestVisible = false;
            await closingSettings.PlayCloseTransitionAsync();
            if (transitionVersion != _settingsTransitionVersion)
            {
                return;
            }
        }

        SettingsOverlay.Visibility = Visibility.Collapsed;
        SettingsOverlay.Content = null;
        RootFrame.IsEnabled = true;
        SettingsOverlay.IsHitTestVisible = true;
        UIElement? previousFocus = _focusBeforeSettings;
        _focusBeforeSettings = null;
        if (!wasSettingsOpen || !restoreFocus)
        {
            return;
        }

        if (previousFocus?.Focus(FocusState.Programmatic) != true)
        {
            Page.FocusDefaultAction();
        }
    }

    internal void ApplyTheme(ElementTheme theme)
    {
        ((FrameworkElement)Content).RequestedTheme = theme;
        Page.RequestedTheme = theme;
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.PreferredTheme = theme switch
            {
                ElementTheme.Light => TitleBarTheme.Light,
                ElementTheme.Dark => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode,
            };
        }
    }

    /// <summary>Creates the native shell and its document-first page.</summary>
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _windowIcon = Windows.NativeMethods.LoadAppIcon(32);
        AppWindow.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(_windowIcon));
        Closed += (_, _) =>
        {
            if (_windowIcon != 0)
            {
                Windows.NativeMethods.DestroyIcon(_windowIcon);
                _windowIcon = 0;
            }
        };
        Page = new MainPage();
        RootFrame.Content = Page;
        RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        double scale = Math.Max(1, Windows.NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
        int width = Math.Min((int)(1120 * scale), work.Width);
        int height = Math.Min((int)(800 * scale), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 560;
            presenter.PreferredMinimumHeight = 420;
        }
    }
}
