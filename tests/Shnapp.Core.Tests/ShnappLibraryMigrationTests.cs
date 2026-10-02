using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class ShnappLibraryMigrationTests
{
    [TestMethod]
    public async Task ImportCopiesPortableSettingsWhenStoreHasNone()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        await portable.SaveSettingsAsync(new ShnappSettings { Theme = "Light", AutoCopy = true });

        ShnappLibraryMigration.Result result = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);

        Assert.IsTrue(result.Complete);
        Assert.IsTrue(result.SettingsImported);
        ShnappSettings imported = await store.LoadSettingsAsync();
        Assert.AreEqual("Light", imported.Theme);
        Assert.IsTrue(imported.AutoCopy);
        Assert.AreEqual("Light", (await portable.LoadSettingsAsync()).Theme);
    }

    [TestMethod]
    public async Task ImportCopiesPortableLibraryOnceWithoutReplacingStoreData()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument imported = TestDocuments.Create() with { Title = "Portable capture" };
        ShnappDocument existing = TestDocuments.Create() with { Title = "Portable title" };
        await portable.SaveAsync(imported);
        await portable.SaveAsync(existing);
        await portable.SaveSettingsAsync(new ShnappSettings { Theme = "Light" });
        Directory.CreateDirectory(portable.GetDocumentDirectory(imported.Id));
        byte[] original = Convert.FromBase64String(TestDocuments.OnePixelPngBase64);
        await File.WriteAllBytesAsync(portable.GetOriginalPath(imported.Id), original);
        await store.SaveAsync(existing with { Title = "Store title" });
        await store.SaveSettingsAsync(new ShnappSettings { Theme = "Dark" });

        ShnappLibraryMigration.Result first = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);

        Assert.IsTrue(first.Complete);
        Assert.IsTrue(ShnappLibraryMigration.IsComplete(store.RootPath));
        Assert.AreEqual(1, first.ImportedDocuments);
        Assert.IsFalse(first.SettingsImported);
        Assert.AreEqual("Portable capture", (await store.OpenAsync(imported.Id))?.Title);
        Assert.AreEqual("Store title", (await store.OpenAsync(existing.Id))?.Title);
        Assert.AreEqual("Dark", (await store.LoadSettingsAsync()).Theme);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(store.GetOriginalPath(imported.Id)));

        await store.DeleteAsync(imported.Id);
        ShnappLibraryMigration.Result second = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsTrue(second.Complete);
        Assert.AreEqual(0, second.ImportedDocuments);
        Assert.IsNull(await store.OpenAsync(imported.Id));
        Assert.AreEqual("Portable capture", (await portable.OpenAsync(imported.Id))?.Title);
    }

    [TestMethod]
    public async Task InterruptedStagingIsDiscardedBeforeAtomicDocumentImport()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument document = TestDocuments.Create();
        await portable.SaveAsync(document);
        byte[] png = Convert.FromBase64String(TestDocuments.OnePixelPngBase64);
        await File.WriteAllBytesAsync(portable.GetOriginalPath(document.Id), png);
        await File.WriteAllBytesAsync(portable.GetPreviewPath(document.Id), png);
        string stageRoot = Path.Combine(store.RootPath,
            ".portable-import-" + document.Id.ToString("N"));
        string stageDirectory = Path.Combine(stageRoot, "shnapps", document.Id.ToString("N"));
        Directory.CreateDirectory(stageDirectory);
        await File.WriteAllBytesAsync(Path.Combine(stageDirectory, "original.png"), [9]);

        ShnappLibraryMigration.Result result = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);

        Assert.IsTrue(result.Complete);
        Assert.AreEqual(1, result.ImportedDocuments);
        Assert.AreEqual(document, await store.OpenAsync(document.Id));
        CollectionAssert.AreEqual(png, await File.ReadAllBytesAsync(store.GetOriginalPath(document.Id)));
        CollectionAssert.AreEqual(png, await File.ReadAllBytesAsync(store.GetPreviewPath(document.Id)));
        Assert.IsFalse(Directory.Exists(stageRoot));
    }

    [TestMethod]
    public async Task InvalidPortableDocumentDoesNotFinishImportAndCanBeRetried()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument document = TestDocuments.Create();
        Directory.CreateDirectory(portable.GetDocumentDirectory(document.Id));
        await File.WriteAllTextAsync(Path.Combine(portable.GetDocumentDirectory(document.Id), "document.json"), "{}");

        ShnappLibraryMigration.Result first = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);

        Assert.IsFalse(first.Complete);
        Assert.AreEqual(1, first.FailedDocuments);
        Assert.IsNull(await store.OpenAsync(document.Id));
        await portable.SaveAsync(document);

        ShnappLibraryMigration.Result second = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsTrue(second.Complete);
        Assert.AreEqual(1, second.ImportedDocuments);
        Assert.AreEqual(document, await store.OpenAsync(document.Id));
    }

    [TestMethod]
    public async Task IncompleteStoreDirectoryIsPreservedForManualRecovery()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument document = TestDocuments.Create();
        await portable.SaveAsync(document);
        Directory.CreateDirectory(store.GetDocumentDirectory(document.Id));
        byte[] existing = [1, 2, 3];
        await File.WriteAllBytesAsync(store.GetOriginalPath(document.Id), existing);

        ShnappLibraryMigration.Result result = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);

        Assert.IsFalse(result.Complete);
        Assert.AreEqual(1, result.FailedDocuments);
        CollectionAssert.AreEqual(existing, await File.ReadAllBytesAsync(store.GetOriginalPath(document.Id)));
        Assert.IsFalse(File.Exists(Path.Combine(store.GetDocumentDirectory(document.Id), "document.json")));
        Assert.AreEqual(document, await portable.OpenAsync(document.Id));
    }

    [TestMethod]
    public async Task ChangedPortableImageIsNotCommittedOrMarkedComplete()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument document = TestDocuments.Create();
        await portable.SaveAsync(document);
        string sourceImage = portable.GetOriginalPath(document.Id);
        await File.WriteAllBytesAsync(sourceImage,
            Convert.FromBase64String(TestDocuments.OnePixelPngBase64));

        ShnappLibraryMigration.Result interrupted = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath,
            async _ => await File.WriteAllBytesAsync(sourceImage, [1, 2, 3]));

        Assert.IsFalse(interrupted.Complete);
        Assert.IsFalse(ShnappLibraryMigration.IsComplete(store.RootPath));
        Assert.AreEqual(1, interrupted.FailedDocuments);
        Assert.IsNull(await store.OpenAsync(document.Id));
        Assert.IsFalse(File.Exists(Path.Combine(store.RootPath, "portable-library-import-v1.complete")));
        Assert.IsFalse(Directory.Exists(Path.Combine(store.RootPath,
            ".portable-import-" + document.Id.ToString("N"))));

        await File.WriteAllBytesAsync(sourceImage,
            Convert.FromBase64String(TestDocuments.OnePixelPngBase64));
        ShnappLibraryMigration.Result retried = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsTrue(retried.Complete);
        Assert.AreEqual(document, await store.OpenAsync(document.Id));
    }

    [TestMethod]
    public async Task PendingPortableSaveIsRetriedAfterItsTemporaryFileDisappears()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument document = TestDocuments.Create();
        await portable.SaveAsync(document);
        string pending = Path.Combine(portable.GetDocumentDirectory(document.Id),
            ".document.json." + Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(pending, "pending save");

        ShnappLibraryMigration.Result interrupted = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsFalse(interrupted.Complete);
        Assert.AreEqual(1, interrupted.FailedDocuments);
        Assert.IsNull(await store.OpenAsync(document.Id));

        File.Delete(pending);
        ShnappLibraryMigration.Result retried = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsTrue(retried.Complete);
        Assert.AreEqual(document, await store.OpenAsync(document.Id));
    }

    [TestMethod]
    public async Task ChangedPortableMetadataIsImportedOnlyAfterRetry()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument oldDocument = TestDocuments.Create() with { Title = "Old title" };
        ShnappDocument updatedDocument = oldDocument with { Title = "New title" };
        await portable.SaveAsync(oldDocument);

        ShnappLibraryMigration.Result interrupted = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath,
            async _ => await portable.SaveAsync(updatedDocument));

        Assert.IsFalse(interrupted.Complete);
        Assert.IsNull(await store.OpenAsync(oldDocument.Id));
        Assert.IsFalse(ShnappLibraryMigration.IsComplete(store.RootPath));

        ShnappLibraryMigration.Result retried = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsTrue(retried.Complete);
        Assert.AreEqual(updatedDocument, await store.OpenAsync(oldDocument.Id));
    }

    [TestMethod]
    public async Task CorruptStoreDocumentBlocksCompletionWithoutReplacingItsBytes()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument document = TestDocuments.Create();
        await portable.SaveAsync(document);
        Directory.CreateDirectory(store.GetDocumentDirectory(document.Id));
        string storeMetadata = Path.Combine(store.GetDocumentDirectory(document.Id), "document.json");
        await File.WriteAllTextAsync(storeMetadata, "{}");

        ShnappLibraryMigration.Result result = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);

        Assert.IsFalse(result.Complete);
        Assert.AreEqual(1, result.FailedDocuments);
        Assert.AreEqual("{}", await File.ReadAllTextAsync(storeMetadata));
        Assert.IsFalse(File.Exists(Path.Combine(store.RootPath, "portable-library-import-v1.complete")));
        Assert.AreEqual(document, await portable.OpenAsync(document.Id));

        ShnappDocument repaired = document with { Title = "Recovered in Store" };
        await store.SaveAsync(repaired);
        ShnappLibraryMigration.Result retried = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsTrue(retried.Complete);
        Assert.AreEqual(repaired, await store.OpenAsync(document.Id));
    }

    [TestMethod]
    public async Task NewPortableCaptureDuringImportIsPickedUpOnRetry()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument first = TestDocuments.Create();
        ShnappDocument createdDuringImport = TestDocuments.Create();
        await portable.SaveAsync(first);

        ShnappLibraryMigration.Result interrupted = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath,
            async _ => await portable.SaveAsync(createdDuringImport));

        Assert.IsFalse(interrupted.Complete);
        Assert.IsFalse(File.Exists(Path.Combine(store.RootPath, "portable-library-import-v1.complete")));
        Assert.AreEqual(first, await store.OpenAsync(first.Id));
        Assert.IsNull(await store.OpenAsync(createdDuringImport.Id));

        ShnappLibraryMigration.Result retried = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsTrue(retried.Complete);
        Assert.AreEqual(createdDuringImport, await store.OpenAsync(createdDuringImport.Id));
    }
}
