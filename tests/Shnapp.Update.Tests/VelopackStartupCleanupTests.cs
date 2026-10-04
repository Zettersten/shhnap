using Shnapp.App.Windows;

namespace Shnapp.Update.Tests;

[TestClass]
public sealed class VelopackStartupCleanupTests
{
    [TestMethod]
    public void MatchesOnlyTheRunEntryForThisInstall()
    {
        string root = Path.Combine(Path.GetTempPath(), InstallationChannelDetector.VelopackX64Id);
        string running = Path.Combine(root, "current", "Shnapp.exe");
        string launcher = Path.Combine(root, "Shnapp.exe");

        Assert.AreEqual(launcher, VelopackStartupCleanup.ExpectedLauncherForHook(running,
            InstallationChannelDetector.VelopackX64Id, root));
        Assert.IsTrue(VelopackStartupCleanup.IsOwnedRunCommand($"\"{launcher}\" --background", launcher));
        Assert.IsFalse(VelopackStartupCleanup.IsOwnedRunCommand(
            $"\"{Path.Combine(Path.GetTempPath(), InstallationChannelDetector.VelopackArm64Id, "Shnapp.exe")}\" --background",
            launcher));
        Assert.IsFalse(VelopackStartupCleanup.IsOwnedRunCommand($"\"{launcher}\" --custom", launcher));
    }

    [TestMethod]
    public void RefusesUnrelatedExecutablePaths()
    {
        string root = Path.Combine(Path.GetTempPath(), InstallationChannelDetector.VelopackX64Id);
        Assert.IsNull(VelopackStartupCleanup.ExpectedLauncherForHook(
            Path.Combine(root, "versions", "v1.0.11", "Shnapp.exe"),
            InstallationChannelDetector.VelopackX64Id, root));
        Assert.IsNull(VelopackStartupCleanup.ExpectedLauncherForHook(
            Path.Combine(Path.GetTempPath(), "Other.App", "current", "Shnapp.exe"),
            InstallationChannelDetector.VelopackX64Id, root));
        Assert.IsNull(VelopackStartupCleanup.ExpectedLauncherForHook(
            Path.Combine(root, "current", "Another.exe"),
            InstallationChannelDetector.VelopackX64Id, root));
        Assert.IsNull(VelopackStartupCleanup.ExpectedLauncherForHook(
            Path.Combine(root, "current", "Shnapp.exe"), "Other.App", root));
    }

    [TestMethod]
    public void CustomInstallDirectoryUsesVerifiedLocatorRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "Custom Shnapp install");
        string executable = Path.Combine(root, "current", "Shnapp.exe");

        Assert.AreEqual(Path.Combine(root, "Shnapp.exe"),
            VelopackStartupCleanup.ExpectedLauncherForHook(executable,
                InstallationChannelDetector.VelopackArm64Id, root));
    }
}
