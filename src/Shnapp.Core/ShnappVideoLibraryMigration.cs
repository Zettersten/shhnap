using System.Security.Cryptography;

namespace Shnapp.Core;

/// <summary>Imports portable video projects into a packaged library independently of photo import.</summary>
public static class ShnappVideoLibraryMigration
{
    private const string CompletionFileName = "portable-video-import-v1.complete";
    private const long MaximumProjectBytes = 8L * 1024 * 1024;

    private readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc);

    public sealed record Result(int ImportedProjects, int FailedProjects, bool Complete);

    /// <summary>Whether the video import has completed for this packaged library.</summary>
    public static bool IsComplete(string packagedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagedRoot);
        string marker = Path.Combine(Path.GetFullPath(packagedRoot), CompletionFileName);
        ShnappLibraryMigration.EnsureNotReparsePoint(marker);
        return File.Exists(marker);
    }

    /// <summary>
    /// Copies complete projects without replacing Store projects or modifying portable files.
    /// A failed or interrupted project is retried on the next launch.
    /// </summary>
    public static Task<Result> ImportOnceAsync(string portableRoot, string packagedRoot,
        CancellationToken cancellationToken = default) =>
        ImportOnceAsync(portableRoot, packagedRoot, beforeSourceRecheck: null, cancellationToken);

    // The callback lets tests simulate a portable edit between staging and the final source check.
    internal static async Task<Result> ImportOnceAsync(string portableRoot, string packagedRoot,
        Func<Guid, Task>? beforeSourceRecheck, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portableRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagedRoot);
        portableRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(portableRoot));
        packagedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packagedRoot));
        if (string.Equals(portableRoot, packagedRoot,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The portable and packaged library paths must differ.");
        }
        if (!Directory.Exists(portableRoot))
        {
            return new Result(0, 0, Complete: true);
        }

        ShnappLibraryMigration.EnsureNotReparsePoint(portableRoot);
        ShnappLibraryMigration.EnsureNotReparsePoint(packagedRoot);
        if (IsComplete(packagedRoot))
        {
            return new Result(0, 0, Complete: true);
        }

        Directory.CreateDirectory(packagedRoot);
        ShnappLibraryMigration.EnsureNotReparsePoint(packagedRoot);
        string sourceRoot = Path.Combine(portableRoot, "videos");
        string destinationRoot = Path.Combine(packagedRoot, "videos");
        string markerPath = Path.Combine(packagedRoot, CompletionFileName);
        ShnappLibraryMigration.EnsureNotReparsePoint(sourceRoot);
        ShnappLibraryMigration.EnsureNotReparsePoint(destinationRoot);
        var source = new VideoProjectStore(sourceRoot);
        var destination = new VideoProjectStore(destinationRoot);
        string[] names = ShnappLibraryMigration.GetSourceDirectoryNames(sourceRoot);
        int imported = 0;
        int failed = 0;

        foreach (string name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(name, "N", out Guid id) || id == Guid.Empty) { continue; }

            string sourceDirectory = source.GetProjectDirectory(id);
            string destinationDirectory = destination.GetProjectDirectory(id);
            string stageRoot = Path.Combine(packagedRoot, ".portable-video-import-" + id.ToString("N"));
            try
            {
                ShnappLibraryMigration.EnsureNotReparsePoint(sourceDirectory);
                EnsureNoPendingSave(sourceDirectory);
                if (Directory.Exists(destinationDirectory))
                {
                    _ = await destination.OpenAsync(id, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string metadataPath = Path.Combine(sourceDirectory, "project.json");
                byte[] metadataBefore = await HashMetadataAsync(metadataPath, cancellationToken)
                    .ConfigureAwait(false);
                VideoProject project = await source.OpenAsync(id, cancellationToken).ConfigureAwait(false);
                ShnappLibraryMigration.DeleteOwnedStage(stageRoot);
                var staged = new VideoProjectStore(Path.Combine(stageRoot, "videos"));
                string stageDirectory = staged.PrepareProjectDirectory(id);
                var sourceStamps = new List<(string Path, FileStamp Stamp)>(project.Assets.Length);
                foreach (VideoAsset asset in project.Assets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string sourcePath = source.ResolveAssetPath(project, asset);
                    string stagePath = Path.Combine(stageDirectory,
                        asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    string parent = Path.GetDirectoryName(stagePath)!;
                    Directory.CreateDirectory(parent);
                    ShnappLibraryMigration.EnsureNotReparsePoint(parent);
                    FileStamp stamp = await CopyMediaAsync(sourcePath, stagePath, cancellationToken)
                        .ConfigureAwait(false);
                    sourceStamps.Add((sourcePath, stamp));
                }

                await staged.SaveAsync(project, cancellationToken).ConfigureAwait(false);
                _ = await staged.OpenAsync(id, cancellationToken).ConfigureAwait(false);
                if (beforeSourceRecheck is not null)
                {
                    await beforeSourceRecheck(id).ConfigureAwait(false);
                }
                EnsureNoPendingSave(sourceDirectory);
                byte[] metadataAfter = await HashMetadataAsync(metadataPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!metadataBefore.AsSpan().SequenceEqual(metadataAfter) ||
                    sourceStamps.Any(item => GetStamp(item.Path) != item.Stamp))
                {
                    throw new IOException("A portable video project changed during import.");
                }

                ShnappLibraryMigration.EnsureNotReparsePoint(destinationRoot);
                Directory.CreateDirectory(destinationRoot);
                ShnappLibraryMigration.EnsureNotReparsePoint(destinationRoot);
                if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
                {
                    throw new IOException("An existing Store video project must not be replaced.");
                }
                Directory.Move(stageDirectory, destinationDirectory);
                imported++;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or
                UnauthorizedAccessException or ArgumentException)
            {
                failed++;
            }
            finally
            {
                ShnappLibraryMigration.DeleteOwnedStage(stageRoot);
            }
        }

        if (!names.SequenceEqual(ShnappLibraryMigration.GetSourceDirectoryNames(sourceRoot),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            failed++;
        }
        if (failed == 0)
        {
            await ShnappLibraryMigration.WriteMarkerAsync(markerPath, cancellationToken)
                .ConfigureAwait(false);
        }
        return new Result(imported, failed, Complete: failed == 0);
    }

    private static async Task<FileStamp> CopyMediaAsync(string source, string destination,
        CancellationToken cancellationToken)
    {
        ShnappLibraryMigration.EnsureNotReparsePoint(source);
        ShnappLibraryMigration.EnsureNotReparsePoint(destination);
        FileStamp before = GetStamp(source);
        if (before.Length <= 0) { throw new InvalidDataException("A portable video asset is empty."); }
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
        {
            if (input.Length != before.Length)
            {
                throw new IOException("A portable video asset changed during import.");
            }
            await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (output.Length != before.Length)
            {
                throw new IOException("A portable video asset was not copied completely.");
            }
        }
        if (GetStamp(source) != before)
        {
            throw new IOException("A portable video asset changed during import.");
        }
        return before;
    }

    private static async Task<byte[]> HashMetadataAsync(string path, CancellationToken cancellationToken)
    {
        ShnappLibraryMigration.EnsureNotReparsePoint(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumProjectBytes)
        {
            throw new InvalidDataException("A portable video project has invalid metadata size.");
        }
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static FileStamp GetStamp(string path)
    {
        ShnappLibraryMigration.EnsureNotReparsePoint(path);
        var file = new FileInfo(path);
        return new FileStamp(file.Length, file.LastWriteTimeUtc);
    }

    private static void EnsureNoPendingSave(string directory)
    {
        if (Directory.EnumerateFiles(directory, ".project-*.tmp").Any())
        {
            throw new IOException("A portable video project is still being saved.");
        }
        string assetsDirectory = Path.Combine(directory, "assets");
        ShnappLibraryMigration.EnsureNotReparsePoint(assetsDirectory);
        if (Directory.Exists(assetsDirectory) &&
            Directory.EnumerateFiles(assetsDirectory, ".import-*.tmp").Any())
        {
            throw new IOException("A portable video asset is still being imported.");
        }
    }
}
