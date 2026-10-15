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
