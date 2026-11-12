using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TvEmulator.Debug;
using TvEmulator.Services;
using Xunit;

namespace TvEmulator.Tests;

public class CompareModeTests
{
    [Fact]
    [Trait("Category", "Feature: compare-mode, Property 6: ValidateAppSource przyjmuje poprawne i odrzuca niepoprawne wejścia")]
    public void ValidateAppSourceAcceptsFolderAndHttp()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
            var local = MainWindow.ValidateAppSource(dir);
            Assert.False(local.IsRemote);
            Assert.Equal(Path.GetFullPath(dir), local.Directory);

            var http = MainWindow.ValidateAppSource("http://app1.example/app");
            Assert.True(http.IsRemote);
            Assert.Equal("http", http.Page!.Scheme);

            var https = MainWindow.ValidateAppSource("  https://app2.example/player  ");
            Assert.True(https.IsRemote);
            Assert.Equal("https", https.Page!.Scheme);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Feature: compare-mode, Property 6: ValidateAppSource przyjmuje poprawne i odrzuca niepoprawne wejścia")]
    public void ValidateAppSourceRejectsEmptyMissingIndexAndFtp()
    {
        var dir = TempDir();
        try
        {
            Assert.Throws<InvalidOperationException>(() => MainWindow.ValidateAppSource(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        Assert.Throws<InvalidOperationException>(() => MainWindow.ValidateAppSource("   "));
        Assert.ThrowsAny<Exception>(() => MainWindow.ValidateAppSource("ftp://example.com/app"));
    }

    [Fact]
    public async Task SingleSourceProfileLeavesCompareOff()
    {
        var root = RepoPaths.Root();
        var dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "index.html"), "<html><head></head><body>ONE</body></html>");
            var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "tizen-2017");
            using var server = new AppServer();
            server.Start(root, dir, profile, pointerEnabled: false);
            using var client = new HttpClient();
            var html = await client.GetStringAsync("http://127.0.0.1:" + server.Port + "/");
            var payload = Profile(html);
            Assert.False(payload["compareMode"]!.GetValue<bool>());
            Assert.Equal(0, payload["compareActive"]!.GetValue<int>());
            Assert.Equal("", payload["compareNotice"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Feature: compare-mode, Property 2: SwitchSource przełącza serwowaną treść na źródło docelowe")]
    [Trait("Category", "Feature: compare-mode, Property 3: compareNotice konsumowany raz po przełączeniu")]
    [Trait("Category", "Feature: compare-mode, Property 5: SwitchSource to no-op gdy cel już aktywny")]
    public async Task CompareSwitchServesTheOtherAppAndConsumesNoticeOnce()
    {
        var root = RepoPaths.Root();
        var dirA = TempDir();
        var dirB = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dirA, "index.html"), "<html><head></head><body>APP_A_MARKER</body></html>");
            await File.WriteAllTextAsync(Path.Combine(dirB, "index.html"), "<html><head></head><body>APP_B_MARKER</body></html>");
            var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "tizen-2017");
            using var server = new AppServer();
            var hub = new DebugHub();
            server.CompareTrace += hub.AcceptCompareTrace;
            server.CompareSwitched += hub.SetCompareIndex;
            server.StartCompare(root, AppSource.Parse(dirA), AppSource.Parse(dirB), "app-a", "app-b", profile, pointerEnabled: false);
            hub.BeginCompare("app-a", "app-b");

            using var client = new HttpClient();
            var first = Profile(await client.GetStringAsync(Page(server)));
            Assert.Contains("APP_A_MARKER", await client.GetStringAsync(Page(server)));
            Assert.True(first["compareMode"]!.GetValue<bool>());
            Assert.Equal(0, first["compareActive"]!.GetValue<int>());
            Assert.Equal("", first["compareNotice"]!.GetValue<string>());

            await PostTrace(client, server.Port, "/#/strona", 1000);
            var before = JsonNode.Parse(hub.CompareJson())!;
            Assert.Equal(0, before["activeIndex"]!.GetValue<int>());
            Assert.Equal(1000, before["routes"]![0]!["dwell"]![0]!["min"]!.GetValue<long>());

            var switched = await PostCompare(client, server.Port, 1);
            Assert.True(switched["ok"]!.GetValue<bool>());
            Assert.True(switched["switched"]!.GetValue<bool>());
            Assert.Equal(1, JsonNode.Parse(hub.CompareJson())!["activeIndex"]!.GetValue<int>());

            var landed = await client.GetStringAsync(Page(server));
            Assert.Contains("APP_B_MARKER", landed);
            var notice = Profile(landed);
            Assert.Equal(1, notice["compareActive"]!.GetValue<int>());
            Assert.Equal("app-b", notice["compareNotice"]!.GetValue<string>());

            var again = Profile(await client.GetStringAsync(Page(server)));
            Assert.Equal("", again["compareNotice"]!.GetValue<string>());
            Assert.Contains("APP_B_MARKER", await client.GetStringAsync(Page(server)));

            var same = await PostCompare(client, server.Port, 1);
            Assert.True(same["ok"]!.GetValue<bool>());
            Assert.False(same["switched"]!.GetValue<bool>());
            var quiet = Profile(await client.GetStringAsync(Page(server)));
            Assert.Equal("", quiet["compareNotice"]!.GetValue<string>());
            Assert.Equal(1, quiet["compareActive"]!.GetValue<int>());

            var rejected = await PostCompare(client, server.Port, 9);
            Assert.False(rejected["ok"]!.GetValue<bool>());

            await PostTrace(client, server.Port, "/#/strona", 4000);
            var after = JsonNode.Parse(hub.CompareJson())!;
            Assert.Equal(4000, after["routes"]![0]!["dwell"]![1]!["min"]!.GetValue<long>());

            var shim = await client.GetStringAsync("http://127.0.0.1:" + server.Port + "/__emulator/platform.js");
            Assert.Contains("compareSwitch", shim);
            Assert.Contains("119", shim);
            Assert.Contains("120", shim);
            Assert.Contains("e.type === \"keydown\"", shim);
            Assert.Contains("compareNotice", shim);
        }
        finally
        {
            Directory.Delete(dirA, true);
            Directory.Delete(dirB, true);
        }
    }

    [Fact]
    public void CompareStatsSplitMemoryAndDwellByApp()
    {
        var hub = new DebugHub();
        hub.AcceptCompareTrace("""{"kind":"visit","route":"/#/strona","dwellMs":5}""");
        Assert.False(JsonNode.Parse(hub.CompareJson())!["active"]!.GetValue<bool>());

        hub.BeginCompare("https://app1.example", "https://app2.example");
        hub.NoteCompareRoute("/#/strona");
        hub.AddMemory(new MemorySample { Source = "engine", Used = 100 });
        hub.AddMemory(new MemorySample { Source = "engine", Used = 200 });
        hub.AddMemory(new MemorySample { Source = "video", Used = 99999 });
        hub.AddMemory(new MemorySample { Source = "engine" });
        hub.NoteCompareVisit("/#/strona", 1000);
        hub.NoteCompareVisit("/#/strona", 3000);
        hub.NoteCompareDocument(100);
        hub.NoteCompareDocument(300);

        hub.SetCompareIndex(1);
        hub.NoteCompareRoute("/#/strona");
        hub.AddMemory(new MemorySample { Source = "engine", Used = 300 });
        hub.AddMemory(new MemorySample { Source = "engine", Used = 500 });
        hub.AcceptCompareTrace("""{"kind":"visit","route":"/#/strona","dwellMs":4000}""");
        hub.AcceptCompareTrace("""{"kind":"visit","route":"/#/strona","dwellMs":8000}""");
        hub.NoteCompareDocument(400);
        hub.NoteCompareDocument(800);

        var snapshot = JsonNode.Parse(hub.CompareJson())!;
        Assert.True(snapshot["active"]!.GetValue<bool>());
        Assert.Equal(1, snapshot["activeIndex"]!.GetValue<int>());
        Assert.Equal("/#/strona", snapshot["route"]!.GetValue<string>());

        var apps = snapshot["apps"]!.AsArray();
        Assert.Equal("https://app1.example", apps[0]!["label"]!.GetValue<string>());
        Assert.Equal("https://app2.example", apps[1]!["label"]!.GetValue<string>());
        AssertStat(apps[0]!["memoryStat"]!, count: 2, min: 100, max: 200, avg: 150);
        AssertStat(apps[1]!["memoryStat"]!, count: 2, min: 300, max: 500, avg: 400);
        AssertStat(apps[0]!["document"]!, count: 2, min: 100, max: 300, avg: 200);
        AssertStat(apps[1]!["document"]!, count: 2, min: 400, max: 800, avg: 600);
        Assert.Equal(2, apps[0]!["memory"]!.AsArray().Count);
        Assert.Equal(2, apps[1]!["memory"]!.AsArray().Count);

        var route = Assert.Single(snapshot["routes"]!.AsArray());
        Assert.Equal("/#/strona", route!["route"]!.GetValue<string>());
        AssertStat(route["dwell"]![0]!, count: 2, min: 1000, max: 3000, avg: 2000);
        AssertStat(route["dwell"]![1]!, count: 2, min: 4000, max: 8000, avg: 6000);
        AssertStat(route["memory"]![0]!, count: 2, min: 100, max: 200, avg: 150);
        AssertStat(route["memory"]![1]!, count: 2, min: 300, max: 500, avg: 400);

        hub.BeginSession("tizen-2017", "Tizen", "chromium-56", diagnostics: false, logFile: null, video: false);
        var cleared = JsonNode.Parse(hub.CompareJson())!;
        Assert.False(cleared["active"]!.GetValue<bool>());
        Assert.Empty(cleared["routes"]!.AsArray());
    }

    private static void AssertStat(JsonNode stat, int count, long min, long max, double avg)
    {
        Assert.Equal(count, stat["count"]!.GetValue<int>());
        Assert.Equal(min, stat["min"]!.GetValue<long>());
        Assert.Equal(max, stat["max"]!.GetValue<long>());
        Assert.Equal(avg, stat["avg"]!.GetValue<double>());
    }

    private static async Task PostTrace(HttpClient client, int port, string route, long dwellMs)
    {
        var body = new StringContent(
            "{\"kind\":\"visit\",\"route\":" + System.Text.Json.JsonSerializer.Serialize(route) + ",\"dwellMs\":" + dwellMs + "}",
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync("http://127.0.0.1:" + port + "/__emulator/compare-trace", body);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<JsonNode> PostCompare(HttpClient client, int port, int target)
    {
        using var response = await client.PostAsync(
            "http://127.0.0.1:" + port + "/__emulator/compare?target=" + target,
            new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static JsonNode Profile(string html)
    {
        const string marker = "window.__TV_EMULATOR_PROFILE__=";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += marker.Length;
        var end = html.IndexOf(";</script>", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return JsonNode.Parse(html[start..end])!;
    }

    private static string Page(AppServer server) => "http://127.0.0.1:" + server.Port + "/";

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
