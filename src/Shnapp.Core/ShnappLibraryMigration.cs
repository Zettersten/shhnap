using System.Security.Cryptography;

namespace Shnapp.Core;

/// <summary>Copies a portable library into a packaged library once, without changing the source.</summary>
public static class ShnappLibraryMigration
{
    private const string CompletionFileName = "portable-library-import-v1.complete";
    private static readonly uint[] PngCrcTable = CreatePngCrcTable();
    private static readonly string[] DocumentFiles =
    [
        "original.png", "shnapp.png", "preview.png", "preview-fit.png",
        "preview-compact.png", "preview-dock.png",
    ];
    private static readonly string[] SnapshotFiles = [.. DocumentFiles, "document.json"];

    private sealed record FileFingerprint(long Length, DateTime LastWriteTimeUtc, string Sha256);

    public sealed record Result(int ImportedDocuments, int FailedDocuments, bool SettingsImported,
        bool SettingsFailed, bool Complete);

    /// <summary>Whether the first portable-to-package import has already completed.</summary>
    public static bool IsComplete(string packagedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagedRoot);
        string markerPath = Path.Combine(Path.GetFullPath(packagedRoot), CompletionFileName);
        EnsureNotReparsePoint(markerPath);
        return File.Exists(markerPath);
    }

    /// <summary>
    /// Imports validated documents and settings from the portable root. Existing destination
    /// files are never replaced. A failed or interrupted import is retried on the next launch.
    /// </summary>
    public static Task<Result> ImportOnceAsync(string portableRoot, string packagedRoot,
        CancellationToken cancellationToken = default) =>
        ImportOnceAsync(portableRoot, packagedRoot, beforeSourceCheck: null, cancellationToken);

    // The callback lets tests make a source edit at the exact point where a concurrent save matters.
    internal static async Task<Result> ImportOnceAsync(string portableRoot, string packagedRoot,
        Func<Guid, Task>? beforeSourceCheck, CancellationToken cancellationToken = default)
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
        if (IsComplete(packagedRoot))
        {
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
        string[] sourceDirectoryNames = GetSourceDirectoryNames(sourceDocuments);
        if (Directory.Exists(sourceDocuments))
        {
            EnsureNotReparsePoint(sourceDocuments);
            foreach (string directoryName in sourceDirectoryNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(directoryName, "N", out Guid id) ||
                    id == Guid.Empty)
                {
                    continue;
                }

                string sourceDirectory = Path.Combine(sourceDocuments, directoryName);
                string destinationDirectory = destination.GetDocumentDirectory(id);
                string destinationDocument = Path.Combine(destinationDirectory, "document.json");
                try
                {
                    EnsureNotReparsePoint(sourceDirectory);
                    EnsureNoPendingSave(sourceDirectory);
                    if (File.Exists(destinationDocument))
                    {
                        EnsureNotReparsePoint(destinationDirectory);
                        EnsureNotReparsePoint(destinationDocument);
                        if (await destination.OpenAsync(id, cancellationToken).ConfigureAwait(false) is null)
                        {
                            throw new InvalidDataException("An existing Store shnapp could not be validated.");
                        }
                        continue;
                    }

                    ShnappDocument? sourceDocument = await source.OpenAsync(id, cancellationToken)
                        .ConfigureAwait(false);
                    if (sourceDocument is null)
                    {
                        failed++;
                        continue;
                    }
                    await ValidateOriginalPngAsync(source.GetOriginalPath(id), sourceDocument,
                        cancellationToken).ConfigureAwait(false);
                    FileFingerprint?[] sourceBefore = await CaptureFingerprintsAsync(sourceDirectory,
                        checkPendingSave: true, cancellationToken).ConfigureAwait(false);

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
                        ShnappDocument? stagedDocument = await stagedLibrary.OpenAsync(id, cancellationToken)
                            .ConfigureAwait(false);
                        if (stagedDocument is null)
                        {
                            throw new InvalidDataException("A copied portable shnapp could not be validated.");
                        }
                        await ValidateOriginalPngAsync(stagedLibrary.GetOriginalPath(id), stagedDocument,
                            cancellationToken).ConfigureAwait(false);

                        if (beforeSourceCheck is not null)
                        {
                            await beforeSourceCheck(id).ConfigureAwait(false);
                        }
                        FileFingerprint?[] staged = await CaptureFingerprintsAsync(stageDirectory,
                            checkPendingSave: false, cancellationToken).ConfigureAwait(false);
                        FileFingerprint?[] sourceAfter = await CaptureFingerprintsAsync(sourceDirectory,
                            checkPendingSave: true, cancellationToken).ConfigureAwait(false);
                        if (!sourceBefore.SequenceEqual(staged) || !sourceBefore.SequenceEqual(sourceAfter))
                        {
                            throw new IOException("The portable shnapp changed during import; retry after closing it.");
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

        if (!sourceDirectoryNames.SequenceEqual(GetSourceDirectoryNames(sourceDocuments),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            // A capture created or removed during import must be considered on the next launch.
            failed++;
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

    private static async Task<FileFingerprint?[]> CaptureFingerprintsAsync(string directory,
        bool checkPendingSave, CancellationToken cancellationToken)
    {
        if (checkPendingSave) { EnsureNoPendingSave(directory); }
        var fingerprints = new FileFingerprint?[SnapshotFiles.Length];
        for (int index = 0; index < SnapshotFiles.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(directory, SnapshotFiles[index]);
            EnsureNotReparsePoint(path);
            if (!File.Exists(path)) { continue; }

            DateTime modified = File.GetLastWriteTimeUtc(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = stream.Length;
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false));
            if (modified != File.GetLastWriteTimeUtc(path) || length != new FileInfo(path).Length)
            {
                throw new IOException("A portable shnapp changed during import.");
            }
            fingerprints[index] = new FileFingerprint(length, modified, hash);
        }
        if (checkPendingSave) { EnsureNoPendingSave(directory); }
        return fingerprints;
    }

    private static void EnsureNoPendingSave(string directory)
    {
        if (EntryExists(Path.Combine(directory, ".save-pending")) ||
            Directory.EnumerateFiles(directory, ".document.json.*.tmp").Any())
        {
            throw new IOException("A portable shnapp is still being saved.");
        }
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static async Task ValidateOriginalPngAsync(string path, ShnappDocument document,
        CancellationToken cancellationToken)
    {
        EnsureNotReparsePoint(path);
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] signature = new byte[8];
        if (await stream.ReadAtLeastAsync(signature, signature.Length, throwOnEndOfStream: false,
            cancellationToken: cancellationToken).ConfigureAwait(false) != signature.Length ||
            !signature.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            throw new InvalidDataException("A portable shnapp is missing a valid original PNG.");
        }

        byte[] chunkHeader = new byte[8];
        byte[] chunkBuffer = new byte[64 * 1024];
        byte[] storedCrc = new byte[4];
        bool hasHeader = false;
        bool hasImageData = false;
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Length - stream.Position < 12)
            {
                throw new InvalidDataException("A portable shnapp has an incomplete original PNG chunk.");
            }

            await stream.ReadExactlyAsync(chunkHeader, cancellationToken).ConfigureAwait(false);
            uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(chunkHeader.AsSpan(0, 4));
            if (length > int.MaxValue || length > stream.Length - stream.Position - storedCrc.Length)
            {
                throw new InvalidDataException("A portable shnapp has an invalid original PNG chunk length.");
            }

            bool isHeader = chunkHeader.AsSpan(4, 4).SequenceEqual("IHDR"u8);
            bool isImageData = chunkHeader.AsSpan(4, 4).SequenceEqual("IDAT"u8);
            bool isEnd = chunkHeader.AsSpan(4, 4).SequenceEqual("IEND"u8);
            if ((!hasHeader && (!isHeader || length != 13)) ||
                (hasHeader && isHeader) || (isEnd && length != 0))
            {
                throw new InvalidDataException("A portable shnapp has an invalid original PNG chunk order.");
            }

            uint crc = UpdatePngCrc(uint.MaxValue, chunkHeader.AsSpan(4, 4));
            int remaining = (int)length;
            while (remaining > 0)
            {
                int count = Math.Min(remaining, chunkBuffer.Length);
                await stream.ReadExactlyAsync(chunkBuffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
                crc = UpdatePngCrc(crc, chunkBuffer.AsSpan(0, count));
                remaining -= count;
            }

            await stream.ReadExactlyAsync(storedCrc, cancellationToken).ConfigureAwait(false);
            if (~crc != System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(storedCrc))
            {
                throw new InvalidDataException("A portable shnapp has an invalid original PNG chunk checksum.");
            }

            if (isHeader)
            {
                uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(chunkBuffer.AsSpan(0, 4));
                uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(chunkBuffer.AsSpan(4, 4));
                if (width != document.PixelWidth || height != document.PixelHeight)
                {
                    throw new InvalidDataException("A portable shnapp's original PNG dimensions do not match its document.");
                }
                hasHeader = true;
            }
            hasImageData |= isImageData && length > 0;
            if (isEnd)
            {
                if (!hasImageData || stream.Position != stream.Length)
                {
                    throw new InvalidDataException("A portable shnapp has an incomplete original PNG.");
                }
                return;
            }
        }

        throw new InvalidDataException("A portable shnapp has an incomplete original PNG.");
    }

    private static uint UpdatePngCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc = PngCrcTable[(int)((crc ^ value) & 0xff)] ^ (crc >> 8);
        }
        return crc;
    }

    private static uint[] CreatePngCrcTable()
    {
        var table = new uint[256];
        for (int index = 0; index < table.Length; index++)
        {
            uint value = (uint)index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320u;
            }
            table[index] = value;
        }
        return table;
    }

    private static string[] GetSourceDirectoryNames(string documentsRoot)
    {
        if (!Directory.Exists(documentsRoot)) { return []; }
        EnsureNotReparsePoint(documentsRoot);
        return Directory.EnumerateDirectories(documentsRoot)
            .Select(static path => Path.GetFileName(path)!)
            .OrderBy(static name => name, OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
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
