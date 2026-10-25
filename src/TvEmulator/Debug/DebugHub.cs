using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TvEmulator.Debug;

public sealed class DebugHub
{
    public const int ConsoleLimit = 2000;
    public const int NetworkLimit = 300;
    public const int LogLimit = 2000;
    public const int MemoryLimit = 90;
    private const int BodyBudget = 8_000_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _gate = new();
    private readonly List<Action<string>> _listeners = new();
    private readonly List<ConsoleLine> _console = new();
    private readonly List<NetworkCall> _network = new();
    private readonly List<string> _logs = new();
    private readonly Dictionary<string, List<MemorySample>> _memory = new(StringComparer.Ordinal)
    {
        ["engine"] = new(),
        ["video"] = new()
    };
    private readonly Dictionary<string, List<HeapGcSample>> _gc = new(StringComparer.Ordinal)
    {
        ["engine"] = new(),
        ["video"] = new()
    };
    private readonly Dictionary<string, HeapReport> _heap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, HeapClass>> _previousHeap = new(StringComparer.Ordinal);
    private readonly Queue<(long Tick, string Key)> _recent = new();
    private readonly DebugStatus _status = new();
    private long _consoleIds;
    private long _networkIds;

    public event Action<string>? SnapshotRequested;

    public long NextNetworkId() => Interlocked.Increment(ref _networkIds);

    public void SetEndpoint(int port, IReadOnlyList<string> urls)
    {
        lock (_gate)
        {
            _status.Port = port;
            _status.Urls = urls.ToList();
        }
    }

    public void BeginSession(string profileId, string profileLabel, string engineId, bool diagnostics, string? logFile, bool video)
    {
        string json;
        Action<string>[] listeners;
        lock (_gate)
        {
            _console.Clear();
            _network.Clear();
            _logs.Clear();
            _memory["engine"].Clear();
            _memory["video"].Clear();
            _gc["engine"].Clear();
            _gc["video"].Clear();
            _heap.Clear();
            _previousHeap.Clear();
            _recent.Clear();
            _status.Active = true;
            _status.Diagnostics = diagnostics;
            _status.ProfileId = profileId;
            _status.Profile = profileLabel;
            _status.Engine = engineId;
            _status.LogFile = logFile ?? "";
            _status.Cdp = new CdpFlags
            {
                Engine = diagnostics ? "connecting" : "off",
                Video = diagnostics && video ? "connecting" : "off"
            };
            json = Serialize(new { type = "reset", status = _status });
            listeners = _listeners.ToArray();
        }

        Dispatch(listeners, json);
    }

    public void EndSession()
    {
        string json;
        Action<string>[] listeners;
        lock (_gate)
        {
            _status.Active = false;
            _status.Diagnostics = false;
            _status.Cdp.Engine = "off";
            _status.Cdp.Video = "off";
            json = Serialize(new { type = "status", status = _status });
            listeners = _listeners.ToArray();
        }

        Dispatch(listeners, json);
    }

    public void SetCdp(string source, string state)
    {
        if (source is not ("engine" or "video") || state is not ("off" or "connecting" or "open"))
            return;
        string? json = null;
        Action<string>[] listeners = [];
        lock (_gate)
        {
            if (!_status.Active || !_status.Diagnostics)
                return;
            var flags = _status.Cdp;
            var current = source == "engine" ? flags.Engine : flags.Video;
            if (current == state)
                return;
            if (source == "engine")
                flags.Engine = state;
            else
                flags.Video = state;
            json = Serialize(new { type = "status", status = _status });
            listeners = _listeners.ToArray();
        }

        if (json is not null)
            Dispatch(listeners, json);
    }

    public bool AddConsole(ConsoleLine line)
    {
        string? json = null;
        Action<string>[] listeners = [];
        lock (_gate)
        {
            if (IsDuplicate(line))
                return false;
            if (line.Text.Length > 16_000)
                line.Text = line.Text.Substring(0, 16_000);
            line.Id = ++_consoleIds;
            if (line.Time.Length == 0)
                line.Time = Stamp();
            _console.Add(line);
            Trim(_console, ConsoleLimit);
            json = Serialize(new { type = "console", entry = line });
            listeners = _listeners.ToArray();
        }

        Dispatch(listeners, json);
        return true;
    }

    public void AddShim(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        if (!string.Equals(CdpProtocol.ReadString(node?["kind"]), "console", StringComparison.Ordinal))
            return;
        var level = CdpProtocol.MapLevel(CdpProtocol.ReadString(node?["level"]));
        var lineNumber = 0;
        if (node?["line"] is JsonValue lineValue && lineValue.TryGetValue<int>(out var parsed))
            lineNumber = parsed;
        var line = new ConsoleLine
        {
            Source = "shim",
            Level = level,
            Text = CdpProtocol.ReadString(node?["text"]),
            Url = CdpProtocol.ReadString(node?["url"]),
            Line = lineNumber,
            Stack = CdpProtocol.ReadString(node?["stack"])
        };

        string? payload = null;
        Action<string>[] listeners = [];
        lock (_gate)
        {
            if (_status.Cdp.Engine == "open")
                return;
            if (IsDuplicate(line))
                return;
            if (line.Text.Length > 16_000)
                line.Text = line.Text.Substring(0, 16_000);
            line.Id = ++_consoleIds;
            line.Time = Stamp();
            _console.Add(line);
            Trim(_console, ConsoleLimit);
            payload = Serialize(new { type = "console", entry = line });
            listeners = _listeners.ToArray();
        }

        if (payload is not null)
            Dispatch(listeners, payload);
    }

    public void AddLog(string message)
    {
        var single = message.Replace("\r", " ").Replace("\n", " ");
        if (single.Length > 4000)
            single = single.Substring(0, 4000) + " …";
        var line = Stamp() + " " + single;
        string json;
        Action<string>[] listeners;
        lock (_gate)
        {
            _logs.Add(line);
            Trim(_logs, LogLimit);
            json = Serialize(new { type = "log", entry = line });
            listeners = _listeners.ToArray();
        }

        Dispatch(listeners, json);
    }

    public void UpsertNetwork(NetworkCall call)
    {
        string json;
        Action<string>[] listeners;
        lock (_gate)
        {
            if (!_network.Contains(call))
            {
                _network.Add(call);
                while (_network.Count > NetworkLimit)
                    _network.RemoveAt(0);
            }

            TrimBodies();
            json = Serialize(new { type = "network", entry = Wire(call) });
            listeners = _listeners.ToArray();
        }

        Dispatch(listeners, json);
    }

    public void AddMemory(MemorySample sample)
    {
        if (sample.Source is not ("engine" or "video"))
            return;
        string json;
        Action<string>[] listeners;
        lock (_gate)
        {
            var list = _memory[sample.Source];
            list.Add(sample);
            Trim(list, MemoryLimit);
            json = Serialize(new { type = "memory", sample });
            listeners = _listeners.ToArray();
        }

        Dispatch(listeners, json);
    }

    public void AddGc(HeapGcSample sample)
    {
        if (sample.Source is not ("engine" or "video"))
            return;
        string json;
        Action<string>[] listeners;
        lock (_gate)
        {
            var list = _gc[sample.Source];
            list.Add(sample);
            Trim(list, 240);
            json = Serialize(new { type = "gc", sample });
            listeners = _listeners.ToArray();
        }

        Dispatch(listeners, json);
    }

    public string NextHeapPath(string source)
    {
        string directory;
        lock (_gate)
            directory = string.IsNullOrEmpty(_status.LogFile) ? "" : Path.GetDirectoryName(_status.LogFile) ?? "";
        if (directory.Length == 0)
            directory = Path.Combine(Path.GetTempPath(), "tv-emulator-heaps");
        Directory.CreateDirectory(directory);
        var safe = source is "engine" or "video" ? source : "heap";
        return Path.Combine(directory, "heap-" + safe + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".heapsnapshot");
    }

    public void PublishHeap(string source, IReadOnlyDictionary<string, HeapClass> current, string? filePath = null)
    {
        if (source is not ("engine" or "video"))
            return;
        string json;
        Action<string>[] listeners;
        lock (_gate)
        {
            _previousHeap.TryGetValue(source, out var previous);
            var report = HeapSummary.Diff(previous, current, source);
            report.FilePath = filePath;
            _previousHeap[source] = new Dictionary<string, HeapClass>(current, StringComparer.Ordinal);
            _heap[source] = report;
            json = Serialize(new { type = "heap", report });
            listeners = _listeners.ToArray();
        }

        Dispatch(listeners, json);
    }

    public void HeapProgress(string source, long done, long total, bool finished)
    {
        Emit(new { type = "heap-progress", source, done, total, finished });
    }

    public void HeapError(string source, string message)
    {
        Emit(new { type = "heap-error", source, message });
    }

    public void Clear(string channel)
    {
        if (channel is not ("console" or "network" or "log"))
            return;
        string json;
        Action<string>[] listeners;
        lock (_gate)
        {
            if (channel == "console")
                _console.Clear();
            else if (channel == "network")
                _network.Clear();
            else
                _logs.Clear();
            json = Serialize(new { type = "cleared", channel });
            listeners = _listeners.ToArray();
        }

        Dispatch(listeners, json);
    }

    public void RequestSnapshot(string source)
    {
        if (source is not ("engine" or "video"))
            return;
        Action<string>? handler;
        var open = false;
        lock (_gate)
        {
            handler = SnapshotRequested;
            open = source == "engine" ? _status.Cdp.Engine == "open" : _status.Cdp.Video == "open";
        }

        if (!open || handler is null)
        {
            HeapError(source, "CDP nie jest podłączone.");
            return;
        }

        handler(source);
    }

    public void HandleClient(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var action = node?["action"]?.ToString();
            if (action == "snapshot")
                RequestSnapshot(node?["target"]?.ToString() ?? "");
            else if (action == "clear")
                Clear(node?["channel"]?.ToString() ?? "");
        }
        catch (JsonException)
        {
        }
    }

    public string StatusJson()
    {
        lock (_gate)
            return Serialize(_status);
    }

    public string? NetworkDetailJson(long id)
    {
        lock (_gate)
        {
            var call = _network.FirstOrDefault(item => item.Id == id);
            if (call is null)
                return null;
            return Serialize(new
            {
                call.Id,
                call.Method,
                call.Url,
                call.Status,
                call.Mime,
                call.Type,
                call.RawType,
                call.Error,
                call.RequestHeaders,
                call.ResponseHeaders,
                call.RequestBody,
                call.ResponseBody,
                call.HasRequestBody,
                call.HasResponseBody,
                call.BodySkipped,
                call.BodySkipReason,
                call.BodyTruncated
            });
        }
    }

    public IDisposable Subscribe(Action<string> listener, out string hello)
    {
        lock (_gate)
        {
            hello = BuildHello();
            _listeners.Add(listener);
            return new Subscription(this, listener);
        }
    }

    public static IReadOnlyList<LanAddress> LanAddresses()
    {
        var list = new List<LanAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(address.Address))
                    continue;
                var bytes = address.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                    continue;
                list.Add(new LanAddress(nic.Name, address.Address.ToString()));
            }
        }

        return list;
    }

    private string BuildHello() => Serialize(new
    {
        type = "hello",
        status = _status,
        console = _console,
        network = _network.Select(Wire).ToArray(),
        log = _logs,
        memory = _memory,
        gc = _gc,
        heap = _heap
    });

    private static object Wire(NetworkCall call) => new
    {
        call.Id,
        call.Source,
        call.Method,
        call.Url,
        call.Type,
        call.RawType,
        call.Status,
        call.Mime,
        call.Error,
        call.Time,
        call.DurationMs,
        call.EncodedSize,
        call.HasRequestBody,
        call.HasResponseBody,
        call.BodySkipped,
        call.BodySkipReason,
        call.BodyTruncated
    };

    private bool IsDuplicate(ConsoleLine line)
    {
        var now = Environment.TickCount64;
        while (_recent.Count > 0 && now - _recent.Peek().Tick > 500)
            _recent.Dequeue();
        var key = line.Level + "\n" + line.Line + "\n" + line.Url + "\n" + (line.Text.Length > 500 ? line.Text.Substring(0, 500) : line.Text);
        foreach (var item in _recent)
        {
            if (item.Key == key)
                return true;
        }

        _recent.Enqueue((now, key));
        while (_recent.Count > 50)
            _recent.Dequeue();
        return false;
    }

    private void TrimBodies()
    {
        long total = 0;
        for (var i = _network.Count - 1; i >= 0; i--)
        {
            var call = _network[i];
            var size = (call.RequestBody?.Length ?? 0) + (call.ResponseBody?.Length ?? 0);
            if (total + size <= BodyBudget)
            {
                total += size;
                continue;
            }

            if (call.RequestBody is not null || call.ResponseBody is not null)
            {
                call.RequestBody = null;
                call.ResponseBody = null;
                call.HasRequestBody = false;
                call.HasResponseBody = false;
                call.BodySkipped = true;
                call.BodySkipReason = "Treść wyrzucona z bufora.";
            }
        }
    }

    private void Emit(object payload)
    {
        var json = Serialize(payload);
        Action<string>[] listeners;
        lock (_gate)
            listeners = _listeners.ToArray();
        Dispatch(listeners, json);
    }

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff");

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload, Json);

    private static void Trim<T>(List<T> list, int limit)
    {
        while (list.Count > limit)
            list.RemoveAt(0);
    }

    private static void Dispatch(Action<string>[] listeners, string json)
    {
        foreach (var listener in listeners)
        {
            try
            {
                listener(json);
            }
            catch (Exception)
            {
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private DebugHub? _hub;
        private readonly Action<string> _listener;

        public Subscription(DebugHub hub, Action<string> listener)
        {
            _hub = hub;
            _listener = listener;
        }

        public void Dispose()
        {
            var hub = Interlocked.Exchange(ref _hub, null);
            if (hub is null)
                return;
            lock (hub._gate)
                hub._listeners.Remove(_listener);
        }
    }
}

public readonly record struct LanAddress(string Name, string Address);
