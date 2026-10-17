using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class AnnotationRotationTests
{
    [TestMethod]
    public void ClockwiseRotationAndInverseShareTheSamePivot()
    {
        Annotation rectangle = TestDocuments.Annotation() with
        {
            Start = new ImagePoint(10, 20),
            End = new ImagePoint(50, 40),
            RotationDegrees = 90,
        };

        Assert.AreEqual(new ImagePoint(30, 30), AnnotationRotation.Pivot(rectangle));
        Assert.AreEqual(new ImagePoint(30, 50),
            AnnotationRotation.Rotate(rectangle, new ImagePoint(50, 30)));
        Assert.AreEqual(new ImagePoint(50, 30),
            AnnotationRotation.Unrotate(rectangle, new ImagePoint(30, 50)));
        Assert.AreEqual(new ImageRect(20, 10, 20, 40),
            AnnotationRotation.RotatedBounds(rectangle, rectangle.Bounds));
    }

    [TestMethod]
    public void StepsAndCalloutsUseTheirVisibleCenters()
    {
        Annotation step = TestDocuments.Annotation(AnnotationKind.Step) with
        {
            Start = new ImagePoint(100, 80),
            RotationDegrees = 90,
        };
        Annotation callout = TestDocuments.Annotation(AnnotationKind.Callout) with
        {
            Start = new ImagePoint(20, 30),
            End = new ImagePoint(100, 120),
            TextBoxWidth = 200,
            TextBoxHeight = 80,
            RotationDegrees = 90,
        };

        Assert.AreEqual(step.Start, AnnotationRotation.Pivot(step));
        Assert.AreEqual(new ImagePoint(200, 160), AnnotationRotation.Pivot(callout));
        Assert.AreEqual(new ImagePoint(200, 150),
            AnnotationRotation.Rotate(callout, new ImagePoint(190, 160)));
    }

    [TestMethod]
    public void AngleNormalizationIsStableAndRejectsNonfiniteValues()
    {
        Assert.AreEqual(-90d, AnnotationRotation.NormalizeDegrees(270));
        Assert.AreEqual(-180d, AnnotationRotation.NormalizeDegrees(180));
        Assert.AreEqual(0d, AnnotationRotation.NormalizeDegrees(720));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            AnnotationRotation.NormalizeDegrees(double.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            AnnotationRotation.NormalizeDegrees(double.PositiveInfinity));
    }

    [TestMethod]
    public void RotatingNearAnEdgeExpandsCanvasAndRotatingBackTrimsItUndoably()
    {
        Annotation shape = TestDocuments.Annotation() with
        {
            Start = new ImagePoint(610, 430),
            End = new ImagePoint(637, 470),
            RotationDegrees = 45,
        };
        var editor = new DocumentEditor(TestDocuments.Create());

        editor.AddAnnotation(shape);
        ImageRect rotated = AnnotationRotation.RotatedBounds(shape, shape.Bounds);
        Assert.IsTrue(rotated.Right > 640);
        Assert.IsTrue(editor.Current.CanvasBounds.Right >= rotated.Right);

        editor.UpdateAnnotation(shape with { RotationDegrees = 0 });
        Assert.AreEqual(new ImageRect(0, 0, 640, 480), editor.Current.CanvasBounds);
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(45d, editor.Current.Annotations[0].RotationDegrees);
        Assert.IsTrue(editor.Redo());
        Assert.AreEqual(0d, editor.Current.Annotations[0].RotationDegrees);
    }

    [TestMethod]
    public async Task RotationPersistsThroughSaveAndCloneWhileFlatteningBakesItIntoPixels()
    {
        using var temporary = new TemporaryLibrary();
        Annotation shape = TestDocuments.Annotation() with { RotationDegrees = -32.5 };
        var editor = new DocumentEditor(TestDocuments.Create());
        editor.AddAnnotation(shape);

        Annotation clone = editor.CloneAnnotation(shape.Id)!;
        Assert.AreEqual(-32.5, clone.RotationDegrees);

        await temporary.Library.SaveAsync(editor.Current);
        string json = await File.ReadAllTextAsync(temporary.GetMetadataPath(editor.Current.Id));
        Assert.IsTrue(json.Contains("\"rotationDegrees\": -32.5", StringComparison.Ordinal));
        ShnappDocument reopened = (await temporary.Library.OpenAsync(editor.Current.Id))!;
        Assert.AreEqual(-32.5, reopened.Annotations[0].RotationDegrees);
        Assert.AreEqual(-32.5, reopened.Annotations[1].RotationDegrees);

        Assert.IsTrue(editor.FlattenAnnotation(shape.Id, TestDocuments.OnePixelPngBase64,
            new ImageRect(10, 20, 1, 1)));
        Assert.AreEqual(0d, editor.Current.Annotations[0].RotationDegrees);
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(-32.5, editor.Current.Annotations[0].RotationDegrees);
    }

    [TestMethod]
    public void NonfiniteRotationCannotEnterTheDocument()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation shape = TestDocuments.Annotation();
        editor.AddAnnotation(shape);

        Assert.Throws<ArgumentException>(() =>
            editor.UpdateAnnotation(shape with { RotationDegrees = double.NaN }));
        Assert.Throws<ArgumentException>(() =>
            editor.AddAnnotation(shape with
            {
                Id = Guid.NewGuid(),
                RotationDegrees = double.PositiveInfinity,
            }));
        Assert.AreEqual(0d, editor.Current.Annotations[0].RotationDegrees);
    }
}
