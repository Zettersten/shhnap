using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Shnapp.App.Windows;

/// <summary>Hands upgrades back to the package manager after Shnapp exits.</summary>
internal static class PackageManagerUpdateRunner
{
    private const string StatusFile = "manager-update-result.json";

    internal static void StartAfterExit(InstallationChannel channel, int processId, string dataRoot)
    {
        if (channel is not (InstallationChannel.Scoop or InstallationChannel.WinGet or InstallationChannel.Chocolatey))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        Directory.CreateDirectory(dataRoot);
        if (channel == InstallationChannel.Chocolatey && !CanStartChocolatey())
        {
            throw new InvalidOperationException(
                "Chocolatey's standard protected installation could not be verified. Update from an Administrator terminal instead.");
        }
        string status = Path.Combine(dataRoot, StatusFile);
        string script = BuildScript(channel, processId, status);
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            // The watcher stays unelevated so writing the result into the user's
            // library cannot become a privileged file-write operation.
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using Process? updater = Process.Start(start);
        if (updater is null) { throw new Win32Exception("Windows could not start the package manager update."); }
    }

    internal static PackageManagerUpdateResult? TakeResult(string dataRoot)
    {
        string path = Path.Combine(dataRoot, StatusFile);
        try
        {
            if (!File.Exists(path)) { return null; }
            PackageManagerUpdateResult? result = JsonSerializer.Deserialize<PackageManagerUpdateResult>(File.ReadAllText(path));
            File.Delete(path);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static string BuildScript(InstallationChannel channel, int processId, string statusPath)
    {
        if (channel is not (InstallationChannel.Scoop or InstallationChannel.WinGet or InstallationChannel.Chocolatey) ||
            processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        string command = channel switch
        {
            InstallationChannel.Scoop => """
                $tool = (Get-Command scoop -ErrorAction Stop).Source
                & $tool update 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Scoop could not refresh its catalog.' }
                & $tool update shnapp 2>&1 | Out-Null
                """,
            InstallationChannel.WinGet => """
                $tool = (Get-Command winget -ErrorAction Stop).Source
                & $tool upgrade --id Zettersten.Shnapp --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1 | Out-Null
                """,
            _ => """
                $tool = '__CHOCOLATEY_EXECUTABLE__'
                $elevated = Start-Process -FilePath $tool -ArgumentList 'upgrade shnapp -y --no-progress' -Verb RunAs -Wait -PassThru -WindowStyle Normal
                $LASTEXITCODE = $elevated.ExitCode
                """.Replace("__CHOCOLATEY_EXECUTABLE__",
                ChocolateyExecutable().Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal),
        };
        string channelName = channel.ToString();
        string literalPath = statusPath.Replace("'", "''", StringComparison.Ordinal);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $exitCode = 1
            try {
                $previous = Get-Process -Id {{processId.ToString(CultureInfo.InvariantCulture)}} -ErrorAction SilentlyContinue
                if ($previous) { Wait-Process -Id {{processId.ToString(CultureInfo.InvariantCulture)}} -Timeout 120 -ErrorAction Stop }
                {{command}}
                $exitCode = $LASTEXITCODE
                if ($null -eq $exitCode) { $exitCode = 1 }
            }
            catch { $exitCode = 1 }
            try {
                $result = [pscustomobject]@{ Channel = '{{channelName}}'; ExitCode = [int]$exitCode; CompletedUtc = [DateTime]::UtcNow.ToString('o') }
                [System.IO.File]::WriteAllText('{{literalPath}}', ($result | ConvertTo-Json -Compress), [System.Text.UTF8Encoding]::new($false))
            }
            catch { }
            exit [int]$exitCode
            """;
    }

    private static string ChocolateyExecutable() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "chocolatey", "bin", "choco.exe");

    internal static bool CanStartChocolatey()
    {
        try
        {
            string executable = ChocolateyExecutable();
            string bin = Path.GetDirectoryName(executable)!;
            string root = Path.GetDirectoryName(bin)!;
            return File.Exists(executable) && !IsReparsePoint(root) &&
                !IsReparsePoint(bin) && !IsReparsePoint(executable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}

internal sealed record PackageManagerUpdateResult(string Channel, int ExitCode, string CompletedUtc);
