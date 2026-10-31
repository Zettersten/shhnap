using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class VideoProjectTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [TestMethod]
    public void RecordingFactoryCreatesAlignedVideoAndSeparateAudioStems()
    {
        VideoProject project = CreateRecording();

        Assert.AreEqual(3, project.Assets.Length);
        Assert.AreEqual(VideoAssetKind.Recording, project.Assets[0].Kind);
        Assert.AreEqual(VideoAssetKind.ComputerAudio, project.Assets[1].Kind);
        Assert.AreEqual(VideoAssetKind.MicrophoneAudio, project.Assets[2].Kind);
        Assert.AreEqual(TimeSpan.FromSeconds(10), project.Duration);
        Assert.AreEqual(2, project.AudioSegments.Length);
        Assert.IsTrue(project.AudioSegments.All(segment => segment.Start == TimeSpan.Zero &&
            segment.SourceIn == TimeSpan.Zero && segment.SourceOut == project.Duration));
        Assert.IsTrue(project.AudioSegments.Select(segment => segment.AssetId)
            .SequenceEqual(project.Assets.Skip(1).Select(asset => asset.Id)));
    }

    [TestMethod]
    public void CutRangeSplitsAndRipplesVideoAudioAndImageLanesAndCanBeUndone()
    {
        var editor = new VideoProjectEditor(CreateRecording());
        Guid recordingId = editor.Current.VideoSegments[0].Id;
        Guid rightId = editor.SplitVideoSegment(recordingId, 4 * Second);
        var imported = new VideoAsset(Guid.NewGuid(), VideoAssetKind.ImportedAudio,
            "assets/music.flac", 6 * Second);
        editor.AddAsset(imported);
        editor.AddAudioSegment(new AudioSegment(Guid.NewGuid(), imported.Id,
            2 * Second, Second, 5 * Second, 0.5));
        var image = new VideoAsset(Guid.NewGuid(), VideoAssetKind.Image,
            "assets/pasted.png", TimeSpan.Zero);
        editor.AddAsset(image);
        editor.AddImageOverlay(new ImageOverlay(Guid.NewGuid(), image.Id, Second,
            8 * Second, new VideoRect(120, 80, 200, 100), 2));
        VideoProject beforeCut = editor.Current;
        int changes = 0;
        editor.Changed += (_, _) => changes++;

        editor.CutRange(3 * Second, 5 * Second);

        Assert.AreEqual(1, changes);
        Assert.AreEqual(8 * Second, editor.Current.Duration);
        Assert.AreEqual(2, editor.Current.VideoSegments.Length);
        Assert.AreEqual((TimeSpan.Zero, 3 * Second, TimeSpan.Zero, 3 * Second),
            (editor.Current.VideoSegments[0].Start, editor.Current.VideoSegments[0].End,
                editor.Current.VideoSegments[0].SourceIn, editor.Current.VideoSegments[0].SourceOut));
        Assert.AreEqual((3 * Second, 8 * Second, 5 * Second, 10 * Second),
            (editor.Current.VideoSegments[1].Start, editor.Current.VideoSegments[1].End,
                editor.Current.VideoSegments[1].SourceIn, editor.Current.VideoSegments[1].SourceOut));
        Assert.AreEqual(rightId, editor.Current.VideoSegments[1].Id);
        foreach (VideoAsset stem in editor.Current.Assets.Where(asset =>
            asset.Kind is VideoAssetKind.ComputerAudio or VideoAssetKind.MicrophoneAudio))
        {
            AudioSegment[] pieces = [.. editor.Current.AudioSegments.Where(segment => segment.AssetId == stem.Id)];
            Assert.AreEqual(2, pieces.Length);
            Assert.AreEqual((TimeSpan.Zero, 3 * Second, TimeSpan.Zero, 3 * Second),
                (pieces[0].Start, pieces[0].End, pieces[0].SourceIn, pieces[0].SourceOut));
            Assert.AreEqual((3 * Second, 8 * Second, 5 * Second, 10 * Second),
                (pieces[1].Start, pieces[1].End, pieces[1].SourceIn, pieces[1].SourceOut));
        }
        AudioSegment[] music = [.. editor.Current.AudioSegments.Where(segment => segment.AssetId == imported.Id)];
        Assert.AreEqual(2, music.Length);
        Assert.AreEqual((2 * Second, 3 * Second, Second, 2 * Second),
            (music[0].Start, music[0].End, music[0].SourceIn, music[0].SourceOut));
        Assert.AreEqual((3 * Second, 4 * Second, 4 * Second, 5 * Second),
            (music[1].Start, music[1].End, music[1].SourceIn, music[1].SourceOut));
        Assert.AreEqual(2, editor.Current.ImageOverlays.Length);
        Assert.AreEqual((Second, 2 * Second),
            (editor.Current.ImageOverlays[0].Start, editor.Current.ImageOverlays[0].Duration));
        Assert.AreEqual((3 * Second, 4 * Second),
            (editor.Current.ImageOverlays[1].Start, editor.Current.ImageOverlays[1].Duration));

        Assert.IsTrue(editor.Undo());
        Assert.AreSame(beforeCut, editor.Current);
        Assert.IsTrue(editor.Redo());
        Assert.AreEqual(8 * Second, editor.Current.Duration);
    }

    [TestMethod]
    public void TrackEditsValidateOverlapsAndGainWithoutChangingHistoryOnFailure()
    {
        var editor = new VideoProjectEditor(CreateRecording());
        Guid first = editor.Current.VideoSegments[0].Id;
        Guid second = editor.SplitVideoSegment(first, 4 * Second);
        Assert.IsTrue(editor.TrimVideoSegment(first, TimeSpan.Zero, 3 * Second));
        Assert.IsTrue(editor.MoveVideoSegment(first, Second));
        Assert.AreEqual(Second, editor.Current.VideoSegments[0].Start);
        Assert.ThrowsExactly<ArgumentException>(() => editor.MoveVideoSegment(first, 2 * Second));
        Assert.AreEqual(Second, editor.Current.VideoSegments[0].Start);
        Assert.IsFalse(editor.MoveVideoSegment(Guid.NewGuid(), Second));

        Guid mic = editor.Current.AudioSegments.Single(segment =>
            editor.Current.Assets.Single(asset => asset.Id == segment.AssetId).Kind ==
                VideoAssetKind.MicrophoneAudio).Id;
        Assert.IsTrue(editor.SetAudioGain(mic, 0.75));
        Assert.IsTrue(editor.SetAudioMuted(mic, true));
        Assert.IsFalse(editor.SetAudioMuted(mic, true));
        Assert.ThrowsExactly<ArgumentException>(() => editor.SetAudioGain(mic, double.NaN));
        Assert.AreEqual(0.75, editor.Current.AudioSegments.Single(segment => segment.Id == mic).Gain);
        Assert.IsTrue(editor.DeleteVideoSegment(first));
        Assert.AreEqual(4 * Second, editor.Current.VideoSegments.Single().Start);
        Assert.AreEqual(second, editor.Current.VideoSegments.Single().Id);
    }

    [TestMethod]
    public void CuttingMicrophoneRangeLeavesOtherTracksAndVideoClockIntact()
    {
        var editor = new VideoProjectEditor(CreateRecording());
        VideoProject original = editor.Current;
        AudioSegment microphone = original.AudioSegments.Single(segment =>
            original.Assets.Single(asset => asset.Id == segment.AssetId).Kind ==
                VideoAssetKind.MicrophoneAudio);
        AudioSegment computer = original.AudioSegments.Single(segment => segment.Id != microphone.Id);

        editor.CutAudioRange(microphone.Id, 3 * Second, 5 * Second);

        Assert.AreEqual(original.Duration, editor.Current.Duration);
        CollectionAssert.AreEqual(original.VideoSegments.ToArray(), editor.Current.VideoSegments.ToArray());
        Assert.AreEqual(computer, editor.Current.AudioSegments.Single(segment => segment.Id == computer.Id));
        AudioSegment[] microphonePieces = [.. editor.Current.AudioSegments
            .Where(segment => segment.AssetId == microphone.AssetId).OrderBy(segment => segment.Start)];
        Assert.AreEqual(2, microphonePieces.Length);
        Assert.AreEqual((TimeSpan.Zero, 3 * Second, TimeSpan.Zero, 3 * Second),
            (microphonePieces[0].Start, microphonePieces[0].End,
                microphonePieces[0].SourceIn, microphonePieces[0].SourceOut));
        Assert.AreEqual((5 * Second, 10 * Second, 5 * Second, 10 * Second),
            (microphonePieces[1].Start, microphonePieces[1].End,
                microphonePieces[1].SourceIn, microphonePieces[1].SourceOut));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            editor.CutAudioRange(microphone.Id, 11 * Second, 12 * Second));
        Assert.IsTrue(editor.Undo());
        Assert.AreSame(original, editor.Current);
    }

    [TestMethod]
    public void InvalidProjectPathsAndMediaRangesAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => VideoProject.CreateRecording("Bad", 2 * Second,
            1280, 720, "../other.mp4"));
        Assert.ThrowsExactly<ArgumentException>(() => VideoProject.CreateRecording("Bad", 2 * Second,
            1280, 720, "C:/outside.mp4"));
        Assert.ThrowsExactly<ArgumentException>(() => VideoProject.CreateRecording("Bad", 2 * Second,
            1280, 720, "assets/source.mp4", "assets/mic.mp3"));
        Assert.ThrowsExactly<ArgumentException>(() => VideoProject.CreateRecording("Bad", 2 * Second,
            1280, 720, "assets/CON.mp4"));
        var editor = new VideoProjectEditor(CreateRecording());
        Guid recordingId = editor.Current.VideoSegments[0].Id;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => editor.SplitVideoSegment(recordingId, TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => editor.CutRange(TimeSpan.Zero, 11 * Second));
        Assert.ThrowsExactly<InvalidOperationException>(() => editor.CutRange(TimeSpan.Zero, 10 * Second));
        Assert.AreEqual(10 * Second, editor.Current.Duration);
    }

    private static VideoProject CreateRecording() => VideoProject.CreateRecording("  Demo  ",
        10 * Second, 1920, 1080, "assets/original.mp4", "assets/computer.wav",
        "assets/microphone.wav");
}

[TestClass]
public sealed class VideoProjectStoreTests
{
    [TestMethod]
    public async Task RecordingFrameRateSurvivesSaveAndOlderProjectsDefaultToThirty()
    {
        using var temp = new TemporaryVideoDirectory();
        var store = new VideoProjectStore(temp.Path);
        VideoProject project = VideoProject.CreateRecording("Sixty fps", TimeSpan.FromSeconds(3),
            1280, 720, "source.mp4", frameRate: 60);
        string directory = store.PrepareProjectDirectory(project.Id);
        await File.WriteAllBytesAsync(Path.Combine(directory, "source.mp4"), [1, 2, 3]);

        await store.SaveAsync(project);
        Assert.AreEqual(60, (await store.OpenAsync(project.Id)).FrameRate);

        string metadata = Path.Combine(directory, "project.json");
        JsonObject legacy = JsonNode.Parse(await File.ReadAllTextAsync(metadata))!.AsObject();
        Assert.IsTrue(legacy.Remove("frameRate"));
        await File.WriteAllTextAsync(metadata, legacy.ToJsonString());
        Assert.AreEqual(30, (await store.OpenAsync(project.Id)).FrameRate);

        Assert.ThrowsExactly<ArgumentException>(() => VideoProject.CreateRecording(
            "Invalid rate", TimeSpan.FromSeconds(3), 1280, 720, "source.mp4", frameRate: 45));
    }

    [TestMethod]
    public async Task StoreRoundTripsProjectImportsAndListsSummaries()
    {
        using var temp = new TemporaryVideoDirectory();
        var store = new VideoProjectStore(temp.Path);
        VideoProject project = VideoProject.CreateRecording("Demo", TimeSpan.FromSeconds(5),
            1280, 720, "assets/original.mp4", "assets/computer.wav");
        string assetDirectory = Path.Combine(store.PrepareProjectDirectory(project.Id), "assets");
        Directory.CreateDirectory(assetDirectory);
        await File.WriteAllBytesAsync(Path.Combine(assetDirectory, "original.mp4"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(assetDirectory, "computer.wav"), [4, 5, 6]);
        await store.SaveAsync(project);
        byte[] image = [11, 12, 13, 14];
        await using var input = new MemoryStream(image);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await store.ImportAssetAsync(project, VideoAssetKind.Image, "wrong.png", input,
                TimeSpan.Zero));
        VideoAsset imported = await store.ImportAssetAsync(project, VideoAssetKind.Image,
            ".png", input, TimeSpan.Zero);
        var editor = new VideoProjectEditor(project);
        editor.AddAsset(imported);
        editor.AddImageOverlay(new ImageOverlay(Guid.NewGuid(), imported.Id,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3),
            new VideoRect(10, 20, 200, 100), 1));
        await store.SaveAsync(editor.Current);

        VideoProject reopened = await store.OpenAsync(project.Id);
        CollectionAssert.AreEqual(image, await File.ReadAllBytesAsync(store.ResolveAssetPath(reopened, imported)));
        Assert.AreEqual(editor.Current.SchemaVersion, reopened.SchemaVersion);
        Assert.AreEqual(editor.Current.Id, reopened.Id);
        Assert.AreEqual(editor.Current.Title, reopened.Title);
        Assert.AreEqual(editor.Current.CreatedAt, reopened.CreatedAt);
        Assert.AreEqual(editor.Current.Width, reopened.Width);
        Assert.AreEqual(editor.Current.Height, reopened.Height);
        CollectionAssert.AreEqual(editor.Current.Assets.ToArray(), reopened.Assets.ToArray());
        CollectionAssert.AreEqual(editor.Current.VideoSegments.ToArray(), reopened.VideoSegments.ToArray());
        CollectionAssert.AreEqual(editor.Current.AudioSegments.ToArray(), reopened.AudioSegments.ToArray());
        CollectionAssert.AreEqual(editor.Current.ImageOverlays.ToArray(), reopened.ImageOverlays.ToArray());
        Assert.AreEqual(1, reopened.ImageOverlays.Length);
        IReadOnlyList<VideoProjectSummary> listed = await store.ListAsync();
        Assert.AreEqual(1, listed.Count);
        Assert.AreEqual(project.Id, listed[0].Id);
        Assert.AreEqual(TimeSpan.FromSeconds(5), listed[0].Duration);
    }

    [TestMethod]
    public async Task MalformedOrUnsupportedJsonCannotBeOpenedAndFailedSaveKeepsPreviousMetadata()
    {
        using var temp = new TemporaryVideoDirectory();
        var store = new VideoProjectStore(temp.Path);
        VideoProject project = VideoProject.CreateRecording("Original", TimeSpan.FromSeconds(2),
            320, 240, "assets/original.mp4");
        string directory = store.GetProjectDirectory(project.Id);
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        await File.WriteAllBytesAsync(Path.Combine(directory, "assets", "original.mp4"), [1]);
        await store.SaveAsync(project);
        string path = Path.Combine(directory, "project.json");
        string original = await File.ReadAllTextAsync(path);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await store.SaveAsync(project with
        {
            Assets = project.Assets.SetItem(0, project.Assets[0] with { RelativePath = "../escape.mp4" }),
        }));
        Assert.AreEqual(original, await File.ReadAllTextAsync(path));

        await File.WriteAllTextAsync(path, original.Replace("\"schemaVersion\": 1",
            "\"schemaVersion\": 2", StringComparison.Ordinal));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await store.OpenAsync(project.Id));
        await File.WriteAllTextAsync(path, original.Replace("\"schemaVersion\": 1",
            "\"schemaVersion\": 1, \"schemaVersion\": 1", StringComparison.Ordinal));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await store.OpenAsync(project.Id));
        Assert.AreEqual(0, (await store.ListAsync()).Count);
    }

    private sealed class TemporaryVideoDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"shnapp-video-test-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); }
        }
    }
}
