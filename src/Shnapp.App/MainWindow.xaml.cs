using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Shnapp.App;

/// <summary>The single native window; ordinary close returns it to the tray.</summary>
public sealed partial class MainWindow : Window
{
    internal MainPage Page { get; }
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

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
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
