using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class PersistenceConcurrencyTests
{
    [TestMethod]
    [Timeout(30000)]
    public async Task ConcurrentIndependentWritersAndReadersSeeOnlyCompleteDocumentSnapshots()
    {
        using var temporary = new TemporaryLibrary();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Annotation annotation = TestDocuments.Annotation(AnnotationKind.Text);
        ShnappDocument original = TestDocuments.Create() with
        {
            Title = "revision-0",
            Annotations = [annotation with { Text = "initial" }],
        };
        await temporary.Library.SaveAsync(original, cancellation.Token);
        ShnappDocument[] candidates = Enumerable.Range(1, 32).Select(index => original with
        {
            Title = $"revision-{index}",
            Annotations = [annotation with { Text = new string((char)('a' + index % 26), 50000) + $"revision-{index}" }],
        }).ToArray();
        Dictionary<string, ShnappDocument> valid = candidates.Prepend(original).ToDictionary(document => document.Title);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] writers = candidates.Select(async document =>
        {
            await start.Task.WaitAsync(cancellation.Token);
            await new ShnappLibrary(temporary.RootPath).SaveAsync(document, cancellation.Token);
        }).ToArray();
        Task writing = Task.WhenAll(writers);
        int reads = 0;
        start.SetResult();
        try
        {
            do
            {
                ShnappDocument? snapshot = await temporary.Library.OpenAsync(original.Id, cancellation.Token);
                Assert.IsNotNull(snapshot);
                Assert.IsTrue(valid.TryGetValue(snapshot.Title, out ShnappDocument? expected));
                Assert.IsNotNull(expected);
                TestDocuments.AssertEquivalent(expected, snapshot);
                reads++;
                await Task.Delay(1, cancellation.Token);
            }
            while (!writing.IsCompleted);
        }
        finally
        {
            await writing;
        }

        Assert.IsTrue(reads > 0);
        ShnappDocument? final = await temporary.Library.OpenAsync(original.Id, cancellation.Token);
        Assert.IsNotNull(final);
        TestDocuments.AssertEquivalent(valid[final.Title], final);
        temporary.AssertNoTemporaryFiles();
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000)]
    public async Task ConcurrentSettingsWritersSucceedWithoutReaders(bool shareLibraryInstance)
    {
        using var temporary = new TemporaryLibrary();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await temporary.Library.SaveSettingsAsync(new ShnappSettings(), cancellation.Token);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] writers = Enumerable.Range(0, 32).Select(async index =>
        {
            await start.Task.WaitAsync(cancellation.Token);
            ShnappLibrary library = shareLibraryInstance ? temporary.Library : new ShnappLibrary(temporary.RootPath);
            await library.SaveSettingsAsync(new ShnappSettings
            {
                Theme = index % 2 == 0 ? "Light" : "Dark",
            }, cancellation.Token);
        }).ToArray();
        start.SetResult();

        await Task.WhenAll(writers);

        Assert.IsTrue((await temporary.Library.LoadSettingsAsync(cancellation.Token)).Theme is "Light" or "Dark");
        temporary.AssertNoTemporaryFiles();
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task RepeatedSequentialSettingsSavesAlwaysKeepTheLatestCompletePreferences()
    {
        using var temporary = new TemporaryLibrary();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        for (int index = 0; index < 64; index++)
        {
            var settings = new ShnappSettings
            {
                Theme = index % 2 == 0 ? "Light" : "Dark",
                AutoCopy = index % 2 == 0,
            };
            await temporary.Library.SaveSettingsAsync(settings, cancellation.Token);
            Assert.AreEqual(settings, await temporary.Library.LoadSettingsAsync(cancellation.Token));
        }

        temporary.AssertNoTemporaryFiles();
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task ConcurrentSavesForDistinctDocumentsStayIsolatedAndListable()
    {
        using var temporary = new TemporaryLibrary();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        ShnappDocument[] documents = Enumerable.Range(0, 24)
            .Select(index => TestDocuments.Create() with { Title = $"document-{index}" }).ToArray();

        await Task.WhenAll(documents.Select(document => temporary.Library.SaveAsync(document, cancellation.Token)));
        IReadOnlyList<ShnappDocument> listed = await temporary.Library.ListAsync(cancellation.Token);

        CollectionAssert.AreEquivalent(documents.Select(document => document.Id).ToArray(),
            listed.Select(document => document.Id).ToArray());
        foreach (ShnappDocument document in documents)
        {
            ShnappDocument? reopened = await temporary.Library.OpenAsync(document.Id, cancellation.Token);
            Assert.IsNotNull(reopened);
            TestDocuments.AssertEquivalent(document, reopened);
        }

        temporary.AssertNoTemporaryFiles();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000)]
    public async Task ConcurrentSettingsReplacementNeverExposesMixedPreferenceValues(bool shareLibraryInstance)
    {
        using var temporary = new TemporaryLibrary();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        ShnappSettings[] valid =
        [
            new() { Theme = "System", AutoCopy = false, StartOnLogin = false, WindowShadow = true },
            new() { Theme = "Light", AutoCopy = true, StartOnLogin = false, WindowShadow = false },
            new() { Theme = "Dark", AutoCopy = false, StartOnLogin = true, WindowShadow = false },
        ];
        await temporary.Library.SaveSettingsAsync(valid[0], cancellation.Token);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] writers = Enumerable.Range(0, 32).Select(async index =>
        {
            await start.Task.WaitAsync(cancellation.Token);
            ShnappLibrary library = shareLibraryInstance ? temporary.Library : new ShnappLibrary(temporary.RootPath);
            await library.SaveSettingsAsync(valid[index % valid.Length], cancellation.Token);
        }).ToArray();
        Task writing = Task.WhenAll(writers);
        start.SetResult();
        try
        {
            do
            {
                ShnappSettings snapshot = await temporary.Library.LoadSettingsAsync(cancellation.Token);
                Assert.Contains(snapshot, valid);
                await Task.Delay(1, cancellation.Token);
            }
            while (!writing.IsCompleted);
        }
        finally
        {
            await writing;
        }

        Assert.Contains(await temporary.Library.LoadSettingsAsync(cancellation.Token), valid);
        temporary.AssertNoTemporaryFiles();
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30000)]
    public async Task CancellationAfterPhysicalTemporaryFileCreationPreservesThePreviousSnapshot()
    {
        using var temporary = new TemporaryLibrary();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        ShnappDocument original = TestDocuments.Create();
        await temporary.Library.SaveAsync(original);
        string directory = temporary.Library.GetDocumentDirectory(original.Id);
        byte[] before = await File.ReadAllBytesAsync(temporary.GetMetadataPath(original.Id));
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, ".document.json.*.tmp")
        {
            NotifyFilter = NotifyFilters.FileName,
        };
        watcher.Created += (_, _) =>
        {
            cancellation.Cancel();
            created.TrySetResult();
        };
        watcher.EnableRaisingEvents = true;
        ShnappDocument replacement = original with
        {
            Title = "cancel during a real write",
            Annotations = [TestDocuments.Annotation(AnnotationKind.Text) with { Text = new string('x', 4 * 1024 * 1024) }],
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => temporary.Library.SaveAsync(replacement, cancellation.Token));
        await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
        watcher.EnableRaisingEvents = false;

        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(temporary.GetMetadataPath(original.Id)));
        ShnappDocument? reopened = await temporary.Library.OpenAsync(original.Id);
        Assert.IsNotNull(reopened);
        TestDocuments.AssertEquivalent(original, reopened);
        temporary.AssertNoTemporaryFiles();
        await temporary.Library.SaveAsync(original with { Title = "subsequent save succeeds" });
        Assert.AreEqual("subsequent save succeeds", (await temporary.Library.OpenAsync(original.Id))!.Title);
    }
}
