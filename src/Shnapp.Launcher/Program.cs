using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Shnapp.Launcher;

internal static partial class Program
{
    private const string PointerName = "current-version.txt";
    private const string BackupName = "current-version.txt.bak";
    private const int WaitForExitMilliseconds = 120_000;
    private static readonly TimeSpan StartupHealthTimeout = TimeSpan.FromSeconds(30);
    private enum StartupHealth { Healthy, StillRunning, ExitedWithoutSignal }

    private static int Main(string[] arguments)
    {
        try
        {
            string[] forwarded = arguments;
            if (arguments.Length > 0 && arguments[0] == "--relaunch-after")
            {
                if (arguments.Length < 2 ||
                    !int.TryParse(arguments[1], out int processId) || processId <= 0)
                {
                    throw new InvalidOperationException("The Shnapp restart request is invalid.");
                }

                WaitForProcess(processId);
                forwarded = arguments[2..];
            }

            forwarded = StripHealthArguments(forwarded);
            string root = Path.GetFullPath(AppContext.BaseDirectory);
            string? selectedCurrent = TryReadPointer(Path.Combine(root, PointerName), out string[] initialPointer)
                ? initialPointer[0] : null;
            string[] candidates = [.. CandidateVersions(root)];
            var failed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string version in candidates)
            {
                string directory = Path.Combine(root, "versions", version);
                string executable = Path.Combine(directory, "Shnapp.exe");
                if (!File.Exists(executable))
                {
                    failed.Add(version);
                    continue;
                }

                string token = Guid.NewGuid().ToString("N");
                string eventName = "Local\\Shnapp.LauncherHealth." + token;
                using var health = new EventWaitHandle(false, EventResetMode.ManualReset,
                    eventName, out bool createdNew);
                if (!createdNew)
                {
                    continue;
                }

                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = directory,
                };
                foreach (string argument in forwarded)
                {
                    start.ArgumentList.Add(argument);
                }
                start.ArgumentList.Add("--launcher-health");
                start.ArgumentList.Add(token);

                try
                {
                    using Process? process = Process.Start(start);
                    if (process is not null)
                    {
                        StartupHealth result = WaitForHealthyStart(process, health);
                        if (result == StartupHealth.Healthy)
                        {
                            if (selectedCurrent is not null && failed.Contains(selectedCurrent) &&
                                !string.Equals(version, selectedCurrent, StringComparison.Ordinal))
                            {
                                TryPersistRollback(root, selectedCurrent, version, failed);
                            }
                            return 0;
                        }
                        if (result == StartupHealth.StillRunning)
                        {
                            return 0;
                        }
                    }
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
                {
                    // A previous version can still launch when the selected payload is damaged.
                }
                failed.Add(version);
            }

            throw new FileNotFoundException(
                "Shnapp could not start its current or previous version. Download a fresh ZIP from shhnap.com/download/.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or Win32Exception)
        {
            MessageBoxW(0, exception.Message, "Shnapp could not start", 0x00000010);
            return 1;
        }
    }

    private static string[] StripHealthArguments(string[] arguments)
    {
        var forwarded = new List<string>(arguments.Length);
        for (int index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] == "--launcher-health")
            {
                if (index + 1 < arguments.Length) { index++; }
                continue;
            }

            forwarded.Add(arguments[index]);
        }

        return [.. forwarded];
    }

    private static StartupHealth WaitForHealthyStart(Process process, EventWaitHandle health)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (health.WaitOne(0))
            {
                return StartupHealth.Healthy;
            }
            if (process.HasExited)
            {
                // A second Shnapp instance signals the event before it forwards
                // activation and exits. Check once more to avoid a race here.
                return health.WaitOne(0) ? StartupHealth.Healthy : StartupHealth.ExitedWithoutSignal;
            }

            TimeSpan remaining = StartupHealthTimeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                // A slow but still-running app is not a failed launch.
                return !process.HasExited ? StartupHealth.StillRunning :
                    health.WaitOne(0) ? StartupHealth.Healthy : StartupHealth.ExitedWithoutSignal;
            }

            health.WaitOne(remaining < TimeSpan.FromMilliseconds(200)
                ? remaining : TimeSpan.FromMilliseconds(200));
        }
    }

    private static void TryPersistRollback(string root, string failedCurrent, string healthy,
        IReadOnlySet<string> failed)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
        string name = "Local\\Shnapp.UpdatePointer." + hash;
        string pointer = Path.Combine(root, PointerName);
        string backup = Path.Combine(root, BackupName);
        string temporary = Path.Combine(root, ".current-version-rollback-" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using var gate = new Mutex(false, name);
            bool owned;
            try { owned = gate.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) { return; }

            try
            {
                // An updater may have selected a different version while this
                // launch was in progress. Only repair the exact failed selection.
                if (!TryReadPointer(pointer, out string[] current) ||
                    !string.Equals(current[0], failedCurrent, StringComparison.Ordinal))
                {
                    return;
                }

                string? older = CandidateVersions(root).FirstOrDefault(candidate =>
                    !string.Equals(candidate, healthy, StringComparison.Ordinal) &&
                    !failed.Contains(candidate) &&
                    File.Exists(Path.Combine(root, "versions", candidate, "Shnapp.exe")));
                byte[] content = Encoding.ASCII.GetBytes(healthy + "\n" + (older ?? "") + "\n");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(content);
                    stream.Flush(flushToDisk: true);
                }

                File.Replace(temporary, pointer, backup, ignoreMetadataErrors: false);
            }
            finally
            {
                gate.ReleaseMutex();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or InvalidOperationException)
        {
            // The healthy app is running. A later launch can retry this repair.
        }
        finally
        {
            try { if (File.Exists(temporary)) { File.Delete(temporary); } }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void WaitForProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.WaitForExit(WaitForExitMilliseconds))
            {
                throw new InvalidOperationException(
                    "The previous Shnapp process is still running. Quit it and open Shnapp again.");
            }
        }
        catch (ArgumentException)
        {
            // The process already exited before the launcher opened its handle.
        }
    }

    private static IEnumerable<string> CandidateVersions(string root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in new[] { PointerName, BackupName })
        {
            if (!TryReadPointer(Path.Combine(root, name), out string[] lines))
            {
                continue;
            }

            foreach (string version in lines)
            {
                if (version.Length > 0 && seen.Add(version))
                {
                    yield return version;
                }
            }
        }
    }

    private static bool TryReadPointer(string path, out string[] lines)
    {
        lines = [];
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 128)
            {
                return false;
            }

            lines = File.ReadAllLines(path, Encoding.ASCII);
            return lines.Length is >= 1 and <= 2 && ValidVersion(lines[0]) &&
                (lines.Length == 1 || lines[1].Length == 0 || ValidVersion(lines[1]));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool ValidVersion(string value)
    {
        if (value.Length is < 6 or > 48 || value[0] != 'v')
        {
            return false;
        }

        ReadOnlySpan<char> rest = value.AsSpan(1);
        for (int part = 0; part < 3; part++)
        {
            int separator = rest.IndexOf('.');
            ReadOnlySpan<char> number = separator >= 0 ? rest[..separator] : rest;
            if (number.IsEmpty || (number.Length > 1 && number[0] == '0') ||
                (part == 0 && number[0] == '0'))
            {
                return false;
            }
            foreach (char digit in number)
            {
                if (digit is < '0' or > '9')
                {
                    return false;
                }
            }

            if (part < 2 && separator < 0 || part == 2 && separator >= 0)
            {
                return false;
            }
            rest = separator >= 0 ? rest[(separator + 1)..] : [];
        }

        return true;
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint window, string message, string caption, uint type);
}
