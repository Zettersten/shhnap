using Shnapp.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class VideoOverlayCropPresetTests
{
    [TestMethod]
    public void CircleCentersPortraitVideoWithoutChangingVisibleScale()
    {
        var overlay = new VideoOverlay(Guid.NewGuid(), Guid.NewGuid(),
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(2),
            new VideoRect(100, 50, 240, 400));

        VideoOverlay circle = VideoOverlayCropPresets.Apply(overlay,
            VideoOverlayShape.Circle);

        Assert.AreEqual(new VideoRect(100, 130, 240, 240), circle.Bounds);
        Assert.AreEqual(new VideoRect(0, .2, 1, .6), circle.SourceCrop);
        Assert.AreEqual(VideoOverlayShape.Circle, circle.Shape);
        Assert.IsNull(overlay.SourceCrop);

        VideoOverlay restored = VideoOverlayCropPresets.Apply(circle,
            VideoOverlayShape.Rectangle);
        Assert.AreEqual(overlay.Bounds, restored.Bounds);
        Assert.IsNull(restored.SourceCrop);
        Assert.IsNull(restored.ShapeOriginalBounds);
    }

    [TestMethod]
    public void SwitchingBetweenSquareShapesDoesNotCropTwice()
    {
        var overlay = new VideoOverlay(Guid.NewGuid(), Guid.NewGuid(),
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(2),
            new VideoRect(0, 0, 400, 200));

        VideoOverlay square = VideoOverlayCropPresets.Apply(overlay,
            VideoOverlayShape.Square);
        VideoOverlay squircle = VideoOverlayCropPresets.Apply(square,
            VideoOverlayShape.Squircle);

        Assert.AreEqual(square.Bounds, squircle.Bounds);
        Assert.AreEqual(square.SourceCrop, squircle.SourceCrop);
    }
}
