using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class AnnotationFlipTests
{
    [TestMethod]
    public void ContentMirrorsInLocalAxesBeforeClockwiseRotation()
    {
        Annotation shape = TestDocuments.Annotation() with
        {
            Start = new ImagePoint(10, 20),
            End = new ImagePoint(50, 40),
            FlipHorizontal = true,
            RotationDegrees = 90,
        };
        ImagePoint source = new(50, 30);

        Assert.AreEqual(new ImagePoint(30, 10),
            AnnotationRotation.TransformContent(shape, source));
        Assert.AreEqual(source,
            AnnotationRotation.UntransformContent(shape, new ImagePoint(30, 10)));
        // Selection chrome still uses only rotation, so handles do not mirror.
        Assert.AreEqual(new ImagePoint(30, 50), AnnotationRotation.Rotate(shape, source));

        Annotation both = shape with { FlipVertical = true };
        ImagePoint diagonal = new(40, 20);
        Assert.AreEqual(new ImagePoint(20, 20),
            AnnotationRotation.TransformContent(both, diagonal));
        Assert.AreEqual(diagonal,
            AnnotationRotation.UntransformContent(both, new ImagePoint(20, 20)));
    }

    [TestMethod]
    public void MirroredCalloutBoundsUseItsTextBoxPivot()
    {
        Annotation callout = TestDocuments.Annotation(AnnotationKind.Callout) with
        {
            Start = new ImagePoint(10, 20),
            End = new ImagePoint(100, 100),
            TextBoxWidth = 100,
            TextBoxHeight = 60,
            FlipHorizontal = true,
        };

        Assert.AreEqual(new ImagePoint(150, 130), AnnotationRotation.Pivot(callout));
        Assert.AreEqual(new ImageRect(100, 20, 190, 140),
            AnnotationRotation.TransformedBounds(callout, callout.Bounds));
    }

    [TestMethod]
    public void FlippedLineCapExpandsCanvasAndFlippingBackTrimsIt()
    {
        Annotation line = TestDocuments.Annotation(AnnotationKind.Line) with
        {
            Start = new ImagePoint(610, 200),
            End = new ImagePoint(630, 200),
            StartCap = LineEndCap.Triangle,
            FlipHorizontal = true,
        };
        var editor = new DocumentEditor(TestDocuments.Create());

        editor.AddAnnotation(line);
        Assert.IsTrue(editor.Current.CanvasBounds.Right > 640);

        editor.UpdateAnnotation(line with { FlipHorizontal = false });
        Assert.AreEqual(editor.Current.OriginalBounds, editor.Current.CanvasBounds);
        Assert.IsTrue(editor.Undo());
        Assert.IsTrue(editor.Current.Annotations[0].FlipHorizontal);
    }

    [TestMethod]
    public void BatchTransformIsOneUndoableChangeAndInvalidBatchIsAtomic()
    {
        Annotation shape = TestDocuments.Annotation();
        Annotation photo = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(100, 100),
            End = new ImagePoint(180, 160),
        };
        var editor = new DocumentEditor(TestDocuments.Create() with
        {
            Annotations = [shape, photo],
        });
        ShnappDocument original = editor.Current;
        int changes = 0;
        editor.Changed += (_, _) => changes++;

        editor.UpdateAnnotations([
            shape with { FlipHorizontal = true, RotationDegrees = 45 },
            photo with { FlipVertical = true, RotationDegrees = -90 },
        ]);

        Assert.AreEqual(1, changes);
        Assert.IsTrue(editor.Current.Annotations[0].FlipHorizontal);
        Assert.IsTrue(editor.Current.Annotations[1].FlipVertical);
        Assert.IsTrue(editor.Undo());
        Assert.AreSame(original, editor.Current);
        Assert.IsFalse(editor.CanUndo);
        Assert.IsTrue(editor.Redo());
        Assert.IsTrue(editor.Current.Annotations[0].FlipHorizontal);

        ShnappDocument beforeInvalid = editor.Current;
        Assert.Throws<ArgumentException>(() => editor.UpdateAnnotations([
            shape with { FlipHorizontal = false },
            photo with { RotationDegrees = double.NaN },
        ]));
        Assert.AreSame(beforeInvalid, editor.Current);
        Assert.AreEqual(3, changes);
    }

    [TestMethod]
    public async Task FlipsPersistThroughCloneAndSaveButFlatteningBakesThemIntoPixels()
    {
        using var temporary = new TemporaryLibrary();
        Annotation photo = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            FlipHorizontal = true,
            FlipVertical = true,
        };
        var editor = new DocumentEditor(TestDocuments.Create());
        editor.AddAnnotation(photo);
        Annotation clone = editor.CloneAnnotation(photo.Id)!;
        Assert.IsTrue(clone.FlipHorizontal && clone.FlipVertical);

        await temporary.Library.SaveAsync(editor.Current);
        string json = await File.ReadAllTextAsync(temporary.GetMetadataPath(editor.Current.Id));
        StringAssert.Contains(json, "\"flipHorizontal\": true");
        StringAssert.Contains(json, "\"flipVertical\": true");
        ShnappDocument reopened = (await temporary.Library.OpenAsync(editor.Current.Id))!;
        Assert.IsTrue(reopened.Annotations.All(item => item.FlipHorizontal && item.FlipVertical));

        Assert.IsTrue(editor.FlattenAnnotation(photo.Id, TestDocuments.OnePixelPngBase64,
            new ImageRect(10, 20, 1, 1)));
        Annotation frozen = editor.Current.Annotations[0];
        Assert.IsFalse(frozen.FlipHorizontal || frozen.FlipVertical);
        Assert.IsTrue(editor.Undo());
        Assert.IsTrue(editor.Current.Annotations[0].FlipHorizontal &&
            editor.Current.Annotations[0].FlipVertical);
    }
}
