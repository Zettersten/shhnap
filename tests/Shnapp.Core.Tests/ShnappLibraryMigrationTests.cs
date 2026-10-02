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
    public async Task InterruptedImportResumesAndKeepsExistingStoreFiles()
    {
        using var temporary = new TemporaryLibrary();
        ShnappLibrary portable = temporary.Library;
        var store = new ShnappLibrary(temporary.GetOwnedPath("store"));
        ShnappDocument document = TestDocuments.Create();
        await portable.SaveAsync(document);
        byte[] png = Convert.FromBase64String(TestDocuments.OnePixelPngBase64);
        await File.WriteAllBytesAsync(portable.GetOriginalPath(document.Id), png);
        await File.WriteAllBytesAsync(portable.GetPreviewPath(document.Id), png);
        Directory.CreateDirectory(store.GetDocumentDirectory(document.Id));
        byte[] storeOriginal = [1, 2, 3];
        await File.WriteAllBytesAsync(store.GetOriginalPath(document.Id), storeOriginal);
        string staleCopy = store.GetPreviewPath(document.Id) + ".portable-import.tmp";
        await File.WriteAllBytesAsync(staleCopy, [9]);

        ShnappLibraryMigration.Result result = await ShnappLibraryMigration.ImportOnceAsync(
            portable.RootPath, store.RootPath);

        Assert.IsTrue(result.Complete);
        Assert.AreEqual(1, result.ImportedDocuments);
        Assert.AreEqual(document, await store.OpenAsync(document.Id));
        CollectionAssert.AreEqual(storeOriginal, await File.ReadAllBytesAsync(store.GetOriginalPath(document.Id)));
        CollectionAssert.AreEqual(png, await File.ReadAllBytesAsync(store.GetPreviewPath(document.Id)));
        Assert.IsFalse(File.Exists(staleCopy));
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
}
