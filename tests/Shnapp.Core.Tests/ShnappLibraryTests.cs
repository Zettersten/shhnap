using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class ShnappLibraryTests
{
    [TestMethod]
    public void RootIsNormalizedAndGuidPathsDoNotCreateFiles()
    {
        using var temporary = new TemporaryLibrary();
        var library = new ShnappLibrary(Path.Combine(temporary.RootPath, "unused", "..") + Path.DirectorySeparatorChar);
        Guid id = Guid.Parse("12345678-1234-5678-abcd-1234567890ab");
        string expected = Path.Combine(temporary.RootPath, "shnapps", "1234567812345678abcd1234567890ab");

        Assert.AreEqual(temporary.RootPath, library.RootPath);
        Assert.AreEqual(expected, library.GetDocumentDirectory(id));
        Assert.AreEqual(Path.Combine(expected, "original.png"), library.GetOriginalPath(id));
        Assert.AreEqual(Path.Combine(expected, "preview.png"), library.GetPreviewPath(id));
        Assert.AreEqual(Path.Combine(expected, "shnapp.png"), library.GetExportPath(id));
        Assert.IsFalse(Directory.Exists(temporary.RootPath));
    }

    [TestMethod]
    public async Task MissingDataReturnsDefaultsWithoutCreatingTheRoot()
    {
        using var temporary = new TemporaryLibrary();

        Assert.IsNull(await temporary.Library.OpenAsync(Guid.NewGuid()));
        Assert.HasCount(0, await temporary.Library.ListAsync());
        ShnappSettings settings = await temporary.Library.LoadSettingsAsync();

        Assert.AreEqual(new ShnappSettings(), settings);
        Assert.IsFalse(settings.StartOnLogin);
        Assert.IsFalse(settings.AutoCopy);
        Assert.IsTrue(settings.WindowShadow);
        Assert.AreEqual("System", settings.Theme);
        Assert.IsFalse(Directory.Exists(temporary.RootPath));
    }

    [TestMethod]
    public async Task EmptyIdsNullRecordsAndInvalidRootsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new ShnappLibrary(null!));
        Assert.Throws<ArgumentException>(() => _ = new ShnappLibrary(string.Empty));
        Assert.Throws<ArgumentException>(() => _ = new ShnappLibrary(" "));
        using var temporary = new TemporaryLibrary();
        ShnappLibrary library = temporary.Library;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => library.GetDocumentDirectory(Guid.Empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => library.GetOriginalPath(Guid.Empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => library.GetPreviewPath(Guid.Empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => library.GetExportPath(Guid.Empty));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => library.OpenAsync(Guid.Empty));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => library.DeleteAsync(Guid.Empty));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => library.SaveAsync(null!));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => library.SaveSettingsAsync(null!));
        Assert.IsFalse(Directory.Exists(temporary.RootPath));
    }

    [TestMethod]
    [DataRow(CaptureKind.Window)]
    [DataRow(CaptureKind.FullScreen)]
    [DataRow(CaptureKind.Region)]
    public async Task DocumentRoundTripsEveryAnnotationPropertyAndCaptureKind(CaptureKind captureKind)
    {
        using var temporary = new TemporaryLibrary();
        ImmutableArray<Annotation> annotations = Enum.GetValues<AnnotationKind>().Select(kind =>
            TestDocuments.Annotation(kind) with
            {
                Start = new ImagePoint(10.25, 20.5),
                End = new ImagePoint(300.75, 200.125),
                Text = "Text with \"quotes\", \\paths\\, line breaks\nand Unicode: shnapp \u2728 \u754c",
                FontFamily = "Segoe UI Variable Text",
                FontSize = 22.25,
                FontWeight = 700,
                Italic = true,
                StrokeArgb = 0xFFE5484D,
                FillArgb = 0xFF111418,
                StepTextArgb = kind == AnnotationKind.Step ? 0xFFFDE68A : 0,
                RedactionMode = kind == AnnotationKind.Redaction ? RedactionMode.Pixelate : RedactionMode.Solid,
                StrokeWidth = 4.25,
                StartArrow = kind is AnnotationKind.Line or AnnotationKind.Arrow,
                EndArrow = kind == AnnotationKind.Line,
                StepDiameter = 32.5,
                StepNumber = kind == AnnotationKind.Step ? 1 : 0,
            }).ToImmutableArray();
        ShnappDocument document = TestDocuments.Create() with
        {
            CaptureKind = captureKind,
            Crop = new ImageRect(50, 60, 300, 220),
            HasWindowShadow = captureKind == CaptureKind.Window,
            Annotations = annotations,
        };

        await temporary.Library.SaveAsync(document);
        ShnappDocument? reopened = await temporary.Library.OpenAsync(document.Id);

        Assert.IsNotNull(reopened);
        TestDocuments.AssertEquivalent(document, reopened);
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(temporary.GetMetadataPath(document.Id)));
        Assert.AreEqual(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(captureKind.ToString(), json.RootElement.GetProperty("captureKind").GetString());
        Assert.IsFalse(json.RootElement.TryGetProperty("viewport", out _));
        Assert.IsFalse(json.RootElement.GetProperty("crop").TryGetProperty("right", out _));
        Assert.IsFalse(json.RootElement.GetProperty("annotations")[0].TryGetProperty("bounds", out _));
        temporary.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task ExistingSchemaOneAnnotationsWithoutOptionalStyleFieldsStillOpen()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument document = TestDocuments.Create() with
        {
            Annotations =
            [
                TestDocuments.Annotation(AnnotationKind.Arrow),
                TestDocuments.Annotation(AnnotationKind.Step) with { FillArgb = 0x40E5484D, StepNumber = 1 },
                TestDocuments.Annotation(AnnotationKind.Redaction),
            ],
        };
        await temporary.Library.SaveAsync(document);
        string path = temporary.GetMetadataPath(document.Id);
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        foreach (JsonNode? item in root["annotations"]!.AsArray())
        {
            JsonObject annotation = item!.AsObject();
            annotation.Remove("startArrow");
            annotation.Remove("endArrow");
            annotation.Remove("stepTextArgb");
            annotation.Remove("redactionMode");
            annotation.Remove("startCap");
            annotation.Remove("endCap");
            annotation.Remove("linePattern");
            annotation.Remove("stepLabelFormat");
            annotation.Remove("stepReset");
        }
        await File.WriteAllTextAsync(path, root.ToJsonString());

        ShnappDocument? opened = await temporary.Library.OpenAsync(document.Id);

        Assert.IsNotNull(opened);
        Assert.AreEqual(AnnotationKind.Arrow, opened.Annotations[0].Kind);
        Assert.IsFalse(opened.Annotations[0].StartArrow);
        Assert.IsFalse(opened.Annotations[0].EndArrow);
        Assert.AreEqual(LineEndCap.Triangle, opened.Annotations[0].EffectiveEndCap);
        Assert.AreEqual(LinePattern.Solid, opened.Annotations[0].LinePattern);
        Assert.AreEqual(StepLabelFormat.Decimal, opened.Annotations[1].StepLabelFormat);
        Assert.IsFalse(opened.Annotations[1].StepReset);
        Assert.AreEqual(0u, opened.Annotations[1].StepTextArgb);
        Assert.AreEqual(0x40E5484Du, opened.Annotations[1].FillArgb);
        Assert.AreEqual(RedactionMode.Solid, opened.Annotations[2].RedactionMode);
    }

    [TestMethod]
    [DataRow(RedactionMode.Blur)]
    [DataRow(RedactionMode.Pixelate)]
    public async Task RedactionModesRemainEditableAfterSaving(RedactionMode mode)
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument document = TestDocuments.Create() with
        {
            Annotations = [TestDocuments.Annotation(AnnotationKind.Redaction) with { RedactionMode = mode }],
        };

        await temporary.Library.SaveAsync(document);
        ShnappDocument? reopened = await temporary.Library.OpenAsync(document.Id);

        Assert.IsNotNull(reopened);
        Assert.AreEqual(mode, reopened.Annotations[0].RedactionMode);
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(temporary.GetMetadataPath(document.Id)));
        Assert.AreEqual(mode.ToString(), json.RootElement.GetProperty("annotations")[0]
            .GetProperty("redactionMode").GetString());
    }

    [TestMethod]
    public async Task LineAndStepStylesRemainEditableAfterSaving()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument document = TestDocuments.Create() with
        {
            Annotations =
            [
                TestDocuments.Annotation(AnnotationKind.Line) with
                {
                    StartCap = LineEndCap.Circle,
                    EndCap = LineEndCap.Triangle,
                    LinePattern = LinePattern.Dashed,
                },
                TestDocuments.Annotation(AnnotationKind.Step) with
                {
                    StepNumber = 1,
                    StepLabelFormat = StepLabelFormat.UpperRoman,
                    StepReset = true,
                },
            ],
        };

        await temporary.Library.SaveAsync(document);
        ShnappDocument? reopened = await temporary.Library.OpenAsync(document.Id);

        Assert.IsNotNull(reopened);
        Assert.AreEqual(LineEndCap.Circle, reopened.Annotations[0].StartCap);
        Assert.AreEqual(LineEndCap.Triangle, reopened.Annotations[0].EndCap);
        Assert.AreEqual(LinePattern.Dashed, reopened.Annotations[0].LinePattern);
        Assert.AreEqual(StepLabelFormat.UpperRoman, reopened.Annotations[1].StepLabelFormat);
        Assert.IsTrue(reopened.Annotations[1].StepReset);
    }

    [TestMethod]
    [DataRow("..\\..\\outside.png")]
    [DataRow("../outside/escaped")]
    [DataRow("C:\\Windows\\System32\\not-a-file")]
    [DataRow("\\\\server\\share\\name")]
    [DataRow("CON")]
    [DataRow("NUL:stream")]
    [DataRow("a/b\\c:*?\"<>|\0\n\u754c")]
    public async Task HostileTitlesRemainMetadataAndNeverDeterminePaths(string title)
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument document = TestDocuments.Create() with { Title = title };

        await temporary.Library.SaveAsync(document);
        ShnappDocument? reopened = await temporary.Library.OpenAsync(document.Id);

        Assert.IsNotNull(reopened);
        Assert.AreEqual(title, reopened.Title);
        Assert.AreEqual(document.Id.ToString("N"),
            Path.GetFileName(temporary.Library.GetDocumentDirectory(document.Id)));
        CollectionAssert.AreEqual(new[] { temporary.GetMetadataPath(document.Id) },
            Directory.GetFiles(temporary.RootPath, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task ReplacingMetadataDoesNotTouchOriginalPreviewOrExportBytes()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument original = TestDocuments.Create() with { Title = new string('a', 2000) };
        await temporary.Library.SaveAsync(original);
        byte[] imageBytes = [137, 80, 78, 71, 13, 10, 26, 10, 123, 45, 67];
        string[] imagePaths =
        [
            temporary.Library.GetOriginalPath(original.Id),
            temporary.Library.GetPreviewPath(original.Id),
            temporary.Library.GetExportPath(original.Id),
        ];
        foreach (string path in imagePaths)
        {
            await File.WriteAllBytesAsync(path, imageBytes);
        }

        ShnappDocument replacement = original with { Title = "short" };
        await temporary.Library.SaveAsync(replacement);

        ShnappDocument? reopened = await temporary.Library.OpenAsync(original.Id);
        Assert.IsNotNull(reopened);
        TestDocuments.AssertEquivalent(replacement, reopened);
        foreach (string path in imagePaths)
        {
            CollectionAssert.AreEqual(imageBytes, await File.ReadAllBytesAsync(path));
        }

        temporary.AssertNoTemporaryFiles();
    }

    [TestMethod]
    [DataRow("System", false, false, true)]
    [DataRow("Light", true, false, false)]
    [DataRow("Dark", false, true, true)]
    public async Task SettingsRoundTripAllPreferencesInAVersionedEnvelope(string theme, bool startOnLogin,
        bool autoCopy, bool windowShadow)
    {
        using var temporary = new TemporaryLibrary();
        var settings = new ShnappSettings
        {
            Theme = theme,
            StartOnLogin = startOnLogin,
            AutoCopy = autoCopy,
            WindowShadow = windowShadow,
        };

        await temporary.Library.SaveSettingsAsync(settings);

        Assert.AreEqual(settings, await temporary.Library.LoadSettingsAsync());
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temporary.RootPath, "settings.json")));
        Assert.AreEqual(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(theme, json.RootElement.GetProperty("settings").GetProperty("theme").GetString());
        Assert.IsFalse(Directory.Exists(Path.Combine(temporary.RootPath, "shnapps")));
        temporary.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task InvalidSettingsDoNotOverwriteAnExistingFile()
    {
        using var temporary = new TemporaryLibrary();
        var original = new ShnappSettings { Theme = "Dark", AutoCopy = true };
        await temporary.Library.SaveSettingsAsync(original);
        foreach (string? theme in new[] { "light", "Sepia", string.Empty, null })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => temporary.Library.SaveSettingsAsync(original with { Theme = theme! }));
        }

        Assert.AreEqual(original, await temporary.Library.LoadSettingsAsync());
        temporary.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task ListReturnsNewestFirstWithDeterministicIdentityOrderForTies()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument oldest = TestDocuments.Create() with { CreatedAt = DateTimeOffset.UnixEpoch };
        ShnappDocument first = TestDocuments.Create() with { Id = Guid.Parse("00000001-0000-0000-0000-000000000000") };
        ShnappDocument second = first with { Id = Guid.Parse("00000002-0000-0000-0000-000000000000") };
        await temporary.Library.SaveAsync(second);
        await temporary.Library.SaveAsync(oldest);
        await temporary.Library.SaveAsync(first);

        IReadOnlyList<ShnappDocument> documents = await temporary.Library.ListAsync();

        CollectionAssert.AreEqual(new[] { first.Id, second.Id, oldest.Id }, documents.Select(document => document.Id).ToArray());
    }

    [TestMethod]
    public async Task DeleteRemovesOnlyTheRequestedDocumentAndIsIdempotent()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument deleted = TestDocuments.Create();
        ShnappDocument kept = TestDocuments.Create();
        await temporary.Library.SaveAsync(deleted);
        await temporary.Library.SaveAsync(kept);
        await temporary.Library.SaveSettingsAsync(new ShnappSettings { Theme = "Dark" });
        await File.WriteAllTextAsync(temporary.Library.GetOriginalPath(deleted.Id), "original sentinel");
        await File.WriteAllTextAsync(temporary.Library.GetPreviewPath(deleted.Id), "preview sentinel");
        await File.WriteAllTextAsync(temporary.Library.GetExportPath(deleted.Id), "export sentinel");
        string sibling = Path.Combine(temporary.RootPath, "keep.txt");
        await File.WriteAllTextAsync(sibling, "do not delete");

        await temporary.Library.DeleteAsync(deleted.Id);
        await temporary.Library.DeleteAsync(deleted.Id);
        await temporary.Library.DeleteAsync(Guid.NewGuid());

        Assert.IsFalse(Directory.Exists(temporary.Library.GetDocumentDirectory(deleted.Id)));
        Assert.IsNull(await temporary.Library.OpenAsync(deleted.Id));
        Assert.IsNotNull(await temporary.Library.OpenAsync(kept.Id));
        Assert.AreEqual("Dark", (await temporary.Library.LoadSettingsAsync()).Theme);
        Assert.AreEqual("do not delete", await File.ReadAllTextAsync(sibling));
    }

    [TestMethod]
    public async Task PreCancelledOperationsDoNotCreateAnyData()
    {
        using var temporary = new TemporaryLibrary();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        CancellationToken token = cancellation.Token;
        ShnappDocument document = TestDocuments.Create();

        await Assert.ThrowsAsync<OperationCanceledException>(() => temporary.Library.SaveAsync(document, token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => temporary.Library.OpenAsync(document.Id, token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => temporary.Library.ListAsync(token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => temporary.Library.DeleteAsync(document.Id, token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => temporary.Library.LoadSettingsAsync(token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => temporary.Library.SaveSettingsAsync(new ShnappSettings(), token));

        Assert.IsFalse(Directory.Exists(temporary.RootPath));
    }

    [TestMethod]
    public async Task CancelledSavesAndDeletionPreserveExistingDocumentsAndSettings()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument original = TestDocuments.Create();
        var settings = new ShnappSettings { Theme = "Light" };
        await temporary.Library.SaveAsync(original);
        await temporary.Library.SaveSettingsAsync(settings);
        byte[] before = await File.ReadAllBytesAsync(temporary.GetMetadataPath(original.Id));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => temporary.Library.SaveAsync(original with { Title = "new" }, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => temporary.Library.DeleteAsync(original.Id, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => temporary.Library.SaveSettingsAsync(new ShnappSettings(), cancellation.Token));

        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(temporary.GetMetadataPath(original.Id)));
        Assert.AreEqual(settings, await temporary.Library.LoadSettingsAsync());
        temporary.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task InvalidDocumentValuesCannotOverwriteAValidSnapshot()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument original = TestDocuments.Create();
        await temporary.Library.SaveAsync(original);
        byte[] before = await File.ReadAllBytesAsync(temporary.GetMetadataPath(original.Id));
        Annotation annotation = TestDocuments.Annotation(AnnotationKind.Step);
        ShnappDocument[] invalid =
        [
            original with { Id = Guid.Empty },
            original with { SchemaVersion = 2 },
            original with { PixelWidth = 0 },
            original with { PixelHeight = -1 },
            original with { Annotations = default },
            original with { Annotations = [annotation] },
            original with { Annotations = [annotation with { StepNumber = 1 }, annotation with { StepNumber = 2 }] },
            original with { Crop = new ImageRect(0.5, 0, 10, 10) },
            original with { Annotations = [TestDocuments.Annotation() with { End = new ImagePoint(double.NaN, 0) }] },
        ];
        foreach (ShnappDocument document in invalid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => temporary.Library.SaveAsync(document));
        }

        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(temporary.GetMetadataPath(original.Id)));
        temporary.AssertNoTemporaryFiles();
    }
}
