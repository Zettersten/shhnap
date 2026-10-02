using Shnapp.App.Windows;

namespace Shnapp.Update.Tests;

[TestClass]
public sealed class PackageManagerUpdateRunnerTests
{
    [TestMethod]
    [DataRow("Scoop", "update shnapp")]
    [DataRow("WinGet", "upgrade --id Zettersten.Shnapp --exact")]
    [DataRow("Chocolatey", "upgrade shnapp -y")]
    public void UsesTheOwningManagerAfterTheAppExits(string channelName, string command)
    {
        InstallationChannel channel = Enum.Parse<InstallationChannel>(channelName);
        string script = PackageManagerUpdateRunner.BuildScript(channel, 1234,
            @"C:\Users\Erik O'Brien\AppData\Local\Shnapp\manager-update-result.json");

        StringAssert.Contains(script, "Wait-Process -Id 1234 -Timeout 120");
        StringAssert.Contains(script, command);
        StringAssert.Contains(script, "Erik O''Brien");
        if (channel == InstallationChannel.Chocolatey)
        {
            Assert.IsFalse(script.Contains("Get-Command choco", StringComparison.Ordinal));
            StringAssert.Contains(script, @"\chocolatey\bin\choco.exe");
        }
    }

    [TestMethod]
    public void ReadsAndClearsThePreviousUpdateResult()
    {
        string directory = Path.Combine(Path.GetTempPath(), "shnapp-manager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "manager-update-result.json"),
                """{"Channel":"WinGet","ExitCode":0,"CompletedUtc":"2026-10-02T12:00:00Z"}""");
            PackageManagerUpdateResult? result = PackageManagerUpdateRunner.TakeResult(directory);
            Assert.IsNotNull(result);
            Assert.AreEqual("WinGet", result.Channel);
            Assert.AreEqual(0, result.ExitCode);
            Assert.IsNull(PackageManagerUpdateRunner.TakeResult(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
