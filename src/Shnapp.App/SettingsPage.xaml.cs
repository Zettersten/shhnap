using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shnapp.Core;

namespace Shnapp.App;

/// <summary>Settings destinations within the existing Shnapp window.</summary>
public sealed partial class SettingsPage : Page
{
    private AppController? _controller;
    private Uri? _latestRelease;

    public SettingsPage() => InitializeComponent();

    internal void Configure(AppController controller, ShnappSettings settings, bool isolated,
        string libraryPath, string version, bool packaged, bool showUpdates)
    {
        _controller = controller;
        StartAtSignIn.IsOn = settings.StartOnLogin;
        StartAtSignIn.IsEnabled = !isolated;
        AutoCopy.IsOn = settings.AutoCopy;
        WindowShadow.IsOn = settings.WindowShadow;
        ThemePicker.SelectedIndex = settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        LibraryPath.Text = libraryPath;
        LibraryNote.Text = isolated
            ? "Verification library. Startup changes are disabled."
            : "Editable originals stay here. Share the exported PNG when a capture contains private details.";
        VersionLabel.Text = "Version " + version;
        RuntimeLabel.Text = $"Windows {Environment.OSVersion.Version} · {RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version}";
        UpdateChannelLabel.Text = packaged
            ? "Packaged build. Microsoft Store can provide updates when this copy was installed from its Store listing. Sideloaded packages depend on their original package source."
            : "Portable build. Shnapp checks published stable releases on GitHub; package manager listings can update later.";
        OpenStoreButton.Visibility = packaged ? Visibility.Visible : Visibility.Collapsed;
        UpdateMessage.Message = packaged
            ? "Microsoft Store normally handles updates for Store-installed copies."
            : "Shnapp checks for a stable GitHub release in the background at most once a day.";
        SettingsNavigation.SelectedItem = SettingsNavigation.MenuItems[showUpdates ? 1 : 0];
        SelectSection(showUpdates ? "updates" : "general");
    }

    internal void SetUpdateResult(UpdateCheckResult result)
    {
        UpdateMessage.IsOpen = true;
        UpdateMessage.Message = result.Message;
        UpdateMessage.Severity = result.IsError ? InfoBarSeverity.Warning :
            result.UpdateAvailable ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        _latestRelease = result.ReleasePage;
        OpenLatestButton.Visibility = _latestRelease is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BackToEditor_Click(object sender, RoutedEventArgs args) =>
        _controller?.CloseSettings();

    private void SettingsNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string section })
        {
            SelectSection(section);
        }
    }

    private void SelectSection(string section)
    {
        GeneralPanel.Visibility = section == "general" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPanel.Visibility = section == "updates" ? Visibility.Visible : Visibility.Collapsed;
        FeedbackPanel.Visibility = section == "feedback" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SavePreferences_Click(object sender, RoutedEventArgs args)
    {
        if (_controller is null)
        {
            return;
        }

        var preferences = new ShnappSettings
        {
            StartOnLogin = StartAtSignIn.IsOn,
            AutoCopy = AutoCopy.IsOn,
            WindowShadow = WindowShadow.IsOn,
            Theme = ThemePicker.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" },
        };
        try
        {
            await _controller.SavePreferencesAsync(preferences);
            PreferenceMessage.Title = "Preferences saved";
            PreferenceMessage.Message = string.Empty;
            PreferenceMessage.Severity = InfoBarSeverity.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.Security.SecurityException or System.Runtime.InteropServices.COMException)
        {
            PreferenceMessage.Title = "Could not save preferences";
            PreferenceMessage.Message = exception.Message;
            PreferenceMessage.Severity = InfoBarSeverity.Error;
        }

        PreferenceMessage.IsOpen = true;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs args)
    {
        if (_controller is null)
        {
            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        UpdateMessage.IsOpen = true;
        UpdateMessage.Severity = InfoBarSeverity.Informational;
        UpdateMessage.Message = "Checking for updates…";
        try
        {
            SetUpdateResult(await _controller.CheckForUpdatesAsync(manual: true));
        }
        catch (OperationCanceledException)
        {
            SetUpdateResult(new UpdateCheckResult("The update check was canceled."));
        }
        catch (Exception)
        {
            SetUpdateResult(new UpdateCheckResult("Could not check for updates right now. Try again later.", IsError: true));
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void OpenLatest_Click(object sender, RoutedEventArgs args)
    {
        if (_latestRelease is not null)
        {
            OpenExternal(_latestRelease);
        }
    }

    private void OpenStore_Click(object sender, RoutedEventArgs args) =>
        OpenExternal(new Uri("ms-windows-store://downloadsandupdates"));

    private void OpenWebsite_Click(object sender, RoutedEventArgs args) =>
        OpenExternal(new Uri("https://shhnap.com/"));

    private void OpenRepository_Click(object sender, RoutedEventArgs args) =>
        OpenExternal(new Uri("https://github.com/Zettersten/shhnap"));

    private void OpenPublisher_Click(object sender, RoutedEventArgs args) =>
        OpenExternal(new Uri("https://zettersten.com/"));

    private void OpenPrivacy_Click(object sender, RoutedEventArgs args) =>
        OpenExternal(new Uri("https://shhnap.com/privacy/"));

    private void ReportBug_Click(object sender, RoutedEventArgs args) =>
        OpenExternal(new Uri("https://github.com/Zettersten/shhnap/issues/new?template=01-bug.yml"));

    private void RequestFeature_Click(object sender, RoutedEventArgs args) =>
        OpenExternal(new Uri("https://github.com/Zettersten/shhnap/issues/new?template=02-feature-request.yml"));

    private void BrowseIssues_Click(object sender, RoutedEventArgs args) =>
        OpenExternal(new Uri("https://github.com/Zettersten/shhnap/issues"));

    private void OpenExternal(Uri address)
    {
        try
        {
            Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            PreferenceMessage.Title = "Could not open the link";
            PreferenceMessage.Message = address.AbsoluteUri;
            PreferenceMessage.Severity = InfoBarSeverity.Warning;
            PreferenceMessage.IsOpen = true;
        }
    }
}
