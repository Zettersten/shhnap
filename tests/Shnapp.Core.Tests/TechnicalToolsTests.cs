using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class TechnicalToolsTests
{
    [TestMethod]
    public void CalloutsStayOutsideCaptureAndSeparateAsTheirTextChanges()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation first = NewCallout(new ImagePoint(620, 170), "The upper control needs a label.");
        Annotation second = NewCallout(new ImagePoint(620, 180), "This lower control also needs a label.");

        editor.AddAnnotation(first);
        editor.AddAnnotation(second);

        Annotation placedFirst = Find(editor, first.Id);
        Annotation placedSecond = Find(editor, second.Id);
        Assert.AreEqual(1, placedFirst.CalloutNumber);
        Assert.AreEqual(2, placedSecond.CalloutNumber);
        Assert.IsTrue(IsOutside(placedFirst, editor.Current.OriginalBounds));
        Assert.IsTrue(IsOutside(placedSecond, editor.Current.OriginalBounds));
        Assert.IsFalse(Overlaps(Box(placedFirst), Box(placedSecond)));
        Assert.IsTrue(editor.Current.CanvasBounds.Contains(new ImagePoint(
            Box(placedFirst).Right, Box(placedFirst).Bottom)));

        ImageRect previousSecond = Box(placedSecond);
        editor.UpdateAnnotation(placedFirst with { Text = string.Join(' ', Enumerable.Repeat("detail", 90)) });
        Annotation editedFirst = Find(editor, first.Id);
        Annotation editedSecond = Find(editor, second.Id);
        Assert.IsTrue(Box(editedFirst).Height > Box(placedFirst).Height);
        Assert.IsFalse(Overlaps(Box(editedFirst), Box(editedSecond)));
        Assert.AreNotEqual(previousSecond, Box(editedSecond));

        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(Box(placedFirst), Box(Find(editor, first.Id)));
        Assert.AreEqual(previousSecond, Box(Find(editor, second.Id)));
    }

    [TestMethod]
    public void CalloutsAvoidExistingMarksInTheMargin()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation obstacle = TestDocuments.Annotation(AnnotationKind.Rectangle) with
        {
            Start = new ImagePoint(676, 100),
            End = new ImagePoint(1050, 310),
        };
        editor.AddAnnotation(obstacle);
        Annotation callout = NewCallout(new ImagePoint(620, 175), "A note beside the control.");
        editor.AddAnnotation(callout);

        Assert.IsFalse(Overlaps(Box(Find(editor, callout.Id)), obstacle.Bounds));
    }

    [TestMethod]
    public void CroppedOutCalloutStaysHiddenAfterAnUnrelatedAnnotationEdit()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation callout = NewCallout(new ImagePoint(620, 175), "This note should stay cropped out.");
        editor.AddAnnotation(callout);
        editor.ApplyCrop(new ImageRect(100, 100, 200, 150));

        editor.AddAnnotation(TestDocuments.Annotation(AnnotationKind.Rectangle) with
        {
            Start = new ImagePoint(120, 120),
            End = new ImagePoint(150, 150),
        });

        Annotation afterEdit = Find(editor, callout.Id);
        Assert.IsTrue(afterEdit.HiddenByCrop);
        Assert.AreEqual(new ImageRect(100, 100, 200, 150), editor.Current.Viewport);
    }

    [TestMethod]
    public void PartlyCroppedCalloutKeepsItsOriginalClipAfterAnUnrelatedEdit()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation callout = NewCallout(new ImagePoint(620, 175), "The anchor remains visible.");
        editor.AddAnnotation(callout);
        ImageRect crop = new(600, 100, 50, 150);
        editor.ApplyCrop(crop);

        editor.AddAnnotation(TestDocuments.Annotation(AnnotationKind.Rectangle) with
        {
            Start = new ImagePoint(610, 120),
            End = new ImagePoint(630, 150),
        });

        Annotation afterEdit = Find(editor, callout.Id);
        Assert.IsFalse(afterEdit.HiddenByCrop);
        Assert.AreEqual(crop, afterEdit.VisibilityClip);
        Assert.AreEqual(crop, editor.Current.Viewport);
    }

    [TestMethod]
    public void MagnifyRejectsTinyLensAndPersistsZoomAndShape()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation lens = TestDocuments.Annotation(AnnotationKind.Magnify) with
        {
            Start = new ImagePoint(100, 100),
            End = new ImagePoint(148, 148),
            MagnifyZoom = 3.5,
            MagnifyShape = MagnifyShape.Rectangle,
        };
        editor.AddAnnotation(lens);
        Assert.ThrowsExactly<ArgumentException>(() => editor.UpdateAnnotation(lens with
        {
            End = new ImagePoint(147, 148),
        }));
        Assert.ThrowsExactly<ArgumentException>(() => editor.UpdateAnnotation(lens with
        {
            MagnifyZoom = 9,
        }));

        string json = JsonSerializer.Serialize(editor.Current);
        ShnappDocument restored = JsonSerializer.Deserialize<ShnappDocument>(json)!;
        Annotation saved = restored.Annotations.Single();
        Assert.AreEqual(3.5, saved.MagnifyZoom);
        Assert.AreEqual(MagnifyShape.Rectangle, saved.MagnifyShape);
    }

    [TestMethod]
    public void MagnifierSamplingRetainsFlattenedCoversAndEditableRedactions()
    {
        Annotation lens = TestDocuments.Annotation(AnnotationKind.Magnify) with
        {
            Start = new ImagePoint(100, 100),
            End = new ImagePoint(160, 160),
            MagnifyZoom = 2,
        };
        Annotation flattenedCover = TestDocuments.Annotation(AnnotationKind.Rectangle) with
        {
            Start = new ImagePoint(110, 110),
            End = new ImagePoint(111, 111),
            IsFlattened = true,
            RasterizedBounds = new ImageRect(110, 110, 1, 1),
            ImagePngBase64 = TestDocuments.OnePixelPngBase64,
            FlattenOrder = 1,
        };
        Annotation editableCover = TestDocuments.Annotation(AnnotationKind.Redaction) with
        {
            Start = new ImagePoint(120, 120),
            End = new ImagePoint(150, 150),
            VisibilityClip = new ImageRect(120, 120, 20, 20),
        };
        ShnappDocument document = TestDocuments.Create() with
        {
            HasWindowShadow = true,
            Annotations = [lens, flattenedCover,
                TestDocuments.Annotation(AnnotationKind.Rectangle), editableCover],
        };

        ShnappDocument? source = MagnifierPrivacySource.Create(document);

        Assert.IsNotNull(source);
        CollectionAssert.AreEqual(new[] { flattenedCover, editableCover }, source.Annotations.ToArray());
        Assert.IsFalse(source.HasWindowShadow);
        Assert.AreEqual(document.OriginalBounds, source.OriginalBounds);
        Assert.AreEqual(editableCover.VisibilityClip, source.Annotations[1].VisibilityClip);
    }

    [TestMethod]
    public void MagnifierPreviewPixelKeyIgnoresLensMotionButInvalidatesPrivacyChanges()
    {
        Annotation lens = TestDocuments.Annotation(AnnotationKind.Magnify);
        Annotation cover = TestDocuments.Annotation(AnnotationKind.Rectangle) with
        {
            IsFlattened = true,
            RasterizedBounds = new ImageRect(10, 20, 70, 80),
            ImagePngBase64 = TestDocuments.OnePixelPngBase64,
        };
        Annotation redaction = TestDocuments.Annotation(AnnotationKind.Redaction);
        Annotation secondRedaction = TestDocuments.Annotation(AnnotationKind.Redaction);
        Annotation ordinaryShape = TestDocuments.Annotation();
        ShnappDocument document = TestDocuments.Create() with
        {
            Annotations = [lens, cover, redaction, secondRedaction, ordinaryShape],
        };

        ShnappDocument movedLens = document with
        {
            Annotations = [lens with { Start = new ImagePoint(200, 200) },
                cover, redaction, secondRedaction, ordinaryShape with { FillArgb = 0xFF0055AA }],
            HasWindowShadow = true,
        };
        Assert.IsTrue(MagnifierPrivacySource.HasSamePixels(document, movedLens));
        Assert.IsFalse(MagnifierPrivacySource.HasSamePixels(document, document with
        {
            Annotations = [lens, cover with { ImagePngBase64 = "changed" },
                redaction, secondRedaction, ordinaryShape],
        }));
        Assert.IsFalse(MagnifierPrivacySource.HasSamePixels(document, document with
        {
            Annotations = [lens, cover, redaction with { HiddenByCrop = true },
                secondRedaction, ordinaryShape],
        }));
        Assert.IsFalse(MagnifierPrivacySource.HasSamePixels(document, document with
        {
            Annotations = [lens, cover, secondRedaction, redaction, ordinaryShape],
        }));
        Assert.IsFalse(MagnifierPrivacySource.HasSamePixels(document, document with
        {
            Crop = new ImageRect(20, 20, 400, 300),
        }));
        Assert.IsFalse(MagnifierPrivacySource.HasSamePixels(document, document with
        {
            BaseImageCrop = new ImageRect(20, 20, 400, 300),
        }));
        Assert.IsFalse(MagnifierPrivacySource.HasSamePixels(document, document with
        {
            HideOriginalImage = true,
        }));
    }

    private static Annotation NewCallout(ImagePoint anchor, string text) => new()
    {
        Kind = AnnotationKind.Callout,
        Start = anchor,
        End = anchor,
        Text = text,
        TextBoxWidth = 300,
        TextBoxHeight = 76,
        StrokeArgb = 0xFF0A84FF,
    };

    private static Annotation Find(DocumentEditor editor, Guid id) =>
        editor.Current.Annotations.Single(item => item.Id == id);

    private static ImageRect Box(Annotation item) => new(item.End.X, item.End.Y,
        item.TextBoxWidth, item.TextBoxHeight);

    private static bool IsOutside(Annotation item, ImageRect image)
    {
        ImageRect box = Box(item);
        return box.X >= image.Right || box.Right <= image.X ||
            box.Y >= image.Bottom || box.Bottom <= image.Y;
    }

    private static bool Overlaps(ImageRect first, ImageRect second) =>
        first.X < second.Right && first.Right > second.X &&
        first.Y < second.Bottom && first.Bottom > second.Y;
}
