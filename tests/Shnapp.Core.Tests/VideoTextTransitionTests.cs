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
        Assert.AreEqual(TimeSpan.FromMilliseconds(350),
            editor.Current.VideoSegments.Single(item => item.Id == outgoingId).FadeOut);
        Assert.AreEqual(TimeSpan.FromMilliseconds(350),
            editor.Current.VideoSegments.Single(item => item.Id == incomingId).FadeIn);

        Assert.IsTrue(editor.Undo());
        Assert.IsTrue(editor.Current.VideoSegments.All(item =>
            item.FadeIn == TimeSpan.Zero && item.FadeOut == TimeSpan.Zero));
        Assert.IsTrue(editor.Redo());
        Assert.AreEqual(TimeSpan.FromMilliseconds(350),
            editor.Current.VideoSegments.Single(item => item.Id == incomingId).FadeIn);
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
        Assert.ThrowsExactly<ArgumentException>(() => editor.SetVideoFades(
            editor.Current.VideoSegments[0].Id, 6 * Second, TimeSpan.Zero));
    }

    private static VideoProject CreateRecording() => VideoProject.CreateRecording(
        "Text and transitions", 10 * Second, 1_280, 720, "source.mp4");
}
