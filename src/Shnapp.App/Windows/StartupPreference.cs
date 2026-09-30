using Microsoft.Win32;

namespace Shnapp.App.Windows;

internal static class StartupPreference
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static void Apply(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Shnapp's executable location is unavailable.");
            key.SetValue("Shnapp", $"\"{executable}\" --background", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("Shnapp", throwOnMissingValue: false);
        }
    }
}
