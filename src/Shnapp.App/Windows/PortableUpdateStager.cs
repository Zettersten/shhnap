using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Shnapp.App.Windows;

/// <summary>
/// Downloads and stages a direct ZIP release beside the running version. Only the small
/// current-version pointer changes; package-manager installations must never call this.
/// </summary>
internal sealed class PortableUpdateStager
{
    private static readonly Regex TagPattern = new(@"^v[1-9][0-9]*\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.Compiled);
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly HttpClient _client;

    internal PortableUpdateStager(HttpClient? client = null) => _client = client ?? SharedClient;

    internal static string CurrentExecutableDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    internal static string? FindCurrentInstallRoot() => FindInstallRoot(CurrentExecutableDirectory);

    internal static string? FindInstallRoot(string appDirectory)
    {
        try
        {
            string versionDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
            if (!TagPattern.IsMatch(Path.GetFileName(versionDirectory))) { return null; }
            string? versionsDirectory = Path.GetDirectoryName(versionDirectory);
            if (versionsDirectory is null ||
                !Path.GetFileName(versionsDirectory).Equals("versions", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string? root = Path.GetDirectoryName(versionsDirectory);
            if (root is null || !File.Exists(Path.Combine(root, "Shnapp.exe")) ||
                !File.Exists(Path.Combine(root, "current-version.txt")) ||
                IsReparsePoint(root) || IsReparsePoint(versionsDirectory) || IsReparsePoint(versionDirectory))
            {
                return null;
            }

            return root;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static bool IsSelectedVersion(string installRoot, string tag)
    {
        try
        {
            return string.Equals(ReadCurrent(Path.Combine(installRoot, "current-version.txt")),
                tag, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    internal async Task<bool> StageAndActivateAsync(ReleaseAsset asset, string installRoot,
        CancellationToken cancellationToken)
    {
        if (!TagPattern.IsMatch(asset.Tag) || asset.Size is <= 0 or > 400_000_000 ||
            !Regex.IsMatch(asset.Sha256, "^[a-f0-9]{64}$") ||
            !asset.Url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !asset.Url.IsDefaultPort || !string.IsNullOrEmpty(asset.Url.UserInfo) ||
            !string.IsNullOrEmpty(asset.Url.Query) || !string.IsNullOrEmpty(asset.Url.Fragment) ||
            !asset.Url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            asset.Url.AbsolutePath != $"/Zettersten/shhnap/releases/download/{asset.Tag}/" +
                (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? "Shnapp-win-arm64.zip" : "Shnapp-win-x64.zip"))
        {
            throw new InvalidDataException("The update release details are invalid.");
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        string versions = Path.Combine(root, "versions");
        string pointer = Path.Combine(root, "current-version.txt");
        string target = Path.Combine(versions, asset.Tag);
        if (!File.Exists(Path.Combine(root, "Shnapp.exe")) || !File.Exists(pointer) ||
            !Directory.Exists(versions) || IsReparsePoint(root) || IsReparsePoint(versions))
        {
            throw new InvalidOperationException("This folder is not a supported direct Shnapp installation.");
        }

        string current;
        using (AcquirePointerLock(root))
        {
            current = ReadCurrent(pointer);
        }

        if (!Version.TryParse(asset.Tag[1..], out Version? targetVersion) ||
            !Version.TryParse(current[1..], out Version? selectedVersion))
        {
            throw new InvalidDataException("The update or selected version is invalid.");
        }
        int order = targetVersion.CompareTo(selectedVersion);
        if (order == 0) { return false; }
        if (order < 0)
        {
            throw new InvalidOperationException("Shnapp will not replace a prepared newer version with an older release.");
        }
        if (Directory.Exists(target))
        {
            throw new IOException("The destination version already exists; Shnapp will not replace it automatically.");
        }
        string work = Path.Combine(root, ".shnapp-update-" + Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(work, "payload");
        string archivePath = Path.Combine(work, "release.zip");
        Directory.CreateDirectory(stage);
        try
        {
            await DownloadVerifiedAsync(asset, archivePath, cancellationToken);
            ExtractPayload(archivePath, stage, asset.Tag);
            using (AcquirePointerLock(root))
            {
                if (ReadCurrent(pointer) != current)
                {
                    throw new InvalidOperationException("Shnapp's selected version changed during the download.");
                }
                if (Directory.Exists(target))
                {
                    throw new IOException("The destination version already exists; Shnapp will not replace it automatically.");
                }
                Directory.Move(stage, target);
                string tempPointer = Path.Combine(root, ".current-version-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var stream = new FileStream(tempPointer, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        byte[] content = Encoding.ASCII.GetBytes(asset.Tag + "\n" + current + "\n");
                        stream.Write(content);
                        stream.Flush(flushToDisk: true);
                    }

                    File.Replace(tempPointer, pointer, pointer + ".bak", ignoreMetadataErrors: false);
                }
                catch
                {
                    if (!IsSelectedVersion(root, asset.Tag) && Directory.Exists(target))
                    {
                        Directory.Delete(target, recursive: true);
                    }
                    throw;
                }
                finally
                {
                    if (File.Exists(tempPointer)) { File.Delete(tempPointer); }
                }
            }

            return true;
        }
        finally
        {
            try
            {
                if (Directory.Exists(work)) { Directory.Delete(work, recursive: true); }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Scratch cleanup must not turn a successful pointer switch into a failed update.
            }
        }
    }

    private static string ReadCurrent(string pointer)
    {
        string current = File.ReadLines(pointer).FirstOrDefault()?.Trim() ?? string.Empty;
        if (!TagPattern.IsMatch(current))
        {
            throw new InvalidDataException("Shnapp's installed version pointer is invalid.");
        }
        return current;
    }

    private static IDisposable AcquirePointerLock(string root)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
        var mutex = new Mutex(false, "Local\\Shnapp.UpdatePointer." + id);
        try
        {
            bool held;
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held)
            {
                throw new IOException("Shnapp's version pointer is busy. Try the update again shortly.");
            }
            return new PointerLock(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class PointerLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private async Task DownloadVerifiedAsync(ReleaseAsset asset, string destination,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.UserAgent.ParseAdd("Shnapp/1.0");
        using HttpResponseMessage response = await _client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } reported && reported != asset.Size)
        {
            throw new InvalidDataException("The update size differs from GitHub's release record.");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long count = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) { break; }
            count += read;
            if (count > asset.Size) { throw new InvalidDataException("The update exceeds its declared size."); }
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (count != asset.Size ||
            !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(),
                Convert.FromHexString(asset.Sha256)))
        {
            throw new InvalidDataException("The update failed its SHA-256 verification.");
        }
    }

    private static void ExtractPayload(string archivePath, string stage, string tag)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is <= 0 or > 3000)
        {
            throw new InvalidDataException("The update archive has an invalid number of files.");
        }

        string prefix = "versions/" + tag + "/";
        string stageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stage)) + Path.DirectorySeparatorChar;
        long total = 0;
        bool hasLauncher = false;
        bool hasPointer = false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;
            if (!names.Add(name) || name.Contains('\\') || name.StartsWith('/') || name.Contains(':') ||
                name.Split('/').Any(part => part is "." or "..") ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (((FileAttributes)entry.ExternalAttributes) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The update archive contains an unsafe path or link.");
            }

            if (name == "Shnapp.exe")
            {
                hasLauncher = true;
                continue;
            }
            if (name is "LICENSE" or "THIRD_PARTY_NOTICES.txt")
            {
                continue;
            }
            if (name == "current-version.txt")
            {
                if (entry.Length > 128)
                {
                    throw new InvalidDataException("The update version pointer is too large.");
                }
                using StreamReader pointer = new(entry.Open(), Encoding.ASCII);
                hasPointer = pointer.ReadLine()?.Trim() == tag;
                continue;
            }
            if (name == prefix.TrimEnd('/'))
            {
                continue;
            }
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The update archive contains an unexpected file.");
            }

            string relative = name[prefix.Length..];
            if (relative.Length == 0) { continue; }
            string destination = Path.GetFullPath(Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(stageRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update archive escapes its staging folder.");
            }

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            total += entry.Length;
            if (entry.Length > 400_000_000 || total > 1_200_000_000)
            {
                throw new InvalidDataException("The update archive is too large to extract safely.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream source = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            byte[] buffer = new byte[128 * 1024];
            long written = 0;
            while (true)
            {
                int read = source.Read(buffer);
                if (read == 0) { break; }
                written += read;
                if (written > entry.Length) { throw new InvalidDataException("An update file exceeds its declared size."); }
                output.Write(buffer, 0, read);
            }
            if (written != entry.Length) { throw new InvalidDataException("An update file is incomplete."); }
        }

        if (!hasLauncher || !hasPointer)
        {
            throw new InvalidDataException("The update archive does not contain a valid launcher and version pointer.");
        }

        foreach (string required in new[] { "Shnapp.exe", "Shnapp.pri", "LICENSE", "THIRD_PARTY_NOTICES.txt" })
        {
            if (!File.Exists(Path.Combine(stage, required)))
            {
                throw new InvalidDataException("The update is missing " + required + ".");
            }
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
