using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class VideoExportPathTests
{
    [TestMethod]
    public void ExportDestinationCannotReplaceLibraryMedia()
    {
        using var temporary = new TemporaryLibrary();
        var store = new VideoProjectStore(Path.Combine(temporary.RootPath, "videos"));
        string projectDirectory = store.GetProjectDirectory(Guid.NewGuid());
        string original = Path.Combine(projectDirectory, "source.mp4");

        Assert.ThrowsExactly<IOException>(() => store.GetExternalExportPath(original));
        Assert.ThrowsExactly<IOException>(() => store.GetExternalExportPath(
            Path.Combine(projectDirectory, "assets", "..", "source.mp4")));
        Assert.ThrowsExactly<IOException>(() => store.GetExternalExportPath(
            original.ToUpperInvariant()));
        Assert.ThrowsExactly<IOException>(() => store.GetExternalExportPath(store.RootPath));
        Assert.ThrowsExactly<IOException>(() => store.GetExternalExportPath(@"\\?\" + original));
    }

    [TestMethod]
    public void ExportDestinationAllowsSiblingDirectory()
    {
        using var temporary = new TemporaryLibrary();
        var store = new VideoProjectStore(Path.Combine(temporary.RootPath, "videos"));
        string destination = Path.Combine(temporary.RootPath, "videos-export", "finished.mp4");

        Assert.AreEqual(Path.GetFullPath(destination), store.GetExternalExportPath(destination));
    }

    [TestMethod]
    public void ExportDestinationAllowsExternalLinkButCannotReachLibraryThroughLink()
    {
        using var temporary = new TemporaryLibrary();
        var store = new VideoProjectStore(Path.Combine(temporary.RootPath, "videos"));
        Directory.CreateDirectory(store.RootPath);
        string link = Path.Combine(temporary.RootPath, "export-link");
        string external = Path.Combine(temporary.RootPath, "external");
        Directory.CreateDirectory(external);
        string externalLink = Path.Combine(temporary.RootPath, "external-link");
        try
        {
            Directory.CreateSymbolicLink(link, store.RootPath);
            Directory.CreateSymbolicLink(externalLink, external);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            Assert.Inconclusive($"This runner cannot create a directory link: {error.Message}");
        }

        Assert.ThrowsExactly<IOException>(() => store.GetExternalExportPath(
            Path.Combine(link, "source.mp4")));
        string allowed = Path.Combine(externalLink, "finished.mp4");
        Assert.AreEqual(Path.GetFullPath(allowed), store.GetExternalExportPath(allowed));
    }
}
