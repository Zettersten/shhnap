using System.Security;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace Shnapp.App.Windows;

internal enum InstallationChannel
{
    Unknown,
    DirectZip,
    Velopack,
    Scoop,
    Chocolatey,
    WinGet,
    Store,
}

internal sealed record WinGetPortableRegistration(string PackageIdentifier, string? TargetPath, string? InstallLocation);

internal sealed record InstallationChannelRoots(
    IReadOnlyCollection<string> Scoop,
    IReadOnlyCollection<string> Chocolatey,
    IReadOnlyCollection<string> WinGetPackages,
    IReadOnlyCollection<string> WinGetLinks)
{
    internal static InstallationChannelRoots Current()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new(
            Roots(Environment.GetEnvironmentVariable("SCOOP"), Path.Combine(profile, "scoop"),
                Environment.GetEnvironmentVariable("SCOOP_GLOBAL"), Path.Combine(programData, "scoop")),
            Roots(Environment.GetEnvironmentVariable("ChocolateyInstall"), Path.Combine(programData, "chocolatey")),
            Roots(Path.Combine(local, "Microsoft", "WinGet", "Packages"),
                Path.Combine(programFiles, "WinGet", "Packages")),
            Roots(Path.Combine(local, "Microsoft", "WinGet", "Links"),
                Path.Combine(programFiles, "WinGet", "Links")));
    }

    private static string[] Roots(params string?[] values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
}

/// <summary>
/// Identifies who owns this executable. A package manager must update its own install;
/// an unrecognized or conflicting install must not be replaced by the direct ZIP updater.
/// </summary>
internal static class InstallationChannelDetector
{
    internal const string VelopackX64Id = "ErikZettersten.Shnapp.x64";
    internal const string VelopackArm64Id = "ErikZettersten.Shnapp.arm64";
    private const string VelopackRepository = "https://github.com/Zettersten/shhnap";
    internal const string MarkerFileName = ".shnapp-install-source";
    private const string PackageIdentifier = "Zettersten.Shnapp";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly Regex ScoopPackagePath = new(
        @"\\apps\\shnapp\\(?:current|v?[0-9]+\.[0-9]+\.[0-9]+)\\",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static InstallationChannel Detect(bool packaged)
    {
        if (packaged)
        {
            // Package identity includes Store and sideloaded MSIX. Neither may be ZIP-updated.
            return InstallationChannel.Store;
        }

        if (!OperatingSystem.IsWindows())
        {
            return InstallationChannel.Unknown;
        }

        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return InstallationChannel.Unknown;
        }

        string? marker;
        try
        {
            marker = ReadMarkerForExecutable(executable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return InstallationChannel.Unknown;
        }

        bool velopackInstalled = IsVelopackInstall();
        bool registrationsAvailable = TryReadWinGetRegistrations(out IReadOnlyList<WinGetPortableRegistration> registrations);
        return registrationsAvailable || velopackInstalled
            ? Classify(executable, marker, registrations, InstallationChannelRoots.Current(), velopackInstalled)
            : InstallationChannel.Unknown;
    }

    private static bool IsVelopackInstall()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(VelopackRepository, null, false));
            return manager.IsInstalled &&
                (string.Equals(manager.AppId, VelopackX64Id, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(manager.AppId, VelopackArm64Id, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or SecurityException or
            System.Text.Json.JsonException or FormatException)
        {
            return false;
        }
    }

    internal static string? ReadMarkerForExecutable(string executable)
    {
        string executableDirectory = Path.GetDirectoryName(executable)!;
        string? marker = ReadMarker(executableDirectory);
        // The direct updater runs each release from root/versions/<tag>/Shnapp.exe.
        // Keep the original install owner's marker at root across version changes.
        DirectoryInfo? versionDirectory = Directory.GetParent(executableDirectory);
        if (versionDirectory?.Name.Equals("versions", StringComparison.OrdinalIgnoreCase) == true &&
            versionDirectory.Parent is { } installRoot)
        {
            string? rootMarker = ReadMarker(installRoot.FullName);
            if (marker is not null && rootMarker is not null && marker != rootMarker)
            {
                return string.Empty;
            }

            marker ??= rootMarker;
        }

        return marker;
    }

    private static string? ReadMarker(string directory)
    {
        string path = Path.Combine(directory, MarkerFileName);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    internal static InstallationChannel Classify(string executablePath, string? marker,
        IEnumerable<WinGetPortableRegistration> registrations, InstallationChannelRoots roots,
        bool velopackInstalled = false)
    {
        if (!TryFullPath(executablePath, out string? executable) || executable is null ||
            !Path.GetFileName(executable).Equals("Shnapp.exe", StringComparison.OrdinalIgnoreCase))
        {
            return InstallationChannel.Unknown;
        }

        string[] scoopRoots = roots.Scoop.Select(root => Path.Combine(root, "apps")).ToArray();
        string[] chocolateyRoots = roots.Chocolatey.Select(root => Path.Combine(root, "lib")).ToArray();
        bool inScoop = IsInShnappPackage(executable, scoopRoots, "shnapp");
        bool inChocolatey = IsInShnappPackage(executable, chocolateyRoots, "shnapp") &&
            PathContains(executable, @"\tools\app\");
        bool inWinGet = roots.WinGetPackages.Any(root => IsInside(executable, root)) ||
            roots.WinGetLinks.Any(root => IsInside(executable, root));
        bool looksScoop = PathContains(executable, @"\scoop\apps\") ||
            ScoopPackagePath.IsMatch(executable) ||
            scoopRoots.Any(root => IsInside(executable, root));
        bool looksChocolatey = PathContains(executable, @"\chocolatey\lib\") ||
            PathContains(executable, @"\lib\shnapp\tools\app\") ||
            chocolateyRoots.Any(root => IsInside(executable, root));
        bool looksWinGet = PathContains(executable, @"\winget\packages\") ||
            PathContains(executable, @"\winget\links\") || inWinGet;
        bool suspiciousManagerPath = looksScoop || looksChocolatey || looksWinGet;

        bool winGetRegistration = registrations.Any(registration =>
            registration.PackageIdentifier.Equals(PackageIdentifier, StringComparison.OrdinalIgnoreCase) &&
            (SamePath(executable, registration.TargetPath) ||
             MatchesWinGetLocation(executable, registration.InstallLocation)));

        if (velopackInstalled)
        {
            return marker is null && !suspiciousManagerPath && !winGetRegistration
                ? InstallationChannel.Velopack : InstallationChannel.Unknown;
        }

        return marker switch
        {
            "scoop:shnapp" when !winGetRegistration && !looksChocolatey && !looksWinGet => InstallationChannel.Scoop,
            "chocolatey:shnapp" when !winGetRegistration && !looksScoop && !looksWinGet => InstallationChannel.Chocolatey,
            "direct-zip:shnapp" when !winGetRegistration && !suspiciousManagerPath => InstallationChannel.DirectZip,
            null when winGetRegistration && !looksScoop && !looksChocolatey => InstallationChannel.WinGet,
            null when inScoop && !inChocolatey && !inWinGet && !winGetRegistration => InstallationChannel.Scoop,
            null when inChocolatey && !inScoop && !inWinGet && !winGetRegistration => InstallationChannel.Chocolatey,
            null when !suspiciousManagerPath && !winGetRegistration => InstallationChannel.DirectZip,
            _ => InstallationChannel.Unknown,
        };
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadWinGetRegistrations(out IReadOnlyList<WinGetPortableRegistration> registrations)
    {
        var found = new List<WinGetPortableRegistration>();
        try
        {
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? uninstall = baseKey.OpenSubKey(UninstallKey);
                    if (uninstall is null) { continue; }

                    foreach (string name in uninstall.GetSubKeyNames())
                    {
                        using RegistryKey? entry = uninstall.OpenSubKey(name);
                        if (entry?.GetValue("WinGetPackageIdentifier") is not string packageId ||
                            !packageId.Equals(PackageIdentifier, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        found.Add(new(packageId, entry.GetValue("PortableTargetFullPath") as string,
                            entry.GetValue("InstallLocation") as string));
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            registrations = [];
            return false;
        }

        registrations = found;
        return true;
    }

    private static bool IsInShnappPackage(string executable, IEnumerable<string> appsRoots, string name) =>
        appsRoots.Any(root => IsInside(executable, Path.Combine(root, name)));

    private static bool PathContains(string path, string segment) =>
        path.Replace('/', '\\').Contains(segment, StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string path, string root)
    {
        if (!TryFullPath(root, out string? fullRoot) || fullRoot is null) { return false; }
        return path.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string path, string? candidate) =>
        TryFullPath(candidate, out string? fullCandidate) &&
        string.Equals(path, fullCandidate, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesWinGetLocation(string executable, string? location)
    {
        if (!TryFullPath(location, out string? root) || root is null)
        {
            return false;
        }

        if (SamePath(executable, Path.Combine(root, "Shnapp.exe")))
        {
            return true;
        }

        // A misplaced in-app update under a WinGet root remains WinGet-owned.
        string relative = Path.GetRelativePath(root, executable);
        string[] segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 &&
            segments[0].Equals("versions", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("Shnapp.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFullPath(string? path, out string? full)
    {
        full = null;
        if (string.IsNullOrWhiteSpace(path)) { return false; }
        try
        {
            if (!Path.IsPathFullyQualified(path)) { return false; }
            full = Path.GetFullPath(path);
            return Path.IsPathFullyQualified(full);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
