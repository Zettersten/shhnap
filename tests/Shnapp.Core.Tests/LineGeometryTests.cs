using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class LineGeometryTests
{
    [TestMethod]
    public void ShiftConstraintUsesTheDominantDragAxis()
    {
        var start = new ImagePoint(10, 20);
        Assert.AreEqual(new ImagePoint(90, 20),
            LineGeometry.ConstrainAxis(start, new ImagePoint(90, 40)));
        Assert.AreEqual(new ImagePoint(10, -60),
            LineGeometry.ConstrainAxis(start, new ImagePoint(25, -60)));
    }

    [TestMethod]
    public void FreehandSmoothingRoundsASharpCornerAndKeepsEndpoints()
    {
        ImmutableArray<ImagePoint> smoothed = LineGeometry.Smooth([
            new ImagePoint(0, 0), new ImagePoint(20, 0), new ImagePoint(20, 20),
        ]);

        Assert.AreEqual(new ImagePoint(0, 0), smoothed[0]);
        Assert.AreEqual(new ImagePoint(20, 20), smoothed[^1]);
        Assert.IsFalse(smoothed.Contains(new ImagePoint(20, 0)));
        Assert.IsTrue(smoothed.Any(point => point.X < 20 && point.Y > 0));

        ImagePoint[] maximumTrace = Enumerable.Range(0, 1024)
            .Select(index => new ImagePoint(index, index % 8)).ToArray();
        Assert.AreEqual(4096, LineGeometry.Smooth(maximumTrace).Length);
    }

    [TestMethod]
    public void CurvedBoundsAndHitDistanceFollowTheActualBezier()
    {
        Annotation line = TestDocuments.Annotation(AnnotationKind.Line) with
        {
            Start = new ImagePoint(0, 0),
            End = new ImagePoint(100, 0),
            AllowCurves = true,
            CurveMidpoint = new ImagePoint(50, 30),
        };

        Assert.AreEqual(new ImageRect(0, 0, 100, 30), line.Bounds);
        Assert.AreEqual(0, LineGeometry.DistanceToPath(line, new ImagePoint(50, 30)), 0.01);
        Assert.IsTrue(LineGeometry.DistanceToPath(line, new ImagePoint(50, 0)) > 20);
    }

    [TestMethod]
    public void FreehandStrokeCanBeConvertedAtItsVisibleHalfwayPoint()
    {
        Annotation freehand = TestDocuments.Annotation(AnnotationKind.Line) with
        {
            Start = new ImagePoint(0, 0),
            End = new ImagePoint(100, 0),
            LinePoints = [new(0, 0), new(50, 40), new(100, 0)],
        };

        Assert.AreEqual(new ImagePoint(50, 40), LineGeometry.PathMidpoint(freehand));
        Annotation curved = freehand with
        {
            AllowCurves = true,
            CurveMidpoint = LineGeometry.PathMidpoint(freehand),
            LinePoints = [],
        };
        Assert.AreEqual(0, LineGeometry.DistanceToPath(curved, new ImagePoint(50, 40)), 0.01);
    }

    [TestMethod]
    public void ClosedFreehandStrokeCanHaveAnEndpointResized()
    {
        Annotation loop = TestDocuments.Annotation(AnnotationKind.Line) with
        {
            Start = new ImagePoint(10, 10),
            End = new ImagePoint(10, 10),
            LinePoints = [new(10, 10), new(80, 10), new(80, 80), new(10, 10)],
        };

        Annotation resized = LineGeometry.Resize(loop, loop.Start, new ImagePoint(20, 20));
        Assert.AreEqual(resized.Start, resized.LinePoints[0]);
        Assert.AreEqual(resized.End, resized.LinePoints[^1]);
        var editor = new DocumentEditor(TestDocuments.Create());
        editor.AddAnnotation(resized);
    }

    [TestMethod]
    public void ResizingFractionalFreehandEndpointKeepsTheStrokeEditable()
    {
        ImagePoint start = new(135.37846864997718, 64.10308427640605);
        ImagePoint end = new(39.329728938043026, 8.64397933813444);
        Annotation stroke = TestDocuments.Annotation(AnnotationKind.Line) with
        {
            Start = start,
            End = end,
            LinePoints = LineGeometry.Smooth([start, new ImagePoint(80.25, 42.75), end]),
        };
        var editor = new DocumentEditor(TestDocuments.Create());
        editor.AddAnnotation(stroke);

        ImagePoint movedEnd = new(end.X + 1, end.Y + 1);
        Annotation resized = LineGeometry.Resize(stroke, start, movedEnd);
        editor.UpdateAnnotation(resized);

        Annotation saved = editor.Current.Annotations.Single();
        Assert.AreEqual(start, saved.LinePoints[0]);
        Assert.AreEqual(movedEnd, saved.LinePoints[^1]);
    }

    [TestMethod]
    public void MovingAndResizingCarrySavedLineGeometry()
    {
        Annotation stroke = TestDocuments.Annotation(AnnotationKind.Line) with
        {
            Start = new ImagePoint(0, 0),
            End = new ImagePoint(100, 0),
            LinePoints = [new(0, 0), new(50, 20), new(100, 0)],
        };

        Annotation moved = LineGeometry.Translate(stroke, 10, 15);
        CollectionAssert.AreEqual(new[] { new ImagePoint(10, 15), new ImagePoint(60, 35),
            new ImagePoint(110, 15) }, moved.LinePoints.ToArray());

        Annotation resized = LineGeometry.Resize(stroke, new ImagePoint(0, 0), new ImagePoint(0, 200));
        CollectionAssert.AreEqual(new[] { new ImagePoint(0, 0), new ImagePoint(-40, 100),
            new ImagePoint(0, 200) }, resized.LinePoints.ToArray());

        Annotation curve = stroke with
        {
            LinePoints = [],
            AllowCurves = true,
            CurveMidpoint = new ImagePoint(50, 30),
        };
        Assert.AreEqual(new ImagePoint(60, 45),
            LineGeometry.Translate(curve, 10, 15).CurveMidpoint);
        Assert.AreEqual(new ImagePoint(-60, 100),
            LineGeometry.Resize(curve, new ImagePoint(0, 0),
                new ImagePoint(0, 200)).CurveMidpoint);
    }

    [TestMethod]
    public void EditorMoveAndDocumentValidationPreserveAFreehandStroke()
    {
        Annotation stroke = TestDocuments.Annotation(AnnotationKind.Line) with
        {
            Start = new ImagePoint(10, 20),
            End = new ImagePoint(80, 100),
            LinePoints = [new(10, 20), new(40, 5), new(80, 100)],
        };
        var editor = new DocumentEditor(TestDocuments.Create());
        editor.AddAnnotation(stroke);
        editor.MoveAnnotations([stroke.Id], 5, 6);

        Annotation moved = editor.Current.Annotations.Single();
        Assert.AreEqual(new ImagePoint(45, 11), moved.LinePoints[1]);
        Assert.AreEqual(moved.Start, moved.LinePoints[0]);
        Assert.AreEqual(moved.End, moved.LinePoints[^1]);
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(stroke.LinePoints[1], editor.Current.Annotations.Single().LinePoints[1]);
        Assert.IsTrue(editor.Redo());
        Assert.AreEqual(moved.LinePoints[1], editor.Current.Annotations.Single().LinePoints[1]);
        Assert.ThrowsExactly<ArgumentException>(() => editor.UpdateAnnotation(moved with
        {
            LinePoints = [new(12, 20), new(80, 100)],
        }));
    }

    [TestMethod]
    public void UndoAndRedoRestoreAChangedBezierMidpoint()
    {
        Annotation line = TestDocuments.Annotation(AnnotationKind.Line) with
        {
            AllowCurves = true,
        };
        var editor = new DocumentEditor(TestDocuments.Create());
        editor.AddAnnotation(line);
        editor.UpdateAnnotation(line with { CurveMidpoint = new ImagePoint(40, 5) });

        Assert.AreEqual(new ImagePoint(40, 5), editor.Current.Annotations[0].CurveMidpoint);
        Assert.IsTrue(editor.Undo());
        Assert.IsNull(editor.Current.Annotations[0].CurveMidpoint);
        Assert.IsTrue(editor.Redo());
        Assert.AreEqual(new ImagePoint(40, 5), editor.Current.Annotations[0].CurveMidpoint);
    }

    [TestMethod]
    public async Task SaveAndReopenKeepFreehandPathAndBezierShape()
    {
        using var temporary = new TemporaryLibrary();
        Annotation stroke = TestDocuments.Annotation(AnnotationKind.Line) with
        {
            LinePoints = [new(10, 20), new(40, 5), new(80, 100)],
        };
        Annotation curve = TestDocuments.Annotation(AnnotationKind.Line) with
        {
            AllowCurves = true,
            CurveMidpoint = new ImagePoint(70, 40),
        };
        ShnappDocument document = TestDocuments.Create() with { Annotations = [stroke, curve] };

        await temporary.Library.SaveAsync(document);
        ShnappDocument? reopened = await temporary.Library.OpenAsync(document.Id);

        Assert.IsNotNull(reopened);
        CollectionAssert.AreEqual(stroke.LinePoints.ToArray(), reopened.Annotations[0].LinePoints.ToArray());
        Assert.IsTrue(reopened.Annotations[1].AllowCurves);
        Assert.AreEqual(curve.CurveMidpoint, reopened.Annotations[1].CurveMidpoint);
    }
}
