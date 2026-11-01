using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class VideoImportedMediaTests
{
    [TestMethod]
    public async Task ImportedMp4CanBeInsertedAndReopened()
    {
        string root = Path.Combine(Path.GetTempPath(), "ShnappImportedVideoTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new VideoProjectStore(root);
            VideoProject project = VideoProject.CreateRecording("First", TimeSpan.FromSeconds(4),
                640, 360, "source.mp4");
            string directory = store.PrepareProjectDirectory(project.Id);
            await File.WriteAllBytesAsync(Path.Combine(directory, "source.mp4"), [1, 2, 3]);
            await store.SaveAsync(project);

            byte[] mp4 = [4, 5, 6, 7];
            await using var source = new MemoryStream(mp4, writable: false);
            VideoAsset imported = await store.ImportAssetAsync(project,
                VideoAssetKind.ImportedVideo, ".mp4", source, TimeSpan.FromSeconds(2));
            var editor = new VideoProjectEditor(project);
            editor.InsertVideoAsset(imported, TimeSpan.FromSeconds(2));
            await store.SaveAsync(editor.Current);

            VideoProject reopened = await store.OpenAsync(project.Id);
            Assert.AreEqual(TimeSpan.FromSeconds(6), reopened.Duration);
            Assert.IsTrue(reopened.VideoSegments.Any(segment => segment.AssetId == imported.Id));
            CollectionAssert.AreEqual(mp4,
                await File.ReadAllBytesAsync(store.ResolveAssetPath(reopened, imported)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportedVideoRejectsUnsupportedExtension()
    {
        string root = Path.Combine(Path.GetTempPath(), "ShnappImportedVideoTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new VideoProjectStore(root);
            VideoProject project = VideoProject.CreateRecording("First", TimeSpan.FromSeconds(4),
                640, 360, "source.mp4");
            await using var source = new MemoryStream([1, 2, 3], writable: false);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.ImportAssetAsync(project,
                VideoAssetKind.ImportedVideo, ".avi", source, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
