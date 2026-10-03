using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Shnapp.App.Windows;

internal static class StartupPreference
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupTaskId = "ShnappStartupTask";

    internal static async Task ApplyAsync(bool enabled, bool packaged)
    {
        if (packaged)
        {
            StartupTask task = await StartupTask.GetAsync(StartupTaskId);
            if (enabled)
            {
                StartupTaskState state = task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy
                    ? task.State : await task.RequestEnableAsync();
                if (state is not (StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy))
                {
                    throw new InvalidOperationException(
                        "Windows has disabled Shnapp at sign-in. Enable it in Windows Settings > Apps > Startup.");
                }
            }
            else if (task.State == StartupTaskState.Enabled)
            {
                task.Disable();
            }

            return;
        }

        ApplyPortable(enabled);
    }

    internal static async Task<bool> IsPackagedEnabledAsync()
    {
        StartupTask task = await StartupTask.GetAsync(StartupTaskId);
        return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }

    /// <summary>Rewrites an old portable Run path after package managers move the executable.</summary>
    internal static void ReconcilePortable(bool enabled) => ApplyPortable(enabled);

    private static void ApplyPortable(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            string executable = StartupExecutable();
            string command = $"\"{executable}\" --background";
            if (!string.Equals(key.GetValue("Shnapp") as string, command, StringComparison.Ordinal))
            {
                key.SetValue("Shnapp", command, RegistryValueKind.String);
            }
        }
        else
        {
            key.DeleteValue("Shnapp", throwOnMissingValue: false);
        }
    }

    private static string StartupExecutable()
    {
        return StartupPathResolver.Resolve(PortableUpdateStager.CurrentExecutableDirectory, Environment.ProcessPath,
            InstallationChannelDetector.Detect(packaged: false));
    }
}
