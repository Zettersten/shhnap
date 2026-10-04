namespace Shnapp.App.Windows;

/// <summary>Chooses a sign-in path that survives a package manager replacing its version directory.</summary>
internal static class StartupPathResolver
{
    internal static string Resolve(string appDirectory, string? processPath, InstallationChannel channel)
    {
        // Scoop launches through apps/shnapp/current, which is a junction. The direct
        // updater intentionally rejects junctions, so discover Scoop's stable path first.
        if (channel == InstallationChannel.Scoop)
        {
            return FindScoopCurrentExecutable(appDirectory) ?? throw new InvalidOperationException(
                "Shnapp could not find Scoop's current launcher. Repair the Scoop installation before enabling start at sign-in.");
        }

        if (channel == InstallationChannel.Velopack)
        {
            string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
            string? installRoot = Path.GetDirectoryName(current);
            string? launcher = installRoot is null ? null : Path.Combine(installRoot, "Shnapp.exe");
            if (!Path.GetFileName(current).Equals("current", StringComparison.OrdinalIgnoreCase) ||
                launcher is null || !File.Exists(launcher) ||
                !File.Exists(Path.Combine(current, "sq.version")))
            {
                throw new InvalidOperationException(
                    "Shnapp could not find its Velopack launcher. Repair the installation before enabling start at sign-in.");
            }

            return launcher;
        }

        if (PortableUpdateStager.FindInstallRoot(appDirectory) is { } root)
        {
            return Path.Combine(root, "Shnapp.exe");
        }

        return processPath ?? throw new InvalidOperationException("Shnapp's executable location is unavailable.");
    }

    private static string? FindScoopCurrentExecutable(string appDirectory)
    {
        try
        {
            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
            string? parent = Path.GetDirectoryName(directory);
            if (parent is not null &&
                Path.GetFileName(parent).Equals("versions", StringComparison.OrdinalIgnoreCase) &&
                IsVersionName(Path.GetFileName(directory)))
            {
                directory = Path.GetDirectoryName(parent)!;
            }

            string? app = Path.GetDirectoryName(directory);
            if (app is null || !Path.GetFileName(app).Equals("shnapp", StringComparison.OrdinalIgnoreCase) ||
                !IsVersionName(Path.GetFileName(directory), allowCurrent: true))
            {
                return null;
            }

            string? apps = Path.GetDirectoryName(app);
            if (apps is null || !Path.GetFileName(apps).Equals("apps", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string executable = Path.Combine(app, "current", "Shnapp.exe");
            return File.Exists(executable) ? executable : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsVersionName(string value, bool allowCurrent = false)
    {
        if (allowCurrent && value.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string number = value.StartsWith('v') ? value[1..] : value;
        return Version.TryParse(number, out Version? version) && version.Build >= 0 && version.Revision < 0;
    }
}
