using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Shnapp.App.Windows;

namespace Shnapp.Update.Tests;

[TestClass]
public sealed class PortableUpdateStagerTests
{
    [TestMethod]
    public async Task StagesVerifiedPayloadAndSwitchesVersionPointer()
    {
        using var install = new TemporaryInstall();
        byte[] archive = CreateArchive();
        using var client = ClientFor(archive);
        var stager = new PortableUpdateStager(client);

        Assert.AreEqual(install.Root, PortableUpdateStager.FindInstallRoot(
            Path.Combine(install.Root, "versions", "v1.0.3")));
        bool staged = await stager.StageAndActivateAsync(Asset(archive), install.Root, CancellationToken.None);

        Assert.IsTrue(staged);
        Assert.IsTrue(File.Exists(Path.Combine(install.Root, "versions", "v1.0.4", "Shnapp.exe")));
        CollectionAssert.AreEqual(new[] { "v1.0.4", "v1.0.3" },
            File.ReadAllLines(Path.Combine(install.Root, "current-version.txt")));
        Assert.AreEqual("v1.0.3", File.ReadAllLines(Path.Combine(install.Root, "current-version.txt.bak"))[0]);
        Assert.IsTrue(PortableUpdateStager.IsSelectedVersion(install.Root, "v1.0.4"));
        Assert.IsFalse(await stager.StageAndActivateAsync(Asset(archive), install.Root, CancellationToken.None));
    }

    [TestMethod]
    public async Task RejectsChangedDownloadWithoutChangingInstalledVersion()
    {
        using var install = new TemporaryInstall();
        byte[] archive = CreateArchive();
        using var client = ClientFor(archive);
        var stager = new PortableUpdateStager(client);
        ReleaseAsset asset = Asset(archive) with { Sha256 = new string('0', 64) };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            stager.StageAndActivateAsync(asset, install.Root, CancellationToken.None));

        Assert.AreEqual("v1.0.3", File.ReadAllLines(Path.Combine(install.Root, "current-version.txt"))[0]);
        Assert.IsFalse(Directory.Exists(Path.Combine(install.Root, "versions", "v1.0.4")));
    }

    [TestMethod]
    public async Task RejectsArchivePathTraversal()
    {
        using var install = new TemporaryInstall();
        byte[] archive = CreateArchive("versions/v1.0.4/../evil.txt");
        using var client = ClientFor(archive);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PortableUpdateStager(client).StageAndActivateAsync(Asset(archive), install.Root,
                CancellationToken.None));

        Assert.AreEqual("v1.0.3", File.ReadAllLines(Path.Combine(install.Root, "current-version.txt"))[0]);
        Assert.IsFalse(File.Exists(Path.Combine(install.Root, "versions", "evil.txt")));
    }

    [TestMethod]
    public async Task DoesNotDowngradeAPreparedNewerVersion()
    {
        using var install = new TemporaryInstall();
        File.WriteAllText(Path.Combine(install.Root, "current-version.txt"), "v1.0.5\nv1.0.3\n");
        byte[] archive = CreateArchive();
        using var client = ClientFor(archive);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PortableUpdateStager(client).StageAndActivateAsync(Asset(archive), install.Root,
                CancellationToken.None));

        Assert.AreEqual("v1.0.5", File.ReadAllLines(Path.Combine(install.Root, "current-version.txt"))[0]);
    }

    [TestMethod]
    public async Task DoesNotSwitchPointerWhenItChangesDuringDownload()
    {
        using var install = new TemporaryInstall();
        byte[] archive = CreateArchive();
        using var client = new HttpClient(new StubHandler(() =>
        {
            File.WriteAllText(Path.Combine(install.Root, "current-version.txt"), "v1.0.5\nv1.0.3\n");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PortableUpdateStager(client).StageAndActivateAsync(Asset(archive), install.Root,
                CancellationToken.None));

        Assert.AreEqual("v1.0.5", File.ReadAllLines(Path.Combine(install.Root, "current-version.txt"))[0]);
        Assert.IsFalse(Directory.Exists(Path.Combine(install.Root, "versions", "v1.0.4")));
    }

    private static ReleaseAsset Asset(byte[] archive)
    {
        string name = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "Shnapp-win-arm64.zip" : "Shnapp-win-x64.zip";
        return new(new Uri("https://github.com/Zettersten/shhnap/releases/download/v1.0.4/" + name),
            Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(), archive.Length, "v1.0.4");
    }

    private static HttpClient ClientFor(byte[] archive) => new(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(archive),
    }));

    private static byte[] CreateArchive(string? extra = null)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "Shnapp.exe", "launcher");
            Add(zip, "current-version.txt", "v1.0.4\n\n");
            Add(zip, "LICENSE", "MIT");
            Add(zip, "THIRD_PARTY_NOTICES.txt", "notices");
            foreach (string required in new[] { "Shnapp.exe", "Shnapp.pri", "LICENSE", "THIRD_PARTY_NOTICES.txt" })
            {
                Add(zip, "versions/v1.0.4/" + required, required);
            }
            if (extra is not null) { Add(zip, extra, "unsafe"); }
        }
        return bytes.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string contents)
    {
        using StreamWriter writer = new(zip.CreateEntry(name).Open());
        writer.Write(contents);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond());
    }

    private sealed class TemporaryInstall : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "shnapp-install-" + Guid.NewGuid().ToString("N"));

        internal TemporaryInstall()
        {
            Directory.CreateDirectory(Path.Combine(Root, "versions", "v1.0.3"));
            File.WriteAllText(Path.Combine(Root, "Shnapp.exe"), "launcher");
            File.WriteAllText(Path.Combine(Root, "current-version.txt"), "v1.0.3\n\n");
            File.WriteAllText(Path.Combine(Root, "versions", "v1.0.3", "Shnapp.exe"), "old app");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
