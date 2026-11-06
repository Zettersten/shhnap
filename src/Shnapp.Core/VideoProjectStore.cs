using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shnapp.Core;

/// <summary>Persists video projects beneath an explicit video-library root.</summary>
/// <param name="rootPath">Directory whose GUID-named children contain projects and media assets.</param>
/// <remarks>Project JSON is replaced atomically. Media files remain immutable and are never rewritten here.</remarks>
public sealed class VideoProjectStore(string rootPath)
{
    private const long MaximumProjectBytes = 8L * 1024 * 1024;
    private const string ProjectFileName = "project.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RootGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = RootGates.GetOrAdd(NormalizeRoot(rootPath),
        static _ => new SemaphoreSlim(1, 1));

    /// <summary>The normalized video-library root.</summary>
    public string RootPath { get; } = NormalizeRoot(rootPath);

    /// <summary>Gets a project's GUID-only directory without creating it.</summary>
    /// <param name="id">Nonempty project identifier.</param>
    /// <returns>The absolute project directory path.</returns>
    public string GetProjectDirectory(Guid id)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        return Path.Combine(RootPath, id.ToString("N"));
    }

    /// <summary>Creates a safe project directory before recording writes its MP4 and WAV files.</summary>
    /// <param name="id">The identifier that will be passed to CreateRecording.</param>
    /// <returns>The absolute GUID-named directory for the new capture.</returns>
    public string PrepareProjectDirectory(Guid id)
    {
        string directory = GetProjectDirectory(id);
        _gate.Wait();
        try
        {
            EnsureSafeDirectory(directory, create: true);
            return directory;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Resolves one declared media asset and rejects links or missing files.</summary>
    /// <param name="project">The project that owns the asset.</param>
    /// <param name="asset">An asset declared by that project.</param>
    /// <returns>An absolute path inside the project directory.</returns>
    public string ResolveAssetPath(VideoProject project, VideoAsset asset)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        VideoProjectValidation.Validate(project);
        if (!project.Assets.Contains(asset))
        {
            throw new ArgumentException("The asset does not belong to this project.", nameof(asset));
        }
        string path = ResolvePath(GetProjectDirectory(project.Id), asset.RelativePath);
        EnsureSafePath(path, isDirectory: false, mustExist: true);
        return path;
    }

    /// <summary>Copies imported image, audio, or video bytes into an immutable project asset file.</summary>
    /// <param name="project">The project receiving the new asset.</param>
    /// <param name="kind">Image, imported audio, or imported video.</param>
    /// <param name="extension">Supported extension, with or without a leading period.</param>
    /// <param name="source">Readable source bytes, such as a clipboard image or selected file.</param>
    /// <param name="duration">Media source duration, or zero for an image.</param>
    /// <param name="cancellationToken">Cancels before the new file is published.</param>
    /// <returns>An asset to add to the project editor, followed by SaveAsync.</returns>
    public async Task<VideoAsset> ImportAssetAsync(VideoProject project, VideoAssetKind kind,
        string extension, Stream source, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (!source.CanRead)
        {
            throw new ArgumentException("The imported media stream must be readable.", nameof(source));
        }
        if (kind is not VideoAssetKind.Image and not VideoAssetKind.ImportedAudio
            and not VideoAssetKind.ImportedVideo)
        {
            throw new ArgumentException("Only image, audio, and video assets can be copied.", nameof(kind));
        }
        VideoProjectValidation.Validate(project);
        string suffix = (extension.StartsWith('.') ? extension[1..] : extension)
            .ToLowerInvariant();
        if (!VideoProjectValidation.SupportsExtension(kind, "." + suffix))
        {
            throw new ArgumentException("The imported media extension is unsupported.", nameof(extension));
        }
        var asset = new VideoAsset(Guid.NewGuid(), kind, $"assets/{Guid.NewGuid():N}.{suffix}", duration);
        VideoProjectValidation.Validate(project with { Assets = project.Assets.Add(asset) });
        long maximumBytes = kind switch
        {
            VideoAssetKind.Image => 128L * 1024 * 1024,
            VideoAssetKind.ImportedVideo => 32L * 1024 * 1024 * 1024,
            _ => 4L * 1024 * 1024 * 1024,
        };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            string projectDirectory = GetProjectDirectory(project.Id);
            EnsureSafeDirectory(projectDirectory, create: true);
            string assetDirectory = Path.Combine(projectDirectory, "assets");
            EnsureSafeDirectory(assetDirectory, create: true);
            string target = ResolvePath(projectDirectory, asset.RelativePath);
            EnsureSafePath(target, isDirectory: false, mustExist: false);
            temporaryPath = Path.Combine(assetDirectory, $".import-{Guid.NewGuid():N}.tmp");
            bool largeMedia = kind is VideoAssetKind.ImportedVideo or VideoAssetKind.ImportedAudio;
            int bufferSize = largeMedia ? 1024 * 1024 : 64 * 1024;
            // Large media imports stay buffered during the copy. The final flush and
            // atomic move still publish a complete asset before project metadata changes.
            FileOptions writeOptions = FileOptions.Asynchronous |
                (largeMedia ? FileOptions.None : FileOptions.WriteThrough);
            await using (FileStream output = new(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, bufferSize, writeOptions))
            {
                byte[] buffer = new byte[bufferSize];
                long written = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += count;
                    if (written > maximumBytes)
                    {
                        throw new InvalidDataException("The imported media file exceeds its size limit.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
                        .ConfigureAwait(false);
                }
                if (written == 0) { throw new InvalidDataException("An imported media file cannot be empty."); }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePath(target, isDirectory: false, mustExist: false);
            File.Move(temporaryPath, target);
            temporaryPath = null;
            return asset;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
            }
            _gate.Release();
        }
    }

    /// <summary>Validates and saves a project without changing any referenced media files.</summary>
    /// <param name="project">Complete valid project whose assets already exist under its directory.</param>
    /// <param name="cancellationToken">Cancels before the atomic metadata replacement.</param>
    /// <returns>A task completed after the JSON is committed.</returns>
    public async Task SaveAsync(VideoProject project, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VideoProjectValidation.Validate(project);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(project, VideoProjectJsonContext.Default.VideoProject);
        if (json.LongLength > MaximumProjectBytes)
        {
            throw new ArgumentException("The video project metadata exceeds 8 MiB.", nameof(project));
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            string directory = GetProjectDirectory(project.Id);
            EnsureSafeDirectory(directory, create: true);
            foreach (VideoAsset asset in project.Assets)
            {
                string assetPath = ResolvePath(directory, asset.RelativePath);
                EnsureSafePath(assetPath, isDirectory: false, mustExist: true);
            }
            string target = Path.Combine(directory, ProjectFileName);
            EnsureSafePath(target, isDirectory: false, mustExist: false);
            temporaryPath = Path.Combine(directory, $".project-{Guid.NewGuid():N}.tmp");
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePath(target, isDirectory: false, mustExist: false);
            File.Move(temporaryPath, target, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
            }
            _gate.Release();
        }
    }

    /// <summary>Opens and validates a project and every referenced media path.</summary>
    /// <param name="id">Project identifier.</param>
    /// <param name="cancellationToken">Cancels reading and validation.</param>
    /// <returns>The immutable project snapshot.</returns>
    public async Task<VideoProject> OpenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = GetProjectDirectory(id);
        EnsureSafeDirectory(directory, create: false);
        string path = Path.Combine(directory, ProjectFileName);
        EnsureSafePath(path, isDirectory: false, mustExist: true);
        FileInfo info = new(path);
        if (info.Length > MaximumProjectBytes)
        {
            throw new InvalidDataException("The video project metadata exceeds 8 MiB.");
        }
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength > MaximumProjectBytes)
        {
            throw new InvalidDataException("The video project metadata exceeds 8 MiB.");
        }
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            RejectDuplicateProperties(json.RootElement);
            VideoProject project = json.RootElement.Deserialize(VideoProjectJsonContext.Default.VideoProject)
                ?? throw new InvalidDataException("The video project JSON is empty.");
            // Projects saved before frame-rate metadata existed retain their 30 fps export rate.
            if (!json.RootElement.TryGetProperty("frameRate", out _))
            {
                project = project with { FrameRate = 30 };
            }
            VideoProjectValidation.Validate(project);
            if (project.Id != id)
            {
                throw new InvalidDataException("The project identifier does not match its directory.");
            }
            foreach (VideoAsset asset in project.Assets)
            {
                string assetPath = ResolvePath(directory, asset.RelativePath);
                EnsureSafePath(assetPath, isDirectory: false, mustExist: true);
            }
            return project;
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("The video project metadata is invalid.", error);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The video project JSON is invalid.", error);
        }
    }

    /// <summary>Lists readable projects, omitting corrupt or incomplete entries.</summary>
    /// <param name="cancellationToken">Cancels directory traversal and reads.</param>
    /// <returns>Recent project summaries, ordered by creation time.</returns>
    public async Task<IReadOnlyList<VideoProjectSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeDirectory(RootPath, create: false);
        if (!Directory.Exists(RootPath)) { return []; }
        var summaries = new List<VideoProjectSummary>();
        foreach (string directory in Directory.EnumerateDirectories(RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id)) { continue; }
            try
            {
                VideoProject project = await OpenAsync(id, cancellationToken).ConfigureAwait(false);
                string recordingPath = project.Assets.Single(static asset =>
                    asset.Kind == VideoAssetKind.Recording).RelativePath;
                summaries.Add(new VideoProjectSummary(project.Id, project.Title, project.CreatedAt,
                    project.Duration, project.Width, project.Height, recordingPath));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or ArgumentException)
            {
                // A damaged or inaccessible capture must not hide healthy library entries.
            }
        }
        return summaries.OrderByDescending(static item => item.CreatedAt)
            .ThenBy(static item => item.Id).ToArray();
    }

    private static string NormalizeRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    private static string ResolvePath(string directory, string relativePath)
    {
        VideoProjectValidation.ValidateRelativePath(relativePath);
        string full = Path.GetFullPath(Path.Combine(directory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("A video asset path escapes its project directory.");
        }
        return full;
    }

    private static void EnsureSafeDirectory(string path, bool create)
    {
        EnsureSafePath(path, isDirectory: true, mustExist: false);
        if (create)
        {
            Directory.CreateDirectory(path);
            EnsureSafePath(path, isDirectory: true, mustExist: true);
        }
    }

    private static void EnsureSafePath(string path, bool isDirectory, bool mustExist)
    {
        string full = Path.GetFullPath(path);
        var components = new Stack<string>();
        string? cursor = full;
        while (cursor is not null)
        {
            components.Push(cursor);
            string? parent = Path.GetDirectoryName(cursor);
            if (parent is null || parent == cursor) { break; }
            cursor = parent;
        }
        while (components.Count > 0)
        {
            string component = components.Pop();
            bool leaf = components.Count == 0;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(component); }
            catch (FileNotFoundException) when (!mustExist || !leaf) { continue; }
            catch (DirectoryNotFoundException) when (!mustExist || !leaf) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Video project paths must not contain links or reparse points.");
            }
            bool actualDirectory = (attributes & FileAttributes.Directory) != 0;
            if (actualDirectory != (!leaf || isDirectory))
            {
                throw new IOException("A video project path has an unexpected file type.");
            }
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException($"Duplicate video project JSON property '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) { RejectDuplicateProperties(item); }
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(VideoProject))]
internal sealed partial class VideoProjectJsonContext : JsonSerializerContext;
