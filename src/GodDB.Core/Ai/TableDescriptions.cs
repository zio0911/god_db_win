using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GodDB.Core.Platform;
using GodDB.Core.Profiles;
using GodDB.Core.Schema;
using static GodDB.Core.L10n;

namespace GodDB.Core.Ai;

/// 테이블 · 컬럼 · 데이터(코드 값) 설명 JSON (Mac 1.4.3 #178, TableDescriptions.swift).
/// 소스 코드를 본 Claude 등에게 분석을 받아 만들고, 접속 · DB 마다 가져와 두면 AI 어시스턴트 · 데이터 찾기가 참고한다.
/// 데이터 행은 담지 않는다 — 코드 값의 뜻(values)만.
/// Swift 의 값 타입(struct)을 옮긴 것이라, 복사가 필요한 곳은 Clone() 을 쓴다. 같음 비교는 JSON 내용으로 한다.
public sealed class TableDescriptionDoc : IEquatable<TableDescriptionDoc>
{
    public const string FormatName = "goddb-table-descriptions";
    public const int CurrentVersion = 1;

    public sealed class Column
    {
        public string Name { get; set; }
        /// 업무 이름 (논리명)
        public string? Title { get; set; }
        public string? Description { get; set; }
        /// 이 컬럼을 찾을 때 쓰는 말 (예: "학년", "grade")
        public List<string>? Terms { get; set; }
        /// 코드 값의 뜻: {"P": "결제 대기", "C": "완료"}
        public Dictionary<string, string>? Values { get; set; }
        /// DB 에 FK 가 없어도 코드에서 가리키는 곳: "members.id"
        public string? References { get; set; }

        public Column(string name, string? title = null, string? description = null, IEnumerable<string>? terms = null,
                      IDictionary<string, string>? values = null, string? references = null)
        {
            Name = name;
            Title = title;
            Description = description;
            Terms = terms?.ToList();
            Values = values == null ? null : new Dictionary<string, string>(values);
            References = references;
        }

        public Column Clone() => new(Name, Title, Description, Terms, Values, References);
    }

    public sealed class Table
    {
        public string Name { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public List<string>? Terms { get; set; }
        public List<Column>? Columns { get; set; }
        /// 조회 · 사용 규칙 (예: "deleted_at IS NULL 조건을 붙인다")
        public List<string>? Notes { get; set; }

        public Table(string name, string? title = null, string? description = null, IEnumerable<string>? terms = null,
                     IEnumerable<Column>? columns = null, IEnumerable<string>? notes = null)
        {
            Name = name;
            Title = title;
            Description = description;
            Terms = terms?.ToList();
            Columns = columns?.ToList();
            Notes = notes?.ToList();
        }

        public Column? Column(string name) =>
            Columns?.FirstOrDefault(c => string.Equals(c.Name.ToLowerInvariant(), name.ToLowerInvariant(), StringComparison.Ordinal));

        public Table Clone() => new(Name, Title, Description, Terms, Columns?.Select(c => c.Clone()), Notes);
    }

    public string? Format { get; set; } = FormatName;
    public int? Version { get; set; } = CurrentVersion;
    /// DB(스키마) 이름. 가져올 때 비어 있으면 지금 DB
    public string? Database { get; set; }
    public string? GeneratedBy { get; set; }
    public string? GeneratedAt { get; set; }
    /// DB 전체 요약 (업무 도메인)
    public string? Overview { get; set; }
    /// 빈 양식에만: 채워 줄 AI 에이전트에게 주는 할 일 (가져올 때는 저장하지 않는다)
    public string? Instructions { get; set; }
    public List<Table> Tables { get; set; }

    public TableDescriptionDoc(string? database = null, string? generatedBy = null, string? generatedAt = null, string? overview = null,
                               IEnumerable<Table>? tables = null)
    {
        Database = database;
        GeneratedBy = generatedBy;
        GeneratedAt = generatedAt;
        Overview = overview;
        Tables = tables?.ToList() ?? [];
    }

    public TableDescriptionDoc Clone() => new(Database, GeneratedBy, GeneratedAt, Overview, Tables.Select(t => t.Clone()))
    {
        Format = Format,
        Version = Version,
        Instructions = Instructions,
    };

    public Table? FindTable(string name)
    {
        var key = Bare(name);
        return Tables.FirstOrDefault(t => Bare(t.Name) == key);
    }

    /// `public.orders` · `orders` 를 같게 본다
    internal static string Bare(string name) =>
        (name.Split('.').LastOrDefault() ?? name).ToLowerInvariant();

    public int ColumnCount => Tables.Sum(t => t.Columns?.Count ?? 0);
    public int ValueCount => Tables.Sum(t => (t.Columns ?? []).Sum(c => c.Values?.Count ?? 0));

    public bool Equals(TableDescriptionDoc? other) =>
        other is not null && Encoded().AsSpan().SequenceEqual(other.Encoded());
    public override bool Equals(object? obj) => Equals(obj as TableDescriptionDoc);
    public override int GetHashCode() => Tables.Count;

    // MARK: 읽기 · 쓰기

    public sealed class ParseException : Exception, IEquatable<ParseException>
    {
        public enum ErrorKind { NotJson, Invalid, WrongFormat, NewerVersion, Empty }

        public ErrorKind Kind { get; }
        /// NotJson: 깨진 위치, Invalid: 경로, WrongFormat: format 값
        public string Detail { get; }
        public string Reason { get; }
        public int NewVersion { get; }

        private ParseException(ErrorKind kind, string detail = "", string reason = "", int version = 0)
            : base(Describe(kind, detail, reason, version))
        {
            Kind = kind;
            Detail = detail;
            Reason = reason;
            NewVersion = version;
        }

        public static ParseException NotJson(string where) => new(ErrorKind.NotJson, where);
        public static ParseException Invalid(string path, string reason) => new(ErrorKind.Invalid, path, reason);
        public static ParseException WrongFormat(string format) => new(ErrorKind.WrongFormat, format);
        public static ParseException NewerVersion(int v) => new(ErrorKind.NewerVersion, version: v);
        public static ParseException Empty() => new(ErrorKind.Empty);

        private static string Describe(ErrorKind kind, string detail, string reason, int version) => kind switch
        {
            ErrorKind.NotJson => Tr("JSON 으로 읽지 못했어요: %@", detail),
            ErrorKind.Invalid => Tr("양식이 맞지 않아요: %@ — %@", detail, reason),
            ErrorKind.WrongFormat => Tr("테이블 설명 파일이 아니에요 (format: %@)", detail),
            ErrorKind.NewerVersion => Tr("더 새 버전(%@)의 양식이에요. GodDB 를 업데이트해 주세요.", version.ToString(CultureInfo.InvariantCulture)),
            _ => Tr("tables 가 비어 있어요."),
        };

        public bool Equals(ParseException? other) =>
            other is not null && Kind == other.Kind && Detail == other.Detail && Reason == other.Reason && NewVersion == other.NewVersion;
        public override bool Equals(object? obj) => Equals(obj as ParseException);
        public override int GetHashCode() => HashCode.Combine(Kind, Detail, Reason, NewVersion);
    }

    public static TableDescriptionDoc Parse(byte[] data) => Parse(Encoding.UTF8.GetString(data));

    public static TableDescriptionDoc Parse(string json)
    {
        JsonDocument jd;
        try
        {
            jd = JsonDocument.Parse(json.TrimStart('﻿'), new JsonDocumentOptions { AllowTrailingCommas = false });
        }
        catch (JsonException e)
        {
            throw ParseException.NotJson(e.Message);
        }
        TableDescriptionDoc doc;
        using (jd) doc = Reader.ReadDoc(jd.RootElement);
        if (doc.Format is { } f && f != FormatName) throw ParseException.WrongFormat(f);
        if (doc.Version is { } v && v > CurrentVersion) throw ParseException.NewerVersion(v);
        if (doc.Tables.Count == 0) throw ParseException.Empty();
        for (var i = 0; i < doc.Tables.Count; i++)
            if (doc.Tables[i].Name.Trim().Length == 0) throw ParseException.Invalid($"tables[{i}].name", Tr("비어 있어요"));
        return doc;
    }

    /// Swift JSONDecoder 의 오류(keyNotFound · typeMismatch)와 같은 경로 · 이유를 내는 읽기
    private static class Reader
    {
        private static string P(string path) => path.Length == 0 ? Tr("(맨 위)") : path;
        private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

        private static ParseException Mismatch(string path, string typeName) =>
            ParseException.Invalid(P(path), Tr("%@ 이어야 해요", typeName));

        private static string TString => Tr("글자");
        private static string TInt => Tr("숫자");
        private static string TStringList => Tr("글자 목록 [\"…\"]");
        private static string TMap => Tr("{\"값\": \"뜻\"} 묶음");
        private static string TOther => Tr("목록 또는 묶음");

        private static JsonElement Object(JsonElement e, string path)
        {
            if (e.ValueKind != JsonValueKind.Object) throw Mismatch(path, TOther);
            return e;
        }

        private static bool Has(JsonElement obj, string key, out JsonElement v) =>
            obj.TryGetProperty(key, out v) && v.ValueKind != JsonValueKind.Null;

        private static string? OptString(JsonElement obj, string key, string path)
        {
            if (!Has(obj, key, out var v)) return null;
            if (v.ValueKind != JsonValueKind.String) throw Mismatch(Join(path, key), TString);
            return v.GetString();
        }

        private static string ReqString(JsonElement obj, string key, string path)
        {
            if (!obj.TryGetProperty(key, out var v)) throw ParseException.Invalid(Join(path, key), Tr("꼭 있어야 하는 칸이 없어요"));
            if (v.ValueKind != JsonValueKind.String) throw Mismatch(Join(path, key), TString);
            return v.GetString() ?? "";
        }

        private static int? OptInt(JsonElement obj, string key, string path)
        {
            if (!Has(obj, key, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n)) throw Mismatch(Join(path, key), TInt);
            return n;
        }

        private static List<string>? OptStrings(JsonElement obj, string key, string path)
        {
            if (!Has(obj, key, out var v)) return null;
            var p = Join(path, key);
            if (v.ValueKind != JsonValueKind.Array) throw Mismatch(p, TStringList);
            var list = new List<string>();
            var i = 0;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw Mismatch($"{p}[{i}]", TString);
                list.Add(item.GetString() ?? "");
                i++;
            }
            return list;
        }

        private static Dictionary<string, string>? OptMap(JsonElement obj, string key, string path)
        {
            if (!Has(obj, key, out var v)) return null;
            var p = Join(path, key);
            if (v.ValueKind != JsonValueKind.Object) throw Mismatch(p, TMap);
            var map = new Dictionary<string, string>();
            foreach (var prop in v.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) throw Mismatch(Join(p, prop.Name), TString);
                map[prop.Name] = prop.Value.GetString() ?? "";
            }
            return map;
        }

        private static List<T>? OptObjects<T>(JsonElement obj, string key, string path, Func<JsonElement, string, T> read)
        {
            if (!Has(obj, key, out var v)) return null;
            var p = Join(path, key);
            if (v.ValueKind != JsonValueKind.Array) throw Mismatch(p, TOther);
            var list = new List<T>();
            var i = 0;
            foreach (var item in v.EnumerateArray())
            {
                list.Add(read(item, $"{p}[{i}]"));
                i++;
            }
            return list;
        }

        public static TableDescriptionDoc ReadDoc(JsonElement root)
        {
            var o = Object(root, "");
            if (!o.TryGetProperty("tables", out _)) throw ParseException.Invalid("tables", Tr("꼭 있어야 하는 칸이 없어요"));
            if (o.GetProperty("tables").ValueKind == JsonValueKind.Null) throw Mismatch("tables", TOther);
            var doc = new TableDescriptionDoc
            {
                Format = OptString(o, "format", ""),
                Version = OptInt(o, "version", ""),
                Database = OptString(o, "database", ""),
                GeneratedBy = OptString(o, "generatedBy", ""),
                GeneratedAt = OptString(o, "generatedAt", ""),
                Overview = OptString(o, "overview", ""),
                Instructions = OptString(o, "instructions", ""),
                Tables = OptObjects(o, "tables", "", ReadTable) ?? [],
            };
            return doc;
        }

        private static Table ReadTable(JsonElement e, string path)
        {
            var o = Object(e, path);
            return new Table(ReqString(o, "name", path), OptString(o, "title", path), OptString(o, "description", path),
                             OptStrings(o, "terms", path), OptObjects(o, "columns", path, ReadColumn), OptStrings(o, "notes", path));
        }

        private static Column ReadColumn(JsonElement e, string path)
        {
            var o = Object(e, path);
            return new Column(ReqString(o, "name", path), OptString(o, "title", path), OptString(o, "description", path),
                              OptStrings(o, "terms", path), OptMap(o, "values", path), OptString(o, "references", path));
        }
    }

    /// Swift JSONEncoder [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes] 와 같은 모양 (빈 칸은 빼고, 키는 정렬)
    public byte[] Encoded()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions)) Write(w);
        return ms.ToArray();
    }

    internal static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        Str(w, "database", Database);
        Str(w, "format", Format);
        Str(w, "generatedAt", GeneratedAt);
        Str(w, "generatedBy", GeneratedBy);
        Str(w, "instructions", Instructions);
        Str(w, "overview", Overview);
        w.WriteStartArray("tables");
        foreach (var t in Tables)
        {
            w.WriteStartObject();
            if (t.Columns != null)
            {
                w.WriteStartArray("columns");
                foreach (var c in t.Columns)
                {
                    w.WriteStartObject();
                    Str(w, "description", c.Description);
                    w.WriteString("name", c.Name);
                    Str(w, "references", c.References);
                    List(w, "terms", c.Terms);
                    Str(w, "title", c.Title);
                    if (c.Values != null)
                    {
                        w.WriteStartObject("values");
                        foreach (var kv in c.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal)) w.WriteString(kv.Key, kv.Value);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            Str(w, "description", t.Description);
            w.WriteString("name", t.Name);
            List(w, "notes", t.Notes);
            List(w, "terms", t.Terms);
            Str(w, "title", t.Title);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (Version is { } v) w.WriteNumber("version", v);
        w.WriteEndObject();

        static void Str(Utf8JsonWriter w, string key, string? v) { if (v != null) w.WriteString(key, v); }
        static void List(Utf8JsonWriter w, string key, List<string>? v)
        {
            if (v == null) return;
            w.WriteStartArray(key);
            foreach (var s in v) w.WriteStringValue(s);
            w.WriteEndArray();
        }
    }

    // MARK: 빈 양식 · 분석 요청문

    private static string? FirstLine(string s)
    {
        var l = (s.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "").Trim(' ', '\t');
        return l.Length == 0 ? null : l;
    }

    /// 지금 테이블 구조로 빈 양식 (이름 · 코멘트 · FK 만, 데이터 값은 넣지 않는다)
    public static TableDescriptionDoc Skeleton(IReadOnlyList<TableMeta> tables, string database)
    {
        var output = tables.Select(t =>
        {
            var fks = t.ForeignKeys;
            var cols = t.Columns.OrderBy(c => c.Position).Select(c =>
            {
                var fk = fks.FirstOrDefault(f => f.Columns.Count == 1 && f.Columns[0].ToLowerInvariant() == c.Name.ToLowerInvariant());
                return new Column(c.Name, FirstLine(c.Comment), references: fk == null ? null : $"{fk.ReferencedTable}.{fk.ReferencedColumns.FirstOrDefault() ?? ""}");
            });
            return new Table(t.Name, FirstLine(t.Comment), columns: cols);
        });
        return new TableDescriptionDoc(database, tables: output);
    }

    /// 내려받는 빈 양식: 지금 구조(+ 이미 있는 설명) 에 AI 에이전트가 할 일(instructions)을 넣는다
    public static TableDescriptionDoc Form(IReadOnlyList<TableMeta> tables, string database, DatabaseKind kind, bool english,
                                           TableDescriptionDoc? current = null)
    {
        var doc = Skeleton(tables, database);
        if (current != null)
        {
            doc.Overview = current.Overview;
            doc.Tables = doc.Tables.Select(t =>
            {
                if (current.FindTable(t.Name) is not { } old) return t;
                var merged = old.Clone();
                var cols = merged.Columns ?? [];
                foreach (var c in t.Columns ?? [])
                    if (!cols.Any(x => x.Name.ToLowerInvariant() == c.Name.ToLowerInvariant())) cols.Add(c);
                merged.Columns = cols;
                if (string.IsNullOrEmpty(merged.Title)) merged.Title = t.Title;
                return merged;
            }).ToList();
        }
        doc.Instructions = RequestPrompt(database, kind, english);
        return doc;
    }

    /// 빈 양식의 instructions: 양식을 받은 AI 에이전트(Claude · Codex 등)가 할 일
    public static string RequestPrompt(string database, DatabaseKind kind, bool english)
    {
        if (english)
            return $"""
                This file is a form for GodDB's AI assistant. Read the source code of the repository you were given and describe every table of the {kind.Title()} database `{database}` by filling in this file. Return only the filled JSON in the same format.
                - Keep format "{FormatName}", version {CurrentVersion}, database "{database}". Set generatedBy (your name) and generatedAt (YYYY-MM-DD). Remove this instructions field.
                - overview: the business domain of this database in 2-3 sentences.
                - tables[].title: business name, description: what one row means, terms: words people use to look for it.
                - tables[].columns[]: title, description (only when not obvious), terms, references ("table.column" for relations the code uses even without a foreign key).
                - columns[].values: meaning of code values, e.g. {"{"}"P": "pending", "C": "completed"{"}"}, taken from enums and constants in the code.
                - tables[].notes: rules for querying (soft delete, status filters, which column joins which).
                Rules: use only table and column names that are already in this file, base everything on the code, and leave a field out when unsure.
                """;
        return $"""
            이 파일은 GodDB AI 어시스턴트용 양식이에요. 받은 저장소의 소스 코드를 보고 {kind.Title()} DB `{database}` 의 모든 테이블을 이 파일에 채워서 설명해 주세요. 같은 양식의 JSON 만 돌려주세요.
            - format "{FormatName}", version {CurrentVersion}, database "{database}" 는 그대로. generatedBy (에이전트 이름), generatedAt (YYYY-MM-DD) 를 채우고 이 instructions 칸은 지우기.
            - overview: 이 DB 의 업무 도메인을 2~3문장으로.
            - tables[].title: 업무 이름, description: 행 하나가 무엇인지, terms: 사람들이 이 데이터를 찾을 때 쓰는 말.
            - tables[].columns[]: title, description (뻔하지 않을 때만), terms, references (FK 가 없어도 코드에서 잇는 곳 "테이블.컬럼").
            - columns[].values: 코드 값의 뜻, 예 {"{"}"P": "결제 대기", "C": "완료"{"}"} — 코드의 enum · 상수에서.
            - tables[].notes: 조회할 때 규칙 (소프트 삭제, 상태 조건, 어떤 컬럼으로 잇는지).
            규칙: 이 파일에 이미 있는 테이블 · 컬럼 이름만 쓰고, 코드에 근거해서 쓰고, 확실하지 않은 칸은 빼기.
            """;
    }

    // MARK: DB 와 맞춰 보기

    public sealed record CheckResult
    {
        /// JSON 에 있는데 DB 에 없는 테이블
        public List<string> MissingTables { get; init; } = [];
        /// DB 에 있는데 JSON 에 없는 테이블
        public List<string> UndescribedTables { get; init; } = [];
        /// JSON 에 있는데 DB 에 없는 컬럼 (table.column)
        public List<string> UnknownColumns { get; init; } = [];

        public bool IsClean => MissingTables.Count == 0 && UnknownColumns.Count == 0;
    }

    public CheckResult Check(IReadOnlyList<TableMeta> meta)
    {
        var c = new CheckResult();
        var byName = new Dictionary<string, TableMeta>();
        foreach (var m in meta) byName.TryAdd(Bare(m.Name), m);
        foreach (var t in Tables)
        {
            if (!byName.TryGetValue(Bare(t.Name), out var m)) { c.MissingTables.Add(t.Name); continue; }
            var cols = m.Columns.Select(x => x.Name.ToLowerInvariant()).ToHashSet();
            foreach (var col in t.Columns ?? [])
                if (!cols.Contains(col.Name.ToLowerInvariant())) c.UnknownColumns.Add($"{t.Name}.{col.Name}");
        }
        var described = Tables.Select(t => Bare(t.Name)).ToHashSet();
        c.UndescribedTables.AddRange(meta.Select(m => m.Name).Where(n => !described.Contains(Bare(n))));
        return c;
    }

    // MARK: AI 에 보낼 것

    /// 코멘트가 빈 테이블 · 컬럼에 title 을 논리명으로 채운 구조 (테이블 목록 · 질문 속 이름 찾기 · 구조 글에 그대로 쓰인다)
    public List<TableMeta> ApplyTitles(IReadOnlyList<TableMeta> meta) => meta.Select(t =>
    {
        if (FindTable(t.Name) is not { } d) return t;
        var comment = t.Comment.Trim(' ', '\t').Length == 0 && !string.IsNullOrEmpty(d.Title) ? d.Title : t.Comment;
        var cols = t.Columns.Select(c =>
            c.Comment.Trim(' ', '\t').Length == 0 && d.Column(c.Name)?.Title is { Length: > 0 } title ? c with { Comment = title } : c).ToList();
        return t with { Comment = comment, Columns = cols };
    }).ToList();

    /// 질문에서 테이블을 찾을 때 쓸 다른 이름 (소문자 테이블 이름 → title · terms)
    public Dictionary<string, List<string>> Aliases
    {
        get
        {
            var output = new Dictionary<string, List<string>>();
            foreach (var t in Tables)
            {
                var names = new[] { t.Title }.OfType<string>().Concat(t.Terms ?? []).Where(s => s.Length > 0).ToList();
                if (names.Count == 0) continue;
                var key = Bare(t.Name);
                if (!output.TryGetValue(key, out var list)) output[key] = list = [];
                list.AddRange(names);
            }
            return output;
        }
    }

    private static string Prefix(string s, int n) => s.Length <= n ? s : s[..n];

    /// 자세히 볼 테이블의 설명 (+ DB 요약). 길이 제한 안에서.
    public string? ContextText(IEnumerable<string> focus, int limit = 8_000)
    {
        var s = "";
        if (!string.IsNullOrEmpty(Overview)) s += $"Database overview (from the team's table descriptions): {Prefix(Overview, 600)}\n";
        var body = "";
        foreach (var name in focus)
        {
            if (FindTable(name) is not { } t) continue;
            var block = $"  {t.Name}";
            if (!string.IsNullOrEmpty(t.Title)) block += $" — {t.Title}";
            if (!string.IsNullOrEmpty(t.Description)) block += $": {Prefix(t.Description, 300)}";
            block += "\n";
            if (t.Terms is { Count: > 0 } terms) block += $"    also called: {string.Join(", ", terms)}\n";
            foreach (var c in t.Columns ?? [])
            {
                var line = "";
                if (!string.IsNullOrEmpty(c.Description)) line += $" {Prefix(c.Description, 200)}";
                if (c.Values is { Count: > 0 } v)
                    line += " values: " + string.Join(", ", v.OrderBy(kv => kv.Key, StringComparer.Ordinal).Take(30).Select(kv => $"{kv.Key}={kv.Value}"));
                if (!string.IsNullOrEmpty(c.References)) line += $" → {c.References}";
                if (c.Terms is { Count: > 0 } ct) line += $" (also called {string.Join(", ", ct)})";
                if (line.Length == 0) continue;
                block += $"    .{c.Name}{(c.Title != null ? $" [{c.Title}]" : "")}:{line}\n";
            }
            foreach (var n in t.Notes ?? []) block += $"    rule: {Prefix(n, 200)}\n";
            if (body.Length + block.Length > limit) { body += "  … (more descriptions truncated)\n"; break; }
            body += block;
        }
        if (body.Length > 0) s += "Table descriptions written by the team (business meaning, code values, rules):\n" + body;
        return s.Length == 0 ? null : s;
    }

    // MARK: 데이터 찾기

    /// 데이터 찾기가 쓰는 "찾는 말 → 위치" 목록 (title · terms). id 는 위치마다 늘 같다.
    public List<TableNote> FinderNotes(Guid profileId, string database)
    {
        var output = new List<TableNote>();
        var epoch = DateTime.UnixEpoch;
        foreach (var t in Tables)
        {
            var tTerms = new[] { t.Title }.OfType<string>().Concat(t.Terms ?? []).Where(s => s.Length > 0).ToList();
            if (tTerms.Count > 0)
                output.Add(new TableNote(profileId, database, t.Name, [], tTerms, t.Description ?? "", TableNote.NoteSource.User, true,
                                         epoch, id: StableId(profileId, database, t.Name, [])));
            foreach (var c in t.Columns ?? [])
            {
                var cTerms = new[] { c.Title }.OfType<string>().Concat(c.Terms ?? []).Where(s => s.Length > 0).ToList();
                if (cTerms.Count == 0) continue;
                output.Add(new TableNote(profileId, database, t.Name, [c.Name], cTerms, c.Description ?? "", TableNote.NoteSource.User, true,
                                         epoch, id: StableId(profileId, database, t.Name, [c.Name])));
            }
        }
        return output;
    }

    /// 위치마다 늘 같은 id (Mac 과 같은 FNV-1a 두 번, 바이트 순서 그대로)
    internal static Guid StableId(Guid profileId, string database, string table, IReadOnlyList<string> columns)
    {
        var key = $"{profileId.ToString().ToUpperInvariant()}|{database}|{table.ToLowerInvariant()}|{string.Join(",", columns.Select(c => c.ToLowerInvariant()))}";
        var bytes = new byte[16];
        var utf8 = Encoding.UTF8.GetBytes(key);
        foreach (var (half, seed) in new[] { (0, 0xcbf29ce484222325UL), (8, 0x84222325cbf29ce4UL) })
        {
            var h = seed;
            foreach (var b in utf8) h = unchecked((h ^ b) * 0x100000001b3UL);
            for (var i = 0; i < 8; i++) bytes[half + i] = (byte)(h >> (8 * i));
        }
        return new Guid(bytes, bigEndian: true);
    }

    /// [맞아요]: 찾는 말을 그 위치(컬럼이 있으면 컬럼마다, 없으면 테이블)에 더한다. 없는 테이블 · 컬럼이면 만든다.
    public void AddTerms(IReadOnlyList<string> terms, string table, IReadOnlyList<string> columns)
    {
        var t = Tables.FirstOrDefault(x => Bare(x.Name) == Bare(table));
        if (t == null)
        {
            t = new Table(table);
            Tables.Add(t);
        }
        if (columns.Count == 0)
        {
            t.Terms = TableNote.MergeTerms(t.Terms ?? [], terms);
            return;
        }
        foreach (var col in columns)
        {
            var cols = t.Columns ?? [];
            if (cols.FirstOrDefault(c => c.Name.ToLowerInvariant() == col.ToLowerInvariant()) is { } found)
                found.Terms = TableNote.MergeTerms(found.Terms ?? [], terms);
            else
                cols.Add(new Column(col, terms: TableNote.MergeTerms([], terms)));
            t.Columns = cols;
        }
    }

    /// [아니에요]: 그 위치에서 찾는 말을 뺀다 (title 은 그대로)
    public void RemoveTerms(IReadOnlyList<string> terms, string table, IReadOnlyList<string> columns)
    {
        var drop = terms.Select(DataFinder.Normalize).ToHashSet();
        List<string>? Keep(List<string>? list)
        {
            var kept = (list ?? []).Where(x => !drop.Contains(DataFinder.Normalize(x))).ToList();
            return kept.Count == 0 ? null : kept;
        }
        if (Tables.FirstOrDefault(x => Bare(x.Name) == Bare(table)) is not { } t) return;
        if (columns.Count == 0)
        {
            t.Terms = Keep(t.Terms);
            return;
        }
        var names = columns.Select(c => c.ToLowerInvariant()).ToHashSet();
        foreach (var c in t.Columns ?? [])
            if (names.Contains(c.Name.ToLowerInvariant())) c.Terms = Keep(c.Terms);
    }

    // Mac 의 merge(notes:) (예전 테이블 메모 옮기기)는 옮기지 않는다 — TableNote 참고
}

/// 접속 · DB 하나의 테이블 설명
public sealed class TableDescriptionEntry
{
    public Guid ProfileId { get; set; }
    public string Database { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// 가져온 파일 이름 (직접 고친 것이면 빈칸)
    public string FileName { get; set; }
    public TableDescriptionDoc Doc { get; set; }

    public TableDescriptionEntry(Guid profileId, string database, TableDescriptionDoc doc, DateTime? updatedAt = null, string fileName = "")
    {
        ProfileId = profileId;
        Database = database;
        Doc = doc;
        UpdatedAt = updatedAt ?? DateTime.UtcNow;
        FileName = fileName;
    }

    public TableDescriptionEntry Clone() => new(ProfileId, Database, Doc.Clone(), UpdatedAt, FileName);
}

public sealed class TableDescriptionLibrary : IEquatable<TableDescriptionLibrary>
{
    public int Version { get; set; } = 1;
    public List<TableDescriptionEntry> Entries { get; set; }
    /// Mac 이 예전 테이블 메모(table-notes.json)를 옮겼는지 적어 두는 칸. Windows 판은 옮길 메모가 없어 늘 true 로 쓴다
    /// (같은 파일을 Mac 에서 읽어도 옮기기를 다시 하지 않게).
    public bool MigratedNotes { get; set; } = true;

    public TableDescriptionLibrary(IEnumerable<TableDescriptionEntry>? entries = null) => Entries = entries?.ToList() ?? [];

    public TableDescriptionEntry? Entry(Guid profileId, string database) =>
        Entries.FirstOrDefault(e => e.ProfileId == profileId && e.Database == database);

    public TableDescriptionDoc? Doc(Guid profileId, string database) => Entry(profileId, database)?.Doc;

    public void Set(TableDescriptionEntry entry)
    {
        var i = Entries.FindIndex(e => e.ProfileId == entry.ProfileId && e.Database == entry.Database);
        if (i >= 0) Entries[i] = entry; else Entries.Add(entry);
    }

    public void Remove(Guid profileId, string database) =>
        Entries.RemoveAll(e => e.ProfileId == profileId && e.Database == database);

    /// 설명을 고친다 (없으면 빈 설명에서 시작)
    public void Edit(Guid profileId, string database, Action<TableDescriptionDoc> change, DateTime? now = null)
    {
        var e = Entry(profileId, database)?.Clone()
                ?? new TableDescriptionEntry(profileId, database, new TableDescriptionDoc(database), now ?? DateTime.UtcNow);
        change(e.Doc);
        e.UpdatedAt = now ?? DateTime.UtcNow;
        Set(e);
    }

    public bool Equals(TableDescriptionLibrary? other) =>
        other is not null && TableDescriptionStore.Encode(this).AsSpan().SequenceEqual(TableDescriptionStore.Encode(other));
    public override bool Equals(object? obj) => Equals(obj as TableDescriptionLibrary);
    public override int GetHashCode() => Entries.Count;
}

/// table-descriptions.json (앱 데이터 폴더 %APPDATA%\GodDB, 본인만 읽고 쓰게). 읽기 · 쓰기는 이 클래스 한 곳에서만 한다.
/// 형식은 Mac 과 같다 (키 정렬, 날짜 ISO 8601 초 단위).
public sealed class TableDescriptionStore
{
    private static readonly Lazy<TableDescriptionStore> SharedStore =
        new(() => new TableDescriptionStore(Path.Combine(AppPaths.DataDirectory, "table-descriptions.json")));
    public static TableDescriptionStore Shared => SharedStore.Value;

    public string FilePath { get; }

    public TableDescriptionStore(string filePath) => FilePath = filePath;

    public static TableDescriptionLibrary Decode(string json)
    {
        using var jd = JsonDocument.Parse(json.TrimStart('﻿'));
        var root = jd.RootElement;
        var lib = new TableDescriptionLibrary();
        if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number) lib.Version = v.GetInt32();
        if (root.TryGetProperty("migratedNotes", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False) lib.MigratedNotes = m.GetBoolean();
        foreach (var e in root.GetProperty("entries").EnumerateArray())
        {
            var doc = TableDescriptionDoc.Parse(e.GetProperty("doc").GetRawText());
            var updated = DateTimeOffset.Parse(e.GetProperty("updatedAt").GetString() ?? "", CultureInfo.InvariantCulture,
                                               DateTimeStyles.AssumeUniversal).UtcDateTime;
            lib.Entries.Add(new TableDescriptionEntry(e.GetProperty("profileID").GetGuid(), e.GetProperty("database").GetString() ?? "",
                                                      doc, updated, e.TryGetProperty("fileName", out var f) ? f.GetString() ?? "" : ""));
        }
        return lib;
    }

    public static byte[] Encode(TableDescriptionLibrary library)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, TableDescriptionDoc.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteStartArray("entries");
            foreach (var e in library.Entries)
            {
                w.WriteStartObject();
                w.WriteString("database", e.Database);
                w.WritePropertyName("doc");
                e.Doc.Write(w);
                w.WriteString("fileName", e.FileName);
                w.WriteString("profileID", e.ProfileId.ToString().ToUpperInvariant());
                w.WriteString("updatedAt", Iso8601SecondsConverter.Format(new DateTimeOffset(DateTime.SpecifyKind(e.UpdatedAt, DateTimeKind.Utc))));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteBoolean("migratedNotes", library.MigratedNotes);
            w.WriteNumber("version", library.Version);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// 없거나 읽지 못하면 null
    public TableDescriptionLibrary? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return Decode(File.ReadAllText(FilePath, Encoding.UTF8));
        }
        catch { return null; }
    }

    public void Save(TableDescriptionLibrary library)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) AppPaths.Ensure(dir);
            var data = Encode(library);
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, data);
            UserOnlyFile.Restrict(tmp);
            File.Move(tmp, FilePath, overwrite: true);
            UserOnlyFile.Restrict(FilePath);
        }
        catch { }
    }
}
