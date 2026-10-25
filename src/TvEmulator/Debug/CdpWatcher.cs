using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace TvEmulator.Debug;

public sealed class CdpWatcher : IDisposable
{
    private readonly DebugHub _hub;
    private readonly bool _video;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<CdpPage> _pages = new();
    private readonly object _gate = new();
    private System.Threading.Timer? _lossTimer;
    private int _disposed;
    private int _engineWasOpen;
    private int _lossAnnounced;

    public event Action? EngineLost;

    private CdpWatcher(DebugHub hub, bool video)
    {
        _hub = hub;
        _video = video;
    }

    public static CdpWatcher Start(DebugHub hub, bool video)
    {
        var watcher = new CdpWatcher(hub, video);
        hub.SnapshotRequested += watcher.OnSnapshot;
        _ = watcher.LoopAsync();
        return watcher;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        CancelLoss();
        _hub.SnapshotRequested -= OnSnapshot;
        _cts.Cancel();
        lock (_gate)
        {
            foreach (var page in _pages)
                page.Dispose();
            _pages.Clear();
        }
    }

    private async Task LoopAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        while (!_cts.IsCancellationRequested)
        {
            await ProbeAsync(http, 9223, "engine");
            if (_video)
                await ProbeAsync(http, 9229, "video");
            try
            {
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProbeAsync(HttpClient http, int port, string source)
    {
        if (_cts.IsCancellationRequested)
            return;
        string? json = null;
        foreach (var path in new[] { "/json/list", "/json" })
        {
            try
            {
                json = await http.GetStringAsync("http://127.0.0.1:" + port + path, _cts.Token);
                if (json.TrimStart().StartsWith('['))
                    break;
                json = null;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                json = null;
            }
        }

        if (json is null)
        {
            Refresh(source);
            return;
        }

        List<CdpTarget> targets;
        try
        {
            targets = CdpProtocol.ReadTargets(json);
        }
        catch (Exception)
        {
            Refresh(source);
            return;
        }

        lock (_gate)
        {
            foreach (var target in targets)
            {
                if (_pages.Any(page => page.Source == source && page.TargetId == target.Id))
                    continue;
                var crashed = source == "engine" ? (Action?)AnnounceLoss : null;
                var page = new CdpPage(_hub, source, target, _cts.Token, Refresh, crashed);
                _pages.Add(page);
                _ = page.RunAsync().ContinueWith(task =>
                {
                    _ = task.Exception;
                    lock (_gate)
                        _pages.Remove(page);
                    Refresh(source);
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        ElectPrimary(source);
        Refresh(source);
    }

    private void Refresh(string source)
    {
        if (_disposed == 1)
            return;
        bool open;
        lock (_gate)
            open = _pages.Any(page => page.Source == source && page.CountsAsPage && page.IsReady && !CdpProtocol.IsDeadPage(page.Url));
        _hub.SetCdp(source, open ? "open" : "connecting");
        if (source == "engine")
            NoteEngine(open);
    }

    private void NoteEngine(bool open)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return;
        if (open)
        {
            Interlocked.Exchange(ref _engineWasOpen, 1);
            Interlocked.Exchange(ref _lossAnnounced, 0);
            CancelLoss();
            return;
        }

        if (Volatile.Read(ref _engineWasOpen) == 0)
            return;
        lock (_gate)
        {
            if (_lossTimer is not null || _disposed == 1)
                return;
            _lossTimer = new System.Threading.Timer(_ => AnnounceLoss(), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }
    }

    private void AnnounceLoss()
    {
        if (Volatile.Read(ref _disposed) == 1 || Volatile.Read(ref _engineWasOpen) == 0)
            return;
        CancelLoss();
        if (EngineOpen())
            return;
        if (Interlocked.Exchange(ref _lossAnnounced, 1) == 1)
            return;
        if (EngineOpen())
        {
            Interlocked.Exchange(ref _lossAnnounced, 0);
            return;
        }
        _hub.HeapError("engine", "Silnik przeglądarki padł. Sesja zostanie zamknięta, a pasek zadań wróci.");
        EngineLost?.Invoke();
    }

    private bool EngineOpen()
    {
        lock (_gate)
            return _pages.Any(page => page.Source == "engine" && page.CountsAsPage && page.IsReady && !CdpProtocol.IsDeadPage(page.Url));
    }

    private void CancelLoss()
    {
        System.Threading.Timer? timer;
        lock (_gate)
        {
            timer = _lossTimer;
            _lossTimer = null;
        }

        timer?.Dispose();
    }

    private void ElectPrimary(string source)
    {
        lock (_gate)
        {
            CdpPage? chosen = null;
            var rank = 0;
            foreach (var page in _pages)
            {
                if (page.Source != source)
                    continue;
                var score = Rank(page, source);
                if (score > rank)
                {
                    rank = score;
                    chosen = page;
                }
            }

            foreach (var page in _pages)
            {
                if (page.Source == source)
                    page.PollMemory = page == chosen && rank > 0;
            }
        }
    }

    private static int Rank(CdpPage page, string source)
    {
        if (!page.CountsAsPage || CdpProtocol.IsDeadPage(page.Url))
            return 0;
        if (source == "engine" && page.Url.Contains("videoplane.html", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (page.Type == "iframe")
            return 2;
        return 3;
    }

    private void OnSnapshot(string source)
    {
        if (_disposed == 1)
            return;
        CdpPage? page;
        lock (_gate)
        {
            page = null;
            var rank = 0;
            foreach (var candidate in _pages)
            {
                if (candidate.Source != source || !candidate.IsReady)
                    continue;
                var score = Rank(candidate, source);
                if (score > rank)
                {
                    rank = score;
                    page = candidate;
                }
            }
        }

        if (page is null)
        {
            _hub.HeapError(source, "CDP nie jest podłączone.");
            return;
        }

        _ = page.SnapshotAsync();
    }

    private sealed class CdpPage : IDisposable
    {
        private readonly DebugHub _hub;
        private readonly CancellationTokenSource _cts;
        private readonly Action<string> _refresh;
        private readonly Action? _engineCrashed;
        private readonly Dictionary<string, NetworkCall> _calls = new(StringComparer.Ordinal);
        private readonly Channel<JsonObject> _events = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly SemaphoreSlim _heap = new(1, 1);
        private readonly object _heapWrite = new();
        private readonly Dictionary<int, (long Count, long Size)> _fragments = new();
        private FileStream? _heapFile;
        private string? _heapPath;
        private bool _trackingAttempted;
        private long _lastGcTick;
        private long _lastGcCount = -1;
        private long _lastGcSize = -1;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
        private ClientWebSocket? _socket;
        private int _nextId;

        public CdpPage(DebugHub hub, string source, CdpTarget target, CancellationToken parent, Action<string> refresh, Action? engineCrashed)
        {
            _hub = hub;
            Source = source;
            TargetId = target.Id;
            Type = target.Type;
            Url = target.Url;
            WebSocketUrl = target.WebSocketUrl;
            Port = PortOf(target.WebSocketUrl);
            _refresh = refresh;
            _engineCrashed = engineCrashed;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        }

        public string Source { get; }
        public string TargetId { get; }
        public string Type { get; }
        public string Url { get; }
        public string WebSocketUrl { get; }
        public int Port { get; }
        private volatile bool _pollMemory;

        public bool IsReady { get; private set; }
        public bool CountsAsPage => Type is "page" or "other" or "iframe";
        public bool PollMemory
        {
            get => _pollMemory;
            set => _pollMemory = value;
        }

        public async Task RunAsync()
        {
            _socket = await OpenAsync(withOrigin: true) ?? await OpenAsync(withOrigin: false);
            if (_socket is null)
                throw new InvalidOperationException("Nie udało się podłączyć CDP " + WebSocketUrl);
            var receive = ReceiveLoopAsync();
            var process = ProcessAsync();
            Task? poll = null;
            try
            {
                var console = await CallAsync("Console.enable", null, TimeSpan.FromSeconds(4), ignoreError: true);
                var runtime = await CallAsync("Runtime.enable", null, TimeSpan.FromSeconds(4), ignoreError: true);
                await CallAsync("Network.enable", null, TimeSpan.FromSeconds(4), ignoreError: true);
                await CallAsync("Log.enable", null, TimeSpan.FromSeconds(4), ignoreError: true);
                await CallAsync("HeapProfiler.enable", null, TimeSpan.FromSeconds(4), ignoreError: true);
                IsReady = console is not null || runtime is not null;
                _refresh(Source);
                poll = PollAsync();
                await receive;
            }
            finally
            {
                try { _cts.Cancel(); } catch (ObjectDisposedException) { }
                _events.Writer.TryComplete();
                try { await receive; } catch (Exception) { }
                try { await process; } catch (Exception) { }
                if (poll is not null)
                {
                    try { await poll; } catch (Exception) { }
                }
            }
        }

        public async Task SnapshotAsync()
        {
            if (!await _heap.WaitAsync(0, _cts.Token))
            {
                _hub.HeapError(Source, "Zrzut sterty już trwa.");
                return;
            }

            var published = false;
            try
            {
                OpenHeapFile();
                await CallAsync("HeapProfiler.enable", null, TimeSpan.FromSeconds(4), ignoreError: true);
                await CallAsync("HeapProfiler.takeHeapSnapshot", new JsonObject { ["reportProgress"] = true }, TimeSpan.FromSeconds(90), ignoreError: false);
                var path = CloseHeapFile();
                if (path is null || !File.Exists(path) || new FileInfo(path).Length == 0)
                    throw new InvalidOperationException("Silnik nie odesłał zrzutu sterty.");
                var summary = HeapSummary.SummarizeFile(path);
                _hub.PublishHeap(Source, summary, path);
                published = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _hub.HeapError(Source, ClosedSocket(ex) ? "Zrzut sterty urwał silnik przeglądarki." : ex.Message);
            }
            finally
            {
                if (!published)
                    DiscardHeapFile();
                _heap.Release();
            }
        }

        private void OpenHeapFile()
        {
            lock (_heapWrite)
            {
                try
                {
                    _heapFile?.Dispose();
                }
                catch (Exception)
                {
                }

                _heapFile = null;
                _heapPath = _hub.NextHeapPath(Source);
                _heapFile = new FileStream(_heapPath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
            }
        }

        private string? CloseHeapFile()
        {
            lock (_heapWrite)
            {
                try
                {
                    _heapFile?.Flush();
                }
                catch (Exception)
                {
                }

                try
                {
                    _heapFile?.Dispose();
                }
                catch (Exception)
                {
                }

                _heapFile = null;
                return _heapPath;
            }
        }

        private void DiscardHeapFile()
        {
            lock (_heapWrite)
                DiscardHeapLocked();
        }

        private void DiscardHeapLocked()
        {
            try
            {
                _heapFile?.Dispose();
            }
            catch (Exception)
            {
            }

            _heapFile = null;
            if (_heapPath is not null)
            {
                try
                {
                    if (File.Exists(_heapPath))
                        File.Delete(_heapPath);
                }
                catch (Exception)
                {
                }
            }

            _heapPath = null;
        }

        private void WriteHeapChunk(string chunk)
        {
            if (chunk.Length == 0)
                return;
            var bytes = Encoding.UTF8.GetBytes(chunk);
            lock (_heapWrite)
                _heapFile?.Write(bytes, 0, bytes.Length);
        }

        private void NoteGc(JsonNode? stats)
        {
            var total = CdpProtocol.ApplyHeapStats(stats, _fragments);
            var now = Environment.TickCount64;
            var drop = _lastGcCount >= 0 && (total.Count < _lastGcCount || total.Size < _lastGcSize);
            if (!drop && _lastGcTick != 0 && now - _lastGcTick < 500)
                return;
            _lastGcTick = now;
            _lastGcCount = total.Count;
            _lastGcSize = total.Size;
            _hub.AddGc(new HeapGcSample
            {
                Source = Source,
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Count = total.Count,
                Size = total.Size
            });
        }

        private static bool ClosedSocket(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is WebSocketException)
                    return true;
                if (current.Message.Contains("zamknięte", StringComparison.OrdinalIgnoreCase)
                    || current.Message.Contains("closed", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _socket?.Abort();
            }
            catch (Exception)
            {
            }
        }

        private async Task<ClientWebSocket?> OpenAsync(bool withOrigin)
        {
            var socket = new ClientWebSocket();
            if (withOrigin)
                socket.Options.SetRequestHeader("Origin", "http://127.0.0.1:" + Port);
            try
            {
                await socket.ConnectAsync(new Uri(WebSocketUrl), _cts.Token);
                return socket;
            }
            catch (Exception) when (!_cts.IsCancellationRequested)
            {
                socket.Dispose();
                return null;
            }
        }

        private async Task PollAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, _cts.Token);
                }
                catch (Exception)
                {
                    break;
                }

                if (!PollMemory || !IsReady)
                    continue;
                if (!_trackingAttempted)
                {
                    _trackingAttempted = true;
                    await CallAsync("HeapProfiler.startTrackingHeapObjects", null, TimeSpan.FromSeconds(4), ignoreError: true);
                }

                try
                {
                    var result = await CallAsync("Runtime.evaluate", new JsonObject
                    {
                        ["expression"] = CdpProtocol.MemoryExpression,
                        ["returnByValue"] = true
                    }, TimeSpan.FromSeconds(3), ignoreError: true);
                    var sample = CdpProtocol.ReadMemory(result, Source);
                    if (sample is not null)
                        _hub.AddMemory(sample);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                }
            }
        }

        private async Task ProcessAsync()
        {
            await foreach (var message in _events.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    Handle(message);
                }
                catch (Exception)
                {
                }
            }
        }

        private void Handle(JsonObject message)
        {
            var method = message["method"]?.ToString();
            if (method is null || message["params"] is not JsonNode param)
                return;
            var console = CdpProtocol.ReadConsole(method, param, Source);
            if (console is not null)
            {
                _hub.AddConsole(console);
                return;
            }

            switch (method)
            {
                case "Network.requestWillBeSent":
                    OnRequest(param);
                    break;
                case "Network.responseReceived":
                    OnKnown(param, CdpProtocol.ApplyResponse);
                    break;
                case "Network.loadingFinished":
                    OnFinished(param);
                    break;
                case "Network.loadingFailed":
                    OnKnown(param, CdpProtocol.ApplyFailed);
                    break;
                case "Network.webSocketCreated":
                    OnSocketCreated(param);
                    break;
                case "Network.webSocketFrameSent":
                    OnFrame(param, sent: true);
                    break;
                case "Network.webSocketFrameReceived":
                    OnFrame(param, sent: false);
                    break;
            }
        }

        private void OnRequest(JsonNode param)
        {
            var requestId = CdpProtocol.ReadString(param["requestId"]);
            if (requestId.Length == 0)
                return;
            if (param["redirectResponse"] is not null && _calls.TryGetValue(requestId, out var previous))
            {
                CdpProtocol.ApplyRedirect(previous, param);
                _hub.UpsertNetwork(previous);
                _calls.Remove(requestId);
            }

            var call = new NetworkCall
            {
                Id = _hub.NextNetworkId(),
                Source = Source,
                CdpId = requestId,
                Time = DateTime.Now.ToString("HH:mm:ss.fff")
            };
            CdpProtocol.ApplyRequest(call, param);
            _calls[requestId] = call;
            _hub.UpsertNetwork(call);
        }

        private void OnKnown(JsonNode param, Action<NetworkCall, JsonNode> apply)
        {
            var requestId = CdpProtocol.ReadString(param["requestId"]);
            if (!_calls.TryGetValue(requestId, out var call))
                return;
            apply(call, param);
            _hub.UpsertNetwork(call);
        }

        private void OnFinished(JsonNode param)
        {
            var requestId = CdpProtocol.ReadString(param["requestId"]);
            if (!_calls.TryGetValue(requestId, out var call))
                return;
            CdpProtocol.ApplyFinished(call, param);
            _hub.UpsertNetwork(call);
            if (!CdpProtocol.ShouldFetchBody(call))
            {
                if (call.Type is "image" or "media" or "font")
                {
                    call.BodySkipped = true;
                    call.BodySkipReason = "Treść pominięta (obraz, media albo font).";
                    _hub.UpsertNetwork(call);
                }

                return;
            }

            _ = FetchBodyAsync(call);
        }

        private async Task FetchBodyAsync(NetworkCall call)
        {
            try
            {
                var result = await CallAsync("Network.getResponseBody", new JsonObject { ["requestId"] = call.CdpId }, TimeSpan.FromSeconds(8), ignoreError: true);
                if (result is null)
                {
                    call.BodySkipped = true;
                    if (call.BodySkipReason.Length == 0)
                        call.BodySkipReason = "Brak treści odpowiedzi.";
                }
                else
                {
                    var read = CdpProtocol.ReadBody(result);
                    if (read.Truncated)
                        call.BodyTruncated = true;
                    if (read.Skip.Length > 0)
                    {
                        call.BodySkipped = true;
                        call.BodySkipReason = read.Skip;
                    }
                    else
                    {
                        call.ResponseBody = read.Text;
                        call.HasResponseBody = true;
                    }
                }

                _hub.UpsertNetwork(call);
            }
            catch (Exception)
            {
            }
        }

        private void OnSocketCreated(JsonNode param)
        {
            var requestId = CdpProtocol.ReadString(param["requestId"]);
            if (requestId.Length == 0)
                return;
            if (!_calls.TryGetValue(requestId, out var call))
            {
                call = new NetworkCall
                {
                    Id = _hub.NextNetworkId(),
                    Source = Source,
                    CdpId = requestId,
                    Time = DateTime.Now.ToString("HH:mm:ss.fff"),
                    Url = CdpProtocol.ReadString(param["url"])
                };
                _calls[requestId] = call;
            }

            call.Type = "websocket";
            call.RawType = "WebSocket";
            if (call.Url.Length == 0)
                call.Url = CdpProtocol.ReadString(param["url"]);
            _hub.UpsertNetwork(call);
        }

        private void OnFrame(JsonNode param, bool sent)
        {
            var requestId = CdpProtocol.ReadString(param["requestId"]);
            if (!_calls.TryGetValue(requestId, out var call))
                return;
            CdpProtocol.AppendFrame(call, param, sent);
            _hub.UpsertNetwork(call);
        }

        private async Task ReceiveLoopAsync()
        {
            var socket = _socket;
            if (socket is null)
                return;
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (!_cts.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, _cts.Token);
                }
                catch (Exception)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                Accept(text);
            }
        }

        private void Accept(string text)
        {
            if (text.Contains("addHeapSnapshotChunk", StringComparison.Ordinal) && TryExtractChunk(text, out var chunk))
            {
                WriteHeapChunk(chunk);
                return;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                return;
            }

            if (node is null)
                return;
            if (node["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var id) && _pending.TryRemove(id, out var pending))
            {
                if (node["error"] is JsonNode error)
                    pending.TrySetException(new InvalidOperationException(error["message"]?.ToString() ?? "CDP error"));
                else
                    pending.TrySetResult(node["result"] as JsonObject ?? new JsonObject());
                return;
            }

            var method = node["method"]?.ToString();
            if (method == "HeapProfiler.addHeapSnapshotChunk")
            {
                WriteHeapChunk(CdpProtocol.ReadString(node["params"]?["chunk"]));
                return;
            }

            if (method == "HeapProfiler.heapStatsUpdate")
            {
                NoteGc(node["params"]?["statsUpdate"]);
                return;
            }

            if (method == "Inspector.targetCrashed")
            {
                _engineCrashed?.Invoke();
                return;
            }

            if (method == "Inspector.detached")
            {
                var reason = CdpProtocol.ReadString(node["params"]?["reason"]);
                if (reason.Contains("crash", StringComparison.OrdinalIgnoreCase) || reason.Contains("gone", StringComparison.OrdinalIgnoreCase))
                    _engineCrashed?.Invoke();
                return;
            }

            if (method == "HeapProfiler.reportHeapSnapshotProgress")
            {
                var done = ReadLoose(node["params"]?["done"]);
                var total = ReadLoose(node["params"]?["total"]);
                var finished = node["params"]?["finished"] is JsonValue finishedValue && finishedValue.TryGetValue<bool>(out var flag) && flag;
                _hub.HeapProgress(Source, done, total, finished);
                return;
            }

            if (node is JsonObject obj && method is not null)
                _events.Writer.TryWrite(obj);
        }

        private static bool TryExtractChunk(string text, out string chunk)
        {
            chunk = "";
            const string marker = "\"chunk\":\"";
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                return false;
            var builder = new StringBuilder();
            var i = at + marker.Length;
            while (i < text.Length)
            {
                var current = text[i++];
                if (current == '"')
                {
                    chunk = builder.ToString();
                    return true;
                }

                if (current != '\\')
                {
                    builder.Append(current);
                    continue;
                }

                if (i >= text.Length)
                    return false;
                var escaped = text[i++];
                switch (escaped)
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(escaped);
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (i + 4 > text.Length)
                            return false;
                        builder.Append((char)Convert.ToInt32(text.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default:
                        builder.Append(escaped);
                        break;
                }
            }

            return false;
        }

        private async Task<JsonObject?> CallAsync(string method, JsonNode? args, TimeSpan timeout, bool ignoreError)
        {
            var id = Interlocked.Increment(ref _nextId);
            var pending = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = pending;
            var payload = new JsonObject { ["id"] = id, ["method"] = method };
            if (args is not null)
                payload["params"] = args;
            try
            {
                await SendAsync(payload.ToJsonString());
                return await pending.Task.WaitAsync(timeout, _cts.Token);
            }
            catch (Exception)
            {
                _pending.TryRemove(id, out _);
                if (ignoreError)
                    return null;
                throw;
            }
        }

        private async Task SendAsync(string json)
        {
            var socket = _socket;
            if (socket is null || socket.State != WebSocketState.Open)
                throw new InvalidOperationException("Gniazdo CDP jest zamknięte.");
            var bytes = Encoding.UTF8.GetBytes(json);
            await _send.WaitAsync(_cts.Token);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
            }
            finally
            {
                _send.Release();
            }
        }

        private static int PortOf(string webSocketUrl)
        {
            if (Uri.TryCreate(webSocketUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
            return 9223;
        }

        private static long ReadLoose(JsonNode? node)
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<long>(out var whole))
                    return whole;
                if (value.TryGetValue<double>(out var real))
                    return (long)real;
            }

            return 0;
        }
    }
}
