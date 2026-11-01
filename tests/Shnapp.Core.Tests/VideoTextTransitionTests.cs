using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class VideoTextTransitionTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [TestMethod]
    public async Task OldProjectJsonWithoutTextOrFadesStillOpens()
    {
        VideoProject original = CreateRecording();
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(original,
            VideoProjectJsonContext.Default.VideoProject))!.AsObject();
        json.Remove("textOverlays");
        json.Remove("transitions");
        foreach (JsonNode? node in json["videoSegments"]!.AsArray())
        {
            node!.AsObject().Remove("fadeIn");
            node.AsObject().Remove("fadeOut");
        }

        string root = Path.Combine(Path.GetTempPath(),
            $"shnapp-video-compat-{Guid.NewGuid():N}");
        try
        {
            var store = new VideoProjectStore(root);
            string directory = store.PrepareProjectDirectory(original.Id);
            await File.WriteAllBytesAsync(Path.Combine(directory, "source.mp4"), [1]);
            await File.WriteAllTextAsync(Path.Combine(directory, "project.json"), json.ToJsonString());

            VideoProject reopened = await store.OpenAsync(original.Id);
            var editor = new VideoProjectEditor(reopened);
            Assert.IsFalse(editor.Current.TextOverlays.IsDefault);
            Assert.AreEqual(0, editor.Current.TextOverlays.Length);
            Assert.IsFalse(editor.Current.Transitions.IsDefault);
            Assert.AreEqual(0, editor.Current.Transitions.Length);
            Assert.AreEqual(TimeSpan.Zero, editor.Current.VideoSegments[0].FadeIn);
            Assert.AreEqual(TimeSpan.Zero, editor.Current.VideoSegments[0].FadeOut);
            Assert.AreEqual(1, (await store.ListAsync()).Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TextCanBeStyledTrimmedAndExtendedWithUndo()
    {
        var editor = new VideoProjectEditor(CreateRecording());
        var text = new TextOverlay(Guid.NewGuid(), 9 * Second, 4 * Second, "First title",
            new VideoRect(120, 400, 1_000, 180), 64, "#F5F5F5");

        editor.AddTextOverlay(text);
        Assert.AreEqual(13 * Second, editor.Current.Duration);
        Assert.AreEqual(text.Id, editor.Current.TextOverlays.Single().Id);

        Assert.IsTrue(editor.UpdateTextOverlay(text with
        {
            Text = "Revised title",
            Duration = 2 * Second,
            Alignment = TextOverlayAlignment.Left,
        }));
        Assert.AreEqual(11 * Second, editor.Current.Duration);
        Assert.AreEqual("Revised title", editor.Current.TextOverlays.Single().Text);

        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(text, editor.Current.TextOverlays.Single() with { LayerId = Guid.Empty });
        Assert.AreEqual(13 * Second, editor.Current.Duration);
        Assert.IsTrue(editor.Redo());
        Assert.AreEqual("Revised title", editor.Current.TextOverlays.Single().Text);
    }

    [TestMethod]
    public void InsertAndRippleCutKeepTextAlignedWithVideo()
    {
        var editor = new VideoProjectEditor(CreateRecording());
        var text = new TextOverlay(Guid.NewGuid(), 2 * Second, 6 * Second, "Overlay",
            new VideoRect(100, 100, 1_000, 200));
        editor.AddTextOverlay(text);
        var imported = new VideoAsset(Guid.NewGuid(), VideoAssetKind.ImportedVideo,
            "assets/insert.mp4", 2 * Second);

        editor.InsertVideoAsset(imported, 4 * Second);
        Assert.AreEqual(2, editor.Current.TextOverlays.Length);
        Assert.AreEqual((2 * Second, 4 * Second),
            (editor.Current.TextOverlays[0].Start, editor.Current.TextOverlays[0].End));
        Assert.AreEqual((6 * Second, 10 * Second),
            (editor.Current.TextOverlays[1].Start, editor.Current.TextOverlays[1].End));

        editor.CutRange(3 * Second, 7 * Second);
        Assert.AreEqual(2, editor.Current.TextOverlays.Length);
        Assert.AreEqual((2 * Second, 3 * Second),
            (editor.Current.TextOverlays[0].Start, editor.Current.TextOverlays[0].End));
        Assert.AreEqual((3 * Second, 6 * Second),
            (editor.Current.TextOverlays[1].Start, editor.Current.TextOverlays[1].End));
    }

    [TestMethod]
    public void DipToBlackAtTouchingCutIsOneUndoableEdit()
    {
        var editor = new VideoProjectEditor(CreateRecording());
        Guid outgoingId = editor.Current.VideoSegments[0].Id;
        Guid incomingId = editor.SplitVideoSegment(outgoingId, 5 * Second);
        int changes = 0;
        editor.Changed += (_, _) => changes++;

        Assert.IsTrue(editor.ApplyDipToBlackAtCut(outgoingId,
            TimeSpan.FromMilliseconds(350)));
        Assert.AreEqual(1, changes);
        VideoTransition transition = editor.Current.Transitions.Single();
        Assert.AreEqual(VideoTransitionLane.Video, transition.Lane);
        Assert.AreEqual(VideoTransitionEffect.DipToBlack, transition.Effect);
        Assert.AreEqual(TimeSpan.FromMilliseconds(700), transition.Duration);
        Assert.AreEqual(outgoingId, transition.OutgoingId);
        Assert.AreEqual(incomingId, transition.IncomingId);

        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(0, editor.Current.Transitions.Length);
        Assert.IsTrue(editor.Redo());
        Assert.AreEqual(transition, editor.Current.Transitions.Single());
    }

    [TestMethod]
    public void TransitionAtVideoCutCanBeEditedAndPrunesWhenCutMoves()
    {
        var editor = new VideoProjectEditor(CreateRecording());
        Guid outgoingId = editor.Current.VideoSegments[0].Id;
        Guid incomingId = editor.SplitVideoSegment(outgoingId, 5 * Second);
        Guid id = editor.AddTransition(VideoTransitionLane.Video, outgoingId,
            incomingId, VideoTransitionEffect.Fade, Second);
        VideoTransition transition = editor.Current.Transitions.Single();
        Assert.AreEqual(id, transition.Id);
        Assert.IsTrue(VideoTransitionGeometry.TryGetSpan(editor.Current, transition,
            out VideoTransitionSpan span));
        Assert.AreEqual(5 * Second, span.Cut);
        Assert.AreEqual(TimeSpan.FromMilliseconds(4500), span.Start);
        Assert.AreEqual(TimeSpan.FromMilliseconds(5500), span.End);

        Assert.IsTrue(editor.UpdateTransition(transition with
        {
            Effect = VideoTransitionEffect.SlideLeft,
        }));
        Assert.AreEqual(VideoTransitionEffect.SlideLeft,
            editor.Current.Transitions.Single().Effect);
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(VideoTransitionEffect.Fade,
            editor.Current.Transitions.Single().Effect);
        Assert.IsTrue(editor.Redo());

        Assert.IsTrue(editor.SetVideoFades(outgoingId, TimeSpan.Zero,
            TimeSpan.FromMilliseconds(300)));
        Assert.AreEqual(0, editor.Current.Transitions.Length);

        editor.AddTransition(VideoTransitionLane.Video, outgoingId,
            incomingId, VideoTransitionEffect.Fade, Second);
        Assert.AreEqual(TimeSpan.Zero,
            editor.Current.VideoSegments.Single(item => item.Id == outgoingId).FadeOut);
        VideoSegment incoming = editor.Current.VideoSegments.Single(item => item.Id == incomingId);
        Assert.IsTrue(editor.UpdateVideoSegment(incoming with { Start = 6 * Second }));
        Assert.AreEqual(0, editor.Current.Transitions.Length);
    }

    [TestMethod]
    public void SplitTextAcceptsTransitionOnItsLogicalLayer()
    {
        var editor = new VideoProjectEditor(CreateRecording());
        var text = new TextOverlay(Guid.NewGuid(), Second, 6 * Second,
            "Caption", new VideoRect(100, 100, 500, 120))
        {
            FontFamily = "Arial",
            Bold = true,
            Italic = true,
            BackgroundColor = "#AA203040",
            Padding = 12,
            BorderColor = "#FFFFFF",
            BorderThickness = 2,
            CornerRadius = 8,
        };
        editor.AddTextOverlay(text);
        Guid incomingId = editor.SplitTextOverlay(text.Id, 4 * Second);
        Guid id = editor.AddTransition(VideoTransitionLane.Text, text.Id,
            incomingId, VideoTransitionEffect.ZoomIn, Second);
        Assert.AreEqual(id, editor.Current.Transitions.Single().Id);
        Assert.IsTrue(editor.Current.TextOverlays.All(item => item.FontFamily == "Arial" &&
            item.Bold && item.Italic && item.Padding == 12));
        Assert.ThrowsExactly<ArgumentException>(() => editor.AddTransition(
            VideoTransitionLane.Image, text.Id, incomingId,
            VideoTransitionEffect.Fade, Second));
    }

    [TestMethod]
    public void AdjacentImagePiecesAcceptOneTransition()
    {
        VideoProject recording = CreateRecording();
        Guid assetId = Guid.NewGuid();
        Guid layerId = Guid.NewGuid();
        var first = new ImageOverlay(Guid.NewGuid(), assetId, Second,
            2 * Second, new VideoRect(120, 100, 400, 300)) { LayerId = layerId };
        var second = new ImageOverlay(Guid.NewGuid(), assetId, 3 * Second,
            2 * Second, new VideoRect(200, 100, 400, 300)) { LayerId = layerId };
        var project = recording with
        {
            Assets = recording.Assets.Add(new VideoAsset(assetId,
                VideoAssetKind.Image, "assets/photo.png", TimeSpan.Zero)),
            ImageOverlays = ImmutableArray.Create(first, second),
        };
        var editor = new VideoProjectEditor(project);
        Guid id = editor.AddTransition(VideoTransitionLane.Image, first.Id,
            second.Id, VideoTransitionEffect.SlideDown, Second);
        Assert.AreEqual(id, editor.Current.Transitions.Single().Id);
        Assert.IsTrue(editor.DeleteTransition(id));
        Assert.AreEqual(0, editor.Current.Transitions.Length);
    }

    [TestMethod]
    public void OlderTextJsonUsesSafeStyleDefaults()
    {
        VideoProject recording = CreateRecording();
        var text = new TextOverlay(Guid.NewGuid(), Second, Second,
            "Legacy caption", new VideoRect(10, 10, 500, 100));
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(recording with
        {
            TextOverlays = ImmutableArray.Create(text),
        }, VideoProjectJsonContext.Default.VideoProject))!.AsObject();
        JsonObject oldText = json["textOverlays"]!.AsArray()[0]!.AsObject();
        foreach (string property in new[] { "fontFamily", "bold", "italic",
            "backgroundColor", "padding", "borderColor", "borderThickness",
            "cornerRadius" })
            oldText.Remove(property);
        VideoProject restored = JsonSerializer.Deserialize(json.ToJsonString(),
            VideoProjectJsonContext.Default.VideoProject)!;
        TextOverlay caption = restored.TextOverlays.Single();
        Assert.AreEqual("Segoe UI Variable Display", caption.FontFamily);
        Assert.IsFalse(caption.Bold);
        Assert.IsNull(caption.BackgroundColor);
        Assert.AreEqual(0d, caption.Padding);
        VideoProjectValidation.Validate(restored);
    }

    [TestMethod]
    public void TransitionSamplerKeepsEndpointsAndDirectionStable()
    {
        foreach (VideoTransitionLane lane in Enum.GetValues<VideoTransitionLane>())
            foreach (VideoTransitionEffect effect in Enum.GetValues<VideoTransitionEffect>())
                Assert.IsTrue(VideoTransitionGeometry.CanRender(effect, lane));
        Assert.IsFalse(VideoTransitionGeometry.CanRender((VideoTransitionEffect)999,
            VideoTransitionLane.Video));
        VideoTransitionSample fadeStart = VideoTransitionGeometry.Sample(
            VideoTransitionEffect.Fade, 0);
        VideoTransitionSample fadeEnd = VideoTransitionGeometry.Sample(
            VideoTransitionEffect.Fade, 1);
        Assert.AreEqual(1d, fadeStart.OutgoingOpacity);
        Assert.AreEqual(0d, fadeStart.IncomingOpacity);
        Assert.AreEqual(0d, fadeEnd.OutgoingOpacity);
        Assert.AreEqual(1d, fadeEnd.IncomingOpacity);
        VideoTransitionSample slideMiddle = VideoTransitionGeometry.Sample(
            VideoTransitionEffect.SlideLeft, .5);
        Assert.AreEqual(-.5d, slideMiddle.OutgoingOffsetX);
        Assert.AreEqual(.5d, slideMiddle.IncomingOffsetX);
        VideoTransitionSample dipMiddle = VideoTransitionGeometry.Sample(
            VideoTransitionEffect.DipToBlack, .5);
        Assert.AreEqual(0d, dipMiddle.OutgoingOpacity);
        Assert.AreEqual(0d, dipMiddle.IncomingOpacity);
        Assert.AreEqual("#000000", dipMiddle.BackdropColor);
    }

    [TestMethod]
    public void TransitionsCannotOverlapInsideShortMiddleClip()
    {
        var editor = new VideoProjectEditor(CreateRecording());
        Guid firstId = editor.Current.VideoSegments[0].Id;
        Guid middleId = editor.SplitVideoSegment(firstId, 4 * Second);
        Guid lastId = editor.SplitVideoSegment(middleId, 5 * Second);
        editor.AddTransition(VideoTransitionLane.Video, firstId, middleId,
            VideoTransitionEffect.Fade, 2 * Second);
        Assert.ThrowsExactly<ArgumentException>(() => editor.AddTransition(
            VideoTransitionLane.Video, middleId, lastId,
            VideoTransitionEffect.Fade, 2 * Second));
        Assert.AreEqual(1, editor.Current.Transitions.Length);
    }

    [TestMethod]
    public void TransitionCannotOverlapAFadeAtTheOtherClipEdge()
    {
        var editor = new VideoProjectEditor(CreateRecording());
        Guid firstId = editor.Current.VideoSegments[0].Id;
        Guid nextId = editor.SplitVideoSegment(firstId, 5 * Second);
        editor.SetVideoFades(firstId, TimeSpan.FromMilliseconds(4500),
            TimeSpan.Zero);
        Assert.ThrowsExactly<ArgumentException>(() => editor.AddTransition(
            VideoTransitionLane.Video, firstId, nextId,
            VideoTransitionEffect.Fade, 2 * Second));
    }

    [TestMethod]
    public void TextAndFadeValidationRejectsInvalidValues()
    {
        var editor = new VideoProjectEditor(CreateRecording());
        var text = new TextOverlay(Guid.NewGuid(), TimeSpan.Zero, Second, "A",
            new VideoRect(0, 0, 300, 100));
        Assert.ThrowsExactly<ArgumentException>(() => editor.AddTextOverlay(text with
        {
            Color = "transparent",
        }));
        Assert.ThrowsExactly<ArgumentException>(() => editor.AddTextOverlay(text with
        {
            Text = "\u0001",
        }));
        Assert.ThrowsExactly<ArgumentException>(() => editor.AddTextOverlay(text with
        {
            FontFamily = "Bad\u0001Font",
        }));
        Assert.ThrowsExactly<ArgumentException>(() => editor.AddTextOverlay(text with
        {
            BackgroundColor = "transparent",
        }));
        Assert.ThrowsExactly<ArgumentException>(() => editor.AddTextOverlay(text with
        {
            Padding = double.NaN,
        }));
        Assert.ThrowsExactly<ArgumentException>(() => editor.SetVideoFades(
            editor.Current.VideoSegments[0].Id, 6 * Second, TimeSpan.Zero));
    }

    private static VideoProject CreateRecording() => VideoProject.CreateRecording(
        "Text and transitions", 10 * Second, 1_280, 720, "source.mp4");
}
