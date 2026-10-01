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
    public void CloneInsertsAboveSourceAndIsOneUndoableEdit()
    {
        Annotation source = TestDocuments.Annotation();
        Annotation above = TestDocuments.Annotation(AnnotationKind.Text) with { Text = "Above" };
        ShnappDocument original = TestDocuments.Create() with { Annotations = [source, above] };
        var editor = new DocumentEditor(original);
        int changes = 0;
        editor.Changed += (_, _) => changes++;

        Annotation clone = editor.CloneAnnotation(source.Id)!;

        Assert.AreNotEqual(source.Id, clone.Id);
        Assert.AreEqual(source.Start with { X = source.Start.X + 12, Y = source.Start.Y + 12 }, clone.Start);
        Assert.AreEqual(source.End with { X = source.End.X + 12, Y = source.End.Y + 12 }, clone.End);
        Assert.AreEqual(source, editor.Current.Annotations[0]);
        Assert.AreEqual(clone, editor.Current.Annotations[1]);
        Assert.AreEqual(above, editor.Current.Annotations[2]);
        Assert.AreEqual(1, changes);
        Assert.IsTrue(editor.Undo());
        Assert.AreSame(original, editor.Current);
        Assert.IsTrue(editor.Redo());
        Assert.AreEqual(clone, editor.Current.Annotations[1]);
    }

    [TestMethod]
    public void CloneImageSharesPngAndExpandsTransparentCanvas()
    {
        Annotation image = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(630, 470),
            End = new ImagePoint(640, 480),
            VisibilityClip = new ImageRect(631, 471, 8, 8),
        };
        var editor = new DocumentEditor(TestDocuments.Create() with { Annotations = [image] });

        Annotation clone = editor.CloneAnnotation(image.Id)!;

        Assert.AreEqual(AnnotationKind.Image, clone.Kind);
        Assert.AreNotEqual(image.Id, clone.Id);
        Assert.AreSame(image.ImagePngBase64, clone.ImagePngBase64);
        Assert.AreEqual(new ImagePoint(642, 482), clone.Start);
        Assert.AreEqual(new ImageRect(643, 483, 8, 8), clone.VisibilityClip);
        Assert.AreEqual(new ImageRect(0, 0, 652, 492), editor.Current.CanvasBounds);
        Assert.AreEqual(new ImageRect(0, 0, 652, 492), editor.Current.Viewport);
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(new ImageRect(0, 0, 640, 480), editor.Current.CanvasBounds);
    }

    [TestMethod]
    public void CloneAfterCropMovesIntoVisibleRoomAndSkipsHiddenMarks()
    {
        Annotation visible = TestDocuments.Annotation() with
        {
            Start = new ImagePoint(270, 210),
            End = new ImagePoint(300, 250),
        };
        Annotation hidden = TestDocuments.Annotation(AnnotationKind.Text) with
        {
            HiddenByCrop = true,
        };
        var editor = new DocumentEditor(TestDocuments.Create() with
        {
            Crop = new ImageRect(100, 100, 200, 150),
            Annotations = [visible, hidden],
        });
        Assert.IsNull(editor.CloneAnnotation(hidden.Id));
        Assert.IsFalse(editor.CanUndo);

        Annotation clone = editor.CloneAnnotation(visible.Id)!;

        Assert.AreEqual(new ImagePoint(258, 198), clone.Start);
        Assert.AreEqual(new ImagePoint(288, 238), clone.End);
        Assert.AreEqual(editor.Current.Viewport, new ImageRect(100, 100, 200, 150));
    }

    [TestMethod]
    public void LayerMovesPreserveStepLabelsAndCanBeUndone()
    {
        Annotation first = TestDocuments.Annotation(AnnotationKind.Step);
        Annotation reset = TestDocuments.Annotation(AnnotationKind.Step) with { StepReset = true };
        Annotation last = TestDocuments.Annotation(AnnotationKind.Step);
        Annotation shape = TestDocuments.Annotation();
        ShnappDocument original = TestDocuments.Create() with { Annotations = [first, shape, reset, last] };
        var editor = new DocumentEditor(original);

        editor.MoveAnnotationToFront(first.Id);
        CollectionAssert.AreEqual(new[] { shape.Id, reset.Id, last.Id, first.Id },
            editor.Current.OrderedAnnotations.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(new[] { first.Id, shape.Id, reset.Id, last.Id },
            editor.Current.Annotations.Select(item => item.Id).ToArray());
        AssertStepNumbers(editor, 1, 1, 2);

        editor.MoveAnnotationToBack(last.Id);
        CollectionAssert.AreEqual(new[] { last.Id, shape.Id, reset.Id, first.Id },
            editor.Current.OrderedAnnotations.Select(item => item.Id).ToArray());
        AssertStepNumbers(editor, 1, 1, 2);
        Assert.IsTrue(editor.Undo());
        CollectionAssert.AreEqual(new[] { shape.Id, reset.Id, last.Id, first.Id },
            editor.Current.OrderedAnnotations.Select(item => item.Id).ToArray());
        AssertStepNumbers(editor, 1, 1, 2);
        Assert.IsTrue(editor.Undo());
        AssertStepNumbers(editor, 1, 1, 2);
        Assert.IsTrue(editor.Redo());
        AssertStepNumbers(editor, 1, 1, 2);
    }

    [TestMethod]
    public void ClonesStayAboveTheirSourceAndNewMarksStayOnTopAfterReordering()
    {
        Annotation first = TestDocuments.Annotation();
        Annotation second = TestDocuments.Annotation(AnnotationKind.Ellipse);
        Annotation third = TestDocuments.Annotation(AnnotationKind.Line);
        var editor = new DocumentEditor(TestDocuments.Create() with { Annotations = [first, second, third] });
        editor.MoveAnnotationToFront(first.Id);

        Annotation clone = editor.CloneAnnotation(second.Id)!;
        CollectionAssert.AreEqual(new[] { second.Id, clone.Id, third.Id, first.Id },
            editor.Current.OrderedAnnotations.Select(annotation => annotation.Id).ToArray());

        Annotation newMark = TestDocuments.Annotation(AnnotationKind.Text);
        editor.AddAnnotation(newMark);
        Assert.AreEqual(newMark.Id, editor.Current.OrderedAnnotations.Last().Id);
        Assert.AreEqual(first.Id, editor.Current.Annotations[0].Id);
    }

    [TestMethod]
    public void MissingAndAlreadyExtremeLayerActionsDoNotChangeHistory()
    {
        Annotation first = TestDocuments.Annotation();
        Annotation last = TestDocuments.Annotation(AnnotationKind.Text);
        ShnappDocument original = TestDocuments.Create() with { Annotations = [first, last] };
        var editor = new DocumentEditor(original);
        int changes = 0;
        editor.Changed += (_, _) => changes++;

        Assert.IsNull(editor.CloneAnnotation(Guid.NewGuid()));
        editor.MoveAnnotationToFront(Guid.NewGuid());
        editor.MoveAnnotationToBack(Guid.NewGuid());
        editor.MoveAnnotationToFront(last.Id);
        editor.MoveAnnotationToBack(first.Id);

        Assert.AreSame(original, editor.Current);
        Assert.AreEqual(0, changes);
        Assert.IsFalse(editor.CanUndo);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => editor.CloneAnnotation(Guid.Empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => editor.MoveAnnotationToFront(Guid.Empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => editor.MoveAnnotationToBack(Guid.Empty));
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
    public void ResetStepRestartsSequenceAndRenumbersLaterDotsOnUndo()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation first = TestDocuments.Annotation(AnnotationKind.Step);
        Annotation second = TestDocuments.Annotation(AnnotationKind.Step);
        Annotation third = TestDocuments.Annotation(AnnotationKind.Step);
        editor.AddAnnotation(first);
        editor.AddAnnotation(second);
        editor.AddAnnotation(third);
        AssertStepNumbers(editor, 1, 2, 3);

        editor.UpdateAnnotation(editor.Current.Annotations[1] with { StepReset = true });
        AssertStepNumbers(editor, 1, 1, 2);
        Assert.IsTrue(editor.Undo());
        AssertStepNumbers(editor, 1, 2, 3);
        Assert.IsTrue(editor.Redo());
        AssertStepNumbers(editor, 1, 1, 2);
        editor.RemoveAnnotation(second.Id);
        AssertStepNumbers(editor, 1, 2);
    }

    [TestMethod]
    public void LetterAndRomanLabelsHandleSequenceBoundaries()
    {
        Assert.AreEqual("1", StepLabels.Format(1, StepLabelFormat.Decimal));
        Assert.AreEqual("Z", StepLabels.Format(26, StepLabelFormat.UpperLetters));
        Assert.AreEqual("aa", StepLabels.Format(27, StepLabelFormat.LowerLetters));
        Assert.AreEqual("CMXLIV", StepLabels.Format(944, StepLabelFormat.UpperRoman));
        Assert.AreEqual("xiv", StepLabels.Format(14, StepLabelFormat.LowerRoman));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => StepLabels.Format(0, StepLabelFormat.Decimal));
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
            valid with { StartCap = (LineEndCap)99 },
            valid with { EndCap = (LineEndCap)99 },
            valid with { LinePattern = (LinePattern)99 },
            valid with { StepLabelFormat = (StepLabelFormat)99 },
            valid with { StepReset = true },
            valid with { LayerOrder = -1 },
            valid with { Start = new ImagePoint(double.NaN, 1) },
            valid with { Start = new ImagePoint(1, double.PositiveInfinity) },
            valid with { Start = new ImagePoint(-1, 0) },
            valid with { Start = new ImagePoint(0, 481) },
            valid with { End = new ImagePoint(641, 0) },
            valid with { End = new ImagePoint(0, double.NegativeInfinity) },
            valid with { StrokeWidth = -1 },
            valid with { StrokeWidth = double.NaN },
            valid with { HideOutline = true, FillArgb = 0 },
            valid with { HideOutline = true, FillArgb = 0x00E5484D },
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
            HideOutline = true,
        };

        editor.AddAnnotation(annotation);

        Assert.AreEqual(annotation, editor.Current.Annotations.Single());
    }

    [TestMethod]
    public void PastedImageExpandsTransparentCanvasInEveryDirectionAndUndoRestoresIt()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation image = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(-50.25, -25.5),
            End = new ImagePoint(700.4, 510.2),
        };

        editor.AddAnnotation(image);

        Assert.AreEqual(new ImageRect(-51, -26, 752, 537), editor.Current.CanvasBounds);
        Assert.AreEqual(editor.Current.CanvasBounds, editor.Current.Viewport);
        Assert.AreEqual(new ImageRect(0, 0, 640, 480), editor.Current.OriginalBounds);
        Assert.AreEqual(640, editor.Current.PixelWidth);
        editor.AddAnnotation(TestDocuments.Annotation(AnnotationKind.Text) with
        {
            Start = new ImagePoint(-20, -10),
            End = new ImagePoint(0, 0),
            Text = "On transparent canvas",
        });
        Assert.HasCount(2, editor.Current.Annotations);

        Assert.IsTrue(editor.Undo());
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(new ImageRect(0, 0, 640, 480), editor.Current.CanvasBounds);
        Assert.IsTrue(editor.Redo());
        Assert.AreEqual(new ImageRect(-51, -26, 752, 537), editor.Current.Viewport);
        editor.RemoveAnnotation(image.Id);
        Assert.AreEqual(new ImageRect(0, 0, 640, 480), editor.Current.CanvasBounds);
        Assert.AreEqual(editor.Current.OriginalBounds, editor.Current.Viewport);
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(new ImageRect(-51, -26, 752, 537), editor.Current.CanvasBounds);
    }

    [TestMethod]
    public void MovingAndRemovingImagesTrimOnlyUnusedTransparentMargins()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation left = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(-100, 100),
            End = new ImagePoint(50, 200),
        };
        Annotation right = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(600, 100),
            End = new ImagePoint(800, 200),
        };
        editor.AddAnnotation(left);
        editor.AddAnnotation(right);
        Assert.AreEqual(new ImageRect(-100, 0, 900, 480), editor.Current.CanvasBounds);

        editor.UpdateAnnotation(left with
        {
            Start = new ImagePoint(100, 100),
            End = new ImagePoint(250, 200),
        });
        Assert.AreEqual(new ImageRect(0, 0, 800, 480), editor.Current.CanvasBounds);

        editor.RemoveAnnotation(right.Id);
        Assert.AreEqual(editor.Current.OriginalBounds, editor.Current.CanvasBounds);
    }

    [TestMethod]
    public void RemovingImageAfterCropRestoresTheOriginalVisibleRegion()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        ImageRect originalCrop = new(100, 100, 200, 150);
        editor.ApplyCrop(originalCrop);
        Annotation image = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(-20, -20),
            End = new ImagePoint(40, 40),
        };
        editor.AddAnnotation(image);
        Assert.AreEqual(new ImageRect(-20, -20, 320, 270), editor.Current.Viewport);

        editor.RemoveAnnotation(image.Id);

        Assert.AreEqual(editor.Current.OriginalBounds, editor.Current.CanvasBounds);
        Assert.AreEqual(originalCrop, editor.Current.Viewport);
        Assert.IsNull(editor.Current.BaseImageCrop);
        Assert.IsFalse(editor.Current.HideOriginalImage);
    }

    [TestMethod]
    public void RemovingLastLayerFromImageOnlyCropLeavesATransparentPixel()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation image = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(700, 40),
            End = new ImagePoint(900, 240),
        };
        editor.AddAnnotation(image);
        editor.ApplyCrop(new ImageRect(700, 40, 200, 200));
        Assert.IsTrue(editor.Current.HideOriginalImage);

        editor.RemoveAnnotation(image.Id);

        Assert.IsTrue(editor.Current.HideOriginalImage);
        Assert.AreEqual(new ImageRect(0, 0, 1, 1), editor.Current.Viewport);
        Assert.AreEqual(editor.Current.OriginalBounds, editor.Current.CanvasBounds);
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(new ImageRect(700, 40, 200, 200), editor.Current.Viewport);
    }

    [TestMethod]
    public void InvisibleImageOutsideCropDoesNotHoldCanvasOpen()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation image = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(-300, 100),
            End = new ImagePoint(-100, 300),
        };
        Annotation step = TestDocuments.Annotation(AnnotationKind.Step) with
        {
            Start = new ImagePoint(100, 100),
            End = new ImagePoint(100, 100),
        };
        editor.AddAnnotation(image);
        editor.ApplyCrop(editor.Current.OriginalBounds);
        editor.AddAnnotation(step);

        editor.RemoveAnnotation(step.Id);

        Assert.AreEqual(editor.Current.OriginalBounds, editor.Current.CanvasBounds);
        Assert.HasCount(1, editor.Current.Annotations);
        _ = new DocumentEditor(editor.Current);
    }

    [TestMethod]
    public void StepAtCaptureEdgeDoesNotGrowCanvasWhenAnotherLayerIsDeleted()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation step = TestDocuments.Annotation(AnnotationKind.Step) with
        {
            Start = new ImagePoint(0, 0),
            End = new ImagePoint(0, 0),
        };
        Annotation image = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(700, 100),
            End = new ImagePoint(900, 300),
        };
        editor.AddAnnotation(step);
        editor.AddAnnotation(image);

        editor.RemoveAnnotation(image.Id);

        Assert.AreEqual(editor.Current.OriginalBounds, editor.Current.CanvasBounds);
    }

    [TestMethod]
    public void LongTextGrowsCanvasAndTrimsWhenShortenedOrRemoved()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation text = TestDocuments.Annotation(AnnotationKind.Text) with
        {
            Start = new ImagePoint(600, 100),
            End = new ImagePoint(900, 190),
            Text = "A long annotation",
        };

        editor.AddAnnotation(text);
        Assert.AreEqual(new ImageRect(0, 0, 900, 480), editor.Current.CanvasBounds);

        editor.UpdateAnnotation(text with { End = new ImagePoint(630, 190), Text = "Short" });
        Assert.AreEqual(editor.Current.OriginalBounds, editor.Current.CanvasBounds);

        editor.RemoveAnnotation(text.Id);
        Assert.AreEqual(editor.Current.OriginalBounds, editor.Current.CanvasBounds);
        Assert.IsTrue(editor.Undo());
        Assert.HasCount(1, editor.Current.Annotations);
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(new ImageRect(0, 0, 900, 480), editor.Current.CanvasBounds);
    }

    [TestMethod]
    public void PastingBeyondAnExistingCropKeepsOriginalMaskAndLaterCropClipsImage()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        ImageRect oldCrop = new(100, 100, 200, 150);
        editor.ApplyCrop(oldCrop);
        Annotation image = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(-20, -20),
            End = new ImagePoint(40, 40),
        };

        editor.AddAnnotation(image);

        Assert.AreEqual(oldCrop, editor.Current.BaseImageCrop);
        Assert.AreEqual(new ImageRect(-20, -20, 320, 270), editor.Current.Crop);
        Assert.AreEqual(new ImageRect(-20, -20, 660, 500), editor.Current.CanvasBounds);
        editor.ApplyCrop(new ImageRect(100, 100, 200, 150));
        Assert.AreEqual(oldCrop, editor.Current.Viewport);
        Assert.AreEqual(oldCrop, editor.Current.BaseImageCrop);
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(new ImageRect(-20, -20, 320, 270), editor.Current.Viewport);
    }

    [TestMethod]
    public void RecroppingTightensOriginalMaskBeforeAnotherImageExpandsTheViewport()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        editor.ApplyCrop(new ImageRect(100, 100, 200, 150));
        editor.AddAnnotation(TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(-20, -20),
            End = new ImagePoint(40, 40),
        });

        ImageRect narrow = new(150, 150, 50, 50);
        editor.ApplyCrop(narrow);
        Assert.AreEqual(narrow, editor.Current.BaseImageCrop);
        Assert.IsFalse(editor.Current.HideOriginalImage);

        editor.AddAnnotation(TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(320, 320),
            End = new ImagePoint(350, 350),
        });
        Assert.AreEqual(narrow, editor.Current.BaseImageCrop);
        Assert.AreEqual(new ImageRect(150, 150, 200, 200), editor.Current.Viewport);

        editor.ApplyCrop(new ImageRect(320, 320, 30, 30));
        Assert.IsNull(editor.Current.BaseImageCrop);
        Assert.IsTrue(editor.Current.HideOriginalImage);
        editor.AddAnnotation(TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(0, 0),
            End = new ImagePoint(20, 20),
        });
        Assert.IsTrue(editor.Current.HideOriginalImage);
        Assert.AreEqual(new ImageRect(0, 0, 350, 350), editor.Current.Viewport);
    }

    [TestMethod]
    public void ExpandingARecroppedCanvasDoesNotRestoreOlderTextOrPastedImages()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation text = TestDocuments.Annotation(AnnotationKind.Text) with
        {
            Start = new ImagePoint(400, 400),
            End = new ImagePoint(400, 400),
            Text = "Previously cropped out",
        };
        Annotation olderImage = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(420, 410),
            End = new ImagePoint(450, 440),
        };
        editor.AddAnnotation(text);
        editor.AddAnnotation(olderImage);
        editor.ApplyCrop(new ImageRect(100, 100, 100, 100));

        Annotation newImage = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(500, 500),
            End = new ImagePoint(550, 550),
        };
        editor.AddAnnotation(newImage);

        Assert.AreEqual(new ImageRect(100, 100, 450, 450), editor.Current.Viewport);
        Assert.AreEqual(new ImageRect(100, 100, 100, 100),
            editor.Current.Annotations.Single(item => item.Id == text.Id).VisibilityClip);
        Assert.AreEqual(new ImageRect(100, 100, 100, 100),
            editor.Current.Annotations.Single(item => item.Id == olderImage.Id).VisibilityClip);
        Assert.IsNull(editor.Current.Annotations.Single(item => item.Id == newImage.Id).VisibilityClip);

        editor.ApplyCrop(new ImageRect(500, 500, 50, 50));
        editor.AddAnnotation(TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(0, 0),
            End = new ImagePoint(20, 20),
        });
        Assert.IsTrue(editor.Current.Annotations.Single(item => item.Id == text.Id).HiddenByCrop);
        Assert.IsTrue(editor.Current.Annotations.Single(item => item.Id == olderImage.Id).HiddenByCrop);

        Assert.IsTrue(editor.Undo());
        Assert.IsFalse(editor.Current.Annotations.Single(item => item.Id == text.Id).HiddenByCrop);
    }

    [TestMethod]
    public void MovingClippedPastedImageMovesItsVisibleClip()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation image = TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(100, 100),
            End = new ImagePoint(200, 200),
        };
        editor.AddAnnotation(image);
        editor.ApplyCrop(new ImageRect(125, 125, 50, 50));
        editor.AddAnnotation(TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(500, 400),
            End = new ImagePoint(550, 450),
        });

        Annotation moved = image with
        {
            Start = new ImagePoint(200, 200),
            End = new ImagePoint(300, 300),
        };
        Annotation preview = editor.PreviewAnnotation(moved);
        Assert.AreEqual(new ImageRect(225, 225, 50, 50), preview.VisibilityClip);
        Assert.AreEqual(new ImageRect(125, 125, 50, 50),
            editor.Current.Annotations.Single(item => item.Id == image.Id).VisibilityClip);
        editor.UpdateAnnotation(moved);

        Assert.AreEqual(new ImageRect(225, 225, 50, 50),
            editor.Current.Annotations.Single(item => item.Id == image.Id).VisibilityClip);
    }

    [TestMethod]
    public void UpdatingACompletelyCroppedOutAnnotationKeepsItHidden()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation text = TestDocuments.Annotation(AnnotationKind.Text) with { Text = "secret" };
        editor.AddAnnotation(text);
        editor.ApplyCrop(new ImageRect(100, 100, 100, 100));
        editor.AddAnnotation(TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(300, 300),
            End = new ImagePoint(350, 350),
        });
        editor.ApplyCrop(new ImageRect(300, 300, 50, 50));
        editor.AddAnnotation(TestDocuments.Annotation(AnnotationKind.Image) with
        {
            Start = new ImagePoint(0, 0),
            End = new ImagePoint(20, 20),
        });
        Assert.IsTrue(editor.Current.Annotations.Single(item => item.Id == text.Id).HiddenByCrop);

        editor.UpdateAnnotation(new Annotation
        {
            Id = text.Id,
            Kind = AnnotationKind.Text,
            Start = new ImagePoint(10, 20),
            End = new ImagePoint(10, 20),
            Text = "still secret",
        });

        Assert.IsTrue(editor.Current.Annotations.Single(item => item.Id == text.Id).HiddenByCrop);
    }

    [TestMethod]
    public void InvalidOrOversizedPastedImagesAreRejectedWithoutHistory()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation valid = TestDocuments.Annotation(AnnotationKind.Image);
        Annotation[] invalid =
        [
            valid with { ImagePngBase64 = null },
            valid with { ImagePngBase64 = "not a PNG" },
            valid with { Start = new ImagePoint(0, 0), End = new ImagePoint(0, 10) },
            valid with { Start = new ImagePoint(50_000, 0), End = new ImagePoint(50_010, 10) },
        ];

        foreach (Annotation image in invalid)
        {
            Assert.Throws<ArgumentException>(() => editor.AddAnnotation(image));
        }

        Assert.IsTrue(editor.Current.Annotations.IsEmpty);
        Assert.IsFalse(editor.CanUndo);
    }

    private static void AssertStepNumbers(DocumentEditor editor, params int[] expected) =>
        CollectionAssert.AreEqual(expected,
            editor.Current.Annotations.Where(annotation => annotation.Kind == AnnotationKind.Step)
                .Select(annotation => annotation.StepNumber).ToArray());
}
