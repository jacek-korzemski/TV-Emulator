using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TvEmulator.Debug;

public static class CdpProtocol
{
    public const int MaxBodyBytes = 256 * 1024;

    public const string MemoryExpression =
        "(function(){var m=(typeof performance!==\"undefined\")&&performance.memory;var nodes=0;try{nodes=document.getElementsByTagName(\"*\").length;}catch(e){}return JSON.stringify({used:m?m.usedJSHeapSize:null,total:m?m.totalJSHeapSize:null,limit:m?m.jsHeapSizeLimit:null,nodes:nodes});})()";

    public static ConsoleLine? ReadConsole(string method, JsonNode? param, string source)
    {
        if (param is null)
            return null;
        if (method == "Runtime.consoleAPICalled")
            return FromApi(param, source);
        if (method == "Console.messageAdded")
            return FromMessage(param["message"], source);
        if (method == "Runtime.exceptionThrown")
            return FromException(param["exceptionDetails"], source);
        if (method == "Log.entryAdded")
            return FromLog(param["entry"], source);
        return null;
    }

    public static void ApplyRequest(NetworkCall call, JsonNode param)
    {
        var request = param["request"];
        call.Method = Text(request?["method"]);
        if (call.Method.Length == 0)
            call.Method = "GET";
        call.Url = Text(request?["url"]);
        if (param["type"] is not null)
        {
            call.RawType = Text(param["type"]);
            call.Type = MapType(call.RawType);
        }

        call.RequestHeaders = Cap(FormatHeaders(request?["headers"]), 32_000);
        var post = Text(request?["postData"]);
        if (post.Length > 0)
        {
            call.HasRequestBody = true;
            if (post.Length > MaxBodyBytes)
            {
                call.RequestBody = post.Substring(0, MaxBodyBytes);
                call.BodyTruncated = true;
            }
            else
            {
                call.RequestBody = post;
            }
        }

        if (param["timestamp"] is JsonNode stamp)
        {
            call.StartedMono = ReadDouble(stamp);
            call.HasStart = true;
        }

        var wall = ReadDouble(param["wallTime"]);
        if (wall > 0)
        {
            var ms = (long)(wall * 1000);
            call.Time = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss.fff");
        }
    }

    public static void ApplyResponse(NetworkCall call, JsonNode param)
    {
        if (param["type"] is not null)
        {
            call.RawType = Text(param["type"]);
            call.Type = MapType(call.RawType);
        }

        var response = param["response"];
        if (response is null)
            return;
        call.Status = (int)ReadLong(response["status"]);
        call.Mime = Text(response["mimeType"]);
        call.ResponseHeaders = Cap(FormatHeaders(response["headers"]), 32_000);
    }

    public static void ApplyFinished(NetworkCall call, JsonNode param)
    {
        var size = ReadLong(param["encodedDataLength"]);
        if (param["encodedDataLength"] is not null)
            call.EncodedSize = size;
        if (call.HasStart && param["timestamp"] is not null)
            call.DurationMs = (long)Math.Round((ReadDouble(param["timestamp"]) - call.StartedMono) * 1000);
    }

    public static void ApplyFailed(NetworkCall call, JsonNode param)
    {
        call.Error = Text(param["errorText"]);
        if (call.Error.Length == 0 && param["canceled"] is JsonValue canceled && canceled.TryGetValue<bool>(out var flag) && flag)
            call.Error = "anulowane";
        if (call.HasStart && param["timestamp"] is not null)
            call.DurationMs = (long)Math.Round((ReadDouble(param["timestamp"]) - call.StartedMono) * 1000);
    }

    public static void AppendFrame(NetworkCall call, JsonNode param, bool sent)
    {
        call.Type = "websocket";
        call.RawType = "WebSocket";
        var payload = param["response"];
        var opcode = (int)ReadLong(payload?["opcode"]);
        var data = Text(payload?["payloadData"]);
        var line = (sent ? "> " : "< ") + (opcode is 1 or 0 ? data : "(binarne " + data.Length + " B)");
        var existing = call.ResponseBody ?? "";
        if (existing.Length >= MaxBodyBytes)
        {
            call.BodyTruncated = true;
            return;
        }

        if (existing.Length + line.Length > MaxBodyBytes)
        {
            line = line.Substring(0, Math.Max(0, MaxBodyBytes - existing.Length));
            call.BodyTruncated = true;
        }

        call.ResponseBody = existing.Length == 0 ? line : existing + "\n" + line;
        call.HasResponseBody = true;
    }

    public static bool ShouldFetchBody(NetworkCall call)
    {
        if (call.Type is "image" or "media" or "font" or "websocket")
            return false;
        if (call.EncodedSize is long size && size > MaxBodyBytes)
            return false;
        var mime = call.Mime ?? "";
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || mime.StartsWith("font/", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("wasm", StringComparison.OrdinalIgnoreCase))
            return false;
        return call.Type is "xhr" or "document" or "script" or "stylesheet" or "other";
    }

    public static bool IsDeadPage(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return false;
        return url.StartsWith("chrome-error:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("chrome://crash", StringComparison.OrdinalIgnoreCase)
            || url.Contains("chromewebdata", StringComparison.OrdinalIgnoreCase);
    }

    public static bool UsesOldGc(string? engineId)
    {
        const string prefix = "chromium-";
        if (string.IsNullOrEmpty(engineId) || !engineId.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        return int.TryParse(engineId.AsSpan(prefix.Length), out var milestone) && milestone < 62;
    }

    public static (long Count, long Size) ApplyHeapStats(JsonNode? statsUpdate, Dictionary<int, (long Count, long Size)> fragments)
    {
        if (statsUpdate is JsonArray array)
        {
            for (var i = 0; i + 2 < array.Count; i += 3)
            {
                var index = (int)ReadLong(array[i]);
                fragments[index] = (ReadLong(array[i + 1]), ReadLong(array[i + 2]));
            }
        }

        long count = 0;
        long size = 0;
        foreach (var fragment in fragments.Values)
        {
            count += fragment.Count;
            size += fragment.Size;
        }

        return (count, size);
    }

    public static string MapType(string? raw)
    {
        return raw switch
        {
            "XHR" or "Fetch" or "EventSource" or "Ping" or "Preflight" => "xhr",
            "Document" => "document",
            "Script" => "script",
            "Stylesheet" => "stylesheet",
            "Image" => "image",
            "Media" => "media",
            "Font" => "font",
            "WebSocket" => "websocket",
            _ => "other"
        };
    }

    public static string ReadString(JsonNode? node) => Text(node);

    public static void ApplyRedirect(NetworkCall call, JsonNode requestParams)
    {
        var redirect = requestParams["redirectResponse"];
        if (redirect is null)
            return;
        call.Status = (int)ReadLong(redirect["status"]);
        var mime = Text(redirect["mimeType"]);
        if (mime.Length > 0)
            call.Mime = mime;
        var headers = FormatHeaders(redirect["headers"]);
        if (headers.Length > 0)
            call.ResponseHeaders = Cap(headers, 32_000);
    }

    public static string MapLevel(string? level)
    {
        return level switch
        {
            "error" => "error",
            "warning" or "warn" => "warning",
            "debug" or "verbose" or "trace" => "debug",
            _ => "info"
        };
    }

    public static BodyRead ReadBody(JsonObject result)
    {
        var encoded = result["base64Encoded"] is JsonValue flag && flag.TryGetValue<bool>(out var binary) && binary;
        var body = Text(result["body"]);
        if (!encoded)
        {
            if (body.Length > MaxBodyBytes)
                return new BodyRead(body.Substring(0, MaxBodyBytes), Truncated: true, Skip: "");
            return new BodyRead(body, Truncated: false, Skip: "");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(body);
        }
        catch (FormatException)
        {
            return new BodyRead(null, false, "Nie udało się odczytać odpowiedzi.");
        }

        var truncated = false;
        if (bytes.Length > MaxBodyBytes)
        {
            truncated = true;
            bytes = bytes.Take(MaxBodyBytes).ToArray();
        }

        if (LooksBinary(bytes))
            return new BodyRead(null, truncated, "Odpowiedź binarna, " + bytes.Length + " B.");
        return new BodyRead(Encoding.UTF8.GetString(bytes), truncated, "");
    }

    public static MemorySample? ReadMemory(JsonObject? evaluateResult, string source)
    {
        var inner = evaluateResult?["result"];
        var text = Text(inner?["value"]);
        if (text.Length == 0 || text[0] != '{')
            text = Text(inner?["description"]);
        if (text.Length == 0 || text[0] != '{')
            return null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }

        return new MemorySample
        {
            Source = source,
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Used = ReadOptionalLong(node?["used"]),
            Total = ReadOptionalLong(node?["total"]),
            Limit = ReadOptionalLong(node?["limit"]),
            Nodes = (int)ReadLong(node?["nodes"])
        };
    }

    public static List<CdpTarget> ReadTargets(string json)
    {
        var list = new List<CdpTarget>();
        if (JsonNode.Parse(json) is not JsonArray array)
            return list;
        foreach (var item in array)
        {
            var type = Text(item?["type"]);
            if (type is not ("page" or "iframe" or "worker" or "service_worker" or "shared_worker" or "other"))
                continue;
            var url = Text(item?["url"]);
            if (url.Contains("devtools://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("chrome-devtools:", StringComparison.OrdinalIgnoreCase))
                continue;
            var id = Text(item?["id"]);
            var socket = Text(item?["webSocketDebuggerUrl"]);
            if (socket.Length == 0)
                socket = WebSocketFromFrontend(Text(item?["devtoolsFrontendUrl"]));
            if (id.Length == 0 || socket.Length == 0)
                continue;
            list.Add(new CdpTarget(id, type, url, socket));
        }

        return list;
    }

    public readonly record struct BodyRead(string? Text, bool Truncated, string Skip);

    private static ConsoleLine? FromApi(JsonNode param, string source)
    {
        var type = Text(param["type"]);
        if (type is "clear" or "startGroup" or "startGroupCollapsed" or "endGroup" or "profile" or "profileEnd")
            return null;
        var level = type == "assert" ? "warning" : MapLevel(type);
        var args = param["args"] as JsonArray;
        var text = new StringBuilder();
        if (args is not null)
        {
            foreach (var arg in args)
            {
                if (text.Length > 0)
                    text.Append(' ');
                text.Append(FormatArg(arg));
                if (text.Length > 16_000)
                    break;
            }
        }

        var stack = FormatStack(param["stackTrace"]);
        var url = "";
        var line = 0;
        var column = 0;
        if (param["stackTrace"]?["callFrames"] is JsonArray frames && frames.Count > 0)
        {
            url = Text(frames[0]?["url"]);
            line = (int)ReadLong(frames[0]?["lineNumber"]) + 1;
            column = (int)ReadLong(frames[0]?["columnNumber"]) + 1;
        }

        var entry = new ConsoleLine
        {
            Source = source,
            Level = level,
            Text = Cap(text.ToString(), 16_000),
            Url = url,
            Line = line,
            Column = column,
            Stack = stack
        };
        PreferCaller(entry, param["stackTrace"]);
        return entry;
    }

    private static ConsoleLine? FromMessage(JsonNode? message, string source)
    {
        if (message is null)
            return null;
        var level = MapLevel(Text(message["level"]));
        var entry = new ConsoleLine
        {
            Source = source,
            Level = level,
            Text = Cap(Text(message["text"]), 16_000),
            Url = Text(message["url"]),
            Line = (int)ReadLong(message["line"]),
            Column = (int)ReadLong(message["column"]),
            Stack = FormatStack(message["stackTrace"])
        };
        PreferCaller(entry, message["stackTrace"]);
        return entry;
    }

    private static ConsoleLine? FromException(JsonNode? details, string source)
    {
        if (details is null)
            return null;
        var text = Text(details["text"]);
        var description = Text(details["exception"]?["description"]);
        if (description.Length == 0)
            description = Text(details["exception"]?["value"]);
        if (description.Length > 0 && !text.Contains(description, StringComparison.Ordinal))
            text = text.Length == 0 ? description : text + " " + description;
        var entry = new ConsoleLine
        {
            Source = source,
            Level = "error",
            Text = Cap(text, 16_000),
            Url = Text(details["url"]),
            Line = (int)ReadLong(details["lineNumber"]) + 1,
            Column = (int)ReadLong(details["columnNumber"]) + 1,
            Stack = FormatStack(details["stackTrace"])
        };
        PreferCaller(entry, details["stackTrace"]);
        return entry;
    }

    private static ConsoleLine? FromLog(JsonNode? node, string source)
    {
        if (node is null)
            return null;
        var entry = new ConsoleLine
        {
            Source = source,
            Level = MapLevel(Text(node["level"])),
            Text = Cap(Text(node["text"]), 16_000),
            Url = Text(node["url"]),
            Line = (int)ReadLong(node["lineNumber"]) + 1,
            Stack = FormatStack(node["stackTrace"])
        };
        PreferCaller(entry, node["stackTrace"]);
        return entry;
    }

    private static void PreferCaller(ConsoleLine entry, JsonNode? stack)
    {
        JsonArray? frames = null;
        if (stack is JsonArray direct)
            frames = direct;
        else if (stack?["callFrames"] is JsonArray nested)
            frames = nested;
        if (frames is null)
            return;
        foreach (var frame in frames)
        {
            var url = Text(frame?["url"]);
            if (url.Length == 0 || url.Contains("/__emulator/platform.js", StringComparison.Ordinal))
                continue;
            entry.Url = url;
            entry.Line = (int)ReadLong(frame?["lineNumber"]) + 1;
            entry.Column = (int)ReadLong(frame?["columnNumber"]) + 1;
            return;
        }
    }

    private static string FormatArg(JsonNode? arg)
    {
        if (arg is null)
            return "";
        if (arg["value"] is not null)
            return Text(arg["value"]);
        var description = Text(arg["description"]);
        if (description.Length > 0)
            return description;
        var preview = Text(arg["preview"]?["description"]);
        if (preview.Length > 0)
            return preview;
        var type = Text(arg["type"]);
        return type.Length == 0 ? "" : "[" + type + "]";
    }

    private static string FormatStack(JsonNode? stack)
    {
        JsonArray? frames = null;
        if (stack is JsonArray direct)
            frames = direct;
        else if (stack?["callFrames"] is JsonArray nested)
            frames = nested;
        if (frames is null)
            return "";
        var builder = new StringBuilder();
        foreach (var frame in frames)
        {
            if (builder.Length > 4000)
                break;
            var function = Text(frame?["functionName"]);
            if (function.Length == 0)
                function = "(anonimowa)";
            var url = Text(frame?["url"]);
            var line = (int)ReadLong(frame?["lineNumber"]) + 1;
            var column = (int)ReadLong(frame?["columnNumber"]) + 1;
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append("at ").Append(function).Append(" (").Append(url).Append(':').Append(line).Append(':').Append(column).Append(')');
        }

        return Cap(builder.ToString(), 4000);
    }

    public static string FormatHeaders(JsonNode? headers)
    {
        var builder = new StringBuilder();
        if (headers is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (builder.Length > 0)
                    builder.Append('\n');
                builder.Append(pair.Key).Append(": ").Append(Text(pair.Value));
            }
        }
        else if (headers is JsonArray array)
        {
            foreach (var item in array)
            {
                if (builder.Length > 0)
                    builder.Append('\n');
                builder.Append(Text(item?["name"])).Append(": ").Append(Text(item?["value"]));
            }
        }

        return builder.ToString();
    }

    private static string WebSocketFromFrontend(string frontend)
    {
        var marker = "ws=";
        var at = frontend.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return "";
        var value = frontend.Substring(at + marker.Length);
        var amp = value.IndexOf('&');
        if (amp >= 0)
            value = value.Substring(0, amp);
        value = Uri.UnescapeDataString(value);
        if (value.StartsWith("ws://", StringComparison.Ordinal) || value.StartsWith("wss://", StringComparison.Ordinal))
            return value;
        return value.Length == 0 ? "" : "ws://" + value;
    }

    private static bool LooksBinary(byte[] bytes)
    {
        var length = Math.Min(bytes.Length, 512);
        for (var i = 0; i < length; i++)
        {
            if (bytes[i] == 0)
                return true;
        }

        return false;
    }

    private static string Text(JsonNode? node)
    {
        if (node is null)
            return "";
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
                return text ?? "";
            if (value.TryGetValue<bool>(out var flag))
                return flag ? "true" : "false";
            if (value.TryGetValue<long>(out var whole))
                return whole.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue<double>(out var real))
                return real.ToString(CultureInfo.InvariantCulture);
            var raw = value.ToJsonString();
            if (raw.Length >= 2 && raw[0] == '"')
            {
                try
                {
                    return JsonSerializer.Deserialize<string>(raw) ?? "";
                }
                catch (JsonException)
                {
                    return raw;
                }
            }

            return raw;
        }

        return node.ToJsonString();
    }

    private static long ReadLong(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var whole))
                return whole;
            if (value.TryGetValue<int>(out var integer))
                return integer;
            if (value.TryGetValue<double>(out var real))
                return (long)real;
        }

        return 0;
    }

    private static long? ReadOptionalLong(JsonNode? node)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
            return null;
        return ReadLong(node);
    }

    private static double ReadDouble(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var real))
                return real;
            if (value.TryGetValue<long>(out var whole))
                return whole;
        }

        return 0;
    }

    private static string Cap(string text, int limit)
    {
        if (text.Length <= limit)
            return text;
        return text.Substring(0, limit);
    }
}

public sealed record CdpTarget(string Id, string Type, string Url, string WebSocketUrl);
