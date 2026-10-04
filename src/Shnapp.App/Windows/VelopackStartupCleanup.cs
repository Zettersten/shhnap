using System.Security;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Shnapp.App.Windows;

/// <summary>Removes only this Velopack install's sign-in entry before it is uninstalled.</summary>
internal static class VelopackStartupCleanup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [SupportedOSPlatform("windows")]
    internal static void RemoveCurrentInstallRunEntry(string? appId, string? installRoot)
    {
        string? launcher = ExpectedLauncherForHook(Environment.ProcessPath, appId, installRoot);
        if (launcher is null) { return; }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is not null && IsOwnedRunCommand(key.GetValue("Shnapp") as string, launcher))
            {
                key.DeleteValue("Shnapp", throwOnMissingValue: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // A sign-in preference must not abort the package uninstall.
        }
    }

    internal static string? ExpectedLauncherForHook(string? executablePath, string? appId, string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath) ||
            string.IsNullOrWhiteSpace(installRoot) || !Path.IsPathFullyQualified(installRoot) ||
            !(string.Equals(appId, InstallationChannelDetector.VelopackX64Id, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(appId, InstallationChannelDetector.VelopackArm64Id, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            string executable = Path.GetFullPath(executablePath);
            string rootPath = Path.GetFullPath(installRoot);
            string? current = Path.GetDirectoryName(executable);
            if (!Path.GetFileName(executable).Equals("Shnapp.exe", StringComparison.OrdinalIgnoreCase) ||
                current is null || !string.Equals(current,
                    Path.Combine(rootPath, "current"), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Path.Combine(rootPath, "Shnapp.exe");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static bool IsOwnedRunCommand(string? command, string launcher) =>
        string.Equals(command, $"\"{launcher}\" --background", StringComparison.OrdinalIgnoreCase);
}
