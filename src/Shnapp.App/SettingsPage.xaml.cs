using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shnapp.Core;
using Shnapp.App.Windows;

namespace Shnapp.App;

/// <summary>Settings destinations within the existing Shnapp window.</summary>
public sealed partial class SettingsPage : Page
{
    private AppController? _controller;
    private Uri? _latestRelease;
    private InstallationChannel _managerChannel;

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
        InstallationChannel channel = InstallationChannelDetector.Detect(packaged);
        UpdateChannelLabel.Text = channel switch
        {
            InstallationChannel.Store => "Microsoft Store package. Store-installed copies can update here; sideloaded packages depend on their original source.",
            InstallationChannel.DirectZip => "Direct ZIP copy. Shnapp checks GitHub and prepares verified releases for the next restart.",
            InstallationChannel.Scoop => "Scoop owns this installation. Shnapp hands upgrades back to Scoop.",
            InstallationChannel.WinGet => "WinGet owns this installation. Shnapp hands upgrades back to WinGet.",
            InstallationChannel.Chocolatey => PackageManagerUpdateRunner.CanStartChocolatey()
                ? "Chocolatey owns this installation. Its upgrade may ask for administrator permission."
                : "Chocolatey owns this installation. Upgrade this custom copy from an Administrator Chocolatey terminal.",
            _ => "Shnapp cannot verify this installation's source. Update it from the original source.",
        };
        OpenStoreButton.Visibility = packaged ? Visibility.Visible : Visibility.Collapsed;
        InstallStoreUpdateButton.Visibility = Visibility.Collapsed;
        RestartStoreButton.Visibility = Visibility.Collapsed;
        RestartPortableButton.Visibility = Visibility.Collapsed;
        ManagerUpdateButton.Visibility = Visibility.Collapsed;
        UpdateMessage.Message = packaged
            ? "Microsoft Store normally updates Store-installed copies. Shnapp checks periodically and can install an available update when you choose Update now."
            : "Shnapp checks for a stable GitHub release in the background at most once a day.";
        UpdateHelpText.Text = channel switch
        {
            InstallationChannel.Store => "Microsoft Store controls automatic updates. Shnapp can install an available Store update when you choose Update now. Windows may close the app during installation.",
            InstallationChannel.DirectZip when PortableUpdateStager.FindInstallRoot(AppContext.BaseDirectory) is null =>
                "This older ZIP copy needs one manual download to enable automatic updates. Your library stays in your user folder.",
            InstallationChannel.DirectZip => "Shnapp downloads a verified release in the background and uses it on the next restart. Your library stays in your user folder.",
            InstallationChannel.Chocolatey when !PackageManagerUpdateRunner.CanStartChocolatey() =>
                "This Chocolatey installation uses a custom location. Run choco upgrade shnapp -y from an Administrator terminal.",
            InstallationChannel.Scoop or InstallationChannel.WinGet or InstallationChannel.Chocolatey =>
                "Shnapp closes before asking your package manager to check and install an available upgrade. Catalog approval may lag the GitHub release.",
            _ => "Open the original source of this copy to update it.",
        };
        SettingsNavigation.SelectedItem = SettingsNavigation.MenuItems[showUpdates ? 1 : 0];
        SelectSection(showUpdates ? "updates" : "general");
    }

    internal void SetUpdateResult(UpdateCheckResult result)
    {
        UpdateMessage.IsOpen = true;
        UpdateMessage.Message = result.Message;
        UpdateMessage.Severity = result.IsError ? InfoBarSeverity.Warning :
            result.UpdateAvailable || result.PortableRestartRequired || result.StoreRestartRequired
                ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        _latestRelease = result.ReleasePage;
        OpenLatestButton.Visibility = _latestRelease is null ? Visibility.Collapsed : Visibility.Visible;
        InstallStoreUpdateButton.Visibility = result.StoreInstallAvailable ? Visibility.Visible : Visibility.Collapsed;
        RestartStoreButton.Visibility = result.StoreRestartRequired ? Visibility.Visible : Visibility.Collapsed;
        RestartPortableButton.Visibility = result.PortableRestartRequired ? Visibility.Visible : Visibility.Collapsed;
        _managerChannel = result.ManagerChannel;
        ManagerUpdateButton.Visibility = _managerChannel is InstallationChannel.Scoop or InstallationChannel.WinGet or InstallationChannel.Chocolatey
            ? Visibility.Visible : Visibility.Collapsed;
        if (ManagerUpdateButton.Visibility == Visibility.Visible)
        {
            ManagerUpdateButton.Content = "Update with " + _managerChannel;
        }
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

    private async void InstallStoreUpdate_Click(object sender, RoutedEventArgs args)
    {
        if (_controller is null)
        {
            return;
        }

        InstallStoreUpdateButton.IsEnabled = false;
        CheckUpdatesButton.IsEnabled = false;
        UpdateMessage.IsOpen = true;
        UpdateMessage.Severity = InfoBarSeverity.Informational;
        UpdateMessage.Message = "Saving your work and asking Microsoft Store to install the update…";
        try
        {
            SetUpdateResult(await _controller.InstallStoreUpdateAsync());
        }
        catch (OperationCanceledException)
        {
            SetUpdateResult(new UpdateCheckResult("The update request was canceled."));
        }
        catch (Exception)
        {
            SetUpdateResult(new UpdateCheckResult("Could not install the Store update. Try Microsoft Store updates instead.",
                IsError: true, StoreInstallAvailable: true));
        }
        finally
        {
            InstallStoreUpdateButton.IsEnabled = true;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void RestartStore_Click(object sender, RoutedEventArgs args)
    {
        if (_controller is null)
        {
            return;
        }

        RestartStoreButton.IsEnabled = false;
        try
        {
            SetUpdateResult(await _controller.RestartAfterStoreUpdateAsync());
        }
        finally
        {
            RestartStoreButton.IsEnabled = true;
        }
    }

    private async void RestartPortable_Click(object sender, RoutedEventArgs args)
    {
        if (_controller is null) { return; }
        RestartPortableButton.IsEnabled = false;
        try
        {
            SetUpdateResult(await _controller.RestartAfterPortableUpdateAsync());
        }
        finally
        {
            RestartPortableButton.IsEnabled = true;
        }
    }

    private async void ManagerUpdate_Click(object sender, RoutedEventArgs args)
    {
        if (_controller is null) { return; }
        ManagerUpdateButton.IsEnabled = false;
        UpdateMessage.IsOpen = true;
        UpdateMessage.Severity = InfoBarSeverity.Informational;
        UpdateMessage.Message = "Saving your work before asking " + _managerChannel + " to update Shnapp…";
        try
        {
            SetUpdateResult(await _controller.UpdateWithPackageManagerAsync(_managerChannel));
        }
        finally
        {
            ManagerUpdateButton.IsEnabled = true;
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
