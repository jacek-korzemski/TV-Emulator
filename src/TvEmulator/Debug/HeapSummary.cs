using System.Text;
using System.Text.Json;

namespace TvEmulator.Debug;

public static class HeapSummary
{
    public static Dictionary<string, HeapClass> Summarize(string json) =>
        Summarize(Encoding.UTF8.GetBytes(json));

    public static Dictionary<string, HeapClass> Summarize(ReadOnlySpan<byte> utf8)
    {
        var acc = new Accumulator();
        var reader = new Utf8JsonReader(utf8, isFinalBlock: true, default);
        while (reader.Read())
            acc.OnToken(ref reader);
        return acc.Finish();
    }

    public static Dictionary<string, HeapClass> SummarizeFile(string path)
    {
        var acc = new Accumulator();
        acc.ReadFile(path);
        return acc.Finish();
    }

    public static HeapReport Diff(IReadOnlyDictionary<string, HeapClass>? previous, IReadOnlyDictionary<string, HeapClass> current, string source)
    {
        var report = new HeapReport
        {
            Source = source,
            At = DateTime.Now.ToString("HH:mm:ss"),
            Classes = current.Count,
            TotalSelf = current.Values.Sum(item => item.SelfSize),
            HasPrevious = previous is not null,
            Retained = current
                .OrderByDescending(pair => pair.Value.SelfSize)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(40)
                .Select(pair => new HeapRow { Name = pair.Key, Count = pair.Value.Count, SelfSize = pair.Value.SelfSize })
                .ToList()
        };
        if (previous is null)
            return report;

        var grew = new List<HeapRow>();
        var freed = new List<HeapRow>();
        var names = new HashSet<string>(current.Keys, StringComparer.Ordinal);
        foreach (var name in previous.Keys)
            names.Add(name);
        foreach (var name in names)
        {
            current.TryGetValue(name, out var now);
            previous.TryGetValue(name, out var before);
            var row = new HeapRow
            {
                Name = name,
                Count = now?.Count ?? 0,
                SelfSize = now?.SelfSize ?? 0,
                CountDelta = (now?.Count ?? 0) - (before?.Count ?? 0),
                SizeDelta = (now?.SelfSize ?? 0) - (before?.SelfSize ?? 0)
            };
            if (row.CountDelta > 0 || row.SizeDelta > 0)
                grew.Add(row);
            if (row.CountDelta < 0 || (row.CountDelta == 0 && row.SizeDelta < 0))
                freed.Add(row);
        }

        report.Grew = grew.OrderByDescending(row => row.SizeDelta).ThenByDescending(row => row.CountDelta).Take(40).ToList();
        report.Freed = freed.OrderBy(row => row.CountDelta).ThenBy(row => row.SizeDelta).Take(40).ToList();
        return report;
    }

    private sealed class Accumulator
    {
        private enum Mode { None, NodeFields, NodeTypesOuter, NodeTypesInner, Nodes, Strings, Skip }

        private readonly List<string> _fields = new();
        private readonly List<string> _typeNames = new();
        private readonly Dictionary<(int Name, int Type), HeapClass> _groups = new();
        private readonly HashSet<int> _usedNames = new();
        private readonly Dictionary<int, string> _names = new();
        private Mode _mode;
        private int _skipDepth;
        private string? _pending;
        private int _nameIndex = -1;
        private int _sizeIndex = -1;
        private int _typeIndex = -1;
        private int _stride;
        private int _field;
        private int _type;
        private int _nameId;
        private long _size;
        private int _stringIndex;
        private bool _sawNodes;
        private bool _sawFields;

        public void ReadFile(string path)
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[16 * 1024];
            var left = 0;
            var state = new JsonReaderState();
            while (true)
            {
                var read = stream.Read(buffer, left, buffer.Length - left);
                var dataLength = left + read;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, dataLength), isFinalBlock: read == 0, state);
                while (reader.Read())
                    OnToken(ref reader);
                state = reader.CurrentState;
                left = dataLength - (int)reader.BytesConsumed;
                if (left > 0)
                    Buffer.BlockCopy(buffer, (int)reader.BytesConsumed, buffer, 0, left);
                if (read == 0)
                    break;
                if (left == buffer.Length)
                    Array.Resize(ref buffer, buffer.Length * 2);
            }
        }

        public void OnToken(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    if (_mode == Mode.None)
                        _pending = reader.GetString();
                    return;
                case JsonTokenType.StartObject:
                    if (_mode == Mode.Skip)
                        _skipDepth++;
                    else
                        _pending = null;
                    return;
                case JsonTokenType.EndObject:
                    if (_mode != Mode.Skip)
                        return;
                    _skipDepth--;
                    if (_skipDepth == 0)
                        _mode = Mode.None;
                    return;
                case JsonTokenType.StartArray:
                    StartArray();
                    return;
                case JsonTokenType.EndArray:
                    EndArray();
                    return;
                case JsonTokenType.String:
                    TakeString(reader.GetString() ?? "");
                    return;
                case JsonTokenType.Number:
                    if (_mode == Mode.Nodes)
                        TakeNode(ReadLong(ref reader));
                    else if (_mode == Mode.None)
                        _pending = null;
                    return;
                default:
                    if (_mode == Mode.None)
                        _pending = null;
                    return;
            }
        }

        public Dictionary<string, HeapClass> Finish()
        {
            if (!_sawFields)
                throw new InvalidOperationException("Zrzut sterty nie ma node_fields.");
            if (_nameIndex < 0 || _sizeIndex < 0)
                throw new InvalidOperationException("Zrzut sterty nie zawiera pól name i self_size.");
            if (!_sawNodes)
                throw new InvalidOperationException("Zrzut sterty nie ma nodes.");

            var result = new Dictionary<string, HeapClass>(StringComparer.Ordinal);
            foreach (var pair in _groups)
            {
            var type = pair.Key.Type;
            var typeName = type >= 0 && type < _typeNames.Count ? _typeNames[type] : "";
            string name;
            if (pair.Key.Name < 0 && IsContentTypeName(typeName))
                name = typeName;
            else
            {
                name = _names.TryGetValue(pair.Key.Name, out var found) ? found : "";
                if (name.Length == 0)
                    name = typeName.Length > 0 ? "(" + typeName + ")" : "(unknown)";
                else if (name.Length > 80)
                    name = name.Substring(0, 80) + "…";
            }

                if (!result.TryGetValue(name, out var group))
                {
                    group = new HeapClass();
                    result[name] = group;
                }

                group.Count += pair.Value.Count;
                group.SelfSize += pair.Value.SelfSize;
            }

            return result;
        }

        private void StartArray()
        {
            if (_mode == Mode.Skip)
            {
                _skipDepth++;
                return;
            }

            if (_mode == Mode.NodeTypesOuter)
            {
                _mode = Mode.NodeTypesInner;
                return;
            }

            if (_pending == "node_fields")
                _mode = Mode.NodeFields;
            else if (_pending == "node_types")
                _mode = Mode.NodeTypesOuter;
            else if (_pending == "nodes")
            {
                _mode = Mode.Nodes;
                _field = 0;
                _sawNodes = true;
            }
            else if (_pending == "strings")
            {
                _mode = Mode.Strings;
                _stringIndex = 0;
            }
            else if (_pending is not null)
            {
                _mode = Mode.Skip;
                _skipDepth = 1;
            }

            _pending = null;
        }

        private void EndArray()
        {
            if (_mode == Mode.Skip)
            {
                _skipDepth--;
                if (_skipDepth == 0)
                    _mode = Mode.None;
                return;
            }

            if (_mode == Mode.NodeTypesInner)
            {
                _mode = Mode.NodeTypesOuter;
                return;
            }

            if (_mode == Mode.NodeFields)
                CaptureFields();
            if (_mode is Mode.NodeFields or Mode.NodeTypesOuter or Mode.Nodes or Mode.Strings)
                _mode = Mode.None;
        }

        private void TakeString(string value)
        {
            if (_mode == Mode.NodeFields)
                _fields.Add(value);
            else if (_mode == Mode.NodeTypesInner)
                _typeNames.Add(value);
            else if (_mode == Mode.Strings)
            {
                if (_usedNames.Contains(_stringIndex))
                    _names[_stringIndex] = value;
                _stringIndex++;
            }
            else if (_mode == Mode.None)
                _pending = null;
        }

        private void TakeNode(long value)
        {
            if (_stride == 0)
                return;
            if (_field == _typeIndex)
                _type = (int)value;
            else if (_field == _nameIndex)
                _nameId = (int)value;
            else if (_field == _sizeIndex)
                _size = value;
            _field++;
            if (_field < _stride)
                return;

            var content = IsContentType(_type);
            var key = content ? (-1, _type) : (_nameId, _type);
            if (!_groups.TryGetValue(key, out var group))
            {
                group = new HeapClass();
                _groups[key] = group;
            }

            group.Count++;
            group.SelfSize += _size;
            if (!content)
                _usedNames.Add(_nameId);
            _field = 0;
            _type = 0;
            _nameId = 0;
            _size = 0;
        }

        private void CaptureFields()
        {
            _sawFields = true;
            _stride = _fields.Count;
            _nameIndex = _fields.IndexOf("name");
            _sizeIndex = _fields.IndexOf("self_size");
            _typeIndex = _fields.IndexOf("type");
        }

        private bool IsContentType(int type)
        {
            if (type < 0 || type >= _typeNames.Count)
                return false;
            return IsContentTypeName(_typeNames[type]);
        }

        private static bool IsContentTypeName(string typeName)
        {
            return typeName is "string" or "concatenated string" or "sliced string" or "number" or "bigint";
        }

        private static long ReadLong(ref Utf8JsonReader reader)
        {
            if (reader.TryGetInt64(out var whole))
                return whole;
            return (long)reader.GetDouble();
        }
    }
}
