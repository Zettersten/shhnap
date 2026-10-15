using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class PhotoImageTests
{
    private const string FourByThreePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAYAAAC09K7GAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAASSURBVBhXY/jPwPCfgQTw/z8AY88D/RnFChIAAAAASUVORK5CYII=";

    private static Annotation CroppedPhoto() => TestDocuments.Annotation(AnnotationKind.Image) with
    {
        ImagePngBase64 = FourByThreePngBase64,
        ImageSourceCrop = new ImageRect(1, 1, 2, 2),
    };

    [TestMethod]
    public async Task CameraCaptureAndImageSourceCropSurviveSaveAndReopen()
    {
        using var temporary = new TemporaryLibrary();
        Annotation photo = CroppedPhoto();
        ShnappDocument document = TestDocuments.Create() with
        {
            CaptureKind = CaptureKind.Camera,
            Annotations = [photo],
        };

        await temporary.Library.SaveAsync(document);
        ShnappDocument reopened = (await temporary.Library.OpenAsync(document.Id))!;

        Assert.AreEqual(CaptureKind.Camera, reopened.CaptureKind);
        Assert.AreEqual(photo.ImageSourceCrop, reopened.Annotations[0].ImageSourceCrop);
        Assert.AreEqual(photo.ImagePngBase64, reopened.Annotations[0].ImagePngBase64);
        Assert.AreEqual(photo.Bounds, reopened.Annotations[0].Bounds);
    }

    [TestMethod]
    public void SourceCropMustUseWholePixelsWithinAnEditableImage()
    {
        Annotation photo = CroppedPhoto();
        _ = new DocumentEditor(TestDocuments.Create() with { Annotations = [photo] });

        Annotation[] invalid =
        [
            photo with { ImageSourceCrop = new ImageRect(-1, 0, 2, 2) },
            photo with { ImageSourceCrop = new ImageRect(3, 0, 2, 2) },
            photo with { ImageSourceCrop = new ImageRect(0, 2, 2, 2) },
            photo with { ImageSourceCrop = new ImageRect(0, 0, 0, 2) },
            photo with { ImageSourceCrop = new ImageRect(0.5, 0, 2, 2) },
            photo with { ImageSourceCrop = new ImageRect(0, 0, double.NaN, 2) },
            TestDocuments.Annotation(AnnotationKind.Rectangle) with
            {
                ImageSourceCrop = new ImageRect(0, 0, 1, 1),
            },
            photo with
            {
                IsFlattened = true,
                RasterizedBounds = new ImageRect(10, 20, 1, 1),
                ImagePngBase64 = TestDocuments.OnePixelPngBase64,
            },
        ];
        foreach (Annotation image in invalid)
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                new DocumentEditor(TestDocuments.Create() with { Annotations = [image] }));
        }
    }

    [TestMethod]
    public void SourceCropSurvivesGeometryEditsAndUndoButNotFlattening()
    {
        Annotation photo = CroppedPhoto();
        var editor = new DocumentEditor(TestDocuments.Create() with { Annotations = [photo] });
        Annotation resized = photo with
        {
            Start = new ImagePoint(20, 30),
            End = new ImagePoint(160, 190),
        };

        editor.UpdateAnnotation(resized);
        Assert.AreEqual(photo.ImageSourceCrop, editor.Current.Annotations[0].ImageSourceCrop);
        Assert.AreEqual(resized.Bounds, editor.Current.Annotations[0].Bounds);
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(photo, editor.Current.Annotations[0]);
        Assert.IsTrue(editor.Redo());

        Assert.IsTrue(editor.FlattenAnnotation(photo.Id, TestDocuments.OnePixelPngBase64,
            new ImageRect(20, 30, 1, 1)));
        Assert.IsNull(editor.Current.Annotations[0].ImageSourceCrop);
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(photo.ImageSourceCrop, editor.Current.Annotations[0].ImageSourceCrop);
    }

    [TestMethod]
    public void FlatteningSeveralPhotosClearsSourceCrops()
    {
        Annotation first = CroppedPhoto();
        Annotation second = CroppedPhoto() with { Id = Guid.NewGuid() };
        var editor = new DocumentEditor(TestDocuments.Create() with { Annotations = [first, second] });

        int count = editor.FlattenAnnotations(new Dictionary<Guid, (string, ImageRect)>
        {
            [first.Id] = (TestDocuments.OnePixelPngBase64, new ImageRect(10, 20, 1, 1)),
            [second.Id] = (TestDocuments.OnePixelPngBase64, new ImageRect(20, 30, 1, 1)),
        });

        Assert.AreEqual(2, count);
        Assert.IsTrue(editor.Current.Annotations.All(image => image.IsFlattened && image.ImageSourceCrop is null));
    }

    [TestMethod]
    public async Task CameraPreferenceRoundTripsWithoutBecomingAPrintScreenMode()
    {
        using var temporary = new TemporaryLibrary();
        var settings = new ShnappSettings
        {
            CameraSourceId = "camera-device-1",
            PrintScreenMode = CaptureKind.FullScreen,
        };
        await temporary.Library.SaveSettingsAsync(settings);
        Assert.AreEqual(settings, await temporary.Library.LoadSettingsAsync());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            temporary.Library.SaveSettingsAsync(settings with { PrintScreenMode = CaptureKind.Camera }));
    }
}
