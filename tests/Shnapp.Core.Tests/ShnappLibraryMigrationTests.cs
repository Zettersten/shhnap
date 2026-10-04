using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class ShnappLibraryMigrationTests
{
    private const string ValidOnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";

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
        ShnappDocument imported = CreatePortableDocument() with { Title = "Portable capture" };
        ShnappDocument existing = CreatePortableDocument() with { Title = "Portable title" };
        await SavePortableDocumentAsync(portable, imported);
        await SavePortableDocumentAsync(portable, existing);
        await portable.SaveSettingsAsync(new ShnappSettings { Theme = "Light" });
        byte[] original = Convert.FromBase64String(ValidOnePixelPngBase64);
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
        ShnappDocument document = CreatePortableDocument();
        await SavePortableDocumentAsync(portable, document);
        byte[] png = Convert.FromBase64String(ValidOnePixelPngBase64);
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
        ShnappDocument document = CreatePortableDocument();
        Directory.CreateDirectory(portable.GetDocumentDirectory(document.Id));
        await File.WriteAllTextAsync(Path.Combine(portable.GetDocumentDirectory(document.Id), "document.json"), "{}");

        ShnappLibraryMigration.Result first = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);

        Assert.IsFalse(first.Complete);
        Assert.AreEqual(1, first.FailedDocuments);
        Assert.IsNull(await store.OpenAsync(document.Id));
        await SavePortableDocumentAsync(portable, document);

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
        ShnappDocument document = CreatePortableDocument();
        await SavePortableDocumentAsync(portable, document);
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
        ShnappDocument document = CreatePortableDocument();
        await SavePortableDocumentAsync(portable, document);
        string sourceImage = portable.GetOriginalPath(document.Id);

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
            Convert.FromBase64String(ValidOnePixelPngBase64));
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
        ShnappDocument document = CreatePortableDocument();
        await SavePortableDocumentAsync(portable, document);
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
    public async Task PendingImageSaveMarkerPreventsImportUntilTheSaveFinishes()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument document = CreatePortableDocument();
        await SavePortableDocumentAsync(portable, document);
        string pending = Path.Combine(portable.GetDocumentDirectory(document.Id), ".save-pending");
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
    public async Task PendingImageSaveMarkerBlocksCompletionForAnExistingStoreDocument()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument document = CreatePortableDocument();
        await SavePortableDocumentAsync(portable, document);
        await store.SaveAsync(document with { Title = "Store title" });
        string pending = Path.Combine(portable.GetDocumentDirectory(document.Id), ".save-pending");
        await File.WriteAllTextAsync(pending, "pending save");

        ShnappLibraryMigration.Result interrupted = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsFalse(interrupted.Complete);
        Assert.AreEqual(1, interrupted.FailedDocuments);
        Assert.IsFalse(ShnappLibraryMigration.IsComplete(store.RootPath));

        File.Delete(pending);
        ShnappLibraryMigration.Result retried = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsTrue(retried.Complete);
        Assert.AreEqual("Store title", (await store.OpenAsync(document.Id))?.Title);
    }

    [TestMethod]
    public async Task DirectoryAtPendingSaveMarkerPathBlocksImport()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument document = CreatePortableDocument();
        await SavePortableDocumentAsync(portable, document);
        string markerPath = Path.Combine(portable.GetDocumentDirectory(document.Id), ".save-pending");
        Directory.CreateDirectory(markerPath);

        ShnappLibraryMigration.Result interrupted = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsFalse(interrupted.Complete);
        Assert.AreEqual(1, interrupted.FailedDocuments);
        Assert.IsNull(await store.OpenAsync(document.Id));

        Directory.Delete(markerPath);
        ShnappLibraryMigration.Result retried = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsTrue(retried.Complete);
        Assert.AreEqual(document, await store.OpenAsync(document.Id));
    }

    [TestMethod]
    public async Task DanglingLinkAtPendingSaveMarkerPathBlocksImport()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument document = CreatePortableDocument();
        await SavePortableDocumentAsync(portable, document);
        string markerPath = Path.Combine(portable.GetDocumentDirectory(document.Id), ".save-pending");
        try
        {
            File.CreateSymbolicLink(markerPath, temporary.GetOwnedPath("missing-marker-target"));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Inconclusive($"This runner cannot create a file link: {exception.Message}");
            return;
        }

        ShnappLibraryMigration.Result interrupted = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsFalse(interrupted.Complete);
        Assert.AreEqual(1, interrupted.FailedDocuments);
        Assert.IsNull(await store.OpenAsync(document.Id));

        File.Delete(markerPath);
        ShnappLibraryMigration.Result retried = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsTrue(retried.Complete);
        Assert.AreEqual(document, await store.OpenAsync(document.Id));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("invalid signature")]
    [DataRow("wrong dimensions")]
    [DataRow("truncated")]
    [DataRow("corrupt IDAT")]
    [DataRow("truncated IDAT")]
    public async Task InvalidOriginalPngPreventsImportUntilRepaired(string corruption)
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument document = CreatePortableDocument();
        await SavePortableDocumentAsync(portable, document);
        string originalPath = portable.GetOriginalPath(document.Id);
        byte[] validPng = Convert.FromBase64String(ValidOnePixelPngBase64);
        int middleIdatByte = validPng.AsSpan().IndexOf("IDAT"u8) + 8;
        switch (corruption)
        {
            case "missing":
                File.Delete(originalPath);
                break;
            case "invalid signature":
                await File.WriteAllBytesAsync(originalPath, [1, 2, 3]);
                break;
            case "wrong dimensions":
                byte[] wrongDimensions = (byte[])validPng.Clone();
                wrongDimensions[19] = 2;
                // Keep the IHDR checksum valid so this exercises the metadata dimension check.
                new byte[] { 94, 43, 183, 1 }.CopyTo(wrongDimensions, 29);
                await File.WriteAllBytesAsync(originalPath, wrongDimensions);
                break;
            case "truncated":
                await File.WriteAllBytesAsync(originalPath, validPng[..33]);
                break;
            case "corrupt IDAT":
                byte[] corruptIdat = (byte[])validPng.Clone();
                corruptIdat[middleIdatByte] ^= 1;
                await File.WriteAllBytesAsync(originalPath, corruptIdat);
                break;
            case "truncated IDAT":
                byte[] truncatedIdat = new byte[validPng.Length - 1];
                validPng.AsSpan(0, middleIdatByte).CopyTo(truncatedIdat);
                validPng.AsSpan(middleIdatByte + 1).CopyTo(truncatedIdat.AsSpan(middleIdatByte));
                await File.WriteAllBytesAsync(originalPath, truncatedIdat);
                break;
        }

        ShnappLibraryMigration.Result interrupted = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsFalse(interrupted.Complete);
        Assert.AreEqual(1, interrupted.FailedDocuments);
        Assert.IsFalse(ShnappLibraryMigration.IsComplete(store.RootPath));
        Assert.IsNull(await store.OpenAsync(document.Id));

        await File.WriteAllBytesAsync(originalPath, validPng);
        ShnappLibraryMigration.Result retried = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsTrue(retried.Complete);
        Assert.AreEqual(1, retried.ImportedDocuments);
        Assert.AreEqual(document, await store.OpenAsync(document.Id));
        CollectionAssert.AreEqual(validPng, await File.ReadAllBytesAsync(store.GetOriginalPath(document.Id)));
    }

    [TestMethod]
    public async Task ChangedPortableMetadataIsImportedOnlyAfterRetry()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument oldDocument = CreatePortableDocument() with { Title = "Old title" };
        ShnappDocument updatedDocument = oldDocument with { Title = "New title" };
        await SavePortableDocumentAsync(portable, oldDocument);

        ShnappLibraryMigration.Result interrupted = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath,
            async _ => await SavePortableDocumentAsync(portable, updatedDocument));

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
        ShnappDocument document = CreatePortableDocument();
        await SavePortableDocumentAsync(portable, document);
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
        ShnappDocument first = CreatePortableDocument();
        ShnappDocument createdDuringImport = CreatePortableDocument();
        await SavePortableDocumentAsync(portable, first);

        ShnappLibraryMigration.Result interrupted = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath,
            async _ => await SavePortableDocumentAsync(portable, createdDuringImport));

        Assert.IsFalse(interrupted.Complete);
        Assert.IsFalse(File.Exists(Path.Combine(store.RootPath, "portable-library-import-v1.complete")));
        Assert.AreEqual(first, await store.OpenAsync(first.Id));
        Assert.IsNull(await store.OpenAsync(createdDuringImport.Id));

        ShnappLibraryMigration.Result retried = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);
        Assert.IsTrue(retried.Complete);
        Assert.AreEqual(createdDuringImport, await store.OpenAsync(createdDuringImport.Id));
    }

    private static ShnappDocument CreatePortableDocument() => TestDocuments.Create() with
    {
        PixelWidth = 1,
        PixelHeight = 1,
    };

    private static async Task SavePortableDocumentAsync(ShnappLibrary portable, ShnappDocument document)
    {
        await portable.SaveAsync(document);
        string originalPath = portable.GetOriginalPath(document.Id);
        if (!File.Exists(originalPath))
        {
            await File.WriteAllBytesAsync(originalPath,
                Convert.FromBase64String(ValidOnePixelPngBase64));
        }
    }
}
