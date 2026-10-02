using Shnapp.App.Windows;

namespace Shnapp.Update.Tests;

[TestClass]
public sealed class InstallationChannelTests
{
    private static readonly InstallationChannelRoots Roots = new(
        [@"C:\Users\Erik\scoop", @"D:\Scoop"],
        [@"C:\ProgramData\chocolatey", @"D:\Chocolatey"],
        [@"C:\Users\Erik\AppData\Local\Microsoft\WinGet\Packages", @"C:\Program Files\WinGet\Packages"],
        [@"C:\Users\Erik\AppData\Local\Microsoft\WinGet\Links", @"C:\Program Files\WinGet\Links"]);

    [TestMethod]
    public void DirectZipOutsidePackageManagerRootsCanUpdateItself()
    {
        Assert.AreEqual(InstallationChannel.DirectZip, Classify(@"D:\Apps\Shnapp\Shnapp.exe"));
        Assert.AreEqual(InstallationChannel.DirectZip,
            Classify(@"D:\Apps\Shnapp\Shnapp.exe", "direct-zip:shnapp"));
    }

    [TestMethod]
    public void ScoopMarkerAndKnownPathsBelongToScoop()
    {
        Assert.AreEqual(InstallationChannel.Scoop,
            Classify(@"C:\Users\Erik\scoop\apps\shnapp\1.0.3\Shnapp.exe"));
        Assert.AreEqual(InstallationChannel.Scoop,
            Classify(@"D:\Scoop\apps\shnapp\current\Shnapp.exe", "scoop:shnapp"));
        Assert.AreEqual(InstallationChannel.Scoop,
            Classify(@"E:\ConfiguredScoopRoot\apps\shnapp\1.0.3\Shnapp.exe", "scoop:shnapp"));
    }

    [TestMethod]
    public void ChocolateyMarkerAndKnownPathsBelongToChocolatey()
    {
        Assert.AreEqual(InstallationChannel.Chocolatey,
            Classify(@"C:\ProgramData\chocolatey\lib\shnapp\tools\app\Shnapp.exe"));
        Assert.AreEqual(InstallationChannel.Chocolatey,
            Classify(@"D:\Chocolatey\lib\shnapp\tools\app\Shnapp.exe", "chocolatey:shnapp"));
        Assert.AreEqual(InstallationChannel.Chocolatey,
            Classify(@"E:\CustomInstall\lib\shnapp\tools\app\Shnapp.exe", "chocolatey:shnapp"));
    }

    [TestMethod]
    public void WinGetRequiresMatchingPortableRegistration()
    {
        string target = @"C:\Users\Erik\AppData\Local\Microsoft\WinGet\Packages\Zettersten.Shnapp_Microsoft.Winget.Source_8wekyb3d8bbwe\Shnapp.exe";
        Assert.AreEqual(InstallationChannel.Unknown, Classify(target));
        Assert.AreEqual(InstallationChannel.WinGet, Classify(target, registrations:
            [new("Zettersten.Shnapp", target, null)]));
        Assert.AreEqual(InstallationChannel.WinGet, Classify(target, registrations:
            [new("Zettersten.Shnapp", null, Path.GetDirectoryName(target))]));
        Assert.AreEqual(InstallationChannel.Unknown, Classify(target, registrations:
            [new("Other.App", target, Path.GetDirectoryName(target))]));
    }

    [TestMethod]
    public void CustomWinGetRootIsRecognizedByRegistration()
    {
        string target = @"E:\CustomRoot\Zettersten.Shnapp\Shnapp.exe";
        Assert.AreEqual(InstallationChannel.WinGet, Classify(target, registrations:
            [new("Zettersten.Shnapp", target, null)]));
        Assert.AreEqual(InstallationChannel.WinGet,
            Classify(@"E:\CustomRoot\Zettersten.Shnapp\versions\1.0.3\Shnapp.exe", registrations:
                [new("Zettersten.Shnapp", null, @"E:\CustomRoot\Zettersten.Shnapp")]));
    }

    [TestMethod]
    public void VersionedDirectInstallReadsOwnershipMarkerFromRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "shnapp-channel-" + Guid.NewGuid().ToString("N"));
        string version = Path.Combine(root, "versions", "v1.0.3");
        Directory.CreateDirectory(version);
        try
        {
            string executable = Path.Combine(version, "Shnapp.exe");
            File.WriteAllText(Path.Combine(root, InstallationChannelDetector.MarkerFileName), "direct-zip:shnapp");
            Assert.AreEqual("direct-zip:shnapp", InstallationChannelDetector.ReadMarkerForExecutable(executable));
            Assert.AreEqual(InstallationChannel.DirectZip,
                Classify(executable, InstallationChannelDetector.ReadMarkerForExecutable(executable)));

            File.WriteAllText(Path.Combine(version, InstallationChannelDetector.MarkerFileName), "scoop:shnapp");
            Assert.AreEqual(string.Empty, InstallationChannelDetector.ReadMarkerForExecutable(executable));
            Assert.AreEqual(InstallationChannel.Unknown,
                Classify(executable, InstallationChannelDetector.ReadMarkerForExecutable(executable)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ManagerPathWithoutReliableOwnershipNeverBecomesDirectZip()
    {
        Assert.AreEqual(InstallationChannel.Unknown,
            Classify(@"C:\Program Files\WinGet\Links\shnapp.exe", "direct-zip:shnapp"));
        Assert.AreEqual(InstallationChannel.Unknown,
            Classify(@"D:\Other\scoop\apps\other\Shnapp.exe"));
        Assert.AreEqual(InstallationChannel.Unknown,
            Classify(@"E:\ConfiguredScoopRoot\apps\shnapp\1.0.3\versions\v1.0.3\Shnapp.exe"));
        Assert.AreEqual(InstallationChannel.Unknown,
            Classify(@"E:\ConfiguredChocolateyRoot\lib\shnapp\tools\app\versions\v1.0.3\Shnapp.exe"));
        Assert.AreEqual(InstallationChannel.Unknown,
            Classify(@"D:\Other\WinGet\Packages\Zettersten.Shnapp\Shnapp.exe"));
        Assert.AreEqual(InstallationChannel.Unknown,
            Classify(@"D:\Chocolatey\lib\shnapp\tools\app\Shnapp.exe", "direct-zip:shnapp"));
    }

    [TestMethod]
    public void ConflictingOrInvalidEvidenceFailsClosed()
    {
        string scoopPath = @"C:\Users\Erik\scoop\apps\shnapp\1.0.3\Shnapp.exe";
        Assert.AreEqual(InstallationChannel.Unknown, Classify(scoopPath, "chocolatey:shnapp"));
        Assert.AreEqual(InstallationChannel.Unknown, Classify(scoopPath, "scoop:shnapp",
            [new("Zettersten.Shnapp", scoopPath, null)]));
        Assert.AreEqual(InstallationChannel.Unknown,
            Classify(@"D:\Apps\Shnapp\Shnapp.exe", "unrecognized:shnapp"));
        Assert.AreEqual(InstallationChannel.Unknown,
            Classify(@"D:\Apps\Other.exe"));
    }

    private static InstallationChannel Classify(string executable, string? marker = null,
        IEnumerable<WinGetPortableRegistration>? registrations = null) =>
        InstallationChannelDetector.Classify(executable, marker, registrations ?? [], Roots);
}
