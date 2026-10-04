using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class DocumentSaveJournalTests
{
    [TestMethod]
    public async Task MarkerPersistsUntilCompleteAndSurvivesNewJournalInstance()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument document = TestDocuments.Create();
        var journal = new DocumentSaveJournal(temporary.Library);
        string markerPath = Path.Combine(temporary.Library.GetDocumentDirectory(document.Id), ".save-pending");

        Assert.IsFalse(journal.IsPending(document.Id));
        await journal.BeginAsync(document.Id);
        Assert.IsTrue(File.Exists(markerPath));
        Assert.IsTrue(new DocumentSaveJournal(temporary.Library).IsPending(document.Id));
        Assert.IsFalse(journal.IsVerified(document.Id));

        await temporary.Library.SaveAsync(document);
        Assert.IsTrue(journal.IsPending(document.Id),
            "A metadata commit cannot silently clear an unfinished image save.");
        journal.Complete(document.Id);

        Assert.IsFalse(File.Exists(markerPath));
        Assert.IsFalse(new DocumentSaveJournal(temporary.Library).IsPending(document.Id));
        Assert.IsTrue(new DocumentSaveJournal(temporary.Library).IsVerified(document.Id));
    }

    [TestMethod]
    public async Task AbandonClearsPendingAndVerifiedWithoutVerifyingAnUncommittedDocument()
    {
        using var temporary = new TemporaryLibrary();
        var journal = new DocumentSaveJournal(temporary.Library);
        Guid id = Guid.NewGuid();

        await journal.BeginAsync(id);
        journal.Abandon(id);
        Assert.IsFalse(journal.IsPending(id));
        Assert.IsFalse(journal.IsVerified(id));

        await journal.BeginAsync(id);
        journal.Complete(id);
        await journal.BeginAsync(id);
        journal.Abandon(id);
        Assert.IsFalse(journal.IsPending(id));
        Assert.IsFalse(journal.IsVerified(id));
    }

    [TestMethod]
    public async Task DirectoryAtMarkerPathIsRejectedByAllOperations()
    {
        using var temporary = new TemporaryLibrary();
        Guid id = Guid.NewGuid();
        string directory = temporary.Library.GetDocumentDirectory(id);
        Directory.CreateDirectory(Path.Combine(directory, ".save-pending"));
        var journal = new DocumentSaveJournal(temporary.Library);

        Assert.ThrowsExactly<IOException>(() => journal.IsPending(id));
        await Assert.ThrowsExactlyAsync<IOException>(() => journal.BeginAsync(id));
        Assert.ThrowsExactly<IOException>(() => journal.Complete(id));
    }

    [TestMethod]
    public async Task DirectoryAtVerifiedPathCannotClearPendingMarker()
    {
        using var temporary = new TemporaryLibrary();
        Guid id = Guid.NewGuid();
        var journal = new DocumentSaveJournal(temporary.Library);
        await journal.BeginAsync(id);
        string verifiedPath = Path.Combine(temporary.Library.GetDocumentDirectory(id), ".render-verified");
        Directory.CreateDirectory(verifiedPath);

        Assert.ThrowsExactly<IOException>(() => journal.IsVerified(id));
        await Assert.ThrowsExactlyAsync<IOException>(() => journal.BeginAsync(id));
        Assert.ThrowsExactly<IOException>(() => journal.Complete(id));
        Assert.ThrowsExactly<IOException>(() => journal.Abandon(id));
        Assert.IsTrue(journal.IsPending(id));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LinkedMarkerIsRejectedWithoutChangingItsTarget(bool targetIsMissing)
    {
        using var temporary = new TemporaryLibrary();
        Guid id = Guid.NewGuid();
        string directory = temporary.Library.GetDocumentDirectory(id);
        Directory.CreateDirectory(directory);
        string target = temporary.GetOwnedPath("outside-marker.txt");
        if (!targetIsMissing)
        {
            await File.WriteAllTextAsync(target, "leave intact");
        }
        string markerPath = Path.Combine(directory, ".save-pending");
        try
        {
            File.CreateSymbolicLink(markerPath, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Inconclusive($"This runner cannot create a file link: {exception.Message}");
            return;
        }

        var journal = new DocumentSaveJournal(temporary.Library);
        Assert.ThrowsExactly<IOException>(() => journal.IsPending(id));
        await Assert.ThrowsExactlyAsync<IOException>(() => journal.BeginAsync(id));
        Assert.ThrowsExactly<IOException>(() => journal.Complete(id));
        if (!targetIsMissing)
        {
            Assert.AreEqual("leave intact", await File.ReadAllTextAsync(target));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LinkedVerifiedMarkerCannotClearPendingMarker(bool targetIsMissing)
    {
        using var temporary = new TemporaryLibrary();
        Guid id = Guid.NewGuid();
        var journal = new DocumentSaveJournal(temporary.Library);
        await journal.BeginAsync(id);
        string target = temporary.GetOwnedPath("outside-verified.txt");
        if (!targetIsMissing)
        {
            await File.WriteAllTextAsync(target, "leave intact");
        }
        string verifiedPath = Path.Combine(temporary.Library.GetDocumentDirectory(id), ".render-verified");
        try
        {
            File.CreateSymbolicLink(verifiedPath, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Inconclusive($"This runner cannot create a file link: {exception.Message}");
            return;
        }

        Assert.ThrowsExactly<IOException>(() => journal.IsVerified(id));
        await Assert.ThrowsExactlyAsync<IOException>(() => journal.BeginAsync(id));
        Assert.ThrowsExactly<IOException>(() => journal.Complete(id));
        Assert.ThrowsExactly<IOException>(() => journal.Abandon(id));
        Assert.IsTrue(journal.IsPending(id));
        if (!targetIsMissing)
        {
            Assert.AreEqual("leave intact", await File.ReadAllTextAsync(target));
        }
    }

    [TestMethod]
    public async Task InterruptedExportBeforeMetadataCommitCanBeRecoveredFromCommittedDocument()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument committed = TestDocuments.Create() with
        {
            Annotations = [TestDocuments.Annotation(AnnotationKind.Redaction) with
            {
                RedactionMode = RedactionMode.Solid,
            }],
        };
        await temporary.Library.SaveAsync(committed);
        string exportPath = temporary.Library.GetExportPath(committed.Id);
        await File.WriteAllTextAsync(exportPath, DerivedExport(committed));
        var journal = new DocumentSaveJournal(temporary.Library);

        async Task InterruptAfterDerivedExportAsync()
        {
            await journal.BeginAsync(committed.Id);
            await File.WriteAllTextAsync(exportPath, "uncovered");
            throw new IOException("Injected interruption before metadata commit.");
        }

        await Assert.ThrowsExactlyAsync<IOException>(InterruptAfterDerivedExportAsync);
        ShnappDocument stillCommitted = (await temporary.Library.OpenAsync(committed.Id))!;
        Assert.AreEqual("covered", DerivedExport(stillCommitted));
        Assert.AreEqual("uncovered", await File.ReadAllTextAsync(exportPath),
            "The injected interruption must leave an export that reveals what committed metadata covers.");

        // Recovery derives the export from the last committed document, then clears the marker.
        var reopenedJournal = new DocumentSaveJournal(temporary.Library);
        if (reopenedJournal.IsPending(committed.Id))
        {
            ShnappDocument stored = (await temporary.Library.OpenAsync(committed.Id))!;
            await File.WriteAllTextAsync(exportPath, DerivedExport(stored));
            reopenedJournal.Complete(committed.Id);
        }

        Assert.AreEqual("covered", await File.ReadAllTextAsync(exportPath),
            "Recovery must restore the covered export before the unfinished save is cleared.");
        Assert.IsFalse(reopenedJournal.IsPending(committed.Id));
    }

    [TestMethod]
    public async Task CrashAfterRemovingExportBeforeMetadataCommitRestoresOldCoveredExport()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument committed = TestDocuments.Create() with
        {
            Annotations = [TestDocuments.Annotation(AnnotationKind.Redaction) with
            {
                RedactionMode = RedactionMode.Solid,
            }],
        };
        ShnappDocument proposed = committed with { Annotations = [] };
        await temporary.Library.SaveAsync(committed);
        string exportPath = temporary.Library.GetExportPath(committed.Id);
        await File.WriteAllTextAsync(exportPath, DerivedExport(committed));
        var journal = new DocumentSaveJournal(temporary.Library);
        journal.Complete(committed.Id);

        async Task InterruptBeforeMetadataCommitAsync()
        {
            await using ShnappLibrary.PreparedDocumentSave prepared =
                await temporary.Library.PrepareSaveAsync(proposed);
            await journal.BeginAsync(committed.Id);
            File.Delete(exportPath);
            throw new IOException("Injected crash before metadata commit.");
        }

        IOException crash = await Assert.ThrowsExactlyAsync<IOException>(InterruptBeforeMetadataCommitAsync);
        Assert.AreEqual("Injected crash before metadata commit.", crash.Message);
        Assert.AreEqual("covered", DerivedExport((await temporary.Library.OpenAsync(committed.Id))!),
            "The proposed uncovered metadata must not have committed.");
        Assert.IsFalse(File.Exists(exportPath), "The canonical export must be absent at the crash boundary.");

        var reopenedJournal = new DocumentSaveJournal(temporary.Library);
        Assert.IsTrue(reopenedJournal.IsPending(committed.Id));
        await RecoverExportFromCommittedMetadataAsync(temporary, committed.Id, exportPath, reopenedJournal);
        Assert.AreEqual("covered", await File.ReadAllTextAsync(exportPath));
        Assert.IsFalse(reopenedJournal.IsPending(committed.Id));
        Assert.IsTrue(reopenedJournal.IsVerified(committed.Id));
    }

    [TestMethod]
    public async Task CrashAfterMetadataCommitBeforeExportRegenerationRestoresNewCoveredExport()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument committed = TestDocuments.Create();
        ShnappDocument proposed = committed with
        {
            Annotations = [TestDocuments.Annotation(AnnotationKind.Redaction) with
            {
                RedactionMode = RedactionMode.Solid,
            }],
        };
        await temporary.Library.SaveAsync(committed);
        string exportPath = temporary.Library.GetExportPath(committed.Id);
        await File.WriteAllTextAsync(exportPath, DerivedExport(committed));
        var journal = new DocumentSaveJournal(temporary.Library);
        journal.Complete(committed.Id);

        async Task InterruptAfterMetadataCommitAsync()
        {
            await using ShnappLibrary.PreparedDocumentSave prepared =
                await temporary.Library.PrepareSaveAsync(proposed);
            await journal.BeginAsync(committed.Id);
            File.Delete(exportPath);
            await prepared.CommitAsync();
            throw new IOException("Injected crash before export regeneration.");
        }

        IOException crash = await Assert.ThrowsExactlyAsync<IOException>(InterruptAfterMetadataCommitAsync);
        Assert.AreEqual("Injected crash before export regeneration.", crash.Message);
        Assert.AreEqual("covered", DerivedExport((await temporary.Library.OpenAsync(committed.Id))!),
            "The new redaction metadata must have committed.");
        Assert.IsFalse(File.Exists(exportPath), "The canonical export must be absent at the crash boundary.");

        var reopenedJournal = new DocumentSaveJournal(temporary.Library);
        Assert.IsTrue(reopenedJournal.IsPending(committed.Id));
        await RecoverExportFromCommittedMetadataAsync(temporary, committed.Id, exportPath, reopenedJournal);
        Assert.AreEqual("covered", await File.ReadAllTextAsync(exportPath));
        Assert.IsFalse(reopenedJournal.IsPending(committed.Id));
        Assert.IsTrue(reopenedJournal.IsVerified(committed.Id));
    }

    [TestMethod]
    public async Task LegacyUnverifiedExportIsRestoredFromCoveredMetadata()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument committed = TestDocuments.Create() with
        {
            Annotations = [TestDocuments.Annotation(AnnotationKind.Redaction) with
            {
                RedactionMode = RedactionMode.Solid,
            }],
        };
        await temporary.Library.SaveAsync(committed);
        string exportPath = temporary.Library.GetExportPath(committed.Id);
        await File.WriteAllTextAsync(exportPath, "uncovered");
        var journal = new DocumentSaveJournal(temporary.Library);
        Assert.IsFalse(journal.IsPending(committed.Id));
        Assert.IsFalse(journal.IsVerified(committed.Id));
        Assert.AreEqual("covered", DerivedExport((await temporary.Library.OpenAsync(committed.Id))!));
        Assert.AreEqual("uncovered", await File.ReadAllTextAsync(exportPath));

        // A pre-journal save has no pending marker, but still needs cached-image repair.
        var reopenedJournal = new DocumentSaveJournal(temporary.Library);
        if (reopenedJournal.IsPending(committed.Id) || !reopenedJournal.IsVerified(committed.Id))
        {
            ShnappDocument stored = (await temporary.Library.OpenAsync(committed.Id))!;
            await File.WriteAllTextAsync(exportPath, DerivedExport(stored));
            reopenedJournal.Complete(committed.Id);
        }

        Assert.AreEqual("covered", await File.ReadAllTextAsync(exportPath));
        Assert.IsTrue(reopenedJournal.IsVerified(committed.Id));
        Assert.IsFalse(reopenedJournal.IsPending(committed.Id));
    }

    private static string DerivedExport(ShnappDocument document) =>
        document.Annotations.Any(annotation => annotation.Kind == AnnotationKind.Redaction &&
            annotation.RedactionMode == RedactionMode.Solid) ? "covered" : "uncovered";

    private static async Task RecoverExportFromCommittedMetadataAsync(TemporaryLibrary temporary, Guid id,
        string exportPath, DocumentSaveJournal journal)
    {
        if (journal.IsPending(id) || !journal.IsVerified(id))
        {
            ShnappDocument stored = (await temporary.Library.OpenAsync(id))!;
            await File.WriteAllTextAsync(exportPath, DerivedExport(stored));
            journal.Complete(id);
        }
    }
}
