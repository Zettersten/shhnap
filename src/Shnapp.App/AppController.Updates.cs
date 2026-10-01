using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using Shnapp.App.Windows;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace Shnapp.App;

internal sealed partial class AppController
{
    private readonly ReleaseUpdateChecker _releaseChecker;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private UpdateCheckResult? _lastUpdateResult;

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
            UpdateCheckResult result;
            if (HasPackageIdentity)
            {
                result = manual ? await CheckStoreAsync() :
                    new UpdateCheckResult("Microsoft Store manages updates for Store-installed copies. Sideloaded packages should be updated from their package source.");
            }
            else
            {
                ReleaseCheckResult release = await _releaseChecker.CheckAsync(InstalledVersion, manual, _lifetime.Token);
                result = new UpdateCheckResult(release.Message, release.UpdateAvailable,
                    release.ReleasePage, release.LatestTag, release.IsError);
            }

            UpdateCheckResult? previous = _lastUpdateResult;
            _lastUpdateResult = result;
            _window.Settings?.SetUpdateResult(result);
            if (result.UpdateAvailable)
            {
                if (previous?.LatestTag != result.LatestTag)
                {
                    _page.ShowAvailableUpdate(result.Message);
                }
                if (!manual && !HasPackageIdentity && result.LatestTag is { } tag &&
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

    private static async Task<UpdateCheckResult> CheckStoreAsync()
    {
        try
        {
            StoreContext context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(context, App.WindowHandle);
            IReadOnlyList<StorePackageUpdate> updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            return updates.Count > 0
                ? new UpdateCheckResult("A Microsoft Store package update is available. Open Store updates to review it.", UpdateAvailable: true)
                : new UpdateCheckResult("No Microsoft Store update was found for this package. Sideloaded copies should check their original package source.");
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException)
        {
            return new UpdateCheckResult("Microsoft Store could not check this package right now. If it was sideloaded, use the original package source.", IsError: true);
        }
    }
}

internal sealed record UpdateCheckResult(string Message, bool UpdateAvailable = false,
    Uri? ReleasePage = null, string? LatestTag = null, bool IsError = false);
