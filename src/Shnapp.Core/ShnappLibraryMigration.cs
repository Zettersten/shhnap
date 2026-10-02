namespace Shnapp.Core;

/// <summary>Copies a portable library into a packaged library once, without changing the source.</summary>
public static class ShnappLibraryMigration
{
    private const string CompletionFileName = "portable-library-import-v1.complete";
    private static readonly string[] DocumentFiles =
    [
        "original.png", "shnapp.png", "preview.png", "preview-fit.png",
        "preview-compact.png", "preview-dock.png",
    ];

    public sealed record Result(int ImportedDocuments, int FailedDocuments, bool SettingsImported,
        bool SettingsFailed, bool Complete);

    /// <summary>
    /// Imports validated documents and settings from the portable root. Existing destination
    /// files are never replaced. A failed or interrupted import is retried on the next launch.
    /// </summary>
    public static async Task<Result> ImportOnceAsync(string portableRoot, string packagedRoot,
        CancellationToken cancellationToken = default)
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
            return new Result(0, 0, false, false, Complete: true);
        }

        EnsureNotReparsePoint(portableRoot);
        EnsureNotReparsePoint(packagedRoot);
        string markerPath = Path.Combine(packagedRoot, CompletionFileName);
        EnsureNotReparsePoint(markerPath);
        if (File.Exists(markerPath))
        {
            EnsureNotReparsePoint(markerPath);
            return new Result(0, 0, false, false, Complete: true);
        }

        var source = new ShnappLibrary(portableRoot);
        var destination = new ShnappLibrary(packagedRoot);
        Directory.CreateDirectory(packagedRoot);
        EnsureNotReparsePoint(packagedRoot);

        bool settingsImported = false;
        bool settingsFailed = false;
        string sourceSettings = Path.Combine(portableRoot, "settings.json");
        string destinationSettings = Path.Combine(packagedRoot, "settings.json");
        EnsureNotReparsePoint(destinationSettings);
        if (File.Exists(sourceSettings) && !File.Exists(destinationSettings))
        {
            try
            {
                // Do not carry malformed preferences into a previously usable Store library.
                _ = await source.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
                settingsImported = await CopyFileIfMissingAsync(sourceSettings, destinationSettings,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                UnauthorizedAccessException or ArgumentException)
            {
                settingsFailed = true;
            }
        }

        int imported = 0;
        int failed = 0;
        string sourceDocuments = Path.Combine(portableRoot, "shnapps");
        string destinationDocuments = Path.Combine(packagedRoot, "shnapps");
        EnsureNotReparsePoint(destinationDocuments);
        if (Directory.Exists(sourceDocuments))
        {
            EnsureNotReparsePoint(sourceDocuments);
            foreach (string sourceDirectory in Directory.EnumerateDirectories(sourceDocuments))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileName(sourceDirectory), "N", out Guid id) ||
                    id == Guid.Empty)
                {
                    continue;
                }

                string destinationDirectory = destination.GetDocumentDirectory(id);
                string destinationDocument = Path.Combine(destinationDirectory, "document.json");
                if (File.Exists(destinationDocument))
                {
                    EnsureNotReparsePoint(destinationDirectory);
                    EnsureNotReparsePoint(destinationDocument);
                    continue;
                }

                try
                {
                    EnsureNotReparsePoint(sourceDirectory);
                    if (await source.OpenAsync(id, cancellationToken).ConfigureAwait(false) is null)
                    {
                        failed++;
                        continue;
                    }

                    EnsureNotReparsePoint(destinationDirectory);
                    if (Directory.Exists(destinationDirectory))
                    {
                        // An unrelated or incomplete destination must not be overwritten.
                        failed++;
                        continue;
                    }

                    string stageRoot = Path.Combine(packagedRoot, ".portable-import-" + id.ToString("N"));
                    DeleteOwnedStage(stageRoot);
                    try
                    {
                        var stagedLibrary = new ShnappLibrary(stageRoot);
                        string stageDirectory = stagedLibrary.GetDocumentDirectory(id);
                        Directory.CreateDirectory(stageDirectory);
                        foreach (string fileName in DocumentFiles)
                        {
                            string sourceFile = Path.Combine(sourceDirectory, fileName);
                            if (File.Exists(sourceFile))
                            {
                                await CopyFileIfMissingAsync(sourceFile,
                                    Path.Combine(stageDirectory, fileName), cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }

                        await CopyFileIfMissingAsync(Path.Combine(sourceDirectory, "document.json"),
                            Path.Combine(stageDirectory, "document.json"), cancellationToken)
                            .ConfigureAwait(false);
                        if (await stagedLibrary.OpenAsync(id, cancellationToken).ConfigureAwait(false) is null)
                        {
                            throw new InvalidDataException("A copied portable shnapp could not be validated.");
                        }

                        EnsureNotReparsePoint(destinationDocuments);
                        Directory.CreateDirectory(destinationDocuments);
                        EnsureNotReparsePoint(destinationDocuments);
                        Directory.Move(stageDirectory, destinationDirectory);
                        imported++;
                    }
                    finally
                    {
                        DeleteOwnedStage(stageRoot);
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or
                    UnauthorizedAccessException or ArgumentException)
                {
                    failed++;
                }
            }
        }

        if (failed == 0 && !settingsFailed)
        {
            await WriteMarkerAsync(markerPath, cancellationToken).ConfigureAwait(false);
        }

        return new Result(imported, failed, settingsImported, settingsFailed,
            Complete: failed == 0 && !settingsFailed);
    }

    private static async Task<bool> CopyFileIfMissingAsync(string source, string destination,
        CancellationToken cancellationToken)
    {
        EnsureNotReparsePoint(source);
        if (File.Exists(destination))
        {
            EnsureNotReparsePoint(destination);
            return false;
        }

        string temporary = destination + ".portable-import.tmp";
        EnsureNotReparsePoint(temporary);
        File.Delete(temporary); // Remove only an incomplete copy from a previous attempt.
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, destination);
                return true;
            }
            catch (IOException) when (File.Exists(destination))
            {
                EnsureNotReparsePoint(destination);
                return false;
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static async Task WriteMarkerAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            EnsureNotReparsePoint(path);
            return;
        }

        string temporary = path + ".tmp";
        EnsureNotReparsePoint(temporary);
        File.Delete(temporary);
        try
        {
            await File.WriteAllTextAsync(temporary, "Portable Shnapp library imported.\n", cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { EnsureNotReparsePoint(path); }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Library import cannot use symbolic links or reparse points.");
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void DeleteOwnedStage(string stageRoot)
    {
        if (!Directory.Exists(stageRoot))
        {
            return;
        }

        EnsureSafeTree(stageRoot);
        Directory.Delete(stageRoot, recursive: true);
    }

    private static void EnsureSafeTree(string directory)
    {
        EnsureNotReparsePoint(directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsureNotReparsePoint(entry);
            if (Directory.Exists(entry))
            {
                EnsureSafeTree(entry);
            }
        }
    }
}
