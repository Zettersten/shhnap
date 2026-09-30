using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class DocumentEditorTests
{
    [TestMethod]
    public void ConstructorKeepsAnAlreadyValidSnapshotAndStartsWithEmptyHistory()
    {
        ShnappDocument document = TestDocuments.Create();
        var editor = new DocumentEditor(document);

        Assert.AreSame(document, editor.Current);
        Assert.IsFalse(editor.CanUndo);
        Assert.IsFalse(editor.CanRedo);
        Assert.IsFalse(editor.Undo());
        Assert.IsFalse(editor.Redo());
    }

    [TestMethod]
    public void ConstructorNormalizesStepsWithoutMutatingTheSuppliedSnapshot()
    {
        Annotation first = TestDocuments.Annotation(AnnotationKind.Step) with { StepNumber = 42 };
        Annotation second = TestDocuments.Annotation(AnnotationKind.Step) with { StepNumber = -20 };
        ShnappDocument document = TestDocuments.Create() with
        {
            Annotations = [first, TestDocuments.Annotation(), second],
        };

        var editor = new DocumentEditor(document);

        AssertStepNumbers(editor, 1, 2);
        Assert.AreEqual(42, document.Annotations[0].StepNumber);
        Assert.AreEqual(-20, document.Annotations[2].StepNumber);
        Assert.IsFalse(editor.CanUndo);
    }

    [TestMethod]
    public void NullArgumentsAndEmptyIdentifiersAreRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new DocumentEditor(null!));
        var editor = new DocumentEditor(TestDocuments.Create());

        Assert.ThrowsExactly<ArgumentNullException>(() => editor.AddAnnotation(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => editor.UpdateAnnotation(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => editor.RemoveAnnotation(Guid.Empty));
        Assert.IsFalse(editor.CanUndo);
    }

    [TestMethod]
    public void InvalidDocumentDimensionsSchemaAndCropsAreRejected()
    {
        ShnappDocument valid = TestDocuments.Create();
        ShnappDocument[] invalid =
        [
            valid with { Id = Guid.Empty },
            valid with { SchemaVersion = 0 },
            valid with { SchemaVersion = 2 },
            valid with { PixelWidth = 0 },
            valid with { PixelHeight = -1 },
            valid with { CaptureKind = (CaptureKind)99 },
            valid with { Title = null! },
            valid with { Annotations = default },
            valid with { Annotations = [null!] },
            valid with { Crop = new ImageRect(0.5, 0, 10, 10) },
            valid with { Crop = new ImageRect(0, 0, 10.5, 10) },
            valid with { Crop = new ImageRect(-1, 0, 10, 10) },
            valid with { Crop = new ImageRect(639, 0, 2, 10) },
            valid with { Crop = new ImageRect(0, 0, 0, 10) },
            valid with { Crop = new ImageRect(0, 0, double.NaN, 10) },
        ];
        foreach (ShnappDocument document in invalid)
        {
            Assert.Throws<ArgumentException>(() => _ = new DocumentEditor(document));
        }

        Annotation annotation = TestDocuments.Annotation();
        Assert.Throws<ArgumentException>(() => _ = new DocumentEditor(valid with { Annotations = [annotation, annotation] }));
    }

    [TestMethod]
    public void AddCreatesAnImmutableSnapshotAndNotifiesAfterTheStateIsCommitted()
    {
        ShnappDocument original = TestDocuments.Create();
        var editor = new DocumentEditor(original);
        Annotation annotation = TestDocuments.Annotation();
        int changes = 0;
        editor.Changed += (sender, args) =>
        {
            Assert.AreSame(editor, sender);
            Assert.AreSame(EventArgs.Empty, args);
            Assert.IsTrue(editor.CanUndo);
            Assert.AreEqual(annotation, editor.Current.Annotations.Single());
            changes++;
        };

        editor.AddAnnotation(annotation);

        Assert.AreEqual(1, changes);
        Assert.IsTrue(original.Annotations.IsEmpty);
        Assert.AreEqual(original.Id, editor.Current.Id);
        Assert.AreEqual(original.Title, editor.Current.Title);
        Assert.AreEqual(original.CreatedAt, editor.Current.CreatedAt);
        Assert.IsFalse(editor.CanRedo);
    }

    [TestMethod]
    public void DuplicateAddIsRejectedWithoutChangingSnapshotOrHistory()
    {
        Annotation annotation = TestDocuments.Annotation();
        ShnappDocument original = TestDocuments.Create() with { Annotations = [annotation] };
        var editor = new DocumentEditor(original);

        Assert.ThrowsExactly<ArgumentException>(() => editor.AddAnnotation(annotation with { Text = "duplicate" }));

        Assert.AreSame(original, editor.Current);
        Assert.IsFalse(editor.CanUndo);
    }

    [TestMethod]
    public void UpdatePreservesIdentityOrderAndRestoresExactSnapshots()
    {
        Annotation first = TestDocuments.Annotation();
        Annotation second = TestDocuments.Annotation(AnnotationKind.Text);
        ShnappDocument original = TestDocuments.Create() with { Annotations = [first, second] };
        var editor = new DocumentEditor(original);
        Annotation replacement = first with { Start = new ImagePoint(40, 50), StrokeWidth = 5 };

        editor.UpdateAnnotation(replacement);
        ShnappDocument changed = editor.Current;

        Assert.AreEqual(replacement, changed.Annotations[0]);
        Assert.AreEqual(second, changed.Annotations[1]);
        Assert.AreEqual(first, original.Annotations[0]);
        Assert.IsTrue(editor.Undo());
        Assert.AreSame(original, editor.Current);
        Assert.IsTrue(editor.Redo());
        Assert.AreSame(changed, editor.Current);
    }

    [TestMethod]
    public void MissingAndSemanticallyIdenticalOperationsDoNotCreateHistoryOrEvents()
    {
        Annotation step = TestDocuments.Annotation(AnnotationKind.Step) with { StepNumber = 1 };
        ShnappDocument original = TestDocuments.Create() with { Annotations = [step] };
        var editor = new DocumentEditor(original);
        int changes = 0;
        editor.Changed += (_, _) => changes++;

        editor.UpdateAnnotation(step with { });
        editor.UpdateAnnotation(step with { StepNumber = 999 });
        editor.UpdateAnnotation(TestDocuments.Annotation());
        editor.RemoveAnnotation(Guid.NewGuid());
        editor.ApplyCrop(new ImageRect(-20, -20, 1000, 1000));
        Assert.IsFalse(editor.Undo());
        Assert.IsFalse(editor.Redo());

        Assert.AreSame(original, editor.Current);
        Assert.AreEqual(0, changes);
        Assert.IsFalse(editor.CanUndo);
        Assert.IsFalse(editor.CanRedo);
    }

    [TestMethod]
    public void StepsAreConsecutiveAcrossMixedToolsAndDeletionAndHistoryRestoresNumbering()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation first = TestDocuments.Annotation(AnnotationKind.Step) with { StepNumber = 100 };
        Annotation second = TestDocuments.Annotation(AnnotationKind.Step);
        Annotation third = TestDocuments.Annotation(AnnotationKind.Step);
        editor.AddAnnotation(first);
        editor.AddAnnotation(TestDocuments.Annotation(AnnotationKind.Arrow));
        editor.AddAnnotation(second);
        editor.AddAnnotation(third);
        ShnappDocument beforeDelete = editor.Current;
        AssertStepNumbers(editor, 1, 2, 3);

        editor.RemoveAnnotation(second.Id);
        ShnappDocument afterDelete = editor.Current;

        AssertStepNumbers(editor, 1, 2);
        Assert.AreEqual(third.Id, editor.Current.Annotations[^1].Id);
        Assert.IsTrue(editor.Undo());
        Assert.AreSame(beforeDelete, editor.Current);
        AssertStepNumbers(editor, 1, 2, 3);
        Assert.IsTrue(editor.Redo());
        Assert.AreSame(afterDelete, editor.Current);
        AssertStepNumbers(editor, 1, 2);
        Assert.AreEqual(100, first.StepNumber);
        Assert.AreEqual(0, third.StepNumber);
    }

    [TestMethod]
    public void ChangingAnnotationKindsKeepsStepsNumberedInDocumentOrder()
    {
        Annotation shape = TestDocuments.Annotation();
        Annotation step = TestDocuments.Annotation(AnnotationKind.Step) with { StepNumber = 1 };
        var editor = new DocumentEditor(TestDocuments.Create() with { Annotations = [shape, step] });

        editor.UpdateAnnotation(shape with { Kind = AnnotationKind.Step });
        AssertStepNumbers(editor, 1, 2);
        editor.UpdateAnnotation(editor.Current.Annotations[0] with { Kind = AnnotationKind.Rectangle });
        AssertStepNumbers(editor, 1);
        Assert.IsTrue(editor.Undo());
        AssertStepNumbers(editor, 1, 2);
    }

    [TestMethod]
    public void NewWorkClearsRedoButNoOpsAndRejectedWorkDoNot()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation first = TestDocuments.Annotation();
        editor.AddAnnotation(first);
        editor.AddAnnotation(TestDocuments.Annotation());
        Assert.IsTrue(editor.Undo());
        int changes = 0;
        editor.Changed += (_, _) => changes++;

        editor.UpdateAnnotation(first with { });
        editor.RemoveAnnotation(Guid.NewGuid());
        editor.ApplyCrop(new ImageRect(0, 0, 640, 480));
        Assert.Throws<ArgumentException>(() => editor.ApplyCrop(new ImageRect(0, 0, 0, 1)));
        Assert.IsTrue(editor.CanRedo);
        Assert.AreEqual(0, changes);

        editor.UpdateAnnotation(first with { StrokeWidth = 4 });

        Assert.IsFalse(editor.CanRedo);
        Assert.IsFalse(editor.Redo());
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public void HistoryRetainsExactlyTheMostRecentHundredOperations()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        for (int index = 0; index < 110; index++)
        {
            editor.AddAnnotation(TestDocuments.Annotation(AnnotationKind.Step));
        }

        int undos = 0;
        while (editor.Undo())
        {
            undos++;
        }

        Assert.AreEqual(100, undos);
        Assert.HasCount(10, editor.Current.Annotations);
        AssertStepNumbers(editor, Enumerable.Range(1, 10).ToArray());
        int redos = 0;
        while (editor.Redo())
        {
            redos++;
        }

        Assert.AreEqual(100, redos);
        Assert.HasCount(110, editor.Current.Annotations);
        AssertStepNumbers(editor, Enumerable.Range(1, 110).ToArray());
        Assert.IsFalse(editor.CanRedo);
    }

    [TestMethod]
    [DataRow(10.25, 20.75, 89.5, 40.5, 10, 20, 90, 42)]
    [DataRow(-10.1, -20.2, 30.2, 40.4, 0, 0, 21, 21)]
    [DataRow(639.9, 479.9, 20.0, 20.0, 639, 479, 1, 1)]
    [DataRow(6.1, 8.1, 0.1, 0.1, 6, 8, 1, 1)]
    [DataRow(0.0, 0.0, 640.0, 480.0, 0, 0, 640, 480)]
    public void CropUsesExactOutwardRoundedIntegerPixelBounds(double x, double y, double width, double height,
        int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        var editor = new DocumentEditor(TestDocuments.Create());

        editor.ApplyCrop(new ImageRect(x, y, width, height));

        Assert.AreEqual(new ImageRect(expectedX, expectedY, expectedWidth, expectedHeight), editor.Current.Viewport);
        Assert.IsTrue(editor.Current.Viewport.Width >= 1);
        Assert.IsTrue(editor.Current.Viewport.Height >= 1);
    }

    [TestMethod]
    public void RepeatedCropsUseSourceCoordinatesAndLeaveOriginalAndAnnotationsUnchanged()
    {
        Annotation outside = TestDocuments.Annotation();
        Annotation inside = TestDocuments.Annotation(AnnotationKind.Arrow) with
        {
            Start = new ImagePoint(230, 150),
            End = new ImagePoint(280, 190),
        };
        ShnappDocument original = TestDocuments.Create() with { Annotations = [outside, inside] };
        var editor = new DocumentEditor(original);
        editor.ApplyCrop(new ImageRect(100, 50, 300, 200));
        ShnappDocument firstCrop = editor.Current;

        editor.ApplyCrop(new ImageRect(200.25, 100.75, 100.5, 100.5));
        ShnappDocument secondCrop = editor.Current;

        Assert.AreEqual(new ImageRect(200, 100, 101, 102), secondCrop.Viewport);
        Assert.AreEqual(640, secondCrop.PixelWidth);
        Assert.AreEqual(480, secondCrop.PixelHeight);
        Assert.AreEqual(original.Annotations, secondCrop.Annotations);
        Assert.AreEqual(new ImagePoint(230, 150), secondCrop.Annotations[1].Start);
        Assert.AreEqual(new ImagePoint(30, 50),
            new ImagePoint(inside.Start.X - secondCrop.Viewport.X, inside.Start.Y - secondCrop.Viewport.Y));
        editor.ApplyCrop(new ImageRect(-100, -100, 2000, 2000));
        Assert.AreSame(secondCrop, editor.Current);
        Assert.IsTrue(editor.Undo());
        Assert.AreSame(firstCrop, editor.Current);
        Assert.IsTrue(editor.Undo());
        Assert.AreSame(original, editor.Current);
        Assert.IsNull(editor.Current.Crop);
        Assert.IsTrue(editor.Redo());
        Assert.AreSame(firstCrop, editor.Current);
    }

    [TestMethod]
    public void InvalidAndNonfiniteCropsAreRejectedWithoutHistoryOrEvents()
    {
        ImageRect[] invalid =
        [
            new(double.NaN, 0, 10, 10),
            new(0, double.PositiveInfinity, 10, 10),
            new(0, 0, double.NaN, 10),
            new(0, 0, 10, double.NegativeInfinity),
            new(0, 0, 0, 10),
            new(0, 0, 10, 0),
            new(0, 0, -10, 10),
            new(0, 0, 10, -10),
            new(double.MaxValue, 0, double.MaxValue, 10),
            new(0, double.MaxValue, 10, double.MaxValue),
            new(640, 0, 1, 1),
            new(0, 480, 1, 1),
            new(-100, 0, 50, 10),
        ];
        ShnappDocument original = TestDocuments.Create();
        var editor = new DocumentEditor(original);
        int changes = 0;
        editor.Changed += (_, _) => changes++;
        foreach (ImageRect crop in invalid)
        {
            Assert.Throws<ArgumentException>(() => editor.ApplyCrop(crop));
        }

        Assert.AreSame(original, editor.Current);
        Assert.IsFalse(editor.CanUndo);
        Assert.AreEqual(0, changes);
    }

    [TestMethod]
    public void InvalidAnnotationCoordinatesAndStyleRangesAreRejected()
    {
        Annotation valid = TestDocuments.Annotation();
        Annotation[] invalid =
        [
            valid with { Id = Guid.Empty },
            valid with { Kind = (AnnotationKind)99 },
            valid with { RedactionMode = (RedactionMode)99 },
            valid with { Start = new ImagePoint(double.NaN, 1) },
            valid with { Start = new ImagePoint(1, double.PositiveInfinity) },
            valid with { Start = new ImagePoint(-1, 0) },
            valid with { Start = new ImagePoint(0, 481) },
            valid with { End = new ImagePoint(641, 0) },
            valid with { End = new ImagePoint(0, double.NegativeInfinity) },
            valid with { StrokeWidth = -1 },
            valid with { StrokeWidth = double.NaN },
            valid with { FontSize = 0 },
            valid with { FontSize = double.PositiveInfinity },
            valid with { StepDiameter = 0 },
            valid with { StepDiameter = double.NaN },
            valid with { FontWeight = 99 },
            valid with { FontWeight = 901 },
            valid with { FontFamily = " " },
            valid with { Text = null! },
        ];
        var editor = new DocumentEditor(TestDocuments.Create());
        foreach (Annotation annotation in invalid)
        {
            Assert.Throws<ArgumentException>(() => editor.AddAnnotation(annotation));
            Assert.Throws<ArgumentException>(() => editor.UpdateAnnotation(annotation));
        }

        Assert.IsTrue(editor.Current.Annotations.IsEmpty);
        Assert.IsFalse(editor.CanUndo);
    }

    [TestMethod]
    public void SourceImageEdgesAndZeroStrokeForFilledShapesAreValid()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation annotation = TestDocuments.Annotation() with
        {
            Start = new ImagePoint(640, 480),
            End = new ImagePoint(0, 0),
            StrokeWidth = 0,
            FillArgb = 0xFF0A84FF,
        };

        editor.AddAnnotation(annotation);

        Assert.AreEqual(annotation, editor.Current.Annotations.Single());
    }

    private static void AssertStepNumbers(DocumentEditor editor, params int[] expected) =>
        CollectionAssert.AreEqual(expected,
            editor.Current.Annotations.Where(annotation => annotation.Kind == AnnotationKind.Step)
                .Select(annotation => annotation.StepNumber).ToArray());
}
