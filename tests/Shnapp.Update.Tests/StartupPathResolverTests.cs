using Shnapp.App.Windows;

namespace Shnapp.Update.Tests;

[TestClass]
public sealed class StartupPathResolverTests
{
    [TestMethod]
    public void ScoopCurrentLaunchPathUsesStableLauncherWithoutDirectUpdaterRoot()
    {
        using var files = new TemporaryFiles();
        string app = Path.Combine(files.Root, "scoop", "apps", "shnapp");
        string current = Path.Combine(app, "current");
        string runningDirectory = Path.Combine(current, "versions", "v1.0.3");
        Directory.CreateDirectory(runningDirectory);
        Directory.CreateDirectory(current);
        string stableLauncher = Path.Combine(current, "Shnapp.exe");
        File.WriteAllText(stableLauncher, "launcher");
        string runningExecutable = Path.Combine(runningDirectory, "Shnapp.exe");

        Assert.IsNull(PortableUpdateStager.FindInstallRoot(runningDirectory));
        Assert.AreEqual(stableLauncher, StartupPathResolver.Resolve(runningDirectory,
            runningExecutable, InstallationChannel.Scoop));
    }

    [TestMethod]
    public void ScoopVersionSpecificLaunchPathUsesCurrentLauncher()
    {
        using var files = new TemporaryFiles();
        string app = Path.Combine(files.Root, "scoop", "apps", "shnapp");
        string version = Path.Combine(app, "1.0.3");
        string runningDirectory = Path.Combine(version, "versions", "v1.0.3");
        Directory.CreateDirectory(runningDirectory);
        File.WriteAllText(Path.Combine(version, "Shnapp.exe"), "old launcher");
        File.WriteAllText(Path.Combine(version, "current-version.txt"), "v1.0.3\n");
        string current = Path.Combine(app, "current");
        Directory.CreateDirectory(current);
        string stableLauncher = Path.Combine(current, "Shnapp.exe");
        File.WriteAllText(stableLauncher, "launcher");

        Assert.AreEqual(version, PortableUpdateStager.FindInstallRoot(runningDirectory));
        Assert.AreEqual(stableLauncher, StartupPathResolver.Resolve(runningDirectory,
            Path.Combine(runningDirectory, "Shnapp.exe"), InstallationChannel.Scoop));
    }

    [TestMethod]
    public void ScoopDoesNotRegisterVersionSpecificLauncherWhenCurrentIsMissing()
    {
        using var files = new TemporaryFiles();
        string runningDirectory = Path.Combine(files.Root, "scoop", "apps", "shnapp", "1.0.3",
            "versions", "v1.0.3");
        Directory.CreateDirectory(runningDirectory);

        Assert.ThrowsExactly<InvalidOperationException>(() => StartupPathResolver.Resolve(
            runningDirectory, Path.Combine(runningDirectory, "Shnapp.exe"), InstallationChannel.Scoop));
    }

    [TestMethod]
    public void DirectZipUsesStableRootLauncher()
    {
        using var files = new TemporaryFiles();
        string installRoot = Path.Combine(files.Root, "Shnapp");
        string runningDirectory = Path.Combine(installRoot, "versions", "v1.0.3");
        Directory.CreateDirectory(runningDirectory);
        string launcher = Path.Combine(installRoot, "Shnapp.exe");
        File.WriteAllText(launcher, "launcher");
        File.WriteAllText(Path.Combine(installRoot, "current-version.txt"), "v1.0.3\n");

        Assert.AreEqual(launcher, StartupPathResolver.Resolve(runningDirectory,
            Path.Combine(runningDirectory, "Shnapp.exe"), InstallationChannel.DirectZip));
    }

    [TestMethod]
    public void VelopackUsesStableRootExecutionStub()
    {
        using var files = new TemporaryFiles();
        string root = Path.Combine(files.Root, InstallationChannelDetector.VelopackX64Id);
        string current = Path.Combine(root, "current");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "sq.version"), "manifest");
        string launcher = Path.Combine(root, "Shnapp.exe");
        File.WriteAllText(launcher, "stub");

        Assert.AreEqual(launcher, StartupPathResolver.Resolve(current,
            Path.Combine(current, "Shnapp.exe"), InstallationChannel.Velopack));
    }

    [TestMethod]
    public void VelopackNeverRegistersVersionDirectoryWhenStubIsMissing()
    {
        using var files = new TemporaryFiles();
        string current = Path.Combine(files.Root, InstallationChannelDetector.VelopackX64Id, "current");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "sq.version"), "manifest");

        Assert.ThrowsExactly<InvalidOperationException>(() => StartupPathResolver.Resolve(current,
            Path.Combine(current, "Shnapp.exe"), InstallationChannel.Velopack));
    }

    private sealed class TemporaryFiles : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ShnappStartupTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
        }
    }
}
