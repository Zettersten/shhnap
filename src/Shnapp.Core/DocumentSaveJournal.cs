namespace Shnapp.Core;

/// <summary>
/// Records an unfinished document save before companion image files change.
/// A pending marker remains until the caller commits metadata and explicitly completes the save.
/// </summary>
/// <param name="library">The library that owns the document directory.</param>
public sealed class DocumentSaveJournal(ShnappLibrary library)
{
    private const string MarkerFileName = ".save-pending";
    private const string VerifiedFileName = ".render-verified";
    private static readonly byte[] PendingBytes = "pending\n"u8.ToArray();
    private static readonly byte[] VerifiedBytes = "verified\n"u8.ToArray();
    private readonly ShnappLibrary _library = library ?? throw new ArgumentNullException(nameof(library));

    /// <summary>Creates and flushes a pending marker before image files are changed.</summary>
    /// <param name="id">The nonempty document identifier.</param>
    /// <param name="cancellationToken">Cancels marker creation before it completes.</param>
    /// <returns>A task that completes only after the marker has been flushed to disk.</returns>
    /// <remarks>An existing safe marker is retained so an earlier incomplete save remains detectable.</remarks>
    public async Task BeginAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = _library.GetDocumentDirectory(id);
        EnsureSafeDirectories(directory, create: true);
        string markerPath = Path.Combine(directory, MarkerFileName);
        EnsureSafeEntry(Path.Combine(directory, VerifiedFileName), expectDirectory: false);
        if (EnsureSafeEntry(markerPath, expectDirectory: false))
        {
            return;
        }

        await using FileStream stream = new(markerPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(PendingBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Checks whether a document has an unfinished save.</summary>
    /// <param name="id">The nonempty document identifier.</param>
    /// <returns>True when a safe pending marker exists; false when it is absent.</returns>
    public bool IsPending(Guid id)
    {
        string directory = _library.GetDocumentDirectory(id);
        EnsureSafeDirectories(directory, create: false);
        return EnsureSafeEntry(Path.Combine(directory, MarkerFileName), expectDirectory: false);
    }

    /// <summary>Checks whether cached images were verified against committed metadata.</summary>
    /// <param name="id">The nonempty document identifier.</param>
    /// <returns>True when a safe verification marker exists; false for legacy documents.</returns>
    public bool IsVerified(Guid id)
    {
        string directory = _library.GetDocumentDirectory(id);
        EnsureSafeDirectories(directory, create: false);
        return EnsureSafeEntry(Path.Combine(directory, VerifiedFileName), expectDirectory: false);
    }

    /// <summary>Removes the marker after all document files have committed.</summary>
    /// <param name="id">The nonempty document identifier.</param>
    /// <remarks>Do not call until metadata and derived image writes have completed successfully.</remarks>
    public void Complete(Guid id)
    {
        string directory = _library.GetDocumentDirectory(id);
        EnsureSafeDirectories(directory, create: false);
        string markerPath = Path.Combine(directory, MarkerFileName);
        string verifiedPath = Path.Combine(directory, VerifiedFileName);
        bool pending = EnsureSafeEntry(markerPath, expectDirectory: false);
        if (!EnsureSafeEntry(verifiedPath, expectDirectory: false))
        {
            using FileStream stream = new(verifiedPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough);
            stream.Write(VerifiedBytes);
            stream.Flush(flushToDisk: true);
        }

        if (pending)
        {
            File.Delete(markerPath);
        }
    }

    /// <summary>Clears both markers after an uncommitted first save has been removed.</summary>
    /// <param name="id">The nonempty document identifier.</param>
    /// <remarks>Remove the orphan image files before calling this method.</remarks>
    public void Abandon(Guid id)
    {
        string directory = _library.GetDocumentDirectory(id);
        EnsureSafeDirectories(directory, create: false);
        string markerPath = Path.Combine(directory, MarkerFileName);
        string verifiedPath = Path.Combine(directory, VerifiedFileName);
        bool pending = EnsureSafeEntry(markerPath, expectDirectory: false);
        bool verified = EnsureSafeEntry(verifiedPath, expectDirectory: false);
        if (verified) { File.Delete(verifiedPath); }
        if (pending) { File.Delete(markerPath); }
    }

    private void EnsureSafeDirectories(string directory, bool create)
    {
        EnsureSafeEntry(_library.RootPath, expectDirectory: true);
        EnsureSafeEntry(Path.Combine(_library.RootPath, "shnapps"), expectDirectory: true);
        EnsureSafeEntry(directory, expectDirectory: true);
        if (create)
        {
            Directory.CreateDirectory(directory);
            EnsureSafeEntry(_library.RootPath, expectDirectory: true);
            EnsureSafeEntry(Path.Combine(_library.RootPath, "shnapps"), expectDirectory: true);
            EnsureSafeEntry(directory, expectDirectory: true);
        }
    }

    private static bool EnsureSafeEntry(string path, bool expectDirectory)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            ((attributes & FileAttributes.Directory) != 0) != expectDirectory)
        {
            throw new IOException("Document save paths must not be links or unexpected file types.");
        }

        return true;
    }
}
