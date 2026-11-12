using System.Net;
using System.Text.Json.Nodes;
using TvEmulator.Services;
using Xunit;

namespace TvEmulator.Tests;

public class TvAppShelfTests
{
    private const string CatalogText = """
        var TvApps = { groups: [{ apps: [
          { url: "https://example.com/tv" },
          { url: "https://example.com/tv/kids" }
        ] }] };
        """;

    [Fact]
    public void CatalogHostsAreAllowedAndOtherHostsAreNot()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "catalog.js"), CatalogText);
            var allowed = TvAppShelf.ReadHosts(dir);
            Assert.Equal(new[] { "example.com" }, allowed.OrderBy(host => host).ToArray());

            Assert.True(TvAppShelf.TryParseLaunchUrl("https://example.com/tv", allowed, out var page));
            Assert.Equal("/tv", page.PathAndQuery);
            Assert.True(TvAppShelf.TryParseLaunchUrl("https://example.com/tv/kids", allowed, out _));
            Assert.False(TvAppShelf.TryParseLaunchUrl("https://evil.example/tv", allowed, out _));
            Assert.False(TvAppShelf.TryParseLaunchUrl("http://127.0.0.1/", allowed, out _));
            Assert.False(TvAppShelf.TryParseLaunchUrl("https://127.0.0.1/", allowed, out _));
            Assert.False(TvAppShelf.TryParseLaunchUrl("https://localhost/tv", allowed, out _));
            Assert.False(TvAppShelf.TryParseLaunchUrl("http://example.com/tv", allowed, out _));
            Assert.False(TvAppShelf.TryParseLaunchUrl("https://user@example.com/tv", allowed, out _));
            Assert.False(TvAppShelf.TryParseLaunchUrl("https://example.com:8443/tv", allowed, out _));
            Assert.Empty(TvAppShelf.ReadHosts(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        var shim = File.ReadAllText(Path.Combine(RepoPaths.Root(), "shims", "platform.js"));
        Assert.Contains("profile.shelfReturn && e.keyCode === 118", shim);
        Assert.Contains("/__emulator/open?return=1", shim);
        Assert.Contains("global.location.href = \"/\"", shim);
    }

    [Fact]
    public async Task OpenSwitchesToCatalogHostAndReturnRestoresShelf()
    {
        var root = RepoPaths.Root();
        var dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "index.html"), "<html><head></head><body>SHELF_MARKER</body></html>");
            await File.WriteAllTextAsync(Path.Combine(dir, "catalog.js"), CatalogText);
            var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "tizen-2017");
            using var server = new AppServer();
            server.Start(root, dir, profile, pointerEnabled: false);
            using var client = new HttpClient();

            var home = await client.GetStringAsync(Page(server));
            Assert.Contains("SHELF_MARKER", home);
            Assert.False(Profile(home)["shelfReturn"]!.GetValue<bool>());

            Assert.False((await PostOpen(client, server.Port, "http://127.0.0.1/"))["ok"]!.GetValue<bool>());
            Assert.False((await PostOpen(client, server.Port, "https://evil.example/tv"))["ok"]!.GetValue<bool>());
            Assert.False((await PostReturn(client, server.Port))["ok"]!.GetValue<bool>());
            Assert.Contains("SHELF_MARKER", await client.GetStringAsync(Page(server)));

            var opened = await PostOpen(client, server.Port, "https://example.com/tv");
            Assert.True(opened["ok"]!.GetValue<bool>());
            Assert.Equal("/tv", opened["path"]!.GetValue<string>());
            Assert.False((await PostOpen(client, server.Port, "https://evil.example/"))["ok"]!.GetValue<bool>());

            var back = await PostReturn(client, server.Port);
            Assert.True(back["ok"]!.GetValue<bool>());
            Assert.Equal("/", back["path"]!.GetValue<string>());
            var restored = await client.GetStringAsync(Page(server));
            Assert.Contains("SHELF_MARKER", restored);
            Assert.False(Profile(restored)["shelfReturn"]!.GetValue<bool>());

            var nested = await PostOpen(client, server.Port, "https://example.com/tv/kids");
            Assert.True(nested["ok"]!.GetValue<bool>());
            Assert.Equal("/tv/kids", nested["path"]!.GetValue<string>());
            Assert.True((await PostReturn(client, server.Port))["ok"]!.GetValue<bool>());

            var rootPage = await PostOpen(client, server.Port, "https://example.com/");
            Assert.True(rootPage["ok"]!.GetValue<bool>());
            Assert.Equal("/", rootPage["path"]!.GetValue<string>());
            Assert.True((await PostReturn(client, server.Port))["ok"]!.GetValue<bool>());
            Assert.Contains("SHELF_MARKER", await client.GetStringAsync(Page(server)));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task OpenIsRejectedWhileComparing()
    {
        var root = RepoPaths.Root();
        var dirA = TempDir();
        var dirB = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dirA, "index.html"), "<html><head></head><body>APP_A_MARKER</body></html>");
            await File.WriteAllTextAsync(Path.Combine(dirA, "catalog.js"), CatalogText);
            await File.WriteAllTextAsync(Path.Combine(dirB, "index.html"), "<html><head></head><body>APP_B_MARKER</body></html>");
            var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "tizen-2017");
            using var server = new AppServer();
            server.StartCompare(root, AppSource.Parse(dirA), AppSource.Parse(dirB), "app-a", "app-b", profile, pointerEnabled: false);
            using var client = new HttpClient();

            var denied = await PostOpen(client, server.Port, "https://example.com/tv");
            Assert.False(denied["ok"]!.GetValue<bool>());
            Assert.False((await PostReturn(client, server.Port))["ok"]!.GetValue<bool>());
            var page = await client.GetStringAsync(Page(server));
            Assert.Contains("APP_A_MARKER", page);
            Assert.False(Profile(page)["shelfReturn"]!.GetValue<bool>());
        }
        finally
        {
            Directory.Delete(dirA, true);
            Directory.Delete(dirB, true);
        }
    }

    private static async Task<JsonNode> PostOpen(HttpClient client, int port, string url)
    {
        using var response = await client.PostAsync(
            "http://127.0.0.1:" + port + "/__emulator/open?url=" + Uri.EscapeDataString(url),
            new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static async Task<JsonNode> PostReturn(HttpClient client, int port)
    {
        using var response = await client.PostAsync(
            "http://127.0.0.1:" + port + "/__emulator/open?return=1",
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
        var dir = Path.Combine(Path.GetTempPath(), "tv-shelf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
