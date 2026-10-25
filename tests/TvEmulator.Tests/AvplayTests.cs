using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using TvEmulator.Models;
using TvEmulator.Services;
using Xunit;

namespace TvEmulator.Tests;

public class AvplayTests
{
    [Fact]
    public void BridgeForwardsKnownCommandsAndRejectsTheRest()
    {
        var bridge = new AvplayBridge();
        var seen = new List<string>();
        var traces = new List<string>();
        bridge.Command += command => seen.Add((string)command["cmd"]!);
        bridge.Trace += traces.Add;

        Assert.True((bool)bridge.Accept("{\"cmd\":\"open\",\"url\":\"https://cdn/x.mpd\",\"gen\":1}")["ok"]!);
        Assert.True((bool)bridge.Accept("{\"cmd\":\"shade\",\"items\":[{\"id\":\"shade-1\",\"opacity\":0.5,\"color\":\"secret-css\"}]}")["ok"]!);
        Assert.False((bool)bridge.Accept("{\"cmd\":\"format\"}")["ok"]!);
        Assert.False((bool)bridge.Accept("not json")["ok"]!);
        Assert.Equal(["open", "shade"], seen);
        Assert.DoesNotContain(traces, line => line.Contains("secret-css", StringComparison.Ordinal));
    }

    [Fact]
    public void BridgeTraceKeepsTheLicenseServerAndHidesCustomData()
    {
        var bridge = new AvplayBridge();
        var lines = new List<string>();
        bridge.Trace += lines.Add;
        var props = "{\"LicenseServer\":\"https://drm.example/rightsmanager.asmx\",\"CustomData\":\"secret-token-123\"}";
        var command = new JsonObject { ["cmd"] = "drm", ["drmType"] = "PLAYREADY", ["operation"] = "SetProperties", ["data"] = props };

        bridge.Accept(command.ToJsonString());

        var line = Assert.Single(lines);
        Assert.Contains("https://drm.example/rightsmanager.asmx", line);
        Assert.DoesNotContain("secret-token-123", line);
    }

    [Fact]
    public void WaitReturnsOnlyNewerEventsAndAFreshPageStartsAtTheHead()
    {
        var bridge = new AvplayBridge();
        bridge.Publish(new JsonObject { ["type"] = "time", ["ms"] = 1 });
        bridge.Publish(new JsonObject { ["type"] = "time", ["ms"] = 2 });

        var fresh = bridge.Wait(-1, TimeSpan.FromSeconds(5));
        Assert.Equal(2, (long)fresh["head"]!);
        Assert.Empty(fresh["events"]!.AsArray());

        var later = bridge.Wait(1, TimeSpan.FromSeconds(5));
        var only = Assert.Single(later["events"]!.AsArray());
        Assert.Equal(2, (int)only!["ms"]!);
    }

    [Fact]
    public async Task WaitBlocksUntilAnEventArrives()
    {
        var bridge = new AvplayBridge();
        var waiting = Task.Run(() => bridge.Wait(0, TimeSpan.FromSeconds(10)));
        await Task.Delay(150);
        Assert.False(waiting.IsCompleted);

        bridge.Publish(new JsonObject { ["type"] = "prepared", ["durationMs"] = 5000 });
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        var evt = Assert.Single(result["events"]!.AsArray());
        Assert.Equal("prepared", (string)evt!["type"]!);
    }

    [Fact]
    public async Task CloseReleasesAWaitingPoll()
    {
        var bridge = new AvplayBridge();
        var waiting = Task.Run(() => bridge.Wait(0, TimeSpan.FromSeconds(30)));
        await Task.Delay(100);
        bridge.Close();
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(result["events"]!.AsArray());
    }

    [Fact]
    public void DisplayRectScalesFromTheAppViewportToTheStage()
    {
        var stage = new PixelRect(440, 0, 2560, 1440);
        var viewport = new Viewport { Width = 1920, Height = 1080 };

        Assert.Equal(stage, VideoPlane.MapRect(stage, viewport, 0, 0, 1920, 1080));
        Assert.Equal(new PixelRect(440 + 1280, 720, 1280, 720), VideoPlane.MapRect(stage, viewport, 960, 540, 960, 540));
    }

    [Fact]
    public async Task ServerRoutesAvplayCommandsEventsAndPlaneFiles()
    {
        var root = RepoPaths.Root();
        var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "tizen-2017");
        using var server = new AppServer();
        server.Start(root, Path.Combine(root, "samples", "tizen-hello"), profile, pointerEnabled: false);
        var commands = new List<JsonObject>();
        server.Avplay.Command += commands.Add;
        using var client = new HttpClient();
        var baseUrl = "http://127.0.0.1:" + server.Port;

        var reply = await client.PostAsync(baseUrl + "/__emulator/avplay", new StringContent("{\"cmd\":\"play\",\"gen\":3}", Encoding.UTF8, "application/json"));
        var replyJson = JsonNode.Parse(await reply.Content.ReadAsStringAsync())!;
        Assert.True((bool)replyJson["ok"]!);
        Assert.Equal("play", (string)Assert.Single(commands)["cmd"]!);

        var head = JsonNode.Parse(await client.GetStringAsync(baseUrl + "/__emulator/avplay/events?since=-1"))!;
        var since = (long)head["head"]!;
        server.Avplay.Publish(new JsonObject { ["type"] = "completed", ["gen"] = 3 });
        var events = JsonNode.Parse(await client.GetStringAsync(baseUrl + "/__emulator/avplay/events?since=" + since))!;
        Assert.Equal("completed", (string)Assert.Single(events["events"]!.AsArray())!["type"]!);

        var page = await client.GetStringAsync(baseUrl + "/__emulator/videoplane.html");
        var isoAt = page.IndexOf("codem-isoboxer.min.js", StringComparison.Ordinal);
        var shakaAt = page.IndexOf("shaka-player.compiled.js", StringComparison.Ordinal);
        Assert.True(isoAt >= 0 && shakaAt > isoAt);
        var iso = await client.GetAsync(baseUrl + "/__emulator/vendor/codem-isoboxer.min.js");
        var shaka = await client.GetAsync(baseUrl + "/__emulator/vendor/shaka-player.compiled.js");
        Assert.Equal(System.Net.HttpStatusCode.OK, iso.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, shaka.StatusCode);
    }

    [Fact]
    public void MediaTraceKeepsTheByteCountAndHidesThePayload()
    {
        var bridge = new AvplayBridge();
        var lines = new List<string>();
        JsonObject? forwarded = null;
        bridge.Trace += lines.Add;
        bridge.Command += command => forwarded = command;
        var secret = Convert.ToBase64String(Encoding.UTF8.GetBytes("widevine-license-secret"));

        Assert.True(bridge.Submit(new JsonObject
        {
            ["cmd"] = "media",
            ["op"] = "update",
            ["id"] = "s1",
            ["size"] = 7,
            ["bytes"] = secret
        }));

        var line = Assert.Single(lines);
        Assert.Contains("op=update", line);
        Assert.Contains("size=7", line);
        Assert.DoesNotContain(secret, line);
        Assert.DoesNotContain("widevine-license-secret", line);
        Assert.Equal(secret, (string)forwarded!["bytes"]!);

        bridge.Publish(new JsonObject { ["type"] = "eme-message", ["id"] = "s1", ["bytes"] = secret });
        Assert.Contains(lines, item => item.Contains("eme-message", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, item => item.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServerForwardsWidevineMediaWithoutEchoingBytes()
    {
        var root = RepoPaths.Root();
        var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "webos-4");
        using var server = new AppServer();
        server.Start(root, Path.Combine(root, "samples", "webos-hello"), profile, pointerEnabled: false);
        JsonObject? forwarded = null;
        server.Avplay.Command += command => forwarded = command;
        using var client = new HttpClient();
        var response = await client.PostAsync(
            "http://127.0.0.1:" + server.Port + "/__emulator/media?op=append&id=b1",
            new ByteArrayContent(new byte[] { 9, 8, 7, 6 }));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.True((bool)json["ok"]!);
        Assert.NotNull(forwarded);
        Assert.Equal("media", (string)forwarded!["cmd"]!);
        Assert.Equal("append", (string)forwarded["op"]!);
        Assert.Equal("b1", (string)forwarded["id"]!);
        Assert.Equal(4, (int)forwarded["size"]!);
        Assert.DoesNotContain("widevine-license-secret", json.ToJsonString());
    }

    [Fact]
    public void WebOsUsesTheHtmlWidevinePlaneInsteadOfAvplay()
    {
        var root = RepoPaths.Root();
        var shim = File.ReadAllText(Path.Combine(root, "shims", "platform.js"));
        var plane = File.ReadAllText(Path.Combine(root, "shims", "videoplane.js"));
        var main = File.ReadAllText(Path.Combine(root, "src", "TvEmulator", "MainWindow.xaml.cs"));
        var playbackAt = shim.IndexOf("function installEdgePlayback()", StringComparison.Ordinal);

        Assert.Contains("profile.Player is \"edge\" or \"avplay\"", main);
        Assert.True(playbackAt > 0);
        var playback = shim.Substring(playbackAt);
        Assert.Contains("installEdgePlayback();", shim);
        Assert.Contains("com.widevine.alpha", playback);
        Assert.Contains("/__emulator/media", playback);
        Assert.Contains("data-tv-hole", playback);
        Assert.Contains("data-tv-plane", playback);
        Assert.Contains("function installSourceBridge()", playback);
        Assert.Contains("function holdPlaneSource(", playback);
        Assert.Contains("blob:tv-emulator/", playback);
        Assert.Contains("!== \"SOURCE\"", playback);
        Assert.Contains("node.insertBefore", playback);
        Assert.Contains("if (holdPlaneSource(this, child))", playback);
        Assert.Contains("return child;", playback);
        Assert.Contains("stopImmediatePropagation", playback);
        Assert.Contains("new Uint8Array(1)", playback);
        Assert.Contains("__tvHoldPause", playback);
        Assert.Contains("__tvHoldPlay", playback);
        Assert.Contains("__tvHoldTime", playback);
        Assert.Contains("function releaseCanPlay(", playback);
        Assert.Contains("video.__tvReady < 3", playback);
        var releaseAt = playback.IndexOf("function releaseCanPlay(", StringComparison.Ordinal);
        var release = playback.Substring(releaseAt, 360);
        Assert.Contains("setTimeout", release);
        Assert.Contains("fire(video, \"canplay\")", release);
        Assert.Contains("child.removeAttribute(\"src\")", playback);
        Assert.DoesNotContain("child.src = \"\"", playback);
        Assert.Contains("getAttribute(\"data-tv-hole\") === \"widevine\"", shim);
        Assert.DoesNotContain("createAvplay()", playback);
        Assert.DoesNotContain("webapis", playback);
        Assert.Contains("navigator.requestMediaKeySystemAccess('com.widevine.alpha'", plane);
        Assert.DoesNotContain("com.widevine.alpha'] =", plane);
        var seekMseAt = plane.IndexOf("const seekMse =", StringComparison.Ordinal);
        var seekToAt = plane.IndexOf("const seekTo =", StringComparison.Ordinal);
        Assert.True(seekMseAt >= 0 && seekToAt > seekMseAt);
        var seekMse = plane.Substring(seekMseAt, seekToAt - seekMseAt);
        Assert.Contains("video.readyState === 0", seekMse);
        Assert.Contains("pendingMseTime = target", seekMse);
        Assert.DoesNotContain("setTimeout", seekMse);
        Assert.Contains("played.catch(() => {})", plane);
    }

    [Fact]
    public void ShimPaintsTheSameKeyColorTheHostCutsOut()
    {
        var shim = File.ReadAllText(Path.Combine(RepoPaths.Root(), "shims", "platform.js"));
        var key = "#" + VideoPlane.KeyColor.ToString("x6");
        Assert.Contains("\"" + key + "\"", shim);
        Assert.Contains("avplay: createAvplay()", shim);
        Assert.Contains("application/avplayer", shim);
        Assert.Contains("installPlayerFallback();", shim);
        Assert.Contains("videoComposition === \"alpha\"", shim);
        Assert.Contains("background:transparent !important", shim);
        Assert.Contains("[data-tv-shade]", shim);
        Assert.Contains("document.documentElement.contains(record.element)", shim);
        Assert.Contains("opacity: shadeRound(opacity, 3)", shim);
        Assert.Contains("payload !== shadeLastPayload || Date.now() - shadeLastPost >= 1000", shim);
    }

    [Fact]
    public void VideoPlaneKeepsShadeNodesAcrossIntermediateAndZeroOpacityFrames()
    {
        var script = File.ReadAllText(Path.Combine(RepoPaths.Root(), "shims", "videoplane.js"));
        var markup = File.ReadAllText(Path.Combine(RepoPaths.Root(), "shims", "videoplane.html"));

        Assert.Contains("const shadeNodes = new Map()", script);
        Assert.Contains("let node = shadeNodes.get(id)", script);
        Assert.Contains("paintShade(node, item)", script);
        Assert.Contains("item.opacity == null ? 1", script);
        Assert.DoesNotContain("node.style.transition", script);
        Assert.Contains("clearShades();", script);
        Assert.Contains("id=\"shade-plane\"", markup);
    }

    [Fact]
    public void VideoPlaneUsesSmoothStreamingCapableShaka()
    {
        var root = RepoPaths.Root();
        var markup = File.ReadAllText(Path.Combine(root, "shims", "videoplane.html"));
        var script = File.ReadAllText(Path.Combine(root, "shims", "videoplane.js"));
        var shaka = File.ReadAllText(Path.Combine(root, "shims", "vendor", "shaka-player.compiled.js"));
        var iso = File.ReadAllText(Path.Combine(root, "shims", "vendor", "codem-isoboxer.min.js"));

        var isoAt = markup.IndexOf("codem-isoboxer.min.js", StringComparison.Ordinal);
        var shakaAt = markup.IndexOf("shaka-player.compiled.js", StringComparison.Ordinal);
        Assert.True(isoAt >= 0 && shakaAt > isoAt);
        Assert.Contains("application/vnd.ms-sstr+xml", shaka);
        Assert.Contains("v4.16.54", shaka);
        Assert.Contains("var ISOBoxer={}", iso);
        Assert.Contains("enableTextDisplayer()", script);
        Assert.Contains("MSS_LIVE_CONTENT_NOT_SUPPORTED", script);
        Assert.Contains("normalizeMssVideoDurations(response)", script);
        Assert.Contains("NetworkingEngine.RequestType.MANIFEST", script);
        Assert.Contains("value % limit", script);
        Assert.Contains("mss corrected wrapped video durations=", script);
    }

    [Fact]
    public void ShimRecreatesTheAvplayObjectRemovedByHashRouteUnmount()
    {
        var shim = File.ReadAllText(Path.Combine(RepoPaths.Root(), "shims", "platform.js"));

        Assert.Contains("function ensurePlayerObject(generation)", shim);
        Assert.Contains("ensurePlayerObject(gen);", shim);
        Assert.Contains("candidate.style.display === \"none\"", shim);
        Assert.Contains("object.setAttribute(\"type\", \"application/avplayer\")", shim);
        Assert.Contains("object.setAttribute(\"data-tv-emulator\", \"avplay-recreated\")", shim);
        Assert.Contains("host.appendChild(object)", shim);
        Assert.Contains("avplay-hole recreated", shim);
    }

    [Fact]
    public void MarkupAvplayerObjectsGetFallbackContentAndOthersStayUntouched()
    {
        var html = "<body><object id=\"av\" TYPE='application/avplayer' style=\"width:100%\"></object>"
            + "<object type=\"application/pdf\"></object></body>";

        var filled = AppServer.FillAvplayerObjects(html);

        Assert.Equal(
            "<body><object id=\"av\" TYPE='application/avplayer' style=\"width:100%\"><span data-tv-emulator=\"avplay\"></span></object>"
            + "<object type=\"application/pdf\"></object></body>",
            filled);
    }
}
