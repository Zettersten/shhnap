using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class TextFormattingTests
{
    [TestMethod]
    public async Task CharacterFormattingAndVerticalAlignmentSurviveSaveAndReopen()
    {
        using var temporary = new TemporaryLibrary();
        Annotation text = TestDocuments.Annotation(AnnotationKind.Text) with
        {
            End = new ImagePoint(210, 120),
            Text = "Hello world",
            TextBoxWidth = 200,
            TextBoxHeight = 100,
            FillArgb = 0xCCFFFFFF,
            TextBorderArgb = 0x800A84FF,
            TextBorderWidth = 2.5,
            TextPadding = 12,
            TextVerticalAlignment = TextVerticalAlignment.Middle,
            TextStyleRuns = [new TextStyleRun(6, 5, true, true, true)],
        };
        ShnappDocument document = TestDocuments.Create() with { Annotations = [text] };

        await temporary.Library.SaveAsync(document);
        ShnappDocument reopened = (await temporary.Library.OpenAsync(document.Id))!;

        Assert.AreEqual(TextVerticalAlignment.Middle, reopened.Annotations[0].TextVerticalAlignment);
        Assert.AreEqual(0xCCFFFFFFu, reopened.Annotations[0].FillArgb);
        Assert.AreEqual(0x800A84FFu, reopened.Annotations[0].TextBorderArgb);
        Assert.AreEqual(2.5, reopened.Annotations[0].TextBorderWidth);
        Assert.AreEqual(12, reopened.Annotations[0].TextPadding);
        CollectionAssert.AreEqual(text.TextStyleRuns.ToArray(), reopened.Annotations[0].TextStyleRuns.ToArray());
        Assert.AreEqual(text.Text, reopened.Annotations[0].Text);
    }

    [TestMethod]
    public void CharacterFormattingRejectsOverlapsAndOutOfBoundsRanges()
    {
        Annotation text = TestDocuments.Annotation(AnnotationKind.Text) with { Text = "hello" };
        var invalidRuns = new[]
        {
            ImmutableArray.Create(new TextStyleRun(0, 3, true, false, false),
                new TextStyleRun(2, 2, false, true, false)),
            ImmutableArray.Create(new TextStyleRun(4, 2, true, false, false)),
            ImmutableArray.Create(new TextStyleRun(-1, 1, true, false, false)),
        };

        foreach (ImmutableArray<TextStyleRun> runs in invalidRuns)
        {
            ShnappDocument document = TestDocuments.Create() with
            {
                Annotations = [text with { TextStyleRuns = runs }],
            };
            Assert.ThrowsExactly<ArgumentException>(() => new DocumentEditor(document));
        }
    }

    [TestMethod]
    public void TextAreaBorderIsLimitedToTextAndHasValidWidth()
    {
        Annotation text = TestDocuments.Annotation(AnnotationKind.Text) with
        {
            Text = "label",
            TextBorderArgb = 0x800A84FF,
            TextBorderWidth = 2,
        };
        _ = new DocumentEditor(TestDocuments.Create() with { Annotations = [text] });

        foreach (Annotation invalid in new[]
        {
            text with { TextBorderWidth = 0 },
            text with { TextBorderWidth = double.NaN },
            text with { TextBorderWidth = 25 },
            TestDocuments.Annotation(AnnotationKind.Line) with
            {
                TextBorderArgb = 0x800A84FF,
                TextBorderWidth = 2,
            },
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                new DocumentEditor(TestDocuments.Create() with { Annotations = [invalid] }));
        }
    }

    [TestMethod]
    public void TextAreaPaddingIsLimitedToTextAndValidRange()
    {
        Annotation text = TestDocuments.Annotation(AnnotationKind.Text) with
        {
            Text = "Padded label",
            TextPadding = 16,
        };
        _ = new DocumentEditor(TestDocuments.Create() with { Annotations = [text] });

        foreach (Annotation invalid in new[]
        {
            text with { TextPadding = -1 },
            text with { TextPadding = 65 },
            text with { TextPadding = double.NaN },
            TestDocuments.Annotation(AnnotationKind.Line) with { TextPadding = 8 },
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                new DocumentEditor(TestDocuments.Create() with { Annotations = [invalid] }));
        }
    }

    [TestMethod]
    public async Task LegacyDocumentWithoutCharacterFormattingStillOpens()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument document = TestDocuments.Create() with
        {
            Annotations = [TestDocuments.Annotation(AnnotationKind.Line),
                TestDocuments.Annotation(AnnotationKind.Text) with { Text = "Legacy text" }],
        };
        await temporary.Library.SaveAsync(document);

        string path = Path.Combine(temporary.Library.GetDocumentDirectory(document.Id), "document.json");
        JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        foreach (JsonNode? annotation in json["annotations"]!.AsArray())
        {
            annotation!.AsObject().Remove("textStyleRuns");
        }
        await File.WriteAllTextAsync(path, json.ToJsonString());

        ShnappDocument reopened = (await temporary.Library.OpenAsync(document.Id))!;
        Assert.HasCount(2, reopened.Annotations);
        Assert.IsTrue(reopened.Annotations.All(annotation => annotation.TextStyleRuns.IsDefaultOrEmpty));
        Assert.AreEqual(0, reopened.Annotations[1].TextPadding);
    }
}
