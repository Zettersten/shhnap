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
        VideoProject project = JsonSerializer.Deserialize(json.ToJsonString(),
            VideoProjectJsonContext.Default.VideoProject)!;
        var editor = new VideoProjectEditor(project);
        Assert.IsFalse(editor.Current.VideoOverlays.IsDefault);
        Assert.AreEqual(0, editor.Current.VideoOverlays.Length);
        Assert.IsFalse(editor.Current.HiddenLayerIds.IsDefault);
        Assert.AreEqual(0, editor.Current.HiddenLayerIds.Length);
    }

    private static VideoProject Recording() =>
        VideoProject.CreateRecording("Layer test", 10 * Second, 640, 360, "source.mp4");
}
