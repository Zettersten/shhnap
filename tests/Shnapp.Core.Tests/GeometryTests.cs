using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class GeometryTests
{
    [TestMethod]
    [DataRow(10.5, 20.5, 90.5, 120.5)]
    [DataRow(90.5, 120.5, 10.5, 20.5)]
    [DataRow(90.5, 20.5, 10.5, 120.5)]
    [DataRow(10.5, 120.5, 90.5, 20.5)]
    public void DragGeometryIsNormalizedInEveryDirection(double startX, double startY, double endX, double endY)
    {
        ImageRect rectangle = ImageRect.FromPoints(new ImagePoint(startX, startY), new ImagePoint(endX, endY));

        Assert.AreEqual(new ImageRect(10.5, 20.5, 80, 100), rectangle);
        Assert.AreEqual(90.5, rectangle.Right);
        Assert.AreEqual(120.5, rectangle.Bottom);
    }

    [TestMethod]
    public void RectangleContainmentIncludesEdgesAndRejectsOutsideOrNonfinitePoints()
    {
        var rectangle = new ImageRect(10, 20, 30, 40);

        Assert.IsTrue(rectangle.Contains(new ImagePoint(10, 20)));
        Assert.IsTrue(rectangle.Contains(new ImagePoint(40, 60)));
        Assert.IsTrue(rectangle.Contains(new ImagePoint(25, 35)));
        Assert.IsFalse(rectangle.Contains(new ImagePoint(9.999, 20)));
        Assert.IsFalse(rectangle.Contains(new ImagePoint(40.001, 60)));
        Assert.IsFalse(rectangle.Contains(new ImagePoint(double.NaN, 30)));
        Assert.IsFalse(rectangle.Contains(new ImagePoint(10, double.PositiveInfinity)));
    }

    [TestMethod]
    public void AnnotationBoundsKeepLineDirectionWhileReturningNormalizedGeometry()
    {
        Annotation annotation = TestDocuments.Annotation(AnnotationKind.Arrow) with
        {
            Start = new ImagePoint(80, 100),
            End = new ImagePoint(10, 20),
        };

        Assert.AreEqual(new ImageRect(10, 20, 70, 80), annotation.Bounds);
        Assert.AreEqual(new ImagePoint(80, 100), annotation.Start);
        Assert.AreEqual(new ImagePoint(10, 20), annotation.End);
    }

    [TestMethod]
    public void ViewportDefaultsToTheOriginalImageAndDoesNotTranslateAnnotations()
    {
        ShnappDocument document = TestDocuments.Create();
        Assert.AreEqual(new ImageRect(0, 0, 640, 480), document.Viewport);

        ShnappDocument cropped = document with { Crop = new ImageRect(100, 50, 200, 150) };
        Assert.AreEqual(new ImageRect(100, 50, 200, 150), cropped.Viewport);
        Assert.AreEqual(640, cropped.PixelWidth);
        Assert.AreEqual(480, cropped.PixelHeight);
    }
}
