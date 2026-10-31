using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class ShnappVideoLibraryMigrationTests
{
    [TestMethod]
    public async Task ExistingPhotoMarkerDoesNotSkipCompleteVideoProjects()
    {
        using var temp = new TemporaryLibrary();
        string portableRoot = temp.RootPath;
        string packagedRoot = temp.GetOwnedPath("store");
        var portable = new VideoProjectStore(Path.Combine(portableRoot, "videos"));
        var packaged = new VideoProjectStore(Path.Combine(packagedRoot, "videos"));
        VideoProject project = await SaveProjectAsync(portable);
        Directory.CreateDirectory(packagedRoot);
        await File.WriteAllTextAsync(Path.Combine(packagedRoot,
            "portable-library-import-v1.complete"), "photos imported");

        ShnappVideoLibraryMigration.Result result = await
            ShnappVideoLibraryMigration.ImportOnceAsync(portableRoot, packagedRoot);

        Assert.IsTrue(result.Complete);
        Assert.AreEqual(1, result.ImportedProjects);
        Assert.AreEqual(0, result.FailedProjects);
        Assert.IsTrue(ShnappVideoLibraryMigration.IsComplete(packagedRoot));
        VideoProject copied = await packaged.OpenAsync(project.Id);
        Assert.AreEqual(project.Title, copied.Title);
        Assert.AreEqual(project.Assets.Length, copied.Assets.Length);
        foreach (VideoAsset asset in project.Assets)
        {
            CollectionAssert.AreEqual(await File.ReadAllBytesAsync(portable.ResolveAssetPath(project, asset)),
                await File.ReadAllBytesAsync(packaged.ResolveAssetPath(copied,
                    copied.Assets.Single(item => item.Id == asset.Id))));
        }
        Assert.IsFalse(Directory.Exists(Path.Combine(packagedRoot,
            ".portable-video-import-" + project.Id.ToString("N"))));
    }

    [TestMethod]
    public async Task MissingMediaLeavesMarkerUnsetAndIsRetried()
    {
        using var temp = new TemporaryLibrary();
        string packagedRoot = temp.GetOwnedPath("store");
        var portable = new VideoProjectStore(Path.Combine(temp.RootPath, "videos"));
        var packaged = new VideoProjectStore(Path.Combine(packagedRoot, "videos"));
        VideoProject project = await SaveProjectAsync(portable);
        VideoAsset microphone = project.Assets.Single(asset => asset.Kind == VideoAssetKind.MicrophoneAudio);
        string missing = portable.ResolveAssetPath(project, microphone);
        byte[] bytes = await File.ReadAllBytesAsync(missing);
        File.Delete(missing);

        ShnappVideoLibraryMigration.Result first = await
            ShnappVideoLibraryMigration.ImportOnceAsync(temp.RootPath, packagedRoot);
        Assert.IsFalse(first.Complete);
        Assert.AreEqual(1, first.FailedProjects);
        Assert.IsFalse(ShnappVideoLibraryMigration.IsComplete(packagedRoot));
        Assert.IsFalse(Directory.Exists(packaged.GetProjectDirectory(project.Id)));

        await File.WriteAllBytesAsync(missing, bytes);
        ShnappVideoLibraryMigration.Result second = await
            ShnappVideoLibraryMigration.ImportOnceAsync(temp.RootPath, packagedRoot);
        Assert.IsTrue(second.Complete);
        Assert.AreEqual(1, second.ImportedProjects);
        Assert.AreEqual(project.Title, (await packaged.OpenAsync(project.Id)).Title);
    }

    [TestMethod]
    public async Task ExistingStoreProjectIsNeverOverwritten()
    {
        using var temp = new TemporaryLibrary();
        string packagedRoot = temp.GetOwnedPath("store");
        var portable = new VideoProjectStore(Path.Combine(temp.RootPath, "videos"));
        var packaged = new VideoProjectStore(Path.Combine(packagedRoot, "videos"));
        VideoProject project = await SaveProjectAsync(portable);
        string destination = packaged.PrepareProjectDirectory(project.Id);
        foreach (VideoAsset asset in project.Assets)
        {
            await File.WriteAllBytesAsync(Path.Combine(destination, asset.RelativePath), [42]);
        }
        await packaged.SaveAsync(project with { Title = "Store edit" });

        ShnappVideoLibraryMigration.Result result = await
            ShnappVideoLibraryMigration.ImportOnceAsync(temp.RootPath, packagedRoot);

        Assert.IsTrue(result.Complete);
        Assert.AreEqual(0, result.ImportedProjects);
        Assert.AreEqual("Store edit", (await packaged.OpenAsync(project.Id)).Title);
        CollectionAssert.AreEqual(new byte[] { 42 },
            await File.ReadAllBytesAsync(Path.Combine(destination, "source.mp4")));
    }

    [TestMethod]
    public async Task SourceEditDuringImportDiscardsStageAndRetries()
    {
        using var temp = new TemporaryLibrary();
        string packagedRoot = temp.GetOwnedPath("store");
        var portable = new VideoProjectStore(Path.Combine(temp.RootPath, "videos"));
        var packaged = new VideoProjectStore(Path.Combine(packagedRoot, "videos"));
        VideoProject project = await SaveProjectAsync(portable);
        string metadata = Path.Combine(portable.GetProjectDirectory(project.Id), "project.json");

        ShnappVideoLibraryMigration.Result first = await
            ShnappVideoLibraryMigration.ImportOnceAsync(temp.RootPath, packagedRoot,
                async _ => await File.WriteAllTextAsync(metadata, "{}"));

        Assert.IsFalse(first.Complete);
        Assert.AreEqual(1, first.FailedProjects);
        Assert.IsFalse(ShnappVideoLibraryMigration.IsComplete(packagedRoot));
        Assert.IsFalse(Directory.Exists(packaged.GetProjectDirectory(project.Id)));
        Assert.IsFalse(Directory.Exists(Path.Combine(packagedRoot,
            ".portable-video-import-" + project.Id.ToString("N"))));

        await portable.SaveAsync(project);
        ShnappVideoLibraryMigration.Result second = await
            ShnappVideoLibraryMigration.ImportOnceAsync(temp.RootPath, packagedRoot);
        Assert.IsTrue(second.Complete);
        Assert.AreEqual(1, second.ImportedProjects);
        Assert.AreEqual(project.Title, (await packaged.OpenAsync(project.Id)).Title);
    }

    private static async Task<VideoProject> SaveProjectAsync(VideoProjectStore store)
    {
        VideoProject project = VideoProject.CreateRecording("Portable video",
            TimeSpan.FromSeconds(3), 640, 480, "source.mp4", "computer.wav", "microphone.wav");
        string directory = store.PrepareProjectDirectory(project.Id);
        await File.WriteAllBytesAsync(Path.Combine(directory, "source.mp4"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(directory, "computer.wav"), [4, 5, 6]);
        await File.WriteAllBytesAsync(Path.Combine(directory, "microphone.wav"), [7, 8, 9]);
        await store.SaveAsync(project);
        return project;
    }
}
