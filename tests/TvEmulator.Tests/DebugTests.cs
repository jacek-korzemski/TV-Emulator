using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using TvEmulator.Debug;
using TvEmulator.Services;
using Xunit;

namespace TvEmulator.Tests;

public class DebugTests
{
    [Fact]
    public void LegacyAndModernConsoleEventsCollapseToOneLine()
    {
        var legacy = CdpProtocol.ReadConsole("Console.messageAdded", JsonNode.Parse("""
            {"message":{"source":"console-api","level":"warning","text":"uwaga","url":"http://127.0.0.1/app.js","line":4,"column":2}}
            """), "engine");
        var modern = CdpProtocol.ReadConsole("Runtime.consoleAPICalled", JsonNode.Parse("""
            {"type":"warning","args":[{"type":"string","value":"uwaga"}],"stackTrace":{"callFrames":[{"functionName":"go","url":"http://127.0.0.1/app.js","lineNumber":3,"columnNumber":1}]}}
            """), "engine");

        Assert.NotNull(legacy);
        Assert.NotNull(modern);
        Assert.Equal("warning", legacy!.Level);
        Assert.Equal("warning", modern!.Level);
        Assert.Equal(4, legacy.Line);
        Assert.Equal(4, modern.Line);
        Assert.Equal("http://127.0.0.1/app.js", modern.Url);
        Assert.Contains("at go (http://127.0.0.1/app.js:4:2)", modern.Stack);

        var wrapped = CdpProtocol.ReadConsole("Runtime.consoleAPICalled", JsonNode.Parse("""
            {"type":"log","args":[{"type":"string","value":"hej"}],"stackTrace":{"callFrames":[{"functionName":"wrap","url":"http://127.0.0.1:47900/__emulator/platform.js","lineNumber":342,"columnNumber":4},{"functionName":"start","url":"http://127.0.0.1:47900/app.js","lineNumber":11,"columnNumber":2}]}}
            """), "engine");
        Assert.Equal("http://127.0.0.1:47900/app.js", wrapped!.Url);
        Assert.Equal(12, wrapped.Line);

        var hub = new DebugHub();
        Assert.True(hub.AddConsole(legacy));
        Assert.False(hub.AddConsole(modern));
    }

    [Fact]
    public void NetworkCaptureKeepsXhrPayloadAndSkipsMediaBodies()
    {
        var call = new NetworkCall { Id = 1, Source = "engine" };
        CdpProtocol.ApplyRequest(call, JsonNode.Parse("""
            {"requestId":"1.1","timestamp":10,"wallTime":1700000000,"type":"XHR","request":{"method":"POST","url":"http://example/api","headers":{"Content-Type":"application/json"},"postData":"{\"a\":1}"}}
            """)!);
        CdpProtocol.ApplyResponse(call, JsonNode.Parse("""
            {"requestId":"1.1","timestamp":10.2,"type":"XHR","response":{"status":201,"mimeType":"application/json","headers":[{"name":"Content-Type","value":"application/json"}]}}
            """)!);
        CdpProtocol.ApplyFinished(call, JsonNode.Parse("""
            {"requestId":"1.1","timestamp":10.5,"encodedDataLength":1200}
            """)!);

        Assert.Equal("POST", call.Method);
        Assert.Equal("xhr", call.Type);
        Assert.Equal("{\"a\":1}", call.RequestBody);
        Assert.Equal(201, call.Status);
        Assert.Equal("application/json", call.Mime);
        Assert.Contains("Content-Type: application/json", call.RequestHeaders);
        Assert.Contains("Content-Type: application/json", call.ResponseHeaders);
        Assert.Equal(500, call.DurationMs);
        Assert.Equal(1200, call.EncodedSize);
        Assert.True(CdpProtocol.ShouldFetchBody(call));
        call.ResponseBody = "{\"ok\":true}";
        call.HasResponseBody = true;
        var hub = new DebugHub();
        hub.UpsertNetwork(call);
        var detail = hub.NetworkDetailJson(1);
        Assert.NotNull(detail);
        Assert.Equal("{\"ok\":true}", JsonNode.Parse(detail)!["responseBody"]!.GetValue<string>());

        var media = new NetworkCall();
        CdpProtocol.ApplyRequest(media, JsonNode.Parse("""
            {"requestId":"2","type":"Media","request":{"method":"GET","url":"http://cdn/seg.m4s"}}
            """)!);
        CdpProtocol.ApplyResponse(media, JsonNode.Parse("""
            {"type":"Media","response":{"status":200,"mimeType":"video/mp4"}}
            """)!);
        CdpProtocol.ApplyFinished(media, JsonNode.Parse("""
            {"encodedDataLength":5000000,"timestamp":2}
            """)!);
        Assert.Equal("media", media.Type);
        Assert.False(CdpProtocol.ShouldFetchBody(media));
    }

    [Fact]
    public void HeapDiffGroupsConstructorsFromSnapshotMeta()
    {
        var first = HeapSummary.Summarize(Snapshot(("Array", 2, 110), ("Object", 1, 40)));
        var second = HeapSummary.Summarize(Snapshot(("Array", 1, 20), ("Object", 1, 80), ("Leak", 4, 50)));
        var diff = HeapSummary.Diff(first, second, "engine");

        Assert.Equal(3, diff.Classes);
        Assert.Equal(150, diff.TotalSelf);
        Assert.True(diff.HasPrevious);
        Assert.Contains(diff.Retained, row => row.Name == "Object" && row.SelfSize == 80);
        Assert.Contains(diff.Grew, row => row.Name == "Object" && row.SizeDelta == 40);
        Assert.Contains(diff.Grew, row => row.Name == "Leak" && row.CountDelta == 4);
        Assert.Contains(diff.Freed, row => row.Name == "Array" && row.CountDelta == -1);
        Assert.DoesNotContain(diff.Grew, row => row.Name == "Array");

        var unnamed = HeapSummary.Summarize("""
            {"snapshot":{"meta":{"node_fields":["type","name","self_size"],"node_types":[["hidden","object"]]}},"nodes":[1,0,12],"strings":[""]}
            """);
        Assert.True(unnamed.ContainsKey("(object)"));
        Assert.Equal(12, unnamed["(object)"].SelfSize);
    }

    [Fact]
    public void ShimConsoleIsKeptUntilEngineCdpOpens()
    {
        var hub = new DebugHub();
        hub.BeginSession("tizen-2018", "Samsung Tizen 4.0 (2018)", "chromium-56", true, null, false);
        hub.AddShim("""{"kind":"console","level":"log","text":"tylko-shim","url":"http://tv/","line":3}""");
        hub.SetCdp("engine", "open");
        hub.AddShim("""{"kind":"console","level":"error","text":"po-cdp","url":"http://tv/","line":4}""");

        using var subscription = hub.Subscribe(_ => { }, out var hello);
        Assert.Contains("tylko-shim", hello);
        Assert.DoesNotContain("po-cdp", hello);
        Assert.Contains("\"level\":\"info\"", hello);
    }

    [Fact]
    public void ShimPostsEveryConsoleLevelToTheDebugEndpoint()
    {
        var shim = File.ReadAllText(Path.Combine(RepoPaths.Root(), "shims", "platform.js"));
        Assert.Contains("function postDebug(", shim);
        Assert.Contains("/__emulator/debug", shim);
        Assert.Contains("wrapConsole(\"log\")", shim);
        Assert.Contains("wrapConsole(\"info\")", shim);
        Assert.Contains("wrapConsole(\"debug\")", shim);
        Assert.DoesNotContain("=>", shim);
    }

    [Fact]
    public void CrashPageIsNotALivingEngine()
    {
        Assert.True(CdpProtocol.IsDeadPage("chrome-error://chromewebdata/"));
        Assert.True(CdpProtocol.IsDeadPage("chrome://crash"));
        Assert.False(CdpProtocol.IsDeadPage("http://127.0.0.1:47900/"));
        Assert.False(CdpProtocol.IsDeadPage(""));
    }

    [Fact]
    public void HeapStatsKeepARunningTotalAcrossFragments()
    {
        var fragments = new Dictionary<int, (long Count, long Size)>();
        var first = CdpProtocol.ApplyHeapStats(JsonNode.Parse("[0, 10, 1000, 1, 4, 400]"), fragments);
        Assert.Equal(14, first.Count);
        Assert.Equal(1400, first.Size);
        var collected = CdpProtocol.ApplyHeapStats(JsonNode.Parse("[0, 2, 200]"), fragments);
        Assert.Equal(6, collected.Count);
        Assert.Equal(600, collected.Size);
    }

    [Fact]
    public void OldGcIsChromiumBefore62()
    {
        Assert.True(CdpProtocol.UsesOldGc("chromium-47"));
        Assert.True(CdpProtocol.UsesOldGc("chromium-53"));
        Assert.True(CdpProtocol.UsesOldGc("chromium-56"));
        Assert.False(CdpProtocol.UsesOldGc("chromium-62"));
        Assert.False(CdpProtocol.UsesOldGc("chromium-63"));
        Assert.False(CdpProtocol.UsesOldGc("chromium-85"));
        Assert.False(CdpProtocol.UsesOldGc("electron-10"));
    }

    [Fact]
    public void StreamedSnapshotSkipsEdges()
    {
        var json = Snapshot(("Array", 2, 110), ("Object", 1, 40));
        var withEdges = json.Replace("],\"strings\"", "],\"edges\":[9,8,7,6],\"strings\"", StringComparison.Ordinal);
        var path = Path.Combine(Path.GetTempPath(), "heap-" + Guid.NewGuid().ToString("N") + ".heapsnapshot");
        File.WriteAllText(path, withEdges);
        try
        {
            var fromFile = HeapSummary.SummarizeFile(path);
            var fromText = HeapSummary.Summarize(json);
            Assert.Equal(fromText["Array"].Count, fromFile["Array"].Count);
            Assert.Equal(fromText["Array"].SelfSize, fromFile["Array"].SelfSize);
            Assert.Equal(40, fromFile["Object"].SelfSize);
        }

        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StringAndNumberNodesGroupByTypeNotByText()
    {
        const string json = """
            {"snapshot":{"meta":{"node_fields":["self_size","name","type"],"node_types":[["hidden","object","string","number"]]}},"nodes":[500,1,2,100,2,2,40,3,1,200,4,1],"strings":["","https://cdn.example/film.m3u8","1792886255872","Array","system / JSArrayBufferData"]}
            """;
        var summary = HeapSummary.Summarize(json);
        Assert.Equal(2, summary["string"].Count);
        Assert.Equal(600, summary["string"].SelfSize);
        Assert.Equal(40, summary["Array"].SelfSize);
        Assert.Equal(200, summary["system / JSArrayBufferData"].SelfSize);
        Assert.False(summary.ContainsKey("https://cdn.example/film.m3u8"));
        Assert.False(summary.ContainsKey("1792886255872"));
    }

    [Fact]
    public void MemoryProbeStaysEs5()
    {
        Assert.DoesNotContain("=>", CdpProtocol.MemoryExpression);
        Assert.DoesNotContain("let ", CdpProtocol.MemoryExpression);
        Assert.DoesNotContain("const ", CdpProtocol.MemoryExpression);
        Assert.Contains("performance.memory", CdpProtocol.MemoryExpression);
    }

    [Fact]
    public async Task DebugPostReachesSubscribers()
    {
        var root = RepoPaths.Root();
        var directory = Path.Combine(Path.GetTempPath(), "tv-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), "<head></head>");
        var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "tizen-2018");
        using var server = new AppServer();
        string? body = null;
        server.DebugReported += text => body = text;
        server.Start(root, directory, profile, pointerEnabled: false, debug: true);
        using var client = new HttpClient();
        var response = await client.PostAsync(
            "http://127.0.0.1:" + server.Port + "/__emulator/debug",
            new StringContent("""{"kind":"console","level":"log","text":"z-endpointu"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("z-endpointu", body);
    }

    [Fact]
    public async Task NewSocketReceivesBufferedConsole()
    {
        var ui = Path.Combine(RepoPaths.Root(), "debug-ui");
        await using var server = await DebugServer.StartAsync(ui, 0);
        Assert.True(server.Hub.AddConsole(new ConsoleLine { Level = "error", Text = "z-bufora", Source = "engine", Url = "http://tv/app.js", Line = 8 }));

        using var http = new HttpClient();
        var page = await http.GetStringAsync("http://127.0.0.1:" + server.Port + "/");
        Assert.Contains("Konsola", page);
        Assert.Contains("app.js", page);

        using var socket = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + server.Port + "/ws"), cts.Token);
        var hello = await ReceiveText(socket, cts.Token);
        Assert.Contains("\"type\":\"hello\"", hello);
        Assert.Contains("z-bufora", hello);

        await SendText(socket, """{"action":"snapshot","target":"engine"}""", cts.Token);
        var error = await ReceiveText(socket, cts.Token);
        Assert.Contains("heap-error", error);
        Assert.Contains("CDP nie jest podłączone.", error);
    }

    private static string Snapshot(params (string Name, int Count, long Size)[] groups)
    {
        var strings = new List<string> { "" };
        var nodes = new List<long>();
        var id = 1L;
        foreach (var group in groups)
        {
            var nameIndex = strings.IndexOf(group.Name);
            if (nameIndex < 0)
            {
                nameIndex = strings.Count;
                strings.Add(group.Name);
            }

            var each = group.Count == 0 ? 0 : group.Size / group.Count;
            var remainder = group.Count == 0 ? 0 : group.Size % group.Count;
            for (var i = 0; i < Math.Max(group.Count, 1); i++)
            {
                if (group.Count == 0)
                    break;
                nodes.Add(each + (i == 0 ? remainder : 0));
                nodes.Add(nameIndex);
                nodes.Add(1);
                nodes.Add(id++);
                nodes.Add(0);
            }
        }

        var builder = new StringBuilder();
        builder.Append("""{"snapshot":{"meta":{"node_fields":["self_size","name","type","id","edge_count"],"node_types":[["hidden","object"]]}},"nodes":[""");
        for (var i = 0; i < nodes.Count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(nodes[i]);
        }

        builder.Append("""],"strings":[""");
        for (var i = 0; i < strings.Count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append('"').Append(strings[i]).Append('"');
        }

        builder.Append("]}");
        return builder.ToString();
    }

    private static async Task SendText(ClientWebSocket socket, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string> ReceiveText(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
