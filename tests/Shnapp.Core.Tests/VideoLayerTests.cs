using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class VideoLayerTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [TestMethod]
    public void ImportedVideoAddsAnIndependentLayerWithAudioAndUndo()
    {
        var editor = new VideoProjectEditor(Recording());
        VideoSegment original = editor.Current.VideoSegments.Single();
        var video = new VideoAsset(Guid.NewGuid(), VideoAssetKind.ImportedVideo,
            "assets/overlay.mp4", 8 * Second);
        var audio = new VideoAsset(Guid.NewGuid(), VideoAssetKind.ImportedAudio,
            "assets/overlay.wav", 8 * Second);
        var stem = new AudioSegment(Guid.NewGuid(), audio.Id,
            TimeSpan.Zero, TimeSpan.Zero, 8 * Second);
        int edits = 0;
        editor.Changed += (_, _) => edits++;

        Guid id = editor.AddVideoOverlayAsset(video, 8 * Second,
            new VideoRect(500, -30, 300, 220), [(audio, stem)]);

        Assert.AreEqual(1, edits);
        Assert.AreEqual(original, editor.Current.VideoSegments.Single());
        Assert.AreEqual((8 * Second, 16 * Second, id),
            (editor.Current.VideoOverlays.Single().Start,
                editor.Current.VideoOverlays.Single().End,
                editor.Current.VideoOverlays.Single().LayerId));
        Assert.AreEqual(8 * Second,
            editor.Current.AudioSegments.Single(item => item.AssetId == audio.Id).Start);
        Assert.AreEqual(16 * Second, editor.Current.Duration);
        Assert.AreEqual(640, editor.Current.Width);
        Assert.AreEqual(360, editor.Current.Height);
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(0, editor.Current.VideoOverlays.Length);
        Assert.AreEqual(10 * Second, editor.Current.Duration);
    }

    [TestMethod]
    public void CropSplitAndVisibilitySurviveEditsAndPruneAfterLastClip()
    {
        var editor = new VideoProjectEditor(Recording());
        var asset = new VideoAsset(Guid.NewGuid(), VideoAssetKind.ImportedVideo,
            "assets/crop.mp4", 5 * Second);
        editor.AddAsset(asset);
        Guid id = editor.AddVideoOverlay(asset.Id, Second, TimeSpan.Zero,
            5 * Second, new VideoRect(-80, 20, 400, 230));
        VideoOverlay item = editor.Current.VideoOverlays.Single();
        Assert.IsTrue(editor.UpdateVideoOverlay(item with
        {
            SourceCrop = new VideoRect(.15, .2, .7, .6),
        }));
        Assert.IsTrue(editor.SetLayerVisible(id, false));

        Guid right = editor.SplitVideoOverlay(id, 3 * Second);
        VideoOverlay[] pieces = [.. editor.Current.VideoOverlays.OrderBy(part => part.Start)];
        Assert.AreEqual(2, pieces.Length);
        Assert.IsTrue(pieces.All(part => part.LayerId == id &&
            part.SourceCrop == new VideoRect(.15, .2, .7, .6)));
        Assert.IsTrue(editor.Current.HiddenLayerIds.Contains(id));
        Assert.IsTrue(editor.DeleteVideoOverlay(id));
        Assert.IsTrue(editor.Current.HiddenLayerIds.Contains(id));
        Assert.IsTrue(editor.DeleteVideoOverlay(right));
        Assert.IsFalse(editor.Current.HiddenLayerIds.Contains(id));
        Assert.IsTrue(editor.Undo());
        Assert.IsTrue(editor.Current.HiddenLayerIds.Contains(id));
        Assert.AreEqual(1, editor.Current.VideoOverlays.Length);
    }

    [TestMethod]
    public void TouchingVideoLayersTransitionAndRejectOverlapOrInvalidCrop()
    {
        var editor = new VideoProjectEditor(Recording());
        var asset = new VideoAsset(Guid.NewGuid(), VideoAssetKind.ImportedVideo,
            "assets/two.mp4", 5 * Second);
        editor.AddAsset(asset);
        Guid first = editor.AddVideoOverlay(asset.Id, Second, TimeSpan.Zero,
            2 * Second, new VideoRect(0, 0, 320, 180));
        Guid second = editor.AddVideoOverlay(asset.Id, 3 * Second, 2 * Second,
            4 * Second, new VideoRect(40, 30, 320, 180));
        VideoOverlay incoming = editor.Current.VideoOverlays.Single(item => item.Id == second);
        Assert.IsTrue(editor.UpdateVideoOverlay(incoming with { LayerId = first }));
        Guid transition = editor.AddTransition(VideoTransitionLane.VideoOverlay,
            first, second, VideoTransitionEffect.SlideLeft,
            TimeSpan.FromSeconds(.6));
        Assert.IsTrue(VideoTransitionGeometry.TryGetSpan(editor.Current,
            editor.Current.Transitions.Single(item => item.Id == transition), out var span));
        Assert.AreEqual(3 * Second, span.Cut);

        VideoProject before = editor.Current;
        Assert.ThrowsExactly<ArgumentException>(() =>
            editor.MoveVideoOverlay(second, TimeSpan.FromSeconds(2.5)));
        Assert.ThrowsExactly<ArgumentException>(() => editor.UpdateVideoOverlay(
            incoming with { LayerId = first, SourceCrop = new VideoRect(.7, 0, .5, 1) }));
        Assert.AreSame(before, editor.Current);
    }

    [TestMethod]
    public void OlderProjectJsonOmitsLayerPropertiesSafely()
    {
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(Recording(),
            VideoProjectJsonContext.Default.VideoProject))!.AsObject();
        json.Remove("videoOverlays");
        json.Remove("hiddenLayerIds");
        json.Remove("trackOrder");
        json.Remove("lockedTrackIds");
        VideoProject project = JsonSerializer.Deserialize(json.ToJsonString(),
            VideoProjectJsonContext.Default.VideoProject)!;
        var editor = new VideoProjectEditor(project);
        Assert.IsFalse(editor.Current.VideoOverlays.IsDefault);
        Assert.AreEqual(0, editor.Current.VideoOverlays.Length);
        Assert.IsFalse(editor.Current.HiddenLayerIds.IsDefault);
        Assert.AreEqual(0, editor.Current.HiddenLayerIds.Length);
        Assert.AreEqual(0, editor.Current.TrackOrder.Length);
        Assert.AreEqual(0, editor.Current.LockedTrackIds.Length);
    }

    [TestMethod]
    public void DragOrderPersistsAcrossMediaTypesAndUndo()
    {
        var editor = new VideoProjectEditor(Recording());
        var video = new VideoAsset(Guid.NewGuid(), VideoAssetKind.ImportedVideo,
            "assets/upper.mp4", 5 * Second);
        var image = new VideoAsset(Guid.NewGuid(), VideoAssetKind.Image,
            "assets/still.png", TimeSpan.Zero);
        editor.AddAsset(video);
        editor.AddAsset(image);
        Guid videoId = editor.AddVideoOverlay(video.Id, TimeSpan.Zero,
            TimeSpan.Zero, 5 * Second, new VideoRect(0, 0, 320, 180));
        Guid imageId = Guid.NewGuid();
        editor.AddImageOverlay(new ImageOverlay(imageId, image.Id, TimeSpan.Zero,
            5 * Second, new VideoRect(0, 0, 320, 180)));
        Guid textId = Guid.NewGuid();
        editor.AddTextOverlay(new TextOverlay(textId, TimeSpan.Zero, 5 * Second,
            "Title", new VideoRect(0, 0, 320, 80)));

        Assert.IsTrue(editor.MoveTrack(videoId, textId, after: false));
        Assert.IsTrue(VideoProjectTracks.VisualStackIndex(editor.Current, videoId) >
            VideoProjectTracks.VisualStackIndex(editor.Current, textId));
        Assert.AreEqual(videoId, VideoProjectTracks.OrderedIds(editor.Current)[0]);
        string json = JsonSerializer.Serialize(editor.Current,
            VideoProjectJsonContext.Default.VideoProject);
        VideoProject reopened = JsonSerializer.Deserialize(json,
            VideoProjectJsonContext.Default.VideoProject)!;
        Assert.AreEqual(videoId, VideoProjectTracks.OrderedIds(reopened)[0]);
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(textId, VideoProjectTracks.OrderedIds(editor.Current)[0]);
    }

    [TestMethod]
    public void LockedTrackRejectsEditsButAllowsVisibilityAndUnlock()
    {
        var editor = new VideoProjectEditor(Recording());
        var asset = new VideoAsset(Guid.NewGuid(), VideoAssetKind.ImportedVideo,
            "assets/locked.mp4", 5 * Second);
        editor.AddAsset(asset);
        Guid clipId = editor.AddVideoOverlay(asset.Id, TimeSpan.Zero,
            TimeSpan.Zero, 5 * Second, new VideoRect(0, 0, 320, 180));
        Assert.IsTrue(editor.SetTrackLocked(clipId, true));
        VideoProject before = editor.Current;

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            editor.UpdateVideoOverlay(before.VideoOverlays.Single() with
            {
                Bounds = new VideoRect(10, 10, 320, 180),
            }));
        Assert.ThrowsExactly<InvalidOperationException>(() => editor.DeleteVideoOverlay(clipId));
        Assert.AreSame(before, editor.Current);
        Assert.IsFalse(editor.MoveTrack(clipId, Guid.Empty, after: false));
        Assert.IsTrue(editor.SetLayerVisible(clipId, false));
        Assert.IsTrue(editor.SetTrackLocked(clipId, false));
        Assert.IsTrue(editor.DeleteVideoOverlay(clipId));
        Assert.AreEqual(0, editor.Current.LockedTrackIds.Length);
    }

    [TestMethod]
    public void LockedRecordingAndAudioBlockTimingButAudioCanBeMuted()
    {
        var editor = new VideoProjectEditor(VideoProject.CreateRecording(
            "Locked tracks", 10 * Second, 640, 360, "source.mp4",
            computerAudioRelativePath: "computer.wav"));
        VideoSegment video = editor.Current.VideoSegments.Single();
        AudioSegment audio = editor.Current.AudioSegments.Single();
        Assert.IsTrue(editor.SetTrackLocked(Guid.Empty, true));
        Assert.IsTrue(editor.SetTrackLocked(audio.AssetId, true));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            editor.UpdateVideoSegment(video with { Start = Second }));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            editor.UpdateAudioSegment(audio with { Gain = .5 }));
        Assert.IsTrue(editor.SetAudioTrackMuted(audio.AssetId, true));
        Assert.IsTrue(editor.Current.AudioSegments.Single().Muted);
        Assert.IsTrue(editor.SetTrackLocked(audio.AssetId, false));
        Assert.IsTrue(editor.UpdateAudioSegment(
            editor.Current.AudioSegments.Single() with { Gain = .5 }));
    }

    private static VideoProject Recording() =>
        VideoProject.CreateRecording("Layer test", 10 * Second, 640, 360, "source.mp4");
}
