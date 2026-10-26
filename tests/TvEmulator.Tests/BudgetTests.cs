using TvEmulator.Models;
using TvEmulator.Services;
using Xunit;

namespace TvEmulator.Tests;

public class BudgetTests
{
    [Fact]
    public void OneToOneUsesClockAndIgnoresThreadCount()
    {
        var engine = new EngineBuild { Milestone = 47, Kind = "chromium" };
        Assert.Equal(0.45, TvPower.DesktopGhz(engine));
        Assert.Equal(8, TvPower.PercentForClock(0.45, 5.3));

        var few = TvPower.Describe(engine, "5.3", "4");
        var many = TvPower.Describe(engine, "5,3", "32");
        Assert.Equal(8, few.Percent);
        Assert.Equal(few.Percent, many.Percent);
        Assert.Equal("8% jednego wątku przy 12 wątkach to 0,7% całego procesora.", TvPower.ShareSentence(8, 12));
    }

    [Fact]
    public void DesktopGhzFollowsTheEngine()
    {
        Assert.Equal(0.45, TvPower.DesktopGhz(new EngineBuild { Kind = "firefox", Milestone = 44 }));
        Assert.Equal(0.55, TvPower.DesktopGhz(new EngineBuild { Milestone = 56 }));
        Assert.Equal(0.70, TvPower.DesktopGhz(new EngineBuild { Milestone = 63 }));
        Assert.Equal(1.30, TvPower.DesktopGhz(new EngineBuild { Milestone = 88 }));
        Assert.Equal(2.60, TvPower.DesktopGhz(new EngineBuild { Milestone = 122 }));
        Assert.Null(TvPower.PercentForClock(2.60, 2.0));
    }

    [Fact]
    public void CpuRateRoundsAThreadShareOntoTheWholeMachine()
    {
        Assert.Equal(67, ProcessBudget.CpuRate(8, 12));
        Assert.Equal(1, ProcessBudget.CpuRate(1, 10_000));
    }

    [Fact]
    public void BudgetsRoundTripAndDropEmptyLimits()
    {
        var path = Path.Combine(Path.GetTempPath(), "tv-emulator-budget-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            UserSettingsStore.Save(new UserSettings
            {
                DefaultApp = "samples",
                CpuClockGhz = 5.3,
                Budgets =
                [
                    new ProfileBudget { ProfileId = "tizen-2017", CpuPercent = 8, MemoryMb = 1024 },
                    new ProfileBudget { ProfileId = "webos-4", CpuPercent = 0, MemoryMb = 0 },
                    new ProfileBudget { ProfileId = "Tizen-2017", CpuPercent = 20, MemoryMb = null },
                    new ProfileBudget { ProfileId = " ", CpuPercent = 10, MemoryMb = 10 }
                ]
            }, path);

            var loaded = UserSettingsStore.Load(path);
            Assert.Equal(5.3, loaded.CpuClockGhz);
            var budget = Assert.Single(loaded.Budgets);
            Assert.Equal("tizen-2017", budget.ProfileId, ignoreCase: true);
            Assert.Equal(20, budget.CpuPercent);
            Assert.Null(budget.MemoryMb);
            Assert.Equal(20, UserSettingsStore.BudgetFor(loaded, "tizen-2017")?.CpuPercent);

            var text = File.ReadAllText(path);
            Assert.Contains("\"cpuClockGhz\": 5.3", text, StringComparison.Ordinal);
            Assert.Contains("\"cpuPercent\": 20", text, StringComparison.Ordinal);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AJobOwnsTheEngineProcess()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var session = new EngineSession();
        try
        {
            session.Start(cmd, ["/c", "ping", "-n", "4", "127.0.0.1"], 50, 1024);
            Assert.False(session.HasExited);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Theory]
    [InlineData("", true, null)]
    [InlineData("8", true, 8)]
    [InlineData("0", false, null)]
    [InlineData("101", false, null)]
    public void LimitsRejectNumbersOutsideTheRange(string text, bool ok, int? value)
    {
        Assert.Equal(ok, TvPower.TryReadLimit(text, 1, 100, out var parsed));
        Assert.Equal(value, parsed);
    }
}
