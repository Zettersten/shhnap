using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Shnapp.App;

/// <summary>The single native window; ordinary close returns it to the tray.</summary>
public sealed partial class MainWindow : Window
{
    private nint _windowIcon;
    internal MainPage Page { get; }
    internal SettingsPage? Settings { get; private set; }
    private UIElement? _focusBeforeSettings;
    private int _settingsTransitionVersion;
    private bool _updateAvailable;

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
        UpdateTitleBarState();
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
        UpdateTitleBarState();
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
            AppWindowTitleBar titleBar = AppWindow.TitleBar;
            titleBar.PreferredTheme = theme switch
            {
                ElementTheme.Light => TitleBarTheme.Light,
                ElementTheme.Dark => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode,
            };
            // The XAML title bar paints the same surface as the page. Let that
            // surface show through the native caption buttons as well.
            bool highContrast = new AccessibilitySettings().HighContrast;
            bool dark = theme == ElementTheme.Dark ||
                (theme == ElementTheme.Default && AppTitleBar.ActualTheme == ElementTheme.Dark);
            titleBar.ButtonBackgroundColor = highContrast ? null : Color.FromArgb(0, 0, 0, 0);
            titleBar.ButtonInactiveBackgroundColor = highContrast ? null : Color.FromArgb(0, 0, 0, 0);
            titleBar.ButtonHoverBackgroundColor = highContrast ? null :
                dark ? Color.FromArgb(32, 255, 255, 255) : Color.FromArgb(20, 10, 39, 77);
            titleBar.ButtonPressedBackgroundColor = highContrast ? null :
                dark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(32, 10, 39, 77);
        }
    }

    internal void ToggleFullScreen()
    {
        bool fullScreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.Default : AppWindowPresenterKind.FullScreen);
        if (fullScreen && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 560;
            presenter.PreferredMinimumHeight = 420;
        }
    }

    internal void SetUpdateAttention(bool available)
    {
        _updateAvailable = available;
        UpdateAttentionDot.Visibility = available && Settings is null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PageStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Page.ViewModel.HasDocument) or nameof(Page.ViewModel.Title)
            or nameof(Page.ViewModel.CanUndo) or nameof(Page.ViewModel.CanRedo))
        {
            UpdateTitleBarState();
        }
    }

    private void UpdateTitleBarState()
    {
        bool editing = Page.ViewModel.HasDocument && Settings is null;
        ChromeNavigationHost.Visibility = Settings is null ? Visibility.Visible : Visibility.Collapsed;
        ChromeCommandsHost.Visibility = Settings is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateAttentionDot.Visibility = _updateAvailable && Settings is null
            ? Visibility.Visible : Visibility.Collapsed;
        TitleUndoButton.IsEnabled = editing && Page.ViewModel.CanUndo;
        TitleRedoButton.IsEnabled = editing && Page.ViewModel.CanRedo;
        WindowDocumentTitle.Text = Settings is not null ? "Settings" :
            editing ? Page.ViewModel.Title : "Shnapp";
        UpdateTitleTextVisibility();
        Title = WindowDocumentTitle.Text;
        AppTitleBar.RecomputeDragRegions();
    }

    private void UpdateTitleTextVisibility()
    {
        bool editing = Page.ViewModel.HasDocument && Settings is null;
        EditorTitleActions.Visibility = editing && AppTitleBar.ActualWidth >= 780
            ? Visibility.Visible : Visibility.Collapsed;
        // Reserve the same gap on either side of the centered title. Long
        // document names trim before they can cover the last command.
        double titleRoom = (AppTitleBar.ActualWidth / 2 - TitleLeftControls.ActualWidth - 12) * 2 - 16;
        WindowDocumentTitle.MaxWidth = Math.Min(220, Math.Max(0, titleRoom));
        bool showTitle = Settings is not null ||
            (AppTitleBar.ActualWidth >= 1000 && titleRoom >= 80);
        WindowDocumentTitleButton.Visibility = showTitle && !Page.IsTitleRenameActive
            ? Visibility.Visible : Visibility.Collapsed;
        WindowDocumentTitleButton.IsHitTestVisible = editing && showTitle;
        TitleEditorHost.Margin = new Thickness(0, showTitle ? 8 : 56, 0, 0);
        TitleEditorHost.Visibility = Page.IsTitleRenameActive ? Visibility.Visible : Visibility.Collapsed;
        Page.SetAnnotationToolbarObscured(editing && Page.IsTitleRenameActive && !showTitle);
        AppTitleBar.RecomputeDragRegions();
    }

    private void WindowDocumentTitle_Click(object sender, RoutedEventArgs args) => Page.StartTitleRenameFromChrome();

    private void TitleEdit_Click(object sender, RoutedEventArgs args) => Page.FocusEditorFromChrome();
    private void TitleUndo_Click(object sender, RoutedEventArgs args) => Page.UndoFromChrome();
    private void TitleRedo_Click(object sender, RoutedEventArgs args) => Page.RedoFromChrome();
    private void TitleCopy_Click(object sender, RoutedEventArgs args) => Page.CopyFromChrome();
    private void TitleSave_Click(object sender, RoutedEventArgs args) => Page.SaveFromChrome();

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
        Page.AttachTitleChrome(ChromeCommandsHost, ChromeNavigationHost, TitleEditorHost);
        RootFrame.Content = Page;
        Page.ViewModel.PropertyChanged += PageStateChanged;
        Page.TitleRenameStateChanged += (_, _) => UpdateTitleTextVisibility();
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleTextVisibility();
        TitleLeftControls.SizeChanged += (_, _) => UpdateTitleTextVisibility();
        UpdateTitleBarState();
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
