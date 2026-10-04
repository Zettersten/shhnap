namespace Shnapp.Core;

/// <summary>
/// Makes stable copies of verified rendered PNGs for consumers that may read after a save begins.
/// Callers must hold the application's document save gate while creating a snapshot.
/// </summary>
/// <param name="library">The library that owns the rendered PNGs.</param>
public sealed class DocumentExportSnapshots(ShnappLibrary library)
{
    private const string SnapshotDirectoryName = "export-snapshots";
    private static readonly TimeSpan SnapshotRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private readonly ShnappLibrary _library = library ?? throw new ArgumentNullException(nameof(library));
    private readonly DocumentSaveJournal _journal = new(library);
    private readonly object _cleanupGate = new();
    private DateTime _lastCleanupScheduledUtc = DateTime.MinValue;
    private Task _cleanupTask = Task.CompletedTask;

    /// <summary>Copies a verified export to a unique path that this class never overwrites.</summary>
    /// <param name="id">The document whose export is being copied.</param>
    /// <param name="sourcePngPath">The document's canonical shnapp.png path.</param>
    /// <param name="ct">Cancels the copy before the snapshot is published.</param>
    /// <returns>The stable PNG path beneath the library's export-snapshots directory.</returns>
    public Task<string> CreateAsync(Guid id, string sourcePngPath, CancellationToken ct = default) =>
        CreateSnapshotAsync(id, sourcePngPath, thumbnail: false, ct);

    /// <summary>Copies the verified gallery preview to a unique thumbnail path.</summary>
    /// <param name="id">The document whose thumbnail is being copied.</param>
    /// <param name="sourcePreviewPath">The document's canonical preview-dock.png path.</param>
    /// <param name="ct">Cancels the copy before the thumbnail is published.</param>
    /// <returns>The stable thumbnail path beneath the library's export-snapshots directory.</returns>
    public Task<string> CreateThumbnailAsync(Guid id, string sourcePreviewPath, CancellationToken ct = default) =>
        CreateSnapshotAsync(id, sourcePreviewPath, thumbnail: true, ct);

    private async Task<string> CreateSnapshotAsync(Guid id, string sourcePath, bool thumbnail,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string parameterName = thumbnail ? "sourcePreviewPath" : "sourcePngPath";
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath, parameterName);
        string expectedSource = thumbnail ? _library.GetGalleryPreviewPath(id) : _library.GetExportPath(id);
        if (!string.Equals(Path.GetFullPath(sourcePath), expectedSource, PathComparison))
        {
            throw new ArgumentException(thumbnail
                ? "The source must be this document's gallery preview."
                : "The source must be this document's saved PNG.", parameterName);
        }

        string documentDirectory = _library.GetDocumentDirectory(id);
        EnsureSafeDirectory(_library.RootPath);
        EnsureSafeDirectory(Path.Combine(_library.RootPath, "shnapps"));
        EnsureSafeDirectory(documentDirectory);
        if (_journal.IsPending(id) || !_journal.IsVerified(id))
        {
            throw new InvalidOperationException("The document's saved PNG must be recovered before sharing.");
        }
        if (!EnsureSafeFile(expectedSource))
        {
            throw new FileNotFoundException(thumbnail
                ? "The document's gallery preview is missing."
                : "The document's saved PNG is missing.", expectedSource);
        }

        string snapshotsDirectory = Path.Combine(_library.RootPath, SnapshotDirectoryName);
        EnsureSafeDirectory(snapshotsDirectory);
        Directory.CreateDirectory(snapshotsDirectory);
        EnsureSafeDirectory(snapshotsDirectory);

        string uniqueName = $"{id:N}-{Guid.NewGuid():N}";
        string temporaryPath = Path.Combine(snapshotsDirectory, $".{uniqueName}.tmp");
        string snapshotPath = Path.Combine(snapshotsDirectory, $"{uniqueName}.png");
        try
        {
            await using (FileStream source = new(expectedSource, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream destination = new(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
                await destination.FlushAsync(ct).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            ct.ThrowIfCancellationRequested();
            EnsureSafeDirectory(snapshotsDirectory);
            if (EnsureSafeFile(snapshotPath))
            {
                throw new IOException("The unique export snapshot path is already occupied.");
            }
            File.Move(temporaryPath, snapshotPath, overwrite: false);
            ScheduleCleanup();
            return snapshotPath;
        }
        finally
        {
            if (EnsureSafeFile(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal Task ScheduledCleanup
    {
        get { lock (_cleanupGate) { return _cleanupTask; } }
    }

    private void ScheduleCleanup()
    {
        lock (_cleanupGate)
        {
            DateTime now = DateTime.UtcNow;
            if (!_cleanupTask.IsCompleted || now - _lastCleanupScheduledUtc < CleanupInterval)
            {
                return;
            }

            _lastCleanupScheduledUtc = now;
            _cleanupTask = Task.Run(async () =>
            {
                try
                {
                    await CleanupExpiredAsync(SnapshotRetention, int.MaxValue).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Snapshot cleanup is best effort. A later share retries after the throttle interval.
                }
            });
        }
    }

    /// <summary>Scans snapshot entries and deletes at most <paramref name="maxFiles"/> expired files.</summary>
    /// <param name="maxAge">How long snapshots remain available to external readers.</param>
    /// <param name="maxFiles">Maximum expired files to delete during this cleanup pass.</param>
    /// <param name="ct">Cancels cleanup between entries.</param>
    /// <returns>The number of expired snapshot files deleted.</returns>
    public Task<int> CleanupExpiredAsync(TimeSpan maxAge, int maxFiles, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFiles);
        ct.ThrowIfCancellationRequested();
        string snapshotsDirectory = Path.Combine(_library.RootPath, SnapshotDirectoryName);
        EnsureSafeDirectory(_library.RootPath);
        if (!EnsureSafeDirectory(snapshotsDirectory))
        {
            return Task.FromResult(0);
        }

        DateTime cutoff = DateTime.UtcNow - maxAge;
        int deleted = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(snapshotsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (!IsOwnSnapshotName(name)) { continue; }

            // Never follow or remove a link, even when its name resembles one of our files.
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                continue;
            }

            DateTime lastWrite;
            try { lastWrite = File.GetLastWriteTimeUtc(path); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (lastWrite >= cutoff) { continue; }

            EnsureSafeDirectory(snapshotsDirectory);
            try
            {
                if (EnsureSafeFile(path))
                {
                    File.Delete(path);
                    deleted++;
                    if (deleted >= maxFiles) { break; }
                }
            }
            catch (IOException) { /* A locked or replaced snapshot can be retried later. */ }
            catch (UnauthorizedAccessException) { /* A denied snapshot can be retried later. */ }
        }

        return Task.FromResult(deleted);
    }

    /// <summary>Removes every snapshot and unfinished snapshot copy for one document.</summary>
    /// <param name="id">The document whose snapshots are being removed.</param>
    /// <returns>The number of matching regular files deleted.</returns>
    /// <remarks>Call under the application's save gate before deleting the document.</remarks>
    public int DeleteForDocument(Guid id)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        string snapshotsDirectory = Path.Combine(_library.RootPath, SnapshotDirectoryName);
        EnsureSafeDirectory(_library.RootPath);
        if (!EnsureSafeDirectory(snapshotsDirectory)) { return 0; }

        int deleted = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(snapshotsDirectory))
        {
            if (!TryGetDocumentId(Path.GetFileName(path), out Guid owner) || owner != id)
            {
                continue;
            }

            // A link may point outside this library. Leave it and its target untouched.
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                continue;
            }

            EnsureSafeDirectory(snapshotsDirectory);
            if (EnsureSafeFile(path))
            {
                File.Delete(path);
                deleted++;
            }
        }

        return deleted;
    }

    private static bool IsOwnSnapshotName(string name) => TryGetDocumentId(name, out _);

    private static bool TryGetDocumentId(string name, out Guid id)
    {
        id = default;
        bool temporary = name.Length == 70 && name[0] == '.' &&
            name.EndsWith(".tmp", StringComparison.Ordinal);
        bool snapshot = name.Length == 69 && name.EndsWith(".png", StringComparison.Ordinal);
        if (!temporary && !snapshot) { return false; }

        int start = temporary ? 1 : 0;
        return name[start + 32] == '-' &&
            Guid.TryParseExact(name.AsSpan(start, 32), "N", out id) &&
            Guid.TryParseExact(name.AsSpan(start + 33, 32), "N", out _);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool EnsureSafeDirectory(string path) => EnsureSafeEntry(path, expectDirectory: true);

    private static bool EnsureSafeFile(string path) => EnsureSafeEntry(path, expectDirectory: false);

    private static bool EnsureSafeEntry(string path, bool expectDirectory)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            ((attributes & FileAttributes.Directory) != 0) != expectDirectory)
        {
            throw new IOException("Export snapshot paths must not be links or unexpected file types.");
        }
        return true;
    }
}
