using System.Text.Json;
using System.Text.Json.Nodes;

namespace TvEmulator.Services;

/// <summary>
/// Carries webapis.avplay calls from the page to the video plane and plane events back.
/// The page posts commands synchronously, so their order is the call order.
/// Events wait in a bounded queue that the page drains with a long poll.
/// </summary>
public sealed class AvplayBridge
{
    private const int MaxEvents = 500;

    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "open", "close", "prepare", "play", "pause", "stop", "jump", "seek", "display", "displayMethod",
        "property", "drm", "track", "suspend", "restore", "speed", "reset", "shade", "media"
    };

    private static readonly HashSet<string> QuietEvents = new(StringComparer.Ordinal)
    {
        "time", "buffering", "eme-message", "eme-updated", "eme-generated", "eme-cert", "mse-update"
    };

    private readonly object _gate = new();
    private readonly LinkedList<JsonObject> _events = new();
    private long _head;
    private bool _closed;

    public event Action<JsonObject>? Command;
    public event Action<string>? Trace;

    public JsonObject Accept(string body)
    {
        JsonObject? command;
        try
        {
            command = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            command = null;
        }

        var name = command?["cmd"]?.GetValueKind() == JsonValueKind.String ? (string?)command["cmd"] : null;
        if (command is null || name is null || !Commands.Contains(name))
        {
            Trace?.Invoke("avplay rejected " + Shorten(body, 200));
            return new JsonObject { ["ok"] = false, ["error"] = "InvalidValuesError" };
        }

        // A fade can send a shade frame on every animation frame. Do not flood the
        // session log (or expose arbitrary CSS values from the application).
        TraceCommand(name, command);
        Command?.Invoke(command);
        return new JsonObject { ["ok"] = true };
    }

    public bool Submit(JsonObject command)
    {
        var name = command["cmd"]?.GetValueKind() == JsonValueKind.String ? (string?)command["cmd"] : null;
        if (name is null || !Commands.Contains(name))
        {
            Trace?.Invoke("avplay rejected media");
            return false;
        }

        TraceCommand(name, command);
        Command?.Invoke(command);
        return true;
    }

    public void Publish(JsonObject evt)
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _head++;
            evt["seq"] = _head;
            _events.AddLast(evt);
            while (_events.Count > MaxEvents)
                _events.RemoveFirst();
            Monitor.PulseAll(_gate);
        }

        var type = evt["type"]?.GetValueKind() == JsonValueKind.String ? (string?)evt["type"] : null;
        if (type is not null && QuietEvents.Contains(type))
            Trace?.Invoke("avplay-event " + type + DescribeQuiet(evt));
        else if (type is not null)
            Trace?.Invoke("avplay-event " + Shorten(evt.ToJsonString(), 600));
    }

    /// <summary>
    /// Returns events newer than <paramref name="since"/>. A negative value only reports the head,
    /// so a fresh page skips whatever an earlier page left behind.
    /// </summary>
    public JsonObject Wait(long since, TimeSpan timeout)
    {
        var events = new JsonArray();
        lock (_gate)
        {
            if (since >= 0)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (_head <= since && !_closed)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero)
                        break;
                    Monitor.Wait(_gate, left);
                }

                foreach (var evt in _events)
                {
                    if ((long?)evt["seq"] > since)
                        events.Add(evt.DeepClone());
                }
            }

            return new JsonObject { ["head"] = _head, ["events"] = events };
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            Monitor.PulseAll(_gate);
        }
    }

    private void TraceCommand(string name, JsonObject command)
    {
        if (name == "shade")
            return;
        if (name == "media")
        {
            Trace?.Invoke(DescribeMedia(command));
            return;
        }

        Trace?.Invoke("avplay " + Describe(command));
    }

    private static string DescribeMedia(JsonObject command)
    {
        var op = (string?)command["op"] ?? "";
        var id = (string?)command["id"] ?? "";
        var size = command["size"]?.ToString();
        if (string.IsNullOrEmpty(size) && command["bytes"] is JsonValue bytes && bytes.GetValueKind() == JsonValueKind.String)
            size = ((string?)bytes)?.Length.ToString() ?? "0";
        return "media op=" + op + " id=" + id + " size=" + (size ?? "0");
    }

    private static string DescribeQuiet(JsonObject evt)
    {
        var id = (string?)evt["id"] ?? "";
        var size = evt["bytes"] is JsonValue bytes && bytes.GetValueKind() == JsonValueKind.String
            ? ((string?)bytes)?.Length.ToString() ?? "0"
            : "0";
        return " id=" + id + " bytes=" + size;
    }

    private static string Describe(JsonObject command)
    {
        var copy = (JsonObject)command.DeepClone();
        if (copy["data"] is JsonNode data)
            copy["data"] = DescribeDrm(data);
        return Shorten(copy.ToJsonString(), 600);
    }

    private static string DescribeDrm(JsonNode data)
    {
        try
        {
            var parsed = data.GetValueKind() == JsonValueKind.String ? JsonNode.Parse((string)data!) : data;
            if (parsed is JsonObject props)
            {
                var server = (string?)props["LicenseServer"] ?? "";
                var custom = props["CustomData"]?.ToString() ?? "";
                return "LicenseServer=" + server + " CustomData=" + custom.Length + " chars";
            }
        }
        catch (JsonException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return data.ToJsonString().Length + " chars";
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text.Substring(0, max) + "...";
}
