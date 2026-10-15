using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class PhotoCropGeometryTests
{
    private const string FourByThreePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAYAAAC09K7GAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAASSURBVBhXY/jPwPCfgQTw/z8AY88D/RnFChIAAAAASUVORK5CYII=";

    private static Annotation Photo() => TestDocuments.Annotation(AnnotationKind.Image) with
    {
        Start = new ImagePoint(10, 20),
        End = new ImagePoint(90, 80),
        ImagePngBase64 = FourByThreePngBase64,
        LayerOrder = 4,
    };

    [TestMethod]
    public void CropNarrowsBothSourceAndDestinationWithoutChangingPixelScale()
    {
        Annotation photo = Photo();
        Annotation cropped = PhotoCropGeometry.Crop(photo, new ImageRect(30, 40, 40, 40));

        Assert.AreEqual(new ImageRect(1, 1, 2, 2), cropped.ImageSourceCrop);
        Assert.AreEqual(new ImageRect(30, 40, 40, 40), cropped.Bounds);
        Assert.AreSame(photo.ImagePngBase64, cropped.ImagePngBase64);
        Assert.AreEqual(photo.Id, cropped.Id);
        Assert.AreEqual(photo.LayerOrder, cropped.LayerOrder);
    }

    [TestMethod]
    public void RepeatedCropAndResetRestoreTheOriginalPlacement()
    {
        Annotation photo = Photo();
        Annotation first = PhotoCropGeometry.Crop(photo, new ImageRect(31, 39, 39, 41));
        Assert.AreEqual(new ImageRect(1, 1, 2, 2), first.ImageSourceCrop);
        Assert.AreEqual(new ImageRect(30, 40, 40, 40), first.Bounds);
        Assert.AreSame(first, PhotoCropGeometry.Crop(first, first.Bounds));

        Annotation second = PhotoCropGeometry.Crop(first, new ImageRect(50, 60, 20, 20));
        Assert.AreEqual(new ImageRect(2, 2, 1, 1), second.ImageSourceCrop);
        Assert.AreEqual(new ImageRect(50, 60, 20, 20), second.Bounds);

        Annotation reset = PhotoCropGeometry.Reset(second);
        Assert.IsNull(reset.ImageSourceCrop);
        Assert.AreEqual(photo.Bounds, reset.Bounds);
        Assert.AreSame(reset, PhotoCropGeometry.Reset(reset));
    }

    [TestMethod]
    public void ResetRetainsCurrentScaleAfterTheCroppedImageWasResized()
    {
        Annotation cropped = PhotoCropGeometry.Crop(Photo(), new ImageRect(30, 40, 40, 40));
        Annotation resized = cropped with
        {
            Start = new ImagePoint(100, 120),
            End = new ImagePoint(180, 200),
        };

        Annotation reset = PhotoCropGeometry.Reset(resized);

        Assert.AreEqual(new ImageRect(60, 80, 160, 120), reset.Bounds);
        Assert.IsNull(reset.ImageSourceCrop);
    }

    [TestMethod]
    public void RejectsSelectionsWithoutAVisibleSourcePixelOrOutsideTheImage()
    {
        Annotation photo = Photo();
        foreach (ImageRect selection in new[]
        {
            new ImageRect(91, 20, 5, 5),
            new ImageRect(30.1, 40.1, 0.1, 0.1),
            new ImageRect(30, 40, 0, 10),
            new ImageRect(30, 40, double.NaN, 10),
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() => PhotoCropGeometry.Crop(photo, selection));
        }

        Annotation verySmall = photo with
        {
            Start = new ImagePoint(10, 20),
            End = new ImagePoint(12, 23),
        };
        Assert.ThrowsExactly<ArgumentException>(() =>
            PhotoCropGeometry.Crop(verySmall, new ImageRect(10, 20, 0.6, 1)));
    }

    [TestMethod]
    public void RejectsNonEditableImagesAndMalformedSourceGeometry()
    {
        Annotation photo = Photo();
        foreach (Annotation invalid in new[]
        {
            TestDocuments.Annotation(AnnotationKind.Rectangle),
            photo with { IsFlattened = true },
            photo with { ImagePngBase64 = "not a PNG" },
            photo with { ImageSourceCrop = new ImageRect(3, 0, 2, 2) },
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                PhotoCropGeometry.Crop(invalid, new ImageRect(30, 40, 40, 40)));
            Assert.ThrowsExactly<ArgumentException>(() => PhotoCropGeometry.Reset(invalid));
        }
    }
}
