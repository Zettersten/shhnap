using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class DocumentExportSnapshotsTests
{
    [TestMethod]
    public async Task SnapshotRemainsStableAcrossLaterSaveAndPendingExportCannotBeCopied()
    {
        using var temporary = new TemporaryLibrary();
        Guid id = Guid.NewGuid();
        string sourcePath = temporary.Library.GetExportPath(id);
        Directory.CreateDirectory(temporary.Library.GetDocumentDirectory(id));
        await File.WriteAllTextAsync(sourcePath, "covered");
        var journal = new DocumentSaveJournal(temporary.Library);
        await journal.BeginAsync(id);
        journal.Complete(id);

        var snapshots = new DocumentExportSnapshots(temporary.Library);
        string first = await snapshots.CreateAsync(id, sourcePath);
        Assert.IsTrue(first.StartsWith(Path.Combine(temporary.RootPath, "export-snapshots") +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("covered", await File.ReadAllTextAsync(first));

        await journal.BeginAsync(id);
        await File.WriteAllTextAsync(sourcePath, "uncovered");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => snapshots.CreateAsync(id, sourcePath));
        Assert.AreEqual("covered", await File.ReadAllTextAsync(first));

        journal.Complete(id);
        string second = await snapshots.CreateAsync(id, sourcePath);
        Assert.AreNotEqual(first, second);
        StringAssert.StartsWith(Path.GetFileName(first), $"{id:N}-");
        StringAssert.StartsWith(Path.GetFileName(second), $"{id:N}-");
        Assert.AreEqual("covered", await File.ReadAllTextAsync(first));
        Assert.AreEqual("uncovered", await File.ReadAllTextAsync(second));
        temporary.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task UnverifiedExportAndWrongSourcePathAreRejected()
    {
        using var temporary = new TemporaryLibrary();
        Guid id = Guid.NewGuid();
        string sourcePath = temporary.Library.GetExportPath(id);
        Directory.CreateDirectory(temporary.Library.GetDocumentDirectory(id));
        await File.WriteAllTextAsync(sourcePath, "unverified");
        string previewPath = temporary.Library.GetGalleryPreviewPath(id);
        await File.WriteAllTextAsync(previewPath, "unverified preview");
        var snapshots = new DocumentExportSnapshots(temporary.Library);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => snapshots.CreateAsync(id, sourcePath));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            snapshots.CreateThumbnailAsync(id, previewPath));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => snapshots.CreateAsync(id,
            temporary.Library.GetOriginalPath(id)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => snapshots.CreateThumbnailAsync(id,
            temporary.Library.GetCompactPreviewPath(id)));
        Assert.IsFalse(Directory.Exists(Path.Combine(temporary.RootPath, "export-snapshots")));
    }

    [TestMethod]
    public async Task ThumbnailSnapshotStaysStableAndCannotCopyDuringAPendingSave()
    {
        using var temporary = new TemporaryLibrary();
        (DocumentExportSnapshots snapshots, Guid id, _) = await CreateVerifiedSourceAsync(temporary);
        string previewPath = temporary.Library.GetGalleryPreviewPath(id);
        await File.WriteAllTextAsync(previewPath, "first thumbnail");

        string first = await snapshots.CreateThumbnailAsync(id, previewPath);
        StringAssert.StartsWith(Path.GetFileName(first), $"{id:N}-");
        Assert.AreEqual("first thumbnail", await File.ReadAllTextAsync(first));

        var journal = new DocumentSaveJournal(temporary.Library);
        await journal.BeginAsync(id);
        await File.WriteAllTextAsync(previewPath, "second thumbnail");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            snapshots.CreateThumbnailAsync(id, previewPath));
        Assert.AreEqual("first thumbnail", await File.ReadAllTextAsync(first));

        journal.Complete(id);
        string second = await snapshots.CreateThumbnailAsync(id, previewPath);
        Assert.AreNotEqual(first, second);
        Assert.AreEqual("second thumbnail", await File.ReadAllTextAsync(second));
        Assert.AreEqual("first thumbnail", await File.ReadAllTextAsync(first));
    }

    [TestMethod]
    public async Task CreatingEitherSnapshotKindRemovesExpiredCopiesButKeepsSixDayOldCopies()
    {
        using var temporary = new TemporaryLibrary();
        (DocumentExportSnapshots snapshots, Guid id, string sourcePath) = await CreateVerifiedSourceAsync(temporary);
        string previewPath = temporary.Library.GetGalleryPreviewPath(id);
        await File.WriteAllTextAsync(previewPath, "thumbnail");
        string expiredExport = await snapshots.CreateAsync(id, sourcePath);
        string expiredThumbnail = await snapshots.CreateThumbnailAsync(id, previewPath);
        string retained = await snapshots.CreateAsync(id, sourcePath);
        await snapshots.ScheduledCleanup;
        File.SetLastWriteTimeUtc(expiredExport, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(expiredThumbnail, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(retained, DateTime.UtcNow.AddDays(-6));

        var thumbnailSnapshots = new DocumentExportSnapshots(temporary.Library);
        string newThumbnail = await thumbnailSnapshots.CreateThumbnailAsync(id, previewPath);
        await thumbnailSnapshots.ScheduledCleanup;
        Assert.IsFalse(File.Exists(expiredExport));
        Assert.IsFalse(File.Exists(expiredThumbnail));
        Assert.IsTrue(File.Exists(retained));

        File.SetLastWriteTimeUtc(newThumbnail, DateTime.UtcNow.AddDays(-8));
        var exportSnapshots = new DocumentExportSnapshots(temporary.Library);
        string newExport = await exportSnapshots.CreateAsync(id, sourcePath);
        await exportSnapshots.ScheduledCleanup;
        Assert.IsFalse(File.Exists(newThumbnail));
        Assert.IsTrue(File.Exists(retained));
        Assert.IsTrue(File.Exists(newExport));
    }

    [TestMethod]
    public async Task CleanupDeletesOnlyExpiredSnapshotsAndHonorsTheDeletionLimit()
    {
        using var temporary = new TemporaryLibrary();
        (DocumentExportSnapshots snapshots, Guid id, string sourcePath) = await CreateVerifiedSourceAsync(temporary);
        string oldest = await snapshots.CreateAsync(id, sourcePath);
        string old = await snapshots.CreateAsync(id, sourcePath);
        string recent = await snapshots.CreateAsync(id, sourcePath);
        await snapshots.ScheduledCleanup;
        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddDays(-9));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-6));

        Assert.AreEqual(1, await snapshots.CleanupExpiredAsync(TimeSpan.FromDays(7), maxFiles: 1));
        Assert.AreEqual(2, Directory.GetFiles(Path.GetDirectoryName(oldest)!, "*.png").Length);
        Assert.AreEqual(1, await snapshots.CleanupExpiredAsync(TimeSpan.FromDays(7), maxFiles: 10));
        Assert.IsFalse(File.Exists(oldest));
        Assert.IsFalse(File.Exists(old));
        Assert.IsTrue(File.Exists(recent));
    }

    [TestMethod]
    public async Task FreshEntriesAheadOfExpiredOnesDoNotStarveCleanup()
    {
        using var temporary = new TemporaryLibrary();
        (DocumentExportSnapshots snapshots, Guid id, string sourcePath) = await CreateVerifiedSourceAsync(temporary);
        for (int index = 0; index < 5; index++)
        {
            await snapshots.CreateAsync(id, sourcePath);
        }
        await snapshots.ScheduledCleanup;

        string directory = Path.Combine(temporary.RootPath, "export-snapshots");
        string[] observedOrder = Directory.EnumerateFiles(directory, "*.png").ToArray();
        Assert.AreEqual(5, observedOrder.Length);
        foreach (string fresh in observedOrder.Take(3))
        {
            File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddDays(-1));
        }
        foreach (string expired in observedOrder.Skip(3))
        {
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-9));
        }

        Assert.AreEqual(1, await snapshots.CleanupExpiredAsync(TimeSpan.FromDays(7), maxFiles: 1));
        Assert.AreEqual(1, await snapshots.CleanupExpiredAsync(TimeSpan.FromDays(7), maxFiles: 1));
        Assert.AreEqual(3, Directory.EnumerateFiles(directory, "*.png").Count());
        foreach (string fresh in observedOrder.Take(3)) { Assert.IsTrue(File.Exists(fresh)); }
        foreach (string expired in observedOrder.Skip(3)) { Assert.IsFalse(File.Exists(expired)); }
    }

    [TestMethod]
    public async Task LockedExpiredSnapshotDoesNotBlockCreatingANewSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test requires Windows file-sharing behavior.");
            return;
        }

        using var temporary = new TemporaryLibrary();
        (DocumentExportSnapshots snapshots, Guid id, string sourcePath) = await CreateVerifiedSourceAsync(temporary);
        string expired = await snapshots.CreateAsync(id, sourcePath);
        await snapshots.ScheduledCleanup;
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));

        string created;
        using (var locked = new FileStream(expired, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var nextSnapshots = new DocumentExportSnapshots(temporary.Library);
            created = await nextSnapshots.CreateAsync(id, sourcePath);
            await nextSnapshots.ScheduledCleanup;
            Assert.IsTrue(File.Exists(created));
            Assert.IsTrue(File.Exists(expired));
        }

        Assert.AreEqual(1, await snapshots.CleanupExpiredAsync(TimeSpan.FromDays(7), maxFiles: 10));
        Assert.IsFalse(File.Exists(expired));
        Assert.IsTrue(File.Exists(created));
    }

    [TestMethod]
    public async Task LinkedSnapshotDirectoryIsRejectedWithoutWritingOutsideTheLibrary()
    {
        using var temporary = new TemporaryLibrary();
        (DocumentExportSnapshots snapshots, Guid id, string sourcePath) = await CreateVerifiedSourceAsync(temporary);
        string outside = temporary.GetOwnedPath("outside");
        Directory.CreateDirectory(outside);
        string snapshotDirectory = Path.Combine(temporary.RootPath, "export-snapshots");
        try
        {
            Directory.CreateSymbolicLink(snapshotDirectory, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Inconclusive($"This runner cannot create a directory link: {exception.Message}");
            return;
        }

        await Assert.ThrowsExactlyAsync<IOException>(() => snapshots.CreateAsync(id, sourcePath));
        await Assert.ThrowsExactlyAsync<IOException>(() => snapshots.CleanupExpiredAsync(TimeSpan.FromDays(7), 10));
        Assert.ThrowsExactly<IOException>(() => snapshots.DeleteForDocument(id));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(outside).Length);
    }

    [TestMethod]
    public async Task LinkedExportIsRejectedWithoutCopyingItsTarget()
    {
        using var temporary = new TemporaryLibrary();
        (DocumentExportSnapshots snapshots, Guid id, string sourcePath) = await CreateVerifiedSourceAsync(temporary);
        string outside = temporary.GetOwnedPath("outside.png");
        await File.WriteAllTextAsync(outside, "private bytes");
        File.Delete(sourcePath);
        try
        {
            File.CreateSymbolicLink(sourcePath, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Inconclusive($"This runner cannot create a file link: {exception.Message}");
            return;
        }

        await Assert.ThrowsExactlyAsync<IOException>(() => snapshots.CreateAsync(id, sourcePath));
        Assert.AreEqual("private bytes", await File.ReadAllTextAsync(outside));
        Assert.IsFalse(Directory.Exists(Path.Combine(temporary.RootPath, "export-snapshots")));
    }

    [TestMethod]
    public async Task CleanupSkipsLinksAndUnrecognizedFiles()
    {
        using var temporary = new TemporaryLibrary();
        (DocumentExportSnapshots snapshots, Guid id, string sourcePath) = await CreateVerifiedSourceAsync(temporary);
        string ordinary = await snapshots.CreateAsync(id, sourcePath);
        string directory = Path.GetDirectoryName(ordinary)!;
        string outside = temporary.GetOwnedPath("outside.png");
        await File.WriteAllTextAsync(outside, "leave intact");
        File.SetLastWriteTimeUtc(outside, DateTime.UtcNow.AddDays(-9));
        string link = Path.Combine(directory, $"{id:N}-{Guid.NewGuid():N}.png");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Inconclusive($"This runner cannot create a file link: {exception.Message}");
            return;
        }
        string unrelated = Path.Combine(directory, "notes.txt");
        await File.WriteAllTextAsync(unrelated, "leave intact");
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-9));

        Assert.AreEqual(0, await snapshots.CleanupExpiredAsync(TimeSpan.FromDays(7), 10));
        Assert.IsTrue(File.Exists(link));
        Assert.IsTrue(File.Exists(unrelated));
        Assert.AreEqual("leave intact", await File.ReadAllTextAsync(outside));
    }

    [TestMethod]
    public async Task DeletingOneDocumentRemovesItsSnapshotsAndTempsButLeavesOtherDocumentsAndLinks()
    {
        using var temporary = new TemporaryLibrary();
        (DocumentExportSnapshots snapshots, Guid firstId, string firstSource) = await CreateVerifiedSourceAsync(temporary);
        var (_, secondId, secondSource) = await CreateVerifiedSourceAsync(temporary);
        string first = await snapshots.CreateAsync(firstId, firstSource);
        string second = await snapshots.CreateAsync(firstId, firstSource);
        string firstPreview = temporary.Library.GetGalleryPreviewPath(firstId);
        await File.WriteAllTextAsync(firstPreview, "thumbnail");
        string thumbnail = await snapshots.CreateThumbnailAsync(firstId, firstPreview);
        string other = await snapshots.CreateAsync(secondId, secondSource);
        string directory = Path.GetDirectoryName(first)!;
        string unfinished = Path.Combine(directory, $".{firstId:N}-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(unfinished, "unfinished");
        string outside = temporary.GetOwnedPath("outside.png");
        await File.WriteAllTextAsync(outside, "leave intact");
        string link = Path.Combine(directory, $"{firstId:N}-{Guid.NewGuid():N}.png");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Inconclusive($"This runner cannot create a file link: {exception.Message}");
            return;
        }

        Assert.AreEqual(4, snapshots.DeleteForDocument(firstId));
        Assert.IsFalse(File.Exists(first));
        Assert.IsFalse(File.Exists(second));
        Assert.IsFalse(File.Exists(thumbnail));
        Assert.IsFalse(File.Exists(unfinished));
        Assert.IsTrue(File.Exists(other));
        Assert.IsTrue(File.Exists(link));
        Assert.AreEqual("leave intact", await File.ReadAllTextAsync(outside));
        Assert.AreEqual(0, snapshots.DeleteForDocument(firstId));
    }

    private static async Task<(DocumentExportSnapshots snapshots, Guid id, string sourcePath)>
        CreateVerifiedSourceAsync(TemporaryLibrary temporary)
    {
        Guid id = Guid.NewGuid();
        string sourcePath = temporary.Library.GetExportPath(id);
        Directory.CreateDirectory(temporary.Library.GetDocumentDirectory(id));
        await File.WriteAllTextAsync(sourcePath, "covered");
        var journal = new DocumentSaveJournal(temporary.Library);
        await journal.BeginAsync(id);
        journal.Complete(id);
        return (new DocumentExportSnapshots(temporary.Library), id, sourcePath);
    }
}
