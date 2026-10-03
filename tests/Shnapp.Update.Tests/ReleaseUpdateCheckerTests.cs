using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Shnapp.App.Windows;

namespace Shnapp.Update.Tests;

[TestClass]
public sealed class ReleaseUpdateCheckerTests
{
    [TestMethod]
    public async Task NewerStableReleaseIsCachedAndOnlyNotifiedOnce()
    {
        using var directory = new TemporaryDirectory();
        DateTimeOffset now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        int requests = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests++;
            Assert.AreEqual("https://api.github.com/repos/Zettersten/shhnap/releases/latest", request.RequestUri?.AbsoluteUri);
            var response = Release("v1.2.4");
            response.Headers.ETag = new EntityTagHeaderValue("\"etag-1\"");
            return response;
        }));
        var checker = new ReleaseUpdateChecker(directory.Path, client, () => now);

        ReleaseCheckResult first = await checker.CheckAsync(new Version(1, 2, 3), false, CancellationToken.None);
        ReleaseCheckResult cached = await checker.CheckAsync(new Version(1, 2, 3), false, CancellationToken.None);

        Assert.IsTrue(first.UpdateAvailable);
        Assert.AreEqual("v1.2.4", first.LatestTag);
        Assert.AreEqual("https://github.com/Zettersten/shhnap/releases/tag/v1.2.4", first.ReleasePage?.AbsoluteUri);
        Assert.IsTrue(cached.UpdateAvailable);
        Assert.AreEqual(1, requests);
        Assert.IsTrue(await checker.MarkNotifiedAsync("v1.2.4", CancellationToken.None));
        Assert.IsFalse(await checker.MarkNotifiedAsync("v1.2.4", CancellationToken.None));

        var reloaded = new ReleaseUpdateChecker(directory.Path, client, () => now);
        Assert.IsFalse(await reloaded.MarkNotifiedAsync("v1.2.4", CancellationToken.None));
        Assert.IsTrue(await reloaded.MarkNotifiedAsync("v1.2.5", CancellationToken.None));
    }

    [TestMethod]
    [DataRow("v1.2.3", false)]
    [DataRow("v1.2.3.0", false)]
    [DataRow("1.2.2", false)]
    [DataRow("v1.2.4", true)]
    public async Task ComparesReleaseTagsToInstalledVersion(string tag, bool expectedNewer)
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new StubHandler(_ => Release(tag)));
        var checker = new ReleaseUpdateChecker(directory.Path, client);

        ReleaseCheckResult result = await checker.CheckAsync(new Version(1, 2, 3, 0), true, CancellationToken.None);

        Assert.AreEqual(expectedNewer, result.UpdateAvailable);
    }

    [TestMethod]
    public async Task NotFoundAndOfflineReturnFriendlyStatuses()
    {
        using var directory = new TemporaryDirectory();
        int calls = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return calls == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound) :
                throw new HttpRequestException("network offline");
        }));
        var checker = new ReleaseUpdateChecker(directory.Path, client);

        ReleaseCheckResult missing = await checker.CheckAsync(new Version(1, 0), true, CancellationToken.None);
        ReleaseCheckResult offline = await checker.CheckAsync(new Version(1, 0), true, CancellationToken.None);

        StringAssert.Contains(missing.Message, "No public stable GitHub release");
        StringAssert.Contains(offline.Message, "Could not reach GitHub");
        Assert.IsFalse(missing.UpdateAvailable);
        Assert.IsFalse(offline.UpdateAvailable);
        Assert.IsTrue(offline.IsError);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task ConditionalRequestUsesEtagAndRetainsCachedVersion()
    {
        using var directory = new TemporaryDirectory();
        int calls = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            calls++;
            if (calls == 1)
            {
                var response = Release("v2.0.0");
                response.Headers.ETag = new EntityTagHeaderValue("\"release-two\"");
                return response;
            }

            Assert.AreEqual("\"release-two\"", request.Headers.IfNoneMatch.Single().ToString());
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        }));
        var first = new ReleaseUpdateChecker(directory.Path, client);
        await first.CheckAsync(new Version(1, 0), true, CancellationToken.None);
        var second = new ReleaseUpdateChecker(directory.Path, client);

        ReleaseCheckResult result = await second.CheckAsync(new Version(1, 0), true, CancellationToken.None);

        Assert.AreEqual(2, calls);
        Assert.IsTrue(result.UpdateAvailable);
        Assert.AreEqual("v2.0.0", result.LatestTag);
    }

    [TestMethod]
    public async Task RejectsReleasePagesOutsideTheRepository()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"tag_name":"v2.0.0","html_url":"https://github.com/other/project/releases/tag/v2.0.0"}"""),
        }));
        var checker = new ReleaseUpdateChecker(directory.Path, client);

        ReleaseCheckResult result = await checker.CheckAsync(new Version(1, 0), true, CancellationToken.None);

        Assert.IsFalse(result.UpdateAvailable);
        Assert.IsNull(result.ReleasePage);
    }

    [TestMethod]
    public async Task FollowsGithubRepositoryRenameAndAcceptsItsReleasePage()
    {
        using var directory = new TemporaryDirectory();
        int calls = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            calls++;
            if (calls == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                redirect.Headers.Location = new Uri("https://api.github.com/repos/Zettersten/shnapp-new/releases/latest");
                return redirect;
            }

            Assert.AreEqual("https://api.github.com/repos/Zettersten/shnapp-new/releases/latest", request.RequestUri?.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tag_name":"v2.0.0","html_url":"https://github.com/Zettersten/shnapp-new/releases/tag/v2.0.0"}"""),
            };
        }));
        var checker = new ReleaseUpdateChecker(directory.Path, client);

        ReleaseCheckResult result = await checker.CheckAsync(new Version(1, 0), true, CancellationToken.None);

        Assert.IsTrue(result.UpdateAvailable);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task FailedDownloadBacksOffAcrossRestartsButManualCheckCanRetry()
    {
        using var directory = new TemporaryDirectory();
        DateTimeOffset now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var checker = new ReleaseUpdateChecker(directory.Path, utcNow: () => now);

        Assert.IsTrue(await checker.CanRetryDownloadAsync("v1.0.4", false, CancellationToken.None));
        await checker.RecordDownloadResultAsync("v1.0.4", succeeded: false, CancellationToken.None);
        var reloaded = new ReleaseUpdateChecker(directory.Path, utcNow: () => now);

        Assert.IsFalse(await reloaded.CanRetryDownloadAsync("v1.0.4", false, CancellationToken.None));
        Assert.IsTrue(await reloaded.CanRetryDownloadAsync("v1.0.4", true, CancellationToken.None));
        Assert.IsTrue(await reloaded.CanRetryDownloadAsync("v1.0.5", false, CancellationToken.None));
        now = now.AddDays(1);
        Assert.IsTrue(await reloaded.CanRetryDownloadAsync("v1.0.4", false, CancellationToken.None));
        await reloaded.RecordDownloadResultAsync("v1.0.4", succeeded: true, CancellationToken.None);
        Assert.IsTrue(await reloaded.CanRetryDownloadAsync("v1.0.4", false, CancellationToken.None));
    }

    [TestMethod]
    public async Task ExposesVerifiedArchitectureAssetFromCache()
    {
        using var directory = new TemporaryDirectory();
        string name = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "Shnapp-win-arm64.zip" : "Shnapp-win-x64.zip";
        string digest = new('a', 64);
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                {"tag_name":"v1.2.4","html_url":"https://github.com/Zettersten/shhnap/releases/tag/v1.2.4",
                 "draft":false,"prerelease":false,"immutable":true,"assets":[
                   {"name":"{{name}}","browser_download_url":"https://github.com/Zettersten/shhnap/releases/download/v1.2.4/{{name}}",
                    "digest":"sha256:{{digest}}","size":12345}]}
                """),
        }));
        var checker = new ReleaseUpdateChecker(directory.Path, client);
        ReleaseCheckResult first = await checker.CheckAsync(new Version(1, 2, 3), true, CancellationToken.None);
        ReleaseCheckResult cached = await new ReleaseUpdateChecker(directory.Path, client).CheckAsync(
            new Version(1, 2, 3), false, CancellationToken.None);

        Assert.AreEqual(digest, first.Asset?.Sha256);
        Assert.AreEqual(12345L, cached.Asset?.Size);
        Assert.AreEqual("v1.2.4", cached.Asset?.Tag);
    }

    [TestMethod]
    public async Task DoesNotOfferInstallForUnverifiedReleaseAsset()
    {
        using var directory = new TemporaryDirectory();
        string name = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "Shnapp-win-arm64.zip" : "Shnapp-win-x64.zip";
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                {"tag_name":"v1.2.4","html_url":"https://github.com/Zettersten/shhnap/releases/tag/v1.2.4",
                 "immutable":true,"assets":[{"name":"{{name}}",
                 "browser_download_url":"https://example.com/v1.2.4/{{name}}",
                 "digest":"sha256:{{new string('b', 64)}}","size":12345}]}
                """),
        }));
        ReleaseCheckResult result = await new ReleaseUpdateChecker(directory.Path, client).CheckAsync(
            new Version(1, 2, 3), true, CancellationToken.None);

        Assert.IsTrue(result.UpdateAvailable);
        Assert.IsNull(result.Asset);
    }

    [TestMethod]
    public async Task DoesNotAutoInstallFromMutableRelease()
    {
        using var directory = new TemporaryDirectory();
        string name = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "Shnapp-win-arm64.zip" : "Shnapp-win-x64.zip";
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                {"tag_name":"v1.2.4","html_url":"https://github.com/Zettersten/shhnap/releases/tag/v1.2.4",
                 "immutable":false,"assets":[{"name":"{{name}}",
                 "browser_download_url":"https://github.com/Zettersten/shhnap/releases/download/v1.2.4/{{name}}",
                 "digest":"sha256:{{new string('a', 64)}}","size":12345}]}
                """),
        }));

        ReleaseCheckResult result = await new ReleaseUpdateChecker(directory.Path, client).CheckAsync(
            new Version(1, 2, 3), true, CancellationToken.None);

        Assert.IsTrue(result.UpdateAvailable);
        Assert.IsNull(result.Asset);
    }

    private static HttpResponseMessage Release(string tag) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"tag_name":"{{tag}}","html_url":"https://github.com/Zettersten/shhnap/releases/tag/{{tag}}"}"""),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shnapp-update-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
