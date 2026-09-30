using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class PersistenceFailureTests
{
    [TestMethod]
    [DataRow("futureSchema")]
    [DataRow("pastSchema")]
    [DataRow("missingSchema")]
    [DataRow("stringSchema")]
    [DataRow("mismatchedId")]
    [DataRow("emptyId")]
    [DataRow("zeroWidth")]
    [DataRow("fractionalWidth")]
    [DataRow("overflowingHeight")]
    [DataRow("unknownCaptureKind")]
    [DataRow("missingTitle")]
    [DataRow("nullTitle")]
    [DataRow("invalidTimestamp")]
    [DataRow("nullAnnotations")]
    [DataRow("nullAnnotation")]
    [DataRow("duplicateAnnotationId")]
    [DataRow("unknownAnnotationKind")]
    [DataRow("missingPointCoordinate")]
    [DataRow("negativePoint")]
    [DataRow("outOfBoundsPoint")]
    [DataRow("invalidFontSize")]
    [DataRow("invalidFontWeight")]
    [DataRow("nullText")]
    [DataRow("emptyFontFamily")]
    [DataRow("negativeStroke")]
    [DataRow("invalidStartArrow")]
    [DataRow("invalidEndArrow")]
    [DataRow("invalidStepTextArgb")]
    [DataRow("wrongStepNumber")]
    [DataRow("fractionalCrop")]
    [DataRow("outsideCrop")]
    [DataRow("missingCropDimension")]
    [DataRow("unknownMember")]
    [DataRow("unknownAnnotationMember")]
    [DataRow("duplicateProperty")]
    [DataRow("duplicatePointCoordinate")]
    [DataRow("nonfiniteNumber")]
    [DataRow("nullRoot")]
    [DataRow("truncatedJson")]
    [DataRow("emptyFile")]
    public async Task InvalidDocumentsAreRejectedWithoutRepairingOrChangingTheFile(string corruption)
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument document = TestDocuments.Create() with
        {
            Crop = new ImageRect(10, 20, 100, 150),
            Annotations =
            [
                TestDocuments.Annotation(AnnotationKind.Step) with { StepNumber = 1 },
                TestDocuments.Annotation(),
            ],
        };
        await temporary.Library.SaveAsync(document);
        string path = temporary.GetMetadataPath(document.Id);
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        JsonObject annotation = root["annotations"]![0]!.AsObject();
        JsonObject crop = root["crop"]!.AsObject();
        switch (corruption)
        {
            case "futureSchema": root["schemaVersion"] = 2; break;
            case "pastSchema": root["schemaVersion"] = 0; break;
            case "missingSchema": root.Remove("schemaVersion"); break;
            case "stringSchema": root["schemaVersion"] = "1"; break;
            case "mismatchedId": root["id"] = Guid.NewGuid().ToString(); break;
            case "emptyId": root["id"] = Guid.Empty.ToString(); break;
            case "zeroWidth": root["pixelWidth"] = 0; break;
            case "fractionalWidth": root["pixelWidth"] = 4.5; break;
            case "overflowingHeight": root["pixelHeight"] = long.MaxValue; break;
            case "unknownCaptureKind": root["captureKind"] = "Unknown"; break;
            case "missingTitle": root.Remove("title"); break;
            case "nullTitle": root["title"] = null; break;
            case "invalidTimestamp": root["createdAt"] = "not a timestamp"; break;
            case "nullAnnotations": root["annotations"] = null; break;
            case "nullAnnotation": root["annotations"]![0] = null; break;
            case "duplicateAnnotationId": root["annotations"]![1]!["id"] = annotation["id"]!.GetValue<string>(); break;
            case "unknownAnnotationKind": annotation["kind"] = "Polygon"; break;
            case "missingPointCoordinate": annotation["start"]!.AsObject().Remove("y"); break;
            case "negativePoint": annotation["start"]!["x"] = -1; break;
            case "outOfBoundsPoint": annotation["end"]!["y"] = 481; break;
            case "invalidFontSize": annotation["fontSize"] = 0; break;
            case "invalidFontWeight": annotation["fontWeight"] = 901; break;
            case "nullText": annotation["text"] = null; break;
            case "emptyFontFamily": annotation["fontFamily"] = " "; break;
            case "negativeStroke": annotation["strokeWidth"] = -1; break;
            case "invalidStartArrow": annotation["startArrow"] = "yes"; break;
            case "invalidEndArrow": annotation["endArrow"] = 1; break;
            case "invalidStepTextArgb": annotation["stepTextArgb"] = "white"; break;
            case "wrongStepNumber": annotation["stepNumber"] = 99; break;
            case "fractionalCrop": crop["x"] = 10.25; break;
            case "outsideCrop": crop["width"] = 640; break;
            case "missingCropDimension": crop.Remove("height"); break;
            case "unknownMember": root["futureCaption"] = "not supported"; break;
            case "unknownAnnotationMember": annotation["futureBlur"] = 12; break;
        }

        string json = root.ToJsonString();
        json = corruption switch
        {
            "duplicateProperty" => json.Replace("\"title\":", "\"title\":\"duplicate\",\"title\":", StringComparison.Ordinal),
            "duplicatePointCoordinate" => json.Replace("\"x\":10", "\"x\":10,\"x\":11", StringComparison.Ordinal),
            "nonfiniteNumber" => json.Replace("\"strokeWidth\":3", "\"strokeWidth\":1e400", StringComparison.Ordinal),
            "nullRoot" => "null",
            "truncatedJson" => "{\"schemaVersion\":1",
            "emptyFile" => string.Empty,
            _ => json,
        };
        await File.WriteAllTextAsync(path, json);
        byte[] before = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => temporary.Library.OpenAsync(document.Id));

        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        Assert.HasCount(0, await temporary.Library.ListAsync());
    }

    [TestMethod]
    [DataRow("futureSchema")]
    [DataRow("pastSchema")]
    [DataRow("missingSchema")]
    [DataRow("stringSchema")]
    [DataRow("nullSettings")]
    [DataRow("unknownTheme")]
    [DataRow("nullTheme")]
    [DataRow("missingBoolean")]
    [DataRow("unknownMember")]
    [DataRow("duplicateProperty")]
    [DataRow("duplicatePreference")]
    [DataRow("corruptJson")]
    public async Task InvalidSettingsAreRejectedAndNeverSilentlyReset(string corruption)
    {
        using var temporary = new TemporaryLibrary();
        await temporary.Library.SaveSettingsAsync(new ShnappSettings { Theme = "Light", StartOnLogin = true });
        string path = Path.Combine(temporary.RootPath, "settings.json");
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        JsonObject settings = root["settings"]!.AsObject();
        switch (corruption)
        {
            case "futureSchema": root["schemaVersion"] = 2; break;
            case "pastSchema": root["schemaVersion"] = 0; break;
            case "missingSchema": root.Remove("schemaVersion"); break;
            case "stringSchema": root["schemaVersion"] = "1"; break;
            case "nullSettings": root["settings"] = null; break;
            case "unknownTheme": settings["theme"] = "Custom"; break;
            case "nullTheme": settings["theme"] = null; break;
            case "missingBoolean": settings.Remove("startOnLogin"); break;
            case "unknownMember": settings["futureAI"] = true; break;
        }

        string json = root.ToJsonString();
        json = corruption switch
        {
            "duplicateProperty" => json.Replace("\"schemaVersion\":", "\"schemaVersion\":2,\"schemaVersion\":", StringComparison.Ordinal),
            "duplicatePreference" => json.Replace("\"theme\":", "\"theme\":\"Dark\",\"theme\":", StringComparison.Ordinal),
            "corruptJson" => "this is not JSON",
            _ => json,
        };
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => temporary.Library.LoadSettingsAsync());

        Assert.AreEqual(json, await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task CorruptFutureMissingAndNonGuidItemsDoNotHideHealthyDocuments()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument healthy = TestDocuments.Create();
        ShnappDocument future = TestDocuments.Create();
        ShnappDocument corrupt = TestDocuments.Create();
        ShnappDocument missing = TestDocuments.Create();
        await temporary.Library.SaveAsync(healthy);
        await temporary.Library.SaveAsync(future);
        JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(temporary.GetMetadataPath(future.Id)))!.AsObject();
        json["schemaVersion"] = 100;
        await File.WriteAllTextAsync(temporary.GetMetadataPath(future.Id), json.ToJsonString());
        Directory.CreateDirectory(temporary.Library.GetDocumentDirectory(corrupt.Id));
        await File.WriteAllTextAsync(temporary.GetMetadataPath(corrupt.Id), "{broken");
        Directory.CreateDirectory(temporary.Library.GetDocumentDirectory(missing.Id));
        await File.WriteAllTextAsync(Path.Combine(temporary.Library.GetDocumentDirectory(missing.Id), ".document.json.abandoned.tmp"), "{}");
        Directory.CreateDirectory(Path.Combine(temporary.RootPath, "shnapps", Guid.Empty.ToString("N")));
        Directory.CreateDirectory(Path.Combine(temporary.RootPath, "shnapps", "not-a-guid"));

        IReadOnlyList<ShnappDocument> documents = await temporary.Library.ListAsync();

        Assert.HasCount(1, documents);
        TestDocuments.AssertEquivalent(healthy, documents[0]);
    }

    [TestMethod]
    public async Task FailedAtomicCommitCleansItsUniqueTemporaryFile()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument document = TestDocuments.Create();
        string path = temporary.GetMetadataPath(document.Id);
        Directory.CreateDirectory(path);
        bool failed = false;
        try
        {
            await temporary.Library.SaveAsync(document);
        }
        catch (IOException)
        {
            failed = true;
        }
        catch (UnauthorizedAccessException)
        {
            failed = true;
        }

        Assert.IsTrue(failed, "A metadata destination that is a directory must not be replaced with a file.");
        Assert.IsTrue(Directory.Exists(path));
        temporary.AssertNoTemporaryFiles();
        await temporary.Library.SaveSettingsAsync(new ShnappSettings());
        Assert.AreEqual(new ShnappSettings(), await temporary.Library.LoadSettingsAsync());
    }

    [TestMethod]
    public async Task WindowsSharingFailurePreservesPreviousCompleteMetadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows FileShare.Delete semantics are only applicable on Windows.");
        }

        using var temporary = new TemporaryLibrary();
        ShnappDocument original = TestDocuments.Create();
        await temporary.Library.SaveAsync(original);
        string path = temporary.GetMetadataPath(original.Id);
        byte[] before = await File.ReadAllBytesAsync(path);
        bool failed = false;
        await using (var lockFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                await temporary.Library.SaveAsync(original with { Title = "blocked replacement" });
            }
            catch (IOException)
            {
                failed = true;
            }
            catch (UnauthorizedAccessException)
            {
                failed = true;
            }
        }

        Assert.IsTrue(failed, "A reader that denies delete sharing must prevent atomic replacement on Windows.");
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        ShnappDocument? reopened = await temporary.Library.OpenAsync(original.Id);
        Assert.IsNotNull(reopened);
        TestDocuments.AssertEquivalent(original, reopened);
        temporary.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task LinkedDocumentDirectoriesCannotEscapeLibraryWritesOrDeletion()
    {
        using var temporary = new TemporaryLibrary();
        ShnappDocument document = TestDocuments.Create();
        await temporary.Library.SaveAsync(document);
        string directory = temporary.Library.GetDocumentDirectory(document.Id);
        string target = temporary.GetOwnedPath("outside-library");
        Directory.Move(directory, target);
        try
        {
            Directory.CreateSymbolicLink(directory, target);
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Inconclusive("This OS account cannot create directory links.");
        }
        catch (IOException)
        {
            Assert.Inconclusive("This filesystem does not support creating directory links.");
        }

        try
        {
            byte[] before = await File.ReadAllBytesAsync(Path.Combine(target, "document.json"));
            await Assert.ThrowsAsync<IOException>(() => temporary.Library.OpenAsync(document.Id));
            await Assert.ThrowsAsync<IOException>(() => temporary.Library.SaveAsync(document));
            await Assert.ThrowsAsync<IOException>(() => temporary.Library.DeleteAsync(document.Id));

            Assert.HasCount(0, await temporary.Library.ListAsync());
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(Path.Combine(target, "document.json")));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }
}
