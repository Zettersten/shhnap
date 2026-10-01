using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Shnapp.Core;

/// <summary>
/// Persists editable shnapps and preferences beneath an explicitly supplied local root.
/// Document locations depend only on nonempty GUIDs, never user-facing titles.
/// </summary>
/// <param name="rootPath">The local data directory; construction does not create it.</param>
/// <remarks>
/// JSON writes use unique same-directory temporary files and atomic replacement.
/// Metadata operations sharing a normalized root are coordinated asynchronously in-process,
/// so concurrent callers see complete snapshots on both NTFS and ReFS.
/// Image-file creation and rendering belong to the application, not this metadata store.
/// </remarks>
public sealed class ShnappLibrary(string rootPath)
{
    private const long MaximumDocumentBytes = 256L * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RootAccessGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _access = RootAccessGates.GetOrAdd(NormalizeRoot(rootPath), static _ => new(1, 1));

    /// <summary>Gets the fully qualified, normalized local data root.</summary>
    public string RootPath { get; } = NormalizeRoot(rootPath);

    /// <summary>Gets the GUID-only directory for a shnapp, without creating it.</summary>
    /// <param name="id">The nonempty shnapp identifier.</param>
    /// <returns>The root/shnapps/GUID-N directory.</returns>
    /// <exception cref="ArgumentException">The identifier is empty.</exception>
    public string GetDocumentDirectory(Guid id)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        return Path.Combine(RootPath, "shnapps", id.ToString("N"));
    }

    /// <summary>Gets the location of the unmodified original PNG.</summary>
    /// <param name="id">The nonempty shnapp identifier.</param>
    /// <returns>The document directory's original.png path.</returns>
    public string GetOriginalPath(Guid id) => Path.Combine(GetDocumentDirectory(id), "original.png");

    /// <summary>Gets the location of the library preview PNG.</summary>
    /// <param name="id">The nonempty shnapp identifier.</param>
    /// <returns>The document directory's preview.png path.</returns>
    public string GetPreviewPath(Guid id) => Path.Combine(GetDocumentDirectory(id), "preview.png");

    /// <summary>Gets the smaller preview used by compact library cards and rows.</summary>
    public string GetCompactPreviewPath(Guid id) => Path.Combine(GetDocumentDirectory(id), "preview-compact.png");

    /// <summary>Gets the square preview used by the editor gallery dock.</summary>
    public string GetGalleryPreviewPath(Guid id) => Path.Combine(GetDocumentDirectory(id), "preview-dock.png");

    /// <summary>Gets the location of the default flattened export PNG.</summary>
    /// <param name="id">The nonempty shnapp identifier.</param>
    /// <returns>The document directory's shnapp.png path.</returns>
    public string GetExportPath(Guid id) => Path.Combine(GetDocumentDirectory(id), "shnapp.png");

    /// <summary>Validates and atomically writes an editable document, creating its directory as needed.</summary>
    /// <param name="document">A supported, valid document with consecutive step numbering.</param>
    /// <param name="cancellationToken">Cancels waiting or writing before the atomic commit.</param>
    /// <returns>A task completed after the metadata has been atomically replaced.</returns>
    /// <exception cref="ArgumentException">The document violates its schema or canvas bounds.</exception>
    /// <remarks>Cancellation observed before replacement leaves the previous document unchanged.</remarks>
    public async Task SaveAsync(ShnappDocument document, CancellationToken cancellationToken = default)
    {
        await using PreparedDocumentSave prepared = await PrepareSaveAsync(document, cancellationToken)
            .ConfigureAwait(false);
        await prepared.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serializes and validates metadata before any companion PNG files are replaced.</summary>
    /// <param name="document">The complete editable document to prepare.</param>
    /// <param name="cancellationToken">Cancels waiting or preparation.</param>
    /// <returns>A prepared save that must be committed or disposed.</returns>
    /// <remarks>Disposing without committing leaves the existing metadata untouched.</remarks>
    public Task<PreparedDocumentSave> PrepareSaveAsync(ShnappDocument document,
        CancellationToken cancellationToken = default) =>
        PrepareSaveAsync(document, MaximumDocumentBytes, cancellationToken);

    internal async Task<PreparedDocumentSave> PrepareSaveAsync(ShnappDocument document,
        long maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, MaximumDocumentBytes);
        cancellationToken.ThrowIfCancellationRequested();
        DocumentValidation.Validate(document);
        long estimatedBytes = document.Annotations.Aggregate(0L, static (total, annotation) =>
            total + (annotation.ImagePngBase64?.Length ?? 0) + (long)annotation.Text.Length * 4 + 4096);
        if (estimatedBytes > maximumBytes)
        {
            throw new ArgumentException("This shnapp exceeds the editable document size limit.", nameof(document));
        }
        string directory = GetDocumentDirectory(document.Id);
        await _access.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSafeDirectories(directory, create: true);
            string path = Path.Combine(directory, "document.json");
            string temporaryPath = await WriteTemporaryAsync(path, document,
                ShnappJsonContext.Default.ShnappDocument, cancellationToken,
                maximumBytes).ConfigureAwait(false);
            return new PreparedDocumentSave(_access, temporaryPath, path);
        }
        catch
        {
            _access.Release();
            throw;
        }
    }

    /// <summary>A fully serialized metadata save awaiting its atomic replacement.</summary>
    public sealed class PreparedDocumentSave : IAsyncDisposable
    {
        private readonly SemaphoreSlim _access;
        private readonly string _temporaryPath;
        private readonly string _path;
        private bool _committed;
        private bool _disposed;

        internal PreparedDocumentSave(SemaphoreSlim access, string temporaryPath, string path)
        {
            _access = access;
            _temporaryPath = temporaryPath;
            _path = path;
        }

        /// <summary>Atomically replaces document.json with the prepared metadata.</summary>
        /// <param name="cancellationToken">Cancels before replacement.</param>
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed)
            {
                return;
            }

            await CommitAtomicAsync(_temporaryPath, _path, cancellationToken).ConfigureAwait(false);
            _committed = true;
        }

        /// <summary>Releases the metadata gate and deletes an uncommitted temporary file.</summary>
        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                DeleteTemporaryFile(_temporaryPath);
                _access.Release();
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Reads and validates a stored document without changing any image files.</summary>
    /// <param name="id">The nonempty identifier whose directory must match the stored document.</param>
    /// <param name="cancellationToken">Cancels asynchronous reading.</param>
    /// <returns>The document, or null only when its metadata file does not exist.</returns>
    /// <exception cref="InvalidDataException">Metadata is corrupt, incomplete, or uses an unsupported schema.</exception>
    public async Task<ShnappDocument?> OpenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = GetDocumentDirectory(id);
        await _access.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSafeDirectories(directory, create: false);
            string path = Path.Combine(directory, "document.json");
            EnsureNotReparsePoint(path);
            try
            {
                await using FileStream stream = OpenRead(path);
                if (stream.Length > MaximumDocumentBytes)
                {
                    throw new InvalidDataException("This shnapp exceeds the 256 MiB editable document limit.");
                }
                using JsonDocument json = await JsonDocument.ParseAsync(stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                JsonSchema.ValidateDocument(json.RootElement);
                ShnappDocument document = json.RootElement.Deserialize(ShnappJsonContext.Default.ShnappDocument)
                    ?? throw new InvalidDataException("The stored document is null.");
                DocumentValidation.Validate(document);
                if (document.Id != id)
                {
                    throw new InvalidDataException("The document identifier does not match its directory.");
                }

                return document;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The stored document is not valid Shnapp JSON.", exception);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The stored document contains invalid values.", exception);
            }
        }
        finally
        {
            _access.Release();
        }
    }

    /// <summary>Lists readable, valid documents newest first, skipping corrupt, unsupported, or inaccessible items.</summary>
    /// <param name="cancellationToken">Cancels enumeration or an individual asynchronous read.</param>
    /// <returns>A stable metadata snapshot; missing or empty libraries produce an empty list.</returns>
    /// <remarks>Failure to access the library root is surfaced rather than disguised as an empty library.</remarks>
    public Task<IReadOnlyList<ShnappDocument>> ListAsync(CancellationToken cancellationToken = default) =>
        ListReadableAsync(OpenAsync, static document => document.CreatedAt,
            static document => document.Id, cancellationToken);

    /// <summary>Lists display metadata without deserializing embedded image layers.</summary>
    /// <param name="cancellationToken">Cancels enumeration or an individual asynchronous read.</param>
    /// <returns>Newest-first summaries; missing or empty libraries produce an empty list.</returns>
    /// <remarks>
    /// The entire JSON is parsed for structural checks, but pasted-image strings are not retained.
    /// Top-level values are validated here. A document with an invalid annotation value can appear
    /// in the library; OpenAsync rejects it before editing or mutation.
    /// </remarks>
    public Task<IReadOnlyList<ShnappSummary>> ListSummariesAsync(
        CancellationToken cancellationToken = default) =>
        ListReadableAsync(OpenSummaryAsync, static summary => summary.CreatedAt,
            static summary => summary.Id, cancellationToken);

    private async Task<IReadOnlyList<T>> ListReadableAsync<T>(
        Func<Guid, CancellationToken, Task<T?>> open,
        Func<T, DateTimeOffset> createdAt, Func<T, Guid> identity,
        CancellationToken cancellationToken) where T : class
    {
        IReadOnlyList<Guid> ids = await ListDocumentIdsAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<T>(ids.Count);
        foreach (Guid id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                T? item = await open(id, cancellationToken).ConfigureAwait(false);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
            catch (InvalidDataException)
            {
                // A damaged or future-version item must not hide the rest of the library.
            }
            catch (IOException)
            {
                // A concurrently removed, locked, or linked item does not prevent listing other items.
            }
            catch (UnauthorizedAccessException)
            {
                // Permissions on individual items can differ from the accessible library root.
            }
        }

        return items.OrderByDescending(createdAt).ThenBy(identity).ToArray();
    }

    private async Task<IReadOnlyList<Guid>> ListDocumentIdsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string libraryPath = Path.Combine(RootPath, "shnapps");
        EnsureSafeDirectories(libraryPath, create: false);
        string[] directories;
        try
        {
            directories = await Task.Run(() => Directory.GetDirectories(libraryPath),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        var ids = new List<Guid>(directories.Length);
        foreach (string directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id) && id != Guid.Empty)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private async Task<ShnappSummary?> OpenSummaryAsync(Guid id, CancellationToken cancellationToken)
    {
        string directory = GetDocumentDirectory(id);
        await _access.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSafeDirectories(directory, create: false);
            string path = Path.Combine(directory, "document.json");
            EnsureNotReparsePoint(path);
            try
            {
                await using FileStream stream = OpenRead(path);
                if (stream.Length > MaximumDocumentBytes)
                {
                    throw new InvalidDataException("This shnapp exceeds the 256 MiB editable document limit.");
                }

                using JsonDocument json = await JsonDocument.ParseAsync(stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                JsonElement root = json.RootElement;
                JsonSchema.ValidateDocument(root);
                ShnappDocument header = ReadSummaryHeader(root);
                DocumentValidation.Validate(header);
                if (header.Id != id)
                {
                    throw new InvalidDataException("The document identifier does not match its directory.");
                }

                return new ShnappSummary(id, header.Title, header.CreatedAt,
                    header.CaptureKind, header.Viewport);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The stored document is not valid Shnapp JSON.", exception);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or
                InvalidOperationException or KeyNotFoundException)
            {
                throw new InvalidDataException("The stored document contains invalid values.", exception);
            }
        }
        finally
        {
            _access.Release();
        }
    }

    private static ShnappDocument ReadSummaryHeader(JsonElement root)
    {
        JsonElement capture = root.GetProperty("captureKind");
        CaptureKind kind = capture.ValueKind switch
        {
            JsonValueKind.String when Enum.TryParse(capture.GetString(), ignoreCase: true,
                out CaptureKind parsed) => parsed,
            JsonValueKind.Number => (CaptureKind)capture.GetInt32(),
            _ => throw new InvalidDataException("The stored capture type is invalid."),
        };

        return new ShnappDocument
        {
            SchemaVersion = root.GetProperty("schemaVersion").GetInt32(),
            Id = root.GetProperty("id").GetGuid(),
            Title = root.GetProperty("title").GetString()!,
            CreatedAt = root.GetProperty("createdAt").GetDateTimeOffset(),
            CaptureKind = kind,
            PixelWidth = root.GetProperty("pixelWidth").GetInt32(),
            PixelHeight = root.GetProperty("pixelHeight").GetInt32(),
            Crop = ReadRectangle(root.GetProperty("crop")),
            BaseImageCrop = root.TryGetProperty("baseImageCrop", out JsonElement baseCrop)
                ? ReadRectangle(baseCrop) : null,
            HideOriginalImage = root.TryGetProperty("hideOriginalImage", out JsonElement hideOriginal) &&
                hideOriginal.GetBoolean(),
            ExpandedCanvasBounds = root.TryGetProperty("expandedCanvasBounds", out JsonElement expanded)
                ? ReadRectangle(expanded) : null,
            HasWindowShadow = root.GetProperty("hasWindowShadow").GetBoolean(),
            Annotations = [],
        };
    }

    private static ImageRect? ReadRectangle(JsonElement element) => element.ValueKind == JsonValueKind.Null
        ? null
        : new ImageRect(element.GetProperty("x").GetDouble(), element.GetProperty("y").GetDouble(),
            element.GetProperty("width").GetDouble(), element.GetProperty("height").GetDouble());

    /// <summary>Deletes only the identified shnapp directory and its image/metadata files.</summary>
    /// <param name="id">The nonempty identifier to delete; a missing directory is a no-op.</param>
    /// <param name="cancellationToken">Cancels waiting or validation before deletion begins.</param>
    /// <returns>A task completed after the owned directory has been removed.</returns>
    /// <remarks>Directory deletion cannot be interrupted once the filesystem operation begins.</remarks>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = GetDocumentDirectory(id);
        await _access.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                EnsureSafeDirectories(directory, create: false);
                if (!Directory.Exists(directory))
                {
                    return;
                }

                EnsureSafeTree(directory, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Delete(directory, recursive: true);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _access.Release();
        }
    }

    /// <summary>Loads validated preferences or returns safe defaults when settings.json is absent.</summary>
    /// <param name="cancellationToken">Cancels asynchronous reading.</param>
    /// <returns>The stored preferences, with startup disabled by default for a new library.</returns>
    /// <exception cref="InvalidDataException">Settings are corrupt, incomplete, or use an unsupported schema.</exception>
    public async Task<ShnappSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _access.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSafeDirectories(RootPath, create: false);
            string path = Path.Combine(RootPath, "settings.json");
            EnsureNotReparsePoint(path);
            try
            {
                await using FileStream stream = OpenRead(path);
                using JsonDocument json = await JsonDocument.ParseAsync(stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                JsonSchema.ValidateSettings(json.RootElement);
                SettingsFile file = json.RootElement.Deserialize(ShnappJsonContext.Default.SettingsFile)
                    ?? throw new InvalidDataException("The stored settings are null.");
                DocumentValidation.ValidateSettings(file.Settings);
                return file.Settings;
            }
            catch (FileNotFoundException)
            {
                return new ShnappSettings();
            }
            catch (DirectoryNotFoundException)
            {
                return new ShnappSettings();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The stored settings are not valid Shnapp JSON.", exception);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The stored settings contain invalid values.", exception);
            }
        }
        finally
        {
            _access.Release();
        }
    }

    /// <summary>Validates and atomically saves preferences without applying startup or OS configuration changes.</summary>
    /// <param name="settings">Preferences whose theme is System, Light, or Dark.</param>
    /// <param name="cancellationToken">Cancels waiting or writing before the atomic commit.</param>
    /// <returns>A task completed after settings.json has been replaced.</returns>
    public async Task SaveSettingsAsync(ShnappSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentValidation.ValidateSettings(settings);
        await _access.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSafeDirectories(RootPath, create: true);
            var file = new SettingsFile(DocumentValidation.SchemaVersion, settings);
            await WriteAtomicAsync(Path.Combine(RootPath, "settings.json"), file,
                ShnappJsonContext.Default.SettingsFile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _access.Release();
        }
    }

    private static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            bufferSize: 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task WriteAtomicAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken, long maximumBytes = long.MaxValue)
    {
        string temporaryPath = await WriteTemporaryAsync(path, value, typeInfo, cancellationToken,
            maximumBytes).ConfigureAwait(false);
        try
        {
            await CommitAtomicAsync(temporaryPath, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    private static async Task<string> WriteTemporaryAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken, long maximumBytes)
    {
        string temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length > maximumBytes)
                {
                    throw new ArgumentException("This shnapp exceeds the 256 MiB editable document limit.", nameof(value));
                }
            }

            return temporaryPath;
        }
        catch
        {
            if (created)
            {
                DeleteTemporaryFile(temporaryPath);
            }

            throw;
        }
    }

    private static void DeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
            // A failed cleanup leaves only an ignored, uniquely named temporary file.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original write/cancellation error if cleanup is denied.
        }
    }

    private static async Task CommitAtomicAsync(string temporaryPath, string path, CancellationToken cancellationToken)
    {
        const int retryLimit = 5;
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNotReparsePoint(path);
            try
            {
                File.Move(temporaryPath, path, overwrite: true);
                return;
            }
            catch (Exception exception) when (attempt < retryLimit && IsTransientWindowsMoveFailure(exception) &&
                File.Exists(temporaryPath) && !Directory.Exists(path))
            {
                await Task.Delay(20 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientWindowsMoveFailure(Exception exception) =>
        OperatingSystem.IsWindows() &&
        exception is IOException or UnauthorizedAccessException &&
        (exception.HResult & 0xFFFF) is 5 or 32 or 33;

    private void EnsureSafeDirectories(string directory, bool create)
    {
        EnsureNotReparsePoint(RootPath);
        EnsureNotReparsePoint(Path.Combine(RootPath, "shnapps"));
        EnsureNotReparsePoint(directory);
        if (create)
        {
            Directory.CreateDirectory(directory);
            EnsureNotReparsePoint(RootPath);
            EnsureNotReparsePoint(Path.Combine(RootPath, "shnapps"));
            EnsureNotReparsePoint(directory);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Library files and directories must not be symbolic links or reparse points.");
            }
        }
        catch (FileNotFoundException)
        {
            // Missing locations are valid for reads or will be created by a save.
        }
        catch (DirectoryNotFoundException)
        {
            // Missing locations are valid for reads or will be created by a save.
        }
    }

    private static void EnsureSafeTree(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Refusing to delete a shnapp directory containing linked files or directories.");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                EnsureSafeTree(entry, cancellationToken);
            }
        }
    }
}
