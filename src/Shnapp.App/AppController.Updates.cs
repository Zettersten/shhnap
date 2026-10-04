using System.Reflection;
using System.Diagnostics;
using System.Net;
using Velopack;
using Velopack.Sources;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Shnapp.App.Windows;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Services.Store;

namespace Shnapp.App;

internal sealed partial class AppController
{
    private readonly ReleaseUpdateChecker _releaseChecker;
    private readonly PortableUpdateStager _portableStager = new();
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private DispatcherQueueTimer? _storeUpdateTimer;
    private bool _storeRestartPending;
    private UpdateCheckResult? _lastUpdateResult;
    private VelopackCheckSchedule? _velopackCheckSchedule;
    private const string VelopackRepository = "https://github.com/Zettersten/shhnap";

    private static bool HasPackageIdentity
    {
        get
        {
            try { return Package.Current.Id is not null; }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                return false;
            }
        }
    }

    private static Version InstalledVersion
    {
        get
        {
            if (HasPackageIdentity)
            {
                PackageVersion version = Package.Current.Id.Version;
                return new Version(version.Major, version.Minor, version.Build, version.Revision);
            }

            return typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
        }
    }

    private static string InstalledVersionText
    {
        get
        {
            Version version = InstalledVersion;
            return version.Revision > 0 ? version.ToString(4) : version.ToString(3);
        }
    }

    internal async Task<UpdateCheckResult> CheckForUpdatesAsync(bool manual)
    {
        await _updateGate.WaitAsync(_lifetime.Token);
        try
        {
            bool packaged = HasPackageIdentity;
            InstallationChannel channel = InstallationChannelDetector.Detect(packaged);
            if (packaged && !manual && !_options.Isolated)
            {
                StartStoreUpdateTimer();
            }

            UpdateCheckResult result;
            if (packaged)
            {
                result = await CheckStoreAsync(manual);
            }
            else if (channel == InstallationChannel.Velopack)
            {
                result = await CheckVelopackAsync(manual);
            }
            else
            {
                ReleaseCheckResult release = await _releaseChecker.CheckAsync(InstalledVersion, manual, _lifetime.Token);
                result = channel switch
                {
                    InstallationChannel.DirectZip => await CheckDirectZipAsync(release, manual),
                    InstallationChannel.Chocolatey when !PackageManagerUpdateRunner.CanStartChocolatey() =>
                        new UpdateCheckResult("Chocolatey owns this installation, but its standard protected executable was not found. Upgrade Shnapp from an Administrator Chocolatey terminal.",
                            release.UpdateAvailable, release.ReleasePage, release.LatestTag, release.IsError),
                    InstallationChannel.Scoop or InstallationChannel.Chocolatey or InstallationChannel.WinGet =>
                        new UpdateCheckResult(release.UpdateAvailable
                            ? $"Shnapp {release.LatestTag} is published. {channel} may offer it after its catalog updates. Choose Update with {channel} to check and install through that manager."
                            : $"{channel} owns this installation. Use Update with {channel} to check its catalog.",
                            release.UpdateAvailable, release.ReleasePage, release.LatestTag, release.IsError,
                            ManagerChannel: channel),
                    _ => new UpdateCheckResult(release.UpdateAvailable
                            ? $"Shnapp {release.LatestTag} is published, but this installation's source could not be verified. Update it from its original source."
                            : "This installation's source could not be verified. Use its original source for updates.",
                        release.UpdateAvailable, release.ReleasePage, release.LatestTag, release.IsError),
                };
            }

            UpdateCheckResult? previous = _lastUpdateResult;
            _lastUpdateResult = result;
            _window.Settings?.SetUpdateResult(result);
            _page.SetUpdateAvailability(
                result.UpdateAvailable || result.StoreInstallAvailable || result.PortableRestartRequired ||
                result.VelopackRestartRequired || result.StoreRestartRequired,
                restartRequired: result.PortableRestartRequired || result.VelopackRestartRequired || result.StoreRestartRequired,
                checkFailed: result.IsError);
            if (result.UpdateAvailable || result.PortableRestartRequired || result.VelopackRestartRequired ||
                result.StoreRestartRequired)
            {
                if (result.PortableRestartRequired || result.VelopackRestartRequired || result.StoreRestartRequired ||
                    previous?.UpdateAvailable != true || previous.LatestTag != result.LatestTag)
                {
                    _page.ShowAvailableUpdate(result.Message,
                        restartRequired: result.PortableRestartRequired || result.VelopackRestartRequired ||
                            result.StoreRestartRequired);
                }
                if (!manual && !packaged && result.LatestTag is { } tag &&
                    await _releaseChecker.MarkNotifiedAsync(tag, _lifetime.Token))
                {
                    _tray.ShowUpdateNotification(tag);
                }
            }

            return result;
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private async Task<UpdateCheckResult> CheckVelopackAsync(bool manual)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(VelopackRepository, null, false));
            if (!manager.IsInstalled)
            {
                return new UpdateCheckResult("This copy is not a complete Velopack installation. Repair it from the Shnapp download page.",
                    IsError: true);
            }

            if (manager.UpdatePendingRestart is { } pending)
            {
                string pendingTag = "v" + pending.Version;
                return new UpdateCheckResult($"Shnapp {pendingTag} is ready. Restart now or reopen Shnapp to install it. Your library stays in your user folder.",
                    ReleasePage: VelopackReleasePage(pendingTag), LatestTag: pendingTag,
                    VelopackRestartRequired: true);
            }

            VelopackCheckSchedule schedule = _velopackCheckSchedule ??= new VelopackCheckSchedule(_options.DataRoot);
            (bool shouldCheck, DateTimeOffset? retryAfterUtc) =
                await schedule.ShouldCheckAsync(manual, _lifetime.Token);
            if (!shouldCheck)
            {
                if (retryAfterUtc is not null)
                {
                    return new UpdateCheckResult(
                        $"GitHub is temporarily limiting update checks. Try again after {retryAfterUtc.Value.ToLocalTime():t}.",
                        IsError: true);
                }

                return _lastUpdateResult ?? new UpdateCheckResult(
                    "Shnapp checked for updates recently. Choose Check for updates to check now.");
            }

            UpdateInfo? update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                await schedule.RecordSuccessAsync(_lifetime.Token);
                return new UpdateCheckResult("Shnapp is up to date. Velopack checks stable releases in the background.");
            }

            string tag = "v" + update.TargetFullRelease.Version;
            Uri releasePage = VelopackReleasePage(tag);
            if (_options.Isolated)
            {
                await schedule.RecordSuccessAsync(_lifetime.Token);
                return new UpdateCheckResult($"Shnapp {tag} is available. Automatic updates are disabled for this verification copy.",
                    UpdateAvailable: true, ReleasePage: releasePage, LatestTag: tag);
            }

            _window.Settings?.SetUpdateResult(new UpdateCheckResult($"Preparing Shnapp {tag} with Velopack…"));
            try
            {
                await manager.DownloadUpdatesAsync(update, cancelToken: _lifetime.Token);
                await schedule.RecordSuccessAsync(_lifetime.Token);
                return new UpdateCheckResult($"Shnapp {tag} is ready. Restart now or reopen Shnapp to install it. Your library stays in your user folder.",
                    ReleasePage: releasePage, LatestTag: tag, VelopackRestartRequired: true);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await schedule.RecordFailureAsync(IsVelopackRateLimit(exception), _lifetime.Token);
                Debug.WriteLine("Velopack download failed: " + exception);
                return new UpdateCheckResult($"Shnapp {tag} is available, but the download could not be prepared. Choose Check for updates to retry.",
                    UpdateAvailable: true, ReleasePage: releasePage, LatestTag: tag, IsError: true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (_velopackCheckSchedule is not null)
            {
                await _velopackCheckSchedule.RecordFailureAsync(IsVelopackRateLimit(exception), _lifetime.Token);
            }
            Debug.WriteLine("Velopack update check failed: " + exception);
            return new UpdateCheckResult("Could not check Velopack releases right now. Your current Shnapp copy still works.",
                IsError: true);
        }
    }

    private static bool IsVelopackRateLimit(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } ||
                current.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Uri VelopackReleasePage(string tag) =>
        new($"{VelopackRepository}/releases/tag/{Uri.EscapeDataString(tag)}");

    internal async Task<UpdateCheckResult> RestartAfterVelopackUpdateAsync()
    {
        await _updateGate.WaitAsync(_lifetime.Token);
        try
        {
            if (_options.Isolated || InstallationChannelDetector.Detect(HasPackageIdentity) != InstallationChannel.Velopack)
            {
                return new UpdateCheckResult("This copy cannot restart through Velopack.", IsError: true);
            }
            if (_activeCapture is not null || _dialogOpen)
            {
                return new UpdateCheckResult("Finish the current capture or dialog before restarting.",
                    VelopackRestartRequired: true);
            }

            var manager = new UpdateManager(new GithubSource(VelopackRepository, null, false));
            if (manager.UpdatePendingRestart is not { } pending)
            {
                return new UpdateCheckResult("No Velopack update is ready to install.");
            }

            _saveTimer.Stop();
            _page.CommitText();
            await SaveCurrentAsync();
            manager.WaitExitThenApplyUpdates(pending, restart: true);
            await QuitAsync();
            return new UpdateCheckResult("Shnapp is restarting to install the update.");
        }
        catch (Exception exception)
        {
            Debug.WriteLine("Velopack restart failed: " + exception);
            return new UpdateCheckResult("Shnapp could not restart for this update. Save your work, then quit and reopen the app.",
                IsError: true, VelopackRestartRequired: true);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private async Task<UpdateCheckResult> CheckDirectZipAsync(ReleaseCheckResult release, bool manual)
    {
        if (!release.UpdateAvailable || release.LatestTag is null)
        {
            return new UpdateCheckResult(release.Message, release.UpdateAvailable,
                release.ReleasePage, release.LatestTag, release.IsError);
        }

        string? root = PortableUpdateStager.FindCurrentInstallRoot();
        if (root is null)
        {
            return new UpdateCheckResult(
                $"Shnapp {release.LatestTag} is available. This copy predates automatic updates; download and extract the new ZIP once to enable them.",
                UpdateAvailable: true, ReleasePage: release.ReleasePage, LatestTag: release.LatestTag);
        }

        if (_options.Isolated)
        {
            return new UpdateCheckResult("Automatic updates are disabled for this verification copy.",
                release.UpdateAvailable, release.ReleasePage, release.LatestTag);
        }

        if (PortableUpdateStager.IsSelectedVersion(root, release.LatestTag))
        {
            return new UpdateCheckResult(
                $"Shnapp {release.LatestTag} is ready. Restart Shnapp to use the new version.",
                ReleasePage: release.ReleasePage, LatestTag: release.LatestTag,
                PortableRestartRequired: true);
        }
        if (Directory.Exists(Path.Combine(root, "versions", release.LatestTag)))
        {
            return new UpdateCheckResult(
                $"Shnapp {release.LatestTag} was prepared but could not be selected. This copy is still running the previous version. Download a fresh ZIP or wait for the next release.",
                UpdateAvailable: true, ReleasePage: release.ReleasePage, LatestTag: release.LatestTag,
                IsError: true);
        }

        if (release.Asset is null)
        {
            return new UpdateCheckResult(
                $"Shnapp {release.LatestTag} is available, but its download could not be verified for automatic installation. View the release to update manually.",
                UpdateAvailable: true, ReleasePage: release.ReleasePage, LatestTag: release.LatestTag,
                IsError: true);
        }

        if (!await _releaseChecker.CanRetryDownloadAsync(release.LatestTag, manual, _lifetime.Token))
        {
            return new UpdateCheckResult(
                $"Shnapp {release.LatestTag} is available, but its last download failed. Shnapp will retry tomorrow; choose Check for updates to retry now.",
                UpdateAvailable: true, ReleasePage: release.ReleasePage, LatestTag: release.LatestTag,
                IsError: true);
        }

        _window.Settings?.SetUpdateResult(new UpdateCheckResult(
            $"Preparing the verified Shnapp {release.LatestTag} update in the background…"));
        try
        {
            await _portableStager.StageAndActivateAsync(release.Asset, root, _lifetime.Token);
            await _releaseChecker.RecordDownloadResultAsync(release.LatestTag, succeeded: true, _lifetime.Token);
            return new UpdateCheckResult(
                $"Shnapp {release.LatestTag} is ready. Restart Shnapp to use the new version.",
                ReleasePage: release.ReleasePage, LatestTag: release.LatestTag,
                PortableRestartRequired: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            HttpRequestException or InvalidDataException or InvalidOperationException or
            System.Security.SecurityException)
        {
            await _releaseChecker.RecordDownloadResultAsync(release.LatestTag, succeeded: false, _lifetime.Token);
            return new UpdateCheckResult(
                $"Shnapp {release.LatestTag} is available, but automatic installation could not be prepared. View the release to update manually.",
                UpdateAvailable: true, ReleasePage: release.ReleasePage, LatestTag: release.LatestTag,
                IsError: true);
        }
    }

    internal async Task<UpdateCheckResult> RestartAfterPortableUpdateAsync()
    {
        string? root = PortableUpdateStager.FindCurrentInstallRoot();
        if (InstallationChannelDetector.Detect(HasPackageIdentity) != InstallationChannel.DirectZip || root is null)
        {
            return new UpdateCheckResult("This installation cannot restart through the direct ZIP updater.", IsError: true);
        }

        try
        {
            if (_activeCapture is not null || _dialogOpen)
            {
                return new UpdateCheckResult("Finish the current capture or dialog before restarting.",
                    PortableRestartRequired: true);
            }

            _saveTimer.Stop();
            _page.CommitText();
            await SaveCurrentAsync();
            string launcher = Path.Combine(root, "Shnapp.exe");
            using Process? restart = Process.Start(new ProcessStartInfo(launcher)
            {
                UseShellExecute = false,
                ArgumentList = { "--relaunch-after", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            });
            if (restart is null)
            {
                throw new InvalidOperationException("The Shnapp launcher did not start.");
            }

            await QuitAsync();
            return new UpdateCheckResult("Shnapp is restarting.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new UpdateCheckResult("Shnapp could not restart for the update. Save your work, then quit and reopen it.",
                IsError: true, PortableRestartRequired: true);
        }
    }

    internal async Task<UpdateCheckResult> UpdateWithPackageManagerAsync(InstallationChannel channel)
    {
        if (_options.Isolated || channel != InstallationChannelDetector.Detect(HasPackageIdentity) ||
            channel is not (InstallationChannel.Scoop or InstallationChannel.WinGet or InstallationChannel.Chocolatey))
        {
            return new UpdateCheckResult("Shnapp cannot verify this package manager installation.", IsError: true);
        }

        if (_activeCapture is not null || _dialogOpen)
        {
            return new UpdateCheckResult("Finish the current capture or dialog before updating.",
                ManagerChannel: channel);
        }

        try
        {
            _saveTimer.Stop();
            _page.CommitText();
            await SaveCurrentAsync();
            PackageManagerUpdateRunner.StartAfterExit(channel, Environment.ProcessId, _options.DataRoot);
            await QuitAsync();
            return new UpdateCheckResult($"{channel} is checking for an update.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception or
            System.Security.SecurityException)
        {
            return new UpdateCheckResult($"Shnapp could not start the {channel} update. Your open work is still available.",
                IsError: true, ManagerChannel: channel);
        }
    }

    private void StartStoreUpdateTimer()
    {
        if (_storeUpdateTimer is not null)
        {
            return;
        }

        // Store throttles checks itself; six hours also keeps a long-running tray copy current.
        _storeUpdateTimer = App.DispatcherQueue.CreateTimer();
        _storeUpdateTimer.IsRepeating = true;
        _storeUpdateTimer.Interval = TimeSpan.FromHours(6);
        _storeUpdateTimer.Tick += (_, _) => Run(() => CheckForUpdatesAsync(manual: false));
        _storeUpdateTimer.Start();
    }

    private static StoreContext CreateStoreContext()
    {
        StoreContext context = StoreContext.GetDefault();
        WinRT.Interop.InitializeWithWindow.Initialize(context, App.WindowHandle);
        return context;
    }

    private async Task<UpdateCheckResult> CheckStoreAsync(bool manual)
    {
        if (_storeRestartPending)
        {
            return new UpdateCheckResult("Microsoft Store installed an update. Restart Shnapp to use it.",
                StoreRestartRequired: true);
        }

        try
        {
            StoreContext context = CreateStoreContext();
            IReadOnlyList<StorePackageUpdate> updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            if (updates.Count == 0)
            {
                return new UpdateCheckResult("No Microsoft Store update was found for this package. Sideloaded copies should check their original package source.");
            }

            if (!manual && !_options.Isolated && context.CanSilentlyDownloadStorePackageUpdates)
            {
                StorePackageUpdateResult download = await context.TrySilentDownloadStorePackageUpdatesAsync(updates);
                if (download.OverallState == StorePackageUpdateState.Completed)
                {
                    return new UpdateCheckResult("A Microsoft Store update is downloaded. Choose Update now when you are ready. Store automatic updates may also install it later.",
                        UpdateAvailable: true, StoreInstallAvailable: true);
                }
            }

            return new UpdateCheckResult("A Microsoft Store update is available. Choose Update now to install it, or open Store updates.",
                UpdateAvailable: true, StoreInstallAvailable: true);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException)
        {
            return new UpdateCheckResult("Microsoft Store could not check this package right now. If it was sideloaded, use the original package source.", IsError: true);
        }
    }

    internal async Task<UpdateCheckResult> InstallStoreUpdateAsync()
    {
        await _updateGate.WaitAsync(_lifetime.Token);
        try
        {
            if (!HasPackageIdentity)
            {
                return new UpdateCheckResult("This copy is not a Microsoft Store package.", IsError: true);
            }

            if (_activeCapture is not null || _dialogOpen)
            {
                return new UpdateCheckResult("Finish the current capture or dialog before installing the update.",
                    UpdateAvailable: true, StoreInstallAvailable: true);
            }

            StoreContext context = CreateStoreContext();
            IReadOnlyList<StorePackageUpdate> updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            if (updates.Count == 0)
            {
                return PublishStoreResult(new UpdateCheckResult("No Microsoft Store update is available for this package right now."));
            }

            try
            {
                _saveTimer.Stop();
                _page.CommitText();
                await SaveCurrentAsync();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                InvalidOperationException)
            {
                return PublishStoreResult(new UpdateCheckResult(
                    "Could not save your open capture. The Store update was not started; save your work and try again.",
                    UpdateAvailable: true, IsError: true, StoreInstallAvailable: true));
            }

            // An explicit Update now action uses the Store's consent dialog. Automatic
            // checks may predownload, but only a user action asks Store to install now.
            StorePackageUpdateResult installed =
                await context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
            return PublishStoreInstallResult(installed);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or
            InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            return PublishStoreResult(new UpdateCheckResult(
                "Microsoft Store could not install the update. Try Store updates instead.",
                IsError: true, StoreInstallAvailable: true));
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private UpdateCheckResult PublishStoreInstallResult(StorePackageUpdateResult installed)
    {
        UpdateCheckResult result = installed.OverallState switch
        {
            StorePackageUpdateState.Completed => new UpdateCheckResult(
                "Microsoft Store installed the update. Restart Shnapp to use it.", StoreRestartRequired: true),
            StorePackageUpdateState.Canceled => new UpdateCheckResult(
                "The Microsoft Store update was canceled. You can try again when ready.",
                UpdateAvailable: true, StoreInstallAvailable: true),
            StorePackageUpdateState.ErrorLowBattery => new UpdateCheckResult(
                "The battery is too low to install this Store update. Try again after charging.",
                UpdateAvailable: true, StoreInstallAvailable: true, IsError: true),
            StorePackageUpdateState.ErrorWiFiRequired or StorePackageUpdateState.ErrorWiFiRecommended =>
                new UpdateCheckResult("Microsoft Store needs a suitable network connection for this update.",
                    UpdateAvailable: true, StoreInstallAvailable: true, IsError: true),
            _ => new UpdateCheckResult("Microsoft Store could not finish installing this update. Open Store updates to retry.",
                UpdateAvailable: true, StoreInstallAvailable: true, IsError: true),
        };
        return PublishStoreResult(result);
    }

    private UpdateCheckResult PublishStoreResult(UpdateCheckResult result)
    {
        if (result.StoreRestartRequired)
        {
            _storeRestartPending = true;
        }

        _lastUpdateResult = result;
        _window.Settings?.SetUpdateResult(result);
        _page.SetUpdateAvailability(
            result.UpdateAvailable || result.StoreInstallAvailable || result.PortableRestartRequired || result.StoreRestartRequired,
            restartRequired: result.PortableRestartRequired || result.StoreRestartRequired,
            checkFailed: result.IsError);
        if (result.StoreRestartRequired)
        {
            _page.ShowAvailableUpdate(result.Message, restartRequired: true);
        }
        return result;
    }

    internal async Task<UpdateCheckResult> RestartAfterStoreUpdateAsync()
    {
        try
        {
            _saveTimer.Stop();
            _page.CommitText();
            await SaveCurrentAsync();
            AppRestartFailureReason reason = Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
            return new UpdateCheckResult($"Windows could not restart Shnapp ({reason}). Quit and reopen it to use the update.",
                IsError: true, StoreRestartRequired: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return new UpdateCheckResult("Shnapp could not save before restarting. Your open capture is still available; save it and try again.",
                IsError: true, StoreRestartRequired: true);
        }
    }
}

internal sealed record UpdateCheckResult(string Message, bool UpdateAvailable = false,
    Uri? ReleasePage = null, string? LatestTag = null, bool IsError = false,
    bool StoreInstallAvailable = false, bool StoreRestartRequired = false,
    bool PortableRestartRequired = false, InstallationChannel ManagerChannel = InstallationChannel.Unknown,
    bool VelopackRestartRequired = false);
