using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class BasicShapeGeometryTests
{
    [TestMethod]
    public void EveryPalettePathFitsTheDragBoxAndClosedShapesHaveArea()
    {
        var bounds = new ImageRect(13, 29, 180, 120);
        foreach (BasicShapeKind kind in Enum.GetValues<BasicShapeKind>())
        {
            ImagePoint[] outline = BasicShapeGeometry.Outline(kind, bounds);
            Assert.IsTrue(outline.Length >= 3, $"{kind} needs a drawable contour.");
            Assert.IsTrue(outline.All(point =>
                double.IsFinite(point.X) && double.IsFinite(point.Y) &&
                point.X >= bounds.X - 0.001 && point.X <= bounds.Right + 0.001 &&
                point.Y >= bounds.Y - 0.001 && point.Y <= bounds.Bottom + 0.001),
                $"{kind} must stay inside its drag box.");
            double signedArea = 0;
            for (int index = 0; index < outline.Length; index++)
            {
                ImagePoint current = outline[index];
                ImagePoint next = outline[(index + 1) % outline.Length];
                signedArea += current.X * next.Y - next.X * current.Y;
            }

            if (BasicShapeGeometry.IsClosed(kind))
            {
                Assert.IsTrue(Math.Abs(signedArea) > 1, $"{kind} cannot collapse to a line.");
            }
        }
    }

    [TestMethod]
    public void RightBracketLeavesItsLeftEdgeOpenAndRequiresOnlyAnOutline()
    {
        var bounds = new ImageRect(10, 20, 100, 80);
        ImagePoint[] path = BasicShapeGeometry.Outline(BasicShapeKind.RightBracket, bounds);
        Assert.IsFalse(BasicShapeGeometry.IsClosed(BasicShapeKind.RightBracket));
        CollectionAssert.AreEqual(new[]
        {
            new ImagePoint(10, 20), new ImagePoint(110, 20),
            new ImagePoint(110, 100), new ImagePoint(10, 100),
        }, path);

        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation bracket = TestDocuments.Annotation(AnnotationKind.Shape) with
        {
            Start = new ImagePoint(100, 100),
            End = new ImagePoint(220, 180),
            ShapeKind = BasicShapeKind.RightBracket,
            FillArgb = 0,
            HideOutline = false,
        };
        editor.AddAnnotation(bracket);
        Assert.ThrowsExactly<ArgumentException>(() => editor.AddAnnotation(bracket with
        {
            Id = Guid.NewGuid(), FillArgb = 0xFF123456,
        }));
        Assert.ThrowsExactly<ArgumentException>(() => editor.AddAnnotation(bracket with
        {
            Id = Guid.NewGuid(), HideOutline = true,
        }));
    }

    [TestMethod]
    public void CircleStaysCircularAndCornerShapesKeepTheirOpenCenters()
    {
        var bounds = new ImageRect(10, 20, 200, 100);
        ImagePoint[] circle = BasicShapeGeometry.Outline(BasicShapeKind.Circle, bounds);
        Assert.AreEqual(60, circle.Min(point => point.X), 0.001);
        Assert.AreEqual(160, circle.Max(point => point.X), 0.001);
        Assert.AreEqual(20, circle.Min(point => point.Y), 0.001);
        Assert.AreEqual(120, circle.Max(point => point.Y), 0.001);

        var unit = new ImageRect(0, 0, 100, 100);
        ImagePoint[] u = BasicShapeGeometry.Outline(BasicShapeKind.UShape, unit);
        Assert.IsFalse(Contains(u, new ImagePoint(50, 20)));
        Assert.IsTrue(Contains(u, new ImagePoint(50, 90)));
        ImagePoint[] arch = BasicShapeGeometry.Outline(BasicShapeKind.InvertedUShape, unit);
        Assert.IsTrue(Contains(arch, new ImagePoint(50, 10)));
        Assert.IsFalse(Contains(arch, new ImagePoint(50, 80)));
    }

    [TestMethod]
    public void PaletteShapePersistsItsKindAndRejectsUnknownOutlines()
    {
        var editor = new DocumentEditor(TestDocuments.Create());
        Annotation mark = TestDocuments.Annotation(AnnotationKind.Shape) with
        {
            Start = new ImagePoint(100, 100),
            End = new ImagePoint(220, 180),
            ShapeKind = BasicShapeKind.Star,
        };
        editor.AddAnnotation(mark);
        string json = JsonSerializer.Serialize(editor.Current);
        ShnappDocument restored = JsonSerializer.Deserialize<ShnappDocument>(json)!;
        Assert.AreEqual(BasicShapeKind.Star, restored.Annotations.Single().ShapeKind);
        Assert.ThrowsExactly<ArgumentException>(() => editor.AddAnnotation(mark with
        {
            Id = Guid.NewGuid(),
            ShapeKind = (BasicShapeKind)999,
        }));
    }

    private static bool Contains(ImagePoint[] polygon, ImagePoint point)
    {
        bool inside = false;
        for (int index = 0, previous = polygon.Length - 1; index < polygon.Length;
             previous = index++)
        {
            ImagePoint a = polygon[index];
            ImagePoint b = polygon[previous];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
