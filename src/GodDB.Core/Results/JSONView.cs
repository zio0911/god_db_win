using System.Globalization;
using System.Text;
using GodDB.Core.Schema;

namespace GodDB.Core.Results;

// 이름은 Mac 과 같게 JSON… 으로 둔다 (System.Text.Json.Nodes.JsonNode 와 헷갈리지 않게).

/// JSON 값의 종류
public enum JSONKind { Object, Array, String, Number, Bool, Null }

/// JSON 값 (객체 키 순서를 원래대로 유지). (Mac: JSONView.swift 의 JSONNode)
public sealed class JSONNode : IEquatable<JSONNode>
{
    public JSONKind Kind { get; }
    /// 객체 항목 (Object 일 때)
    public IReadOnlyList<JSONMember> Members { get; } = [];
    /// 배열 항목 (Array 일 때)
    public IReadOnlyList<JSONNode> Items { get; } = [];
    /// 문자 내용 (String) 또는 숫자의 원래 글자 (Number — 정밀도 · 표기 유지)
    public string Text { get; } = "";
    public bool BoolValue { get; }

    private JSONNode(JSONKind kind, IReadOnlyList<JSONMember>? members = null, IReadOnlyList<JSONNode>? items = null,
                     string text = "", bool b = false)
    {
        Kind = kind;
        Members = members ?? [];
        Items = items ?? [];
        Text = text;
        BoolValue = b;
    }

    public static JSONNode Object(IEnumerable<JSONMember> members) => new(JSONKind.Object, members: [.. members]);
    public static JSONNode Array(IEnumerable<JSONNode> items) => new(JSONKind.Array, items: [.. items]);
    public static JSONNode String(string s) => new(JSONKind.String, text: s);
    public static JSONNode Number(string n) => new(JSONKind.Number, text: n);
    public static JSONNode Bool(bool b) => new(JSONKind.Bool, b: b);
    public static readonly JSONNode Null = new(JSONKind.Null);

    public bool IsContainer => Kind is JSONKind.Object or JSONKind.Array;

    public bool Equals(JSONNode? other) =>
        other is not null && Kind == other.Kind && Text == other.Text && BoolValue == other.BoolValue
        && Members.SequenceEqual(other.Members) && Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => Equals(obj as JSONNode);
    public override int GetHashCode() => HashCode.Combine(Kind, Text, BoolValue, Members.Count, Items.Count);
    public override string ToString() => JSONView.Compact(this);
}

public sealed record JSONMember(string Key, JSONNode Value);

/// 경로 한 칸: 객체 키 또는 배열 번호
public readonly record struct JSONPathComponent(string? KeyName, int IndexValue)
{
    public static JSONPathComponent Key(string key) => new(key, 0);
    public static JSONPathComponent Index(int index) => new(null, index);
    public bool IsKey => KeyName != null;
}

/// 행 상세의 JSON 보기 방식 (설정 키 `GodDB.json.viewMode`)
public enum JSONViewMode { Raw, Pretty, Tree }

public static class JSONViewModes
{
    public const string DefaultsKey = "GodDB.json.viewMode";
    public const JSONViewMode Default = JSONViewMode.Tree;

    /// 설정에 쓰는 값 (Mac 과 같게 raw · pretty · tree)
    public static string RawValue(this JSONViewMode m) => m switch
    {
        JSONViewMode.Raw => "raw",
        JSONViewMode.Pretty => "pretty",
        _ => "tree",
    };

    public static JSONViewMode? FromRaw(string? raw) => raw switch
    {
        "raw" => JSONViewMode.Raw,
        "pretty" => JSONViewMode.Pretty,
        "tree" => JSONViewMode.Tree,
        _ => null,
    };

    /// 마지막으로 고른 보기 (기본 트리)
    public static JSONViewMode Current
    {
        get => FromRaw(Prefs.GetString(DefaultsKey)) ?? Default;
        set => Prefs.Set(DefaultsKey, value.RawValue());
    }
}

/// 칸 값이 JSON 인지 판단한 결과
public sealed class JSONDetection : IEquatable<JSONDetection>
{
    public enum Kinds { NotJSON, TooLarge, JSON }

    public Kinds Kind { get; }
    /// JSON 일 때 파싱한 값
    public JSONNode? Node { get; }

    private JSONDetection(Kinds kind, JSONNode? node = null)
    {
        Kind = kind;
        Node = node;
    }

    public static readonly JSONDetection NotJSON = new(Kinds.NotJSON);
    /// JSON 으로 보이지만 1MB 가 넘어 원문만 보여 준다 (파싱하지 않음)
    public static readonly JSONDetection TooLarge = new(Kinds.TooLarge);
    public static JSONDetection Json(JSONNode node) => new(Kinds.JSON, node);

    /// JSON 으로 다룰지 (원문만 보여 주는 TooLarge 포함)
    public bool IsJSON => Kind != Kinds.NotJSON;

    public bool Equals(JSONDetection? other) => other is not null && Kind == other.Kind && Equals(Node, other.Node);
    public override bool Equals(object? obj) => Equals(obj as JSONDetection);
    public override int GetHashCode() => HashCode.Combine(Kind, Node);
    public override string ToString() => Kind == Kinds.JSON ? $"json({Node})" : Kind.ToString();
}

/// JSON 보기 (Mac: JSONView.swift)
public static class JSONView
{
    /// 이보다 크면 정렬 · 트리 대신 원문만 (UTF-8 바이트)
    public const int SizeLimit = 1_048_576;
    /// 이보다 깊으면 JSON 으로 보지 않는다 (재귀 깊이 보호)
    internal const int MaxDepth = 512;

    // MARK: - 판단

    /// 컬럼 타입이 json / jsonb 인지 (`json`, `JSONB`, `json(…)` 등)
    public static bool IsJSONType(string? columnType)
    {
        if (columnType?.Trim(' ', '\t').ToLowerInvariant() is not { } t) return false;
        var parts = t.Split(['(', ' '], StringSplitOptions.RemoveEmptyEntries);
        var b = parts.Length > 0 ? parts[0] : t;
        return b is "json" or "jsonb";
    }

    /// 앞뒤 공백을 뺀 값이 `{` 또는 `[` 로 시작하고 JSON 으로 파싱되면 JSON.
    /// 컬럼 타입이 json / jsonb 이면 시작 글자는 보지 않고 파싱만 확인한다.
    /// 1MB 가 넘으면 파싱하지 않고 (시작 글자 · 타입만 보고) TooLarge.
    public static JSONDetection Detect(string? value, string? columnType = null)
    {
        if (value == null || !MightBeJSON(value, columnType)) return JSONDetection.NotJSON;
        if (Encoding.UTF8.GetByteCount(value) > SizeLimit) return JSONDetection.TooLarge;
        return Parse(value) is { } node ? JSONDetection.Json(node) : JSONDetection.NotJSON;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r';

    /// 파싱 전 빠른 확인: 타입이 json / jsonb 이면 공백이 아닌 글자가 있는지, 아니면 첫 글자가 `{` · `[` 인지
    public static bool MightBeJSON(string value, string? columnType)
    {
        var i = 0;
        while (i < value.Length && IsSpace(value[i])) i++;
        if (i >= value.Length) return false;
        return IsJSONType(columnType) || value[i] is '{' or '[';
    }

    /// 편집한 값이 JSON 으로 파싱되는지 (NULL 은 문제 없음)
    public static bool IsValid(string? value)
    {
        if (value == null) return true;
        if (Encoding.UTF8.GetByteCount(value) > SizeLimit)
        {
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(value, new System.Text.Json.JsonDocumentOptions { MaxDepth = 4096 });
                return true;
            }
            catch { return false; }
        }
        return Parse(value) != null;
    }

    /// 이 칸을 JSON 칸으로 다룰지: 타입이 json / jsonb 이거나 원래 값이 JSON
    public static bool IsJSONColumn(string? original, string? columnType) =>
        IsJSONType(columnType) || Detect(original, null).IsJSON;

    /// 편집 결과에서 JSON 칸인데 JSON 이 깨진 컬럼 이름 (저장 전 경고용, 중복 없이 컬럼 순서대로)
    public static List<string> InvalidEdits(PendingEdits edits, EditTarget target, IReadOnlyList<string> columns,
                                            IReadOnlyList<QueryResult.Row> rows)
    {
        string? Type(int col) =>
            target.ColumnMap.TryGetValue(col, out var name)
                ? target.Table.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.ColumnType
                : null;
        var bad = new HashSet<int>();
        var byId = new Dictionary<int, QueryResult.Row>();
        foreach (var r in rows) byId.TryAdd(r.Id, r);
        foreach (var (rowId, changes) in edits.Updates)
        {
            if (edits.Deletes.Contains(rowId)) continue;
            foreach (var (col, value) in changes)
            {
                var original = byId.TryGetValue(rowId, out var row) && col >= 0 && col < row.Values.Count ? row.Values[col] : null;
                if (IsJSONColumn(original, Type(col)) && !IsValid(value)) bad.Add(col);
            }
        }
        foreach (var insert in edits.Inserts)
            foreach (var (col, value) in insert)
                if (IsJSONType(Type(col)) && !IsValid(value)) bad.Add(col);
        return [.. bad.Order().Where(c => c >= 0 && c < columns.Count).Select(c => columns[c])];
    }

    // MARK: - 파싱 (키 순서 유지)

    /// 엄격한 JSON 파싱. 전체가 하나의 JSON 값이어야 한다 (앞뒤 공백 허용).
    public static JSONNode? Parse(string text)
    {
        var p = new Parser(Encoding.UTF8.GetBytes(text));
        if (p.Value(0) is not { } node) return null;
        p.SkipSpace();
        return p.Pos == p.Bytes.Length ? node : null;
    }

    private sealed class Parser(byte[] bytes)
    {
        public readonly byte[] Bytes = bytes;
        public int Pos;

        public void SkipSpace()
        {
            while (Pos < Bytes.Length && Bytes[Pos] is 0x20 or 0x09 or 0x0A or 0x0D) Pos++;
        }

        private bool Literal(string word)
        {
            if (Pos + word.Length > Bytes.Length) return false;
            for (var i = 0; i < word.Length; i++) if (Bytes[Pos + i] != word[i]) return false;
            Pos += word.Length;
            return true;
        }

        public JSONNode? Value(int depth)
        {
            if (depth > MaxDepth) return null;
            SkipSpace();
            if (Pos >= Bytes.Length) return null;
            switch (Bytes[Pos])
            {
                case (byte)'{': return Object(depth);
                case (byte)'[': return Array(depth);
                case (byte)'"': return String() is { } s ? JSONNode.String(s) : null;
                case (byte)'t': return Literal("true") ? JSONNode.Bool(true) : null;
                case (byte)'f': return Literal("false") ? JSONNode.Bool(false) : null;
                case (byte)'n': return Literal("null") ? JSONNode.Null : null;
                default: return Number();
            }
        }

        private JSONNode? Object(int depth)
        {
            Pos++;
            var members = new List<JSONMember>();
            SkipSpace();
            if (Pos < Bytes.Length && Bytes[Pos] == '}') { Pos++; return JSONNode.Object([]); }
            while (true)
            {
                SkipSpace();
                if (Pos >= Bytes.Length || Bytes[Pos] != '"' || String() is not { } key) return null;
                SkipSpace();
                if (Pos >= Bytes.Length || Bytes[Pos] != ':') return null;
                Pos++;
                if (Value(depth + 1) is not { } v) return null;
                members.Add(new JSONMember(key, v));
                SkipSpace();
                if (Pos >= Bytes.Length) return null;
                if (Bytes[Pos] == ',') { Pos++; continue; }
                if (Bytes[Pos] == '}') { Pos++; return JSONNode.Object(members); }
                return null;
            }
        }

        private JSONNode? Array(int depth)
        {
            Pos++;
            var items = new List<JSONNode>();
            SkipSpace();
            if (Pos < Bytes.Length && Bytes[Pos] == ']') { Pos++; return JSONNode.Array([]); }
            while (true)
            {
                if (Value(depth + 1) is not { } v) return null;
                items.Add(v);
                SkipSpace();
                if (Pos >= Bytes.Length) return null;
                if (Bytes[Pos] == ',') { Pos++; continue; }
                if (Bytes[Pos] == ']') { Pos++; return JSONNode.Array(items); }
                return null;
            }
        }

        private uint? Hex4()
        {
            if (Pos + 4 > Bytes.Length) return null;
            uint v = 0;
            for (var i = Pos; i < Pos + 4; i++)
            {
                var b = Bytes[i];
                v <<= 4;
                if (b is >= 0x30 and <= 0x39) v |= (uint)(b - 0x30);
                else if (b is >= 0x41 and <= 0x46) v |= (uint)(b - 0x41 + 10);
                else if (b is >= 0x61 and <= 0x66) v |= (uint)(b - 0x61 + 10);
                else return null;
            }
            Pos += 4;
            return v;
        }

        /// `"` 에서 시작해 닫는 `"` 다음까지
        private string? String()
        {
            Pos++;
            var output = new List<byte>();
            while (Pos < Bytes.Length)
            {
                var b = Bytes[Pos];
                if (b == '"')
                {
                    Pos++;
                    return Encoding.UTF8.GetString(output.ToArray());
                }
                if (b < 0x20) return null;
                if (b != '\\') { output.Add(b); Pos++; continue; }
                Pos++;
                if (Pos >= Bytes.Length) return null;
                var e = Bytes[Pos];
                Pos++;
                switch (e)
                {
                    case (byte)'"': output.Add(0x22); break;
                    case (byte)'\\': output.Add(0x5C); break;
                    case (byte)'/': output.Add(0x2F); break;
                    case (byte)'b': output.Add(0x08); break;
                    case (byte)'f': output.Add(0x0C); break;
                    case (byte)'n': output.Add(0x0A); break;
                    case (byte)'r': output.Add(0x0D); break;
                    case (byte)'t': output.Add(0x09); break;
                    case (byte)'u':
                        if (Hex4() is not { } code) return null;
                        if (code is >= 0xD800 and <= 0xDBFF)
                        {
                            // 짝이 되는 낮은 서로게이트가 이어지면 합친다
                            if (Pos + 6 <= Bytes.Length && Bytes[Pos] == '\\' && Bytes[Pos + 1] == 'u')
                            {
                                var save = Pos;
                                Pos += 2;
                                if (Hex4() is { } low && low is >= 0xDC00 and <= 0xDFFF)
                                {
                                    code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                                }
                                else
                                {
                                    Pos = save;
                                    code = 0xFFFD;
                                }
                            }
                            else code = 0xFFFD;
                        }
                        else if (code is >= 0xDC00 and <= 0xDFFF) code = 0xFFFD;
                        output.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32((int)code)));
                        break;
                    default:
                        return null;
                }
            }
            return null;
        }

        /// -?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?
        private JSONNode? Number()
        {
            var start = Pos;
            bool Digit(int i) => i < Bytes.Length && Bytes[i] is >= 0x30 and <= 0x39;
            if (Pos < Bytes.Length && Bytes[Pos] == '-') Pos++;
            if (!Digit(Pos)) return null;
            if (Bytes[Pos] == '0') Pos++;
            else while (Digit(Pos)) Pos++;
            if (Pos < Bytes.Length && Bytes[Pos] == '.')
            {
                Pos++;
                if (!Digit(Pos)) return null;
                while (Digit(Pos)) Pos++;
            }
            if (Pos < Bytes.Length && Bytes[Pos] is (byte)'e' or (byte)'E')
            {
                Pos++;
                if (Pos < Bytes.Length && Bytes[Pos] is (byte)'+' or (byte)'-') Pos++;
                if (!Digit(Pos)) return null;
                while (Digit(Pos)) Pos++;
            }
            return JSONNode.Number(Encoding.UTF8.GetString(Bytes, start, Pos - start));
        }
    }

    // MARK: - 정렬 출력

    /// 들여쓰기 2칸, 키는 원래 순서. 한글 등은 그대로 두고 꼭 필요한 것만 이스케이프.
    public static string Pretty(JSONNode node)
    {
        var sb = new StringBuilder();
        Write(node, 0, sb);
        return sb.ToString();
    }

    private static void Write(JSONNode node, int indent, StringBuilder output)
    {
        var pad = new string(' ', (indent + 1) * 2);
        var close = new string(' ', indent * 2);
        switch (node.Kind)
        {
            case JSONKind.Object:
                if (node.Members.Count == 0) { output.Append("{}"); return; }
                output.Append("{\n");
                for (var i = 0; i < node.Members.Count; i++)
                {
                    var m = node.Members[i];
                    output.Append(pad).Append(Quote(m.Key)).Append(": ");
                    Write(m.Value, indent + 1, output);
                    output.Append(i == node.Members.Count - 1 ? "\n" : ",\n");
                }
                output.Append(close).Append('}');
                break;
            case JSONKind.Array:
                if (node.Items.Count == 0) { output.Append("[]"); return; }
                output.Append("[\n");
                for (var i = 0; i < node.Items.Count; i++)
                {
                    output.Append(pad);
                    Write(node.Items[i], indent + 1, output);
                    output.Append(i == node.Items.Count - 1 ? "\n" : ",\n");
                }
                output.Append(close).Append(']');
                break;
            default:
                output.Append(ScalarText(node));
                break;
        }
    }

    /// 한 줄 JSON 글자 (객체 · 배열은 한 줄로)
    public static string Compact(JSONNode node) => node.Kind switch
    {
        JSONKind.Object => "{" + string.Join(",", node.Members.Select(m => Quote(m.Key) + ":" + Compact(m.Value))) + "}",
        JSONKind.Array => "[" + string.Join(",", node.Items.Select(Compact)) + "]",
        _ => ScalarText(node),
    };

    /// 문자 · 숫자 · 참거짓 · null 의 JSON 표기
    public static string ScalarText(JSONNode node) => node.Kind switch
    {
        JSONKind.String => Quote(node.Text),
        JSONKind.Number => node.Text,
        JSONKind.Bool => node.BoolValue ? "true" : "false",
        JSONKind.Null => "null",
        _ => Compact(node),
    };

    /// "값 복사" 에 넣을 글자: 문자는 따옴표 없이 내용만, 객체 · 배열은 정렬 출력
    public static string CopyText(JSONNode node) => node.Kind switch
    {
        JSONKind.String => node.Text,
        JSONKind.Object or JSONKind.Array => Pretty(node),
        _ => ScalarText(node),
    };

    /// JSON 문자열 표기 (따옴표 포함)
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // MARK: - 경로

    /// `$.order.items[0].sku` 꼴. 식별자가 아닌 키는 `$["key with space"]`.
    public static string Path(IEnumerable<JSONPathComponent> components)
    {
        var sb = new StringBuilder("$");
        foreach (var c in components)
        {
            if (!c.IsKey) sb.Append('[').Append(c.IndexValue.ToString(CultureInfo.InvariantCulture)).Append(']');
            else if (IsIdentifier(c.KeyName!)) sb.Append('.').Append(c.KeyName);
            else sb.Append('[').Append(Quote(c.KeyName!)).Append(']');
        }
        return sb.ToString();
    }

    /// 글자(한글 포함) 또는 `_` 로 시작하고 글자 · 숫자 · `_` 만 있으면 식별자
    internal static bool IsIdentifier(string key)
    {
        var runes = key.EnumerateRunes().ToList();
        if (runes.Count == 0) return false;
        static bool Letter(Rune r) => r.Value == '_' || Rune.IsLetter(r)
                                      || Rune.GetUnicodeCategory(r) is UnicodeCategory.LetterNumber;
        static bool Digit(Rune r) => r.Value is >= '0' and <= '9';
        return Letter(runes[0]) && runes.Skip(1).All(r => Letter(r) || Digit(r));
    }
}
