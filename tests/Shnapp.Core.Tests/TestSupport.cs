using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Shnapp.Core.Tests;

internal static class TestDocuments
{
    internal static ShnappDocument Create() => new()
    {
        Title = "A local shnapp",
        PixelWidth = 640,
        PixelHeight = 480,
        CaptureKind = CaptureKind.Region,
        CreatedAt = new DateTimeOffset(2026, 9, 30, 12, 34, 56, TimeSpan.FromHours(5.5)),
    };

    internal static Annotation Annotation(AnnotationKind kind = AnnotationKind.Rectangle) => new()
    {
        Kind = kind,
        Start = new ImagePoint(10, 20),
        End = new ImagePoint(80, 100),
    };

    internal static void AssertEquivalent(ShnappDocument expected, ShnappDocument actual)
    {
        Assert.AreEqual(expected with { Annotations = ImmutableArray<Annotation>.Empty },
            actual with { Annotations = ImmutableArray<Annotation>.Empty });
        CollectionAssert.AreEqual(expected.Annotations.ToArray(), actual.Annotations.ToArray());
    }
}

internal sealed class TemporaryLibrary : IDisposable
{
    private readonly string _ownedPath = Path.Combine(AppContext.BaseDirectory, "temporary-libraries", Guid.NewGuid().ToString("N"));

    internal TemporaryLibrary()
    {
        RootPath = Path.Combine(_ownedPath, "library");
        Library = new ShnappLibrary(RootPath);
    }

    internal string RootPath { get; }

    internal ShnappLibrary Library { get; }

    internal string GetOwnedPath(string name) => Path.Combine(_ownedPath, name);

    internal string GetMetadataPath(Guid id) => Path.Combine(Library.GetDocumentDirectory(id), "document.json");

    internal void AssertNoTemporaryFiles()
    {
        if (Directory.Exists(RootPath))
        {
            Assert.AreEqual(0, Directory.GetFiles(RootPath, "*.tmp", SearchOption.AllDirectories).Length);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_ownedPath))
        {
            Directory.Delete(_ownedPath, recursive: true);
        }
    }
}
