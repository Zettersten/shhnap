using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Shnapp.App.Capture;
using Shnapp.Core;
using Shnapp.App.Windows;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Shnapp.App;

/// <summary>Settings destinations within the existing Shnapp window.</summary>
public sealed partial class SettingsPage : Page
{
    private AppController? _controller;
    private Uri? _latestRelease;
    private InstallationChannel _managerChannel;
    private bool _saving;
    private bool _startupEditable;
    private bool _printScreenEditable;
    private bool _cameraSourcesLoaded;
    private bool _loadingCameraSources;
    private int _availableCameraSourceCount;
    private bool _microphoneSourcesLoaded;
    private bool _loadingMicrophoneSources;
    private int _availableMicrophoneSourceCount;
    private bool _configured;
    private bool _entered;
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private Task _openingTransition = Task.CompletedTask;
    private ShnappSettings _savedSettings = new();
    private ShnappSettings? _queuedSettings;
    private string _selectedSection = "general";

    public SettingsPage()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => UpdateNavForegrounds();
        if (_animationsEnabled)
        {
            SettingsScrim.Opacity = 0;
            SettingsDialogMotion.TranslateY = 14;
            SettingsDialogMotion.ScaleX = 0.985;
            SettingsDialogMotion.ScaleY = 0.985;
        }
    }

    internal void Configure(AppController controller, ShnappSettings settings, bool isolated,
        string libraryPath, string version, bool packaged, bool showUpdates)
    {
        _controller = controller;
        _startupEditable = !isolated;
        _printScreenEditable = !isolated;
        _savedSettings = settings;
        _configured = false;
        StartAtSignIn.IsOn = settings.StartOnLogin;
        StartAtSignIn.IsEnabled = _startupEditable;
        AutoCopy.IsOn = settings.AutoCopy;
        WindowShadow.IsOn = settings.WindowShadow;
        VideoSystemAudio.IsOn = settings.VideoCaptureSystemAudio;
        VideoMicrophone.IsOn = settings.VideoCaptureMicrophone;
        VideoCamera.IsOn = settings.VideoCaptureCamera;
        VideoCursor.IsOn = settings.VideoCaptureCursor;
        PrintScreenEnabled.IsOn = settings.PrintScreenEnabled;
        PrintScreenEnabled.IsEnabled = _printScreenEditable;
        PrintScreenModePicker.SelectedIndex = settings.PrintScreenMode switch
        {
            CaptureKind.Window => 1,
            CaptureKind.Region => 2,
            _ => 0,
        };
        PrintScreenModePicker.IsEnabled = _printScreenEditable && settings.PrintScreenEnabled;
        _cameraSourcesLoaded = false;
        _availableCameraSourceCount = 0;
        CameraSourcePicker.SelectedIndex = 0;
        CameraSourcePicker.IsEnabled = false;
        TakeCameraPhotoButton.IsEnabled = false;
        CameraSourceStatus.Text = "Finding cameras…";
        _microphoneSourcesLoaded = false;
        _availableMicrophoneSourceCount = 0;
        VideoMicrophoneSourcePicker.SelectedIndex = 0;
        VideoMicrophoneSourcePicker.IsEnabled = false;
        VideoMicrophoneSourceStatus.Text = "Finding microphones…";
        ThemePicker.SelectedIndex = settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        LibraryPath.Text = libraryPath;
        LibraryNote.Text = isolated
            ? "Verification library. Startup changes are disabled."
            : "Original captures are stored here.";
        VersionLabel.Text = "Installed version " + version;
        Version windows = Environment.OSVersion.Version;
        string windowsName = windows.Major == 10 && windows.Build >= 22000 ? "Windows 11" : $"Windows {windows.Major}";
        RuntimeLabel.Text = $"{windowsName} · build {windows.Build} · {RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version}";
        InstallationChannel channel = InstallationChannelDetector.Detect(packaged);
        UpdateChannelLabel.Text = channel switch
        {
            InstallationChannel.Store => "Microsoft Store package. Store-installed copies can update here; sideloaded packages depend on their original source.",
            InstallationChannel.DirectZip => "Direct ZIP copy. Shnapp checks GitHub for newer stable releases.",
            InstallationChannel.Velopack => "Velopack installation. Shnapp installs verified updates from stable GitHub releases.",
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
        RestartVelopackButton.Visibility = Visibility.Collapsed;
        ManagerUpdateButton.Visibility = Visibility.Collapsed;
        UpdateMessage.Message = channel switch
        {
            InstallationChannel.Store => "Microsoft Store normally updates Store-installed copies. Shnapp checks periodically and can install an available update when you choose Update now.",
            InstallationChannel.Velopack => "Velopack checks for stable updates in the background.",
            _ => "Shnapp checks for a stable GitHub release in the background at most once a day.",
        };
        UpdateHelpText.Text = channel switch
        {
            InstallationChannel.Store => "Microsoft Store controls automatic updates. Shnapp can install an available Store update when you choose Update now. Windows may close the app during installation.",
            InstallationChannel.DirectZip when PortableUpdateStager.FindCurrentInstallRoot() is null =>
                "This standalone copy needs one manual download to enable automatic updates. Your library stays in your user folder.",
            InstallationChannel.DirectZip => "Shnapp downloads a verified release in the background and uses it on the next restart. Your library stays in your user folder.",
            InstallationChannel.Velopack => "Velopack prepares verified updates in the background. Choose Restart to install update when one is ready. Your library stays in your user folder.",
            InstallationChannel.Chocolatey when !PackageManagerUpdateRunner.CanStartChocolatey() =>
                "This Chocolatey installation uses a custom location. Run choco upgrade shnapp -y from an Administrator terminal.",
            InstallationChannel.Scoop or InstallationChannel.WinGet or InstallationChannel.Chocolatey =>
                "Shnapp closes before asking your package manager to check and install an available upgrade. Catalog approval may lag the GitHub release.",
            _ => "Open the original source of this copy to update it.",
        };
        SelectSection(showUpdates ? "updates" : "general");
        _configured = true;
    }

    internal void ShowUpdates()
    {
        if (_saving) { return; }
        SelectSection("updates");
        UpdatesNavButton.Focus(FocusState.Programmatic);
    }

    internal void SetUpdateResult(UpdateCheckResult result)
    {
        UpdateMessage.IsOpen = true;
        UpdateMessage.Message = result.Message;
        UpdateMessage.Severity = result.IsError ? InfoBarSeverity.Warning :
            result.PortableRestartRequired || result.VelopackRestartRequired || result.StoreRestartRequired
                ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        _latestRelease = result.ReleasePage;
        OpenLatestButton.Visibility = _latestRelease is null ? Visibility.Collapsed : Visibility.Visible;
        InstallStoreUpdateButton.Visibility = result.StoreInstallAvailable ? Visibility.Visible : Visibility.Collapsed;
        RestartStoreButton.Visibility = result.StoreRestartRequired ? Visibility.Visible : Visibility.Collapsed;
        RestartPortableButton.Visibility = result.PortableRestartRequired ? Visibility.Visible : Visibility.Collapsed;
        RestartVelopackButton.Visibility = result.VelopackRestartRequired ? Visibility.Visible : Visibility.Collapsed;
        _managerChannel = result.ManagerChannel;
        ManagerUpdateButton.Visibility = _managerChannel is InstallationChannel.Scoop or InstallationChannel.WinGet or InstallationChannel.Chocolatey
            ? Visibility.Visible : Visibility.Collapsed;
        if (ManagerUpdateButton.Visibility == Visibility.Visible)
        {
            ManagerUpdateButton.Content = "Update with " + _managerChannel;
        }
    }

    private void BackToEditor_Click(object sender, RoutedEventArgs args)
    {
        if (!_saving) { _controller?.CloseSettings(); }
    }

    private void GeneralNav_Click(object sender, RoutedEventArgs args)
    {
        if (!_saving) { SelectSection("general"); }
    }
    private void RecordingNav_Click(object sender, RoutedEventArgs args)
    {
        if (!_saving) { SelectSection("recording"); }
    }
    private void ShortcutsNav_Click(object sender, RoutedEventArgs args)
    {
        if (!_saving) { SelectSection("shortcuts"); }
    }
    private void UpdatesNav_Click(object sender, RoutedEventArgs args)
    {
        if (!_saving) { SelectSection("updates"); }
    }
    private void FeedbackNav_Click(object sender, RoutedEventArgs args)
    {
        if (!_saving) { SelectSection("feedback"); }
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs args)
    {
        (_selectedSection switch
        {
            "recording" => RecordingNavButton,
            "shortcuts" => ShortcutsNavButton,
            "updates" => UpdatesNavButton,
            "feedback" => FeedbackNavButton,
            _ => GeneralNavButton,
        })
            .Focus(FocusState.Programmatic);
        UpdateNavForegrounds();
        if (_animationsEnabled && !_entered)
        {
            _entered = true;
            _openingTransition = AnimateDialogAsync(0, 1, 14, 0, 240, EasingMode.EaseOut);
        }
        await Task.WhenAll(RefreshCameraSourcesAsync(), RefreshMicrophoneSourcesAsync());
    }

    private async Task RefreshCameraSourcesAsync()
    {
        if (!_configured || _loadingCameraSources)
        {
            return;
        }

        _loadingCameraSources = true;
        _cameraSourcesLoaded = false;
        CameraSourcePicker.IsEnabled = false;
        TakeCameraPhotoButton.IsEnabled = false;
        CameraSourceStatus.Text = "Finding cameras…";
        try
        {
            IReadOnlyList<CameraSource> sources = await CameraCaptureService.EnumerateSourcesAsync();
            CameraSourcePicker.Items.Clear();
            CameraSourcePicker.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = string.Empty });
            foreach (CameraSource source in sources)
            {
                CameraSourcePicker.Items.Add(new ComboBoxItem { Content = source.Name, Tag = source.Id });
            }

            _availableCameraSourceCount = sources.Count;
            SelectCameraSource(_savedSettings.CameraSourceId);
            _cameraSourcesLoaded = true;
            CameraSourcePicker.IsEnabled = !_saving && sources.Count > 0;
            TakeCameraPhotoButton.IsEnabled = !_saving && sources.Count > 0;
            UpdateCameraSourceStatus();
        }
        catch (Exception exception)
        {
            _availableCameraSourceCount = 0;
            CameraSourcePicker.IsEnabled = false;
            TakeCameraPhotoButton.IsEnabled = false;
            CameraSourceStatus.Text = "Camera sources could not be loaded. Reopen Settings to try again.";
            Debug.WriteLine($"Shnapp camera source enumeration failed: {exception}");
        }
        finally
        {
            _loadingCameraSources = false;
        }
    }

    private void SelectCameraSource(string? sourceId)
    {
        for (int index = 0; index < CameraSourcePicker.Items.Count; index++)
        {
            if (CameraSourcePicker.Items[index] is ComboBoxItem item &&
                string.Equals(item.Tag as string, sourceId ?? string.Empty, StringComparison.Ordinal))
            {
                CameraSourcePicker.SelectedIndex = index;
                return;
            }
        }

        CameraSourcePicker.Items.Add(new ComboBoxItem
        {
            Content = "Previously selected camera (unavailable)",
            Tag = sourceId,
        });
        CameraSourcePicker.SelectedIndex = CameraSourcePicker.Items.Count - 1;
    }

    private void UpdateCameraSourceStatus()
    {
        if (!_cameraSourcesLoaded)
        {
            return;
        }

        CameraSourceStatus.Text = _availableCameraSourceCount == 0
            ? VideoCamera.IsOn
                ? "No camera is connected. Camera video cannot record until one is available."
                : "No camera is connected. Connect one to take photos or record camera video."
            : CameraSourcePicker.SelectedIndex > _availableCameraSourceCount
                ? "The saved camera is unavailable. Choose Automatic or another camera."
                : CameraSourcePicker.SelectedIndex <= 0
                    ? "Automatic uses an available camera for photos and video."
                    : "This camera will be used for photos and video.";
    }

    private string? SelectedCameraSourceId()
    {
        if (!_cameraSourcesLoaded)
        {
            return _savedSettings.CameraSourceId;
        }

        string? sourceId = (CameraSourcePicker.SelectedItem as ComboBoxItem)?.Tag as string;
        return string.IsNullOrEmpty(sourceId) ? null : sourceId;
    }

    private async Task RefreshMicrophoneSourcesAsync()
    {
        if (!_configured || _loadingMicrophoneSources)
        {
            return;
        }

        _loadingMicrophoneSources = true;
        _microphoneSourcesLoaded = false;
        VideoMicrophoneSourcePicker.IsEnabled = false;
        VideoMicrophoneSourceStatus.Text = "Finding microphones…";
        try
        {
            DeviceInformationCollection sources = await DeviceInformation.FindAllAsync(
                MediaDevice.GetAudioCaptureSelector());
            VideoMicrophoneSourcePicker.Items.Clear();
            VideoMicrophoneSourcePicker.Items.Add(new ComboBoxItem
            {
                Content = "System default",
                Tag = string.Empty,
            });
            foreach (DeviceInformation source in sources)
            {
                VideoMicrophoneSourcePicker.Items.Add(new ComboBoxItem
                {
                    Content = source.Name,
                    Tag = source.Id,
                });
            }

            _availableMicrophoneSourceCount = sources.Count;
            SelectMicrophoneSource(_savedSettings.VideoMicrophoneSourceId);
            _microphoneSourcesLoaded = true;
            VideoMicrophoneSourcePicker.IsEnabled =
                !_saving && VideoMicrophone.IsOn && sources.Count > 0;
            UpdateMicrophoneSourceStatus();
        }
        catch (Exception exception)
        {
            _availableMicrophoneSourceCount = 0;
            VideoMicrophoneSourcePicker.IsEnabled = false;
            VideoMicrophoneSourceStatus.Text =
                "Microphone sources could not be loaded. Reopen Settings to try again.";
            Debug.WriteLine($"Shnapp microphone source enumeration failed: {exception}");
        }
        finally
        {
            _loadingMicrophoneSources = false;
        }
    }

    private void SelectMicrophoneSource(string? sourceId)
    {
        for (int index = 0; index < VideoMicrophoneSourcePicker.Items.Count; index++)
        {
            if (VideoMicrophoneSourcePicker.Items[index] is ComboBoxItem item &&
                string.Equals(item.Tag as string, sourceId ?? string.Empty,
                    StringComparison.Ordinal))
            {
                VideoMicrophoneSourcePicker.SelectedIndex = index;
                return;
            }
        }

        VideoMicrophoneSourcePicker.Items.Add(new ComboBoxItem
        {
            Content = "Previously selected microphone (unavailable)",
            Tag = sourceId,
        });
        VideoMicrophoneSourcePicker.SelectedIndex =
            VideoMicrophoneSourcePicker.Items.Count - 1;
    }

    private void UpdateMicrophoneSourceStatus()
    {
        if (!_microphoneSourcesLoaded)
        {
            return;
        }

        VideoMicrophoneSourceStatus.Text = _availableMicrophoneSourceCount == 0
            ? "No microphone is connected. Connect one to record your voice."
            : VideoMicrophoneSourcePicker.SelectedIndex > _availableMicrophoneSourceCount
                ? "The saved microphone is unavailable. Choose System default or another microphone."
                : !VideoMicrophone.IsOn
                    ? "Turn on Record microphone to use this source."
                : VideoMicrophoneSourcePicker.SelectedIndex <= 0
                    ? "Uses the default Windows input for new recordings."
                    : "This microphone will be used for new recordings.";
    }

    private string? SelectedMicrophoneSourceId()
    {
        if (!_microphoneSourcesLoaded)
        {
            return _savedSettings.VideoMicrophoneSourceId;
        }

        string? sourceId =
            (VideoMicrophoneSourcePicker.SelectedItem as ComboBoxItem)?.Tag as string;
        return string.IsNullOrEmpty(sourceId) ? null : sourceId;
    }

    internal async Task PlayCloseTransitionAsync()
    {
        if (!_animationsEnabled || !_entered)
        {
            return;
        }

        await _openingTransition;
        await AnimateDialogAsync(1, 0, 0, 8, 170, EasingMode.EaseIn);
    }

    private Task AnimateDialogAsync(double fromOpacity, double toOpacity,
        double fromOffset, double toOffset, int milliseconds, EasingMode easingMode)
    {
        var storyboard = new Storyboard();
        var easing = new CubicEase { EasingMode = easingMode };
        var fade = new DoubleAnimation
        {
            From = fromOpacity,
            To = toOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(fade, SettingsScrim);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        var move = new DoubleAnimation
        {
            From = fromOffset,
            To = toOffset,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(move, SettingsDialogMotion);
        Storyboard.SetTargetProperty(move, "TranslateY");
        storyboard.Children.Add(move);

        var scale = new DoubleAnimation
        {
            From = toOpacity > fromOpacity ? 0.985 : 1,
            To = toOpacity > fromOpacity ? 1 : 0.985,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(scale, SettingsDialogMotion);
        Storyboard.SetTargetProperty(scale, "ScaleX");
        storyboard.Children.Add(scale);
        var scaleY = new DoubleAnimation
        {
            From = scale.From,
            To = scale.To,
            Duration = scale.Duration,
            EasingFunction = easing,
        };
        Storyboard.SetTarget(scaleY, SettingsDialogMotion);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        storyboard.Children.Add(scaleY);

        var completed = new TaskCompletionSource<bool>();
        storyboard.Completed += (_, _) =>
        {
            storyboard.Stop();
            SettingsScrim.Opacity = toOpacity;
            SettingsDialogMotion.TranslateY = toOffset;
            SettingsDialogMotion.ScaleX = scale.To ?? 1;
            SettingsDialogMotion.ScaleY = scaleY.To ?? 1;
            completed.TrySetResult(true);
        };
        storyboard.Begin();
        return completed.Task;
    }

    private void SettingsPage_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool compact = args.NewSize.Width < 720;
        SettingsSaveStatus.Visibility = args.NewSize.Width < 560 ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new GridLength(compact ? 52 : 176);
        SidebarBorder.Margin = compact ? new Thickness(0, 0, 8, 16) : new Thickness(0, 0, 22, 16);
        Visibility labelVisibility = compact ? Visibility.Collapsed : Visibility.Visible;
        GeneralNavLabel.Visibility = labelVisibility;
        RecordingNavLabel.Visibility = labelVisibility;
        ShortcutsNavLabel.Visibility = labelVisibility;
        UpdatesNavLabel.Visibility = labelVisibility;
        FeedbackNavLabel.Visibility = labelVisibility;
        foreach (Button button in new[]
            { GeneralNavButton, RecordingNavButton, ShortcutsNavButton, UpdatesNavButton, FeedbackNavButton })
        {
            button.Padding = compact ? new Thickness(8) : new Thickness(10, 6, 10, 6);
            button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }
    }

    private void SettingsPage_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape && !args.Handled &&
            !ThemePicker.IsDropDownOpen &&
            !CameraSourcePicker.IsDropDownOpen &&
            !VideoMicrophoneSourcePicker.IsDropDownOpen &&
            !PrintScreenModePicker.IsDropDownOpen)
        {
            args.Handled = true;
            if (!_saving) { _controller?.CloseSettings(); }
        }
    }

    private void SelectSection(string section)
    {
        _selectedSection = section;
        SettingsLayout.MaxHeight = section switch
        {
            "general" or "feedback" => 500,
            "recording" => 630,
            "updates" => 620,
            _ => 700,
        };
        GeneralPanel.Visibility = section == "general" ? Visibility.Visible : Visibility.Collapsed;
        GeneralContinuationPanel.Visibility = section == "general" ? Visibility.Visible : Visibility.Collapsed;
        RecordingPanel.Visibility = section == "recording" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPanel.Visibility = section == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPanel.Visibility = section == "updates" ? Visibility.Visible : Visibility.Collapsed;
        FeedbackPanel.Visibility = section == "feedback" ? Visibility.Visible : Visibility.Collapsed;
        GeneralNavSelection.Visibility = section == "general" ? Visibility.Visible : Visibility.Collapsed;
        RecordingNavSelection.Visibility = section == "recording" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsNavSelection.Visibility = section == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesNavSelection.Visibility = section == "updates" ? Visibility.Visible : Visibility.Collapsed;
        FeedbackNavSelection.Visibility = section == "feedback" ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavForegrounds();
        ExternalLinkMessage.IsOpen = false;
        SettingsScrollViewer.UpdateLayout();
        SettingsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void UpdateNavForegrounds()
    {
        string themeKey = new AccessibilitySettings().HighContrast
            ? "HighContrast" : ActualTheme == ElementTheme.Light ? "Light" : "Default";
        var themeResources = (ResourceDictionary)Resources.ThemeDictionaries[themeKey];
        var selected = (Brush)themeResources["SettingsSelectedNavForegroundBrush"];
        var normal = (Brush)themeResources["SettingsNavForegroundBrush"];
        GeneralNavButton.Foreground = _selectedSection == "general" ? selected : normal;
        RecordingNavButton.Foreground = _selectedSection == "recording" ? selected : normal;
        ShortcutsNavButton.Foreground = _selectedSection == "shortcuts" ? selected : normal;
        UpdatesNavButton.Foreground = _selectedSection == "updates" ? selected : normal;
        FeedbackNavButton.Foreground = _selectedSection == "feedback" ? selected : normal;
    }

    private void SetSaving(bool saving)
    {
        _saving = saving;
        CloseSettingsButton.IsEnabled = !saving;
        GeneralNavButton.IsEnabled = !saving;
        RecordingNavButton.IsEnabled = !saving;
        ShortcutsNavButton.IsEnabled = !saving;
        UpdatesNavButton.IsEnabled = !saving;
        FeedbackNavButton.IsEnabled = !saving;
        StartAtSignIn.IsEnabled = _startupEditable;
        PrintScreenEnabled.IsEnabled = !saving && _printScreenEditable;
        PrintScreenModePicker.IsEnabled = !saving && _printScreenEditable && PrintScreenEnabled.IsOn;
        VideoSystemAudio.IsEnabled = !saving;
        VideoMicrophone.IsEnabled = !saving;
        VideoMicrophoneSourcePicker.IsEnabled =
            !saving && _microphoneSourcesLoaded &&
            _availableMicrophoneSourceCount > 0 && VideoMicrophone.IsOn;
        VideoCamera.IsEnabled = !saving;
        VideoCursor.IsEnabled = !saving;
        CameraSourcePicker.IsEnabled = !saving && _cameraSourcesLoaded && _availableCameraSourceCount > 0;
        TakeCameraPhotoButton.IsEnabled = !saving && _cameraSourcesLoaded && _availableCameraSourceCount > 0;
    }

    private void TakeCameraPhoto_Click(object sender, RoutedEventArgs args)
    {
        if (!_saving && _availableCameraSourceCount > 0)
        {
            _controller?.Capture(CaptureKind.Camera);
        }
    }

    private void PreferenceToggle_Changed(object sender, RoutedEventArgs args)
    {
        PrintScreenModePicker.IsEnabled = !_saving && _printScreenEditable && PrintScreenEnabled.IsOn;
        VideoMicrophoneSourcePicker.IsEnabled =
            !_saving && _microphoneSourcesLoaded &&
            _availableMicrophoneSourceCount > 0 && VideoMicrophone.IsOn;
        UpdateMicrophoneSourceStatus();
        UpdateCameraSourceStatus();
        SaveChangedPreferences();
    }

    private void ThemePicker_Changed(object sender, SelectionChangedEventArgs args) => SaveChangedPreferences();

    private void PrintScreenModePicker_Changed(object sender, SelectionChangedEventArgs args) => SaveChangedPreferences();

    private void CameraSourcePicker_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_loadingCameraSources && _cameraSourcesLoaded)
        {
            UpdateCameraSourceStatus();
            SaveChangedPreferences();
        }
    }

    private void VideoMicrophoneSourcePicker_Changed(
        object sender, SelectionChangedEventArgs args)
    {
        if (!_loadingMicrophoneSources && _microphoneSourcesLoaded)
        {
            UpdateMicrophoneSourceStatus();
            SaveChangedPreferences();
        }
    }

    private async void SaveChangedPreferences()
    {
        if (!_configured || _controller is null)
        {
            return;
        }

        ShnappSettings preferences = ReadPreferences();
        if (_saving)
        {
            _queuedSettings = preferences;
            return;
        }
        if (preferences == _savedSettings)
        {
            return;
        }

        PreferenceMessage.IsOpen = false;
        SettingsSaveStatus.Text = "Saving changes…";
        SetSaving(true);
        try
        {
            while (preferences != _savedSettings)
            {
                await _controller.SavePreferencesAsync(preferences);
                _savedSettings = preferences;
                preferences = _queuedSettings ?? ReadPreferences();
                _queuedSettings = null;
            }

            SettingsSaveStatus.Text = "All changes saved";
        }
        catch (Exception exception)
        {
            _queuedSettings = null;
            _configured = false;
            StartAtSignIn.IsOn = _savedSettings.StartOnLogin;
            AutoCopy.IsOn = _savedSettings.AutoCopy;
            WindowShadow.IsOn = _savedSettings.WindowShadow;
            VideoSystemAudio.IsOn = _savedSettings.VideoCaptureSystemAudio;
            VideoMicrophone.IsOn = _savedSettings.VideoCaptureMicrophone;
            VideoCamera.IsOn = _savedSettings.VideoCaptureCamera;
            VideoCursor.IsOn = _savedSettings.VideoCaptureCursor;
            PrintScreenEnabled.IsOn = _savedSettings.PrintScreenEnabled;
            PrintScreenModePicker.SelectedIndex = _savedSettings.PrintScreenMode switch
            {
                CaptureKind.Window => 1,
                CaptureKind.Region => 2,
                _ => 0,
            };
            _loadingCameraSources = true;
            SelectCameraSource(_savedSettings.CameraSourceId);
            _loadingCameraSources = false;
            UpdateCameraSourceStatus();
            _loadingMicrophoneSources = true;
            SelectMicrophoneSource(_savedSettings.VideoMicrophoneSourceId);
            _loadingMicrophoneSources = false;
            UpdateMicrophoneSourceStatus();
            ThemePicker.SelectedIndex = _savedSettings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
            _configured = true;
            SettingsSaveStatus.Text = "Change not saved";
            if (ReferenceEquals(((MainWindow)App.Window).Settings, this))
            {
                PreferenceMessage.Title = "Could not save preferences";
                PreferenceMessage.Message = exception.Message + " The previous setting was restored. Try again.";
                PreferenceMessage.Severity = InfoBarSeverity.Error;
                PreferenceMessage.IsOpen = true;
            }
        }
        finally
        {
            SetSaving(false);
        }
    }

    private ShnappSettings ReadPreferences() => new()
        {
            StartOnLogin = StartAtSignIn.IsOn,
            AutoCopy = AutoCopy.IsOn,
            WindowShadow = WindowShadow.IsOn,
            VideoCaptureSystemAudio = VideoSystemAudio.IsOn,
            VideoCaptureMicrophone = VideoMicrophone.IsOn,
            VideoCaptureCamera = VideoCamera.IsOn,
            VideoCaptureCursor = VideoCursor.IsOn,
            VideoMicrophoneSourceId = SelectedMicrophoneSourceId(),
            PrintScreenEnabled = PrintScreenEnabled.IsOn,
            PrintScreenMode = PrintScreenModePicker.SelectedIndex switch
            {
                1 => CaptureKind.Window,
                2 => CaptureKind.Region,
                _ => CaptureKind.FullScreen,
            },
            CameraSourceId = SelectedCameraSourceId(),
            Theme = ThemePicker.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" },
        };

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

    private async void RestartVelopack_Click(object sender, RoutedEventArgs args)
    {
        if (_controller is null) { return; }
        RestartVelopackButton.IsEnabled = false;
        try
        {
            SetUpdateResult(await _controller.RestartAfterVelopackUpdateAsync());
        }
        finally
        {
            RestartVelopackButton.IsEnabled = true;
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
            ExternalLinkMessage.Title = "Could not open the link";
            ExternalLinkMessage.Message = address.AbsoluteUri;
            ExternalLinkMessage.Severity = InfoBarSeverity.Warning;
            ExternalLinkMessage.IsOpen = true;
        }
    }
}
