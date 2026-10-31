using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shnapp.App.Video;

namespace Shnapp.App;

public sealed partial class MainWindow
{
    internal VideoEditorPage? VideoPage { get; private set; }

    internal event Action? VideoBackRequested;
    internal event Action? VideoSettingsRequested;

    internal void ShowVideoEditor(VideoEditorPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (VideoPage is { } previous)
        {
            previous.StateChanged -= VideoPage_StateChanged;
            previous.Close();
        }

        VideoPage = page;
        page.StateChanged += VideoPage_StateChanged;
        RootFrame.Content = page;
        UpdateTitleBarState();
        page.Focus(FocusState.Programmatic);
    }

    internal void ShowPhotoPage()
    {
        if (VideoPage is null)
        {
            return;
        }

        VideoEditorPage previous = VideoPage;
        VideoPage = null;
        previous.StateChanged -= VideoPage_StateChanged;
        RootFrame.Content = Page;
        previous.Close();
        UpdateTitleBarState();
    }

    private void VideoPage_StateChanged(object? sender, EventArgs args) => UpdateTitleBarState();

    private void VideoBack_Click(object sender, RoutedEventArgs args) => VideoBackRequested?.Invoke();

    private void TitleVideoMore_Click(object sender, RoutedEventArgs args)
    {
        var menu = new MenuFlyout();
        var export = new MenuFlyoutItem { Text = "Export MP4…" };
        export.Click += async (_, _) =>
        {
            if (VideoPage is { } page)
            {
                await page.ExportAsync();
            }
        };
        menu.Items.Add(export);
        menu.Items.Add(new MenuFlyoutSeparator());
        var settings = new MenuFlyoutItem { Text = "Settings" };
        settings.Click += (_, _) => VideoSettingsRequested?.Invoke();
        menu.Items.Add(settings);
        var library = new MenuFlyoutItem { Text = "Back to Library" };
        library.Click += (_, _) => VideoBackRequested?.Invoke();
        menu.Items.Add(library);
        menu.ShowAt((FrameworkElement)sender);
    }
}
