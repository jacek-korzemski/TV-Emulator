using TvEmulator.Models;
using TvEmulator.Services;
using Xunit;

namespace TvEmulator.Tests;

public class UserSettingsTests
{
    [Fact]
    public void StartupAppUsesTheSavedDefault()
    {
        var settings = new UserSettings { DefaultApp = @"  D:\apps\vod  " };
        Assert.Equal(@"D:\apps\vod", UserSettingsStore.StartupApp(settings, @"C:\sample"));
    }

    [Fact]
    public void StartupAppFallsBackToTheSample()
    {
        Assert.Equal(@"C:\sample", UserSettingsStore.StartupApp(new UserSettings(), @"C:\sample"));
    }

    [Fact]
    public void SavingSettingsReplacesTheSampleAndThePreviousDefault()
    {
        Assert.Equal(@"D:\new", UserSettingsStore.NextAppField(@"C:\sample", "", @"C:\sample", @"D:\new"));
        Assert.Equal(@"D:\new", UserSettingsStore.NextAppField(@"D:\old\", @"D:\old", @"C:\sample", @"D:\new"));
        Assert.Equal(@"C:\sample", UserSettingsStore.NextAppField(@"D:\old", @"D:\old", @"C:\sample", "  "));
        Assert.Equal(@"D:\new", UserSettingsStore.NextAppField("  ", @"D:\old", @"C:\sample", @"D:\new"));
    }

    [Fact]
    public void ACustomAppStaysWhenTheDefaultChanges()
    {
        Assert.Equal(@"D:\custom", UserSettingsStore.NextAppField(@"D:\custom", @"D:\old", @"C:\sample", @"D:\new"));
        Assert.Equal(@"C:\sample", UserSettingsStore.NextAppField(@"C:\sample", @"D:\old", @"C:\sample", @"D:\new"));
    }

    [Fact]
    public void HiddenProfilesLeaveTheListAndTheFileRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "tv-emulator-settings-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            UserSettingsStore.Save(new UserSettings
            {
                DefaultApp = " https://example.com/app/ ",
                HiddenProfiles = ["webos-4", "WEBOS-4", " "]
            }, path);

            var loaded = UserSettingsStore.Load(path);
            Assert.Equal("https://example.com/app/", loaded.DefaultApp);
            Assert.Equal(["webos-4"], loaded.HiddenProfiles);

            var visible = UserSettingsStore.VisibleProfiles(
            [
                new TvProfile { Id = "tizen-2017", Label = "Tizen" },
                new TvProfile { Id = "webos-4", Label = "webOS" },
                new TvProfile { Id = "vidaa-u6", Label = "VIDAA" }
            ], loaded);
            Assert.Equal(["tizen-2017", "vidaa-u6"], visible.Select(item => item.Id).ToArray());
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AMissingOrBrokenSettingsFileStartsEmpty()
    {
        var missing = UserSettingsStore.Load(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".json"));
        Assert.Equal("", missing.DefaultApp);
        Assert.Empty(missing.HiddenProfiles);

        var path = Path.Combine(Path.GetTempPath(), "tv-emulator-settings-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{");
            var broken = UserSettingsStore.Load(path);
            Assert.Equal("", broken.DefaultApp);
            Assert.Empty(broken.HiddenProfiles);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
