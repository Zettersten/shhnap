using Shnapp.App.Windows;

namespace Shnapp.Update.Tests;

[TestClass]
public sealed class VelopackCheckScheduleTests
{
    [TestMethod]
    public async Task SuccessfulCheckSurvivesRestartAndAllowsExplicitRetry()
    {
        using var files = new TemporaryFiles();
        DateTimeOffset now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var schedule = new VelopackCheckSchedule(files.Root, () => now);
        await schedule.RecordSuccessAsync(CancellationToken.None);

        var restarted = new VelopackCheckSchedule(files.Root, () => now);
        Assert.IsFalse((await restarted.ShouldCheckAsync(false, CancellationToken.None)).Check);
        Assert.IsTrue((await restarted.ShouldCheckAsync(true, CancellationToken.None)).Check);

        now = now.AddDays(1);
        Assert.IsTrue((await restarted.ShouldCheckAsync(false, CancellationToken.None)).Check);
    }

    [TestMethod]
    public async Task RateLimitCooldownBlocksAutomaticAndManualChecksAcrossRestart()
    {
        using var files = new TemporaryFiles();
        DateTimeOffset now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var schedule = new VelopackCheckSchedule(files.Root, () => now);
        await schedule.RecordFailureAsync(rateLimited: true, CancellationToken.None);

        var restarted = new VelopackCheckSchedule(files.Root, () => now);
        var automatic = await restarted.ShouldCheckAsync(false, CancellationToken.None);
        var manual = await restarted.ShouldCheckAsync(true, CancellationToken.None);
        Assert.IsFalse(automatic.Check);
        Assert.IsFalse(manual.Check);
        Assert.AreEqual(now.AddHours(1), automatic.RetryAfterUtc);

        now = now.AddHours(1);
        Assert.IsTrue((await restarted.ShouldCheckAsync(false, CancellationToken.None)).Check);
    }

    [TestMethod]
    public async Task TransientFailureThrottlesAutomaticChecksButAllowsManualRetry()
    {
        using var files = new TemporaryFiles();
        DateTimeOffset now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var schedule = new VelopackCheckSchedule(files.Root, () => now);
        await schedule.RecordFailureAsync(rateLimited: false, CancellationToken.None);

        var restarted = new VelopackCheckSchedule(files.Root, () => now);
        Assert.IsFalse((await restarted.ShouldCheckAsync(false, CancellationToken.None)).Check);
        Assert.IsTrue((await restarted.ShouldCheckAsync(true, CancellationToken.None)).Check);
    }

    private sealed class TemporaryFiles : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),
            "ShnappVelopackCheckTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
        }
    }
}
