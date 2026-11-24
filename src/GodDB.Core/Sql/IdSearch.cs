using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodDB.Core.Dialects;
using GodDB.Core.Platform;
using GodDB.Core.Profiles;
using static GodDB.Core.L10n;

namespace GodDB.Core.Sql;

/// ID 로 찾기 (Ctrl+Alt+F): 로그에서 복사한 글에서 ID 를 뽑아 `SELECT * FROM t WHERE c IN (…)` 로 조회하고,
/// 결과에 없는 ID 를 알려 준다. (Mac IDSearch.swift)
public static partial class IdSearch
{
    /// 한 번에 조회하는 값의 최대 개수
    public const int MaxValues = 1_000;
    /// 미리보기로 보여 주는 값 개수
    public const int PreviewCount = 20;
    /// 자동으로 올리는 결과 행 수 제한의 최대값
    public const int MaxRowLimit = 5_000;

    /// 붙여넣은 글에서 뽑은 값. Values: 조회에 쓰는 값 (순서 유지 · 중복 제거 · 최대 MaxValues 개),
    /// Duplicates: 중복이라 뺀 개수, Overflow: MaxValues 를 넘어서 뺀 개수
    public sealed record Extraction(IReadOnlyList<string> Values, int Duplicates, int Overflow)
    {
        /// 중복을 뺀 전체 개수 (넘친 것 포함)
        public int UniqueCount => Values.Count + Overflow;
    }

    // MARK: - 컬럼 타입

    private static readonly HashSet<string> NumericTypes =
    [
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint",
        "decimal", "numeric", "float", "double", "real", "double precision", "number",
        "int2", "int4", "int8", "float4", "float8", "serial", "smallserial", "bigserial",
        "money", "smallmoney",
    ];

    /// 정수 · 소수 타입인지 (`bigint(20) unsigned`, `numeric(10,2)`, `int4` 등)
    public static bool IsNumericType(string columnType)
    {
        var t = columnType.ToLowerInvariant();
        var p = t.IndexOf('(');
        if (p >= 0) t = t[..p];
        foreach (var word in new[] { " unsigned", " signed", " zerofill", " identity" }) t = t.Replace(word, "");
        return NumericTypes.Contains(t.Trim(' ', '\t'));
    }

    // MARK: - 값 뽑기

    // Mac NSRegularExpression(ICU) 과 같게: \d · \w 는 유니코드 기준
    [GeneratedRegex(@"(?<![\w.-])-?\d+(?![\w.])")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9_\-:.]{0,99}")]
    private static partial Regex TokenRegex();

    /// 날짜 (ISO 8601 날짜+시각 포함) · 시각 꼴
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}(?::\d{2}(?:[.,]\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)?|\d{2}:\d{2}:\d{2}(?:[.,]\d+)?")]
    private static partial Regex DateTimeRegex();

    private static readonly HashSet<string> LevelWords = ["INFO", "WARN", "WARNING", "ERROR", "DEBUG", "TRACE"];

    /// 글에서 ID 를 뽑는다.
    /// <param name="text">붙여넣은 글 (로그 등)</param>
    /// <param name="numeric">숫자 컬럼이고 "문자 ID 포함" 을 켜지 않았으면 true → 숫자만</param>
    public static Extraction Extract(string text, bool numeric)
    {
        var raw = new List<string>();
        if (numeric)
        {
            // 날짜 · 시각 안의 숫자(2026, 10 …)는 빼고
            var dates = DateTimeRegex().Matches(text).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
            foreach (Match m in NumberRegex().Matches(text))
            {
                var end = m.Index + m.Length;
                if (dates.Any(d => m.Index < d.End && d.Index < end)) continue;
                raw.Add(m.Value);
            }
        }
        else
        {
            foreach (Match m in TokenRegex().Matches(text))
            {
                // 문장 끝의 . : - 는 ID 가 아니다 ("id=A-77." → A-77)
                var token = m.Value.TrimEnd('.', ':', '-');
                if (token.Length == 0 || IsDateTime(token) || LevelWords.Contains(token.ToUpperInvariant())) continue;
                raw.Add(token);
            }
        }
        var seen = new HashSet<string>();
        var unique = new List<string>();
        foreach (var v in raw)
            if (seen.Add(numeric ? Normalize(v) : v)) unique.Add(v);
        var used = unique.Take(MaxValues).ToList();
        return new Extraction(used, raw.Count - unique.Count, unique.Count - used.Count);
    }

    /// 글자 전체가 날짜 · 시각 꼴인지
    internal static bool IsDateTime(string token)
    {
        var m = DateTimeRegex().Match(token);
        return m.Success && m.Index == 0 && m.Length == token.Length;
    }

    /// 미리보기: 앞 PreviewCount 개 + "…외 N개"
    public static string Preview(IReadOnlyList<string> values)
    {
        var head = string.Join(", ", values.Take(PreviewCount));
        return values.Count > PreviewCount ? head + " " + Tr("…외 %@개", values.Count - PreviewCount) : head;
    }

    // MARK: - SQL

    /// `SELECT * FROM <테이블> WHERE <컬럼> IN (…)`. 숫자 컬럼은 따옴표 없이, 그 밖은 다이얼렉트 문자열 인용.
    public static string Sql(string table, string column, IReadOnlyList<string> values, bool numeric, ISqlDialect dialect)
    {
        var list = values.Select(v => Literal(v, numeric, dialect));
        return $"SELECT * FROM {dialect.QuoteTable(table)} WHERE {dialect.QuoteIdent(column)} IN ({string.Join(", ", list)})";
    }

    /// 탭 제목: `ID: <테이블>.<컬럼> (N)`
    public static string TabTitle(string table, string column, int count) => $"ID: {table}.{column} ({count})";

    /// 결과 행 수 제한: 평소 설정 그대로, 값이 더 많으면 값 개수 이상으로 (최대 MaxRowLimit). 0 은 "제한 없음".
    public static int RowLimit(int current, int valueCount) =>
        current <= 0 ? 0 : Math.Max(current, Math.Min(valueCount, MaxRowLimit));

    /// 값 하나의 SQL 글자: 숫자 컬럼이고 숫자 꼴이면 그대로, 그 밖은 다이얼렉트 문자열 인용 (MSSQL 은 N'…')
    public static string Literal(string value, bool numeric, ISqlDialect dialect) =>
        numeric && IsNumberLiteral(value) ? value : dialect.QuoteString(value);

    private static bool IsNumberLiteral(string s) => SqlParameters.IsNumber(s);

    // MARK: - 없는 ID

    /// 비교용 값: 대소문자 구분 없이, 숫자는 앞 0(과 소수점 뒤 끝 0)을 뗀다.
    public static string Normalize(string value)
    {
        var v = value.Trim(' ', '\t');
        if (!IsNumberLiteral(v)) return v.ToLowerInvariant();
        var negative = false;
        var s = v;
        if (s.StartsWith('-')) { negative = true; s = s[1..]; }
        var intPart = s;
        var frac = "";
        var dot = s.IndexOf('.');
        if (dot >= 0)
        {
            intPart = s[..dot];
            frac = s[(dot + 1)..].TrimEnd('0');
        }
        while (intPart.Length > 1 && intPart[0] == '0') intPart = intPart[1..];
        var output = intPart + (frac.Length == 0 ? "" : "." + frac);
        if (negative && output != "0") output = "-" + output;
        return output;
    }

    /// Missing: 결과에 없는 값 (요청한 순서)
    public sealed record Comparison(int Requested, int Found, IReadOnlyList<string> Missing);

    /// 요청한 값과 결과의 그 컬럼 값을 비교한다.
    public static Comparison Compare(IReadOnlyList<string> requested, IEnumerable<string?> found)
    {
        var have = new HashSet<string>(found.OfType<string>().Select(Normalize));
        var missing = requested.Where(r => !have.Contains(Normalize(r))).ToList();
        return new Comparison(requested.Count, requested.Count - missing.Count, missing);
    }
}

// MARK: - 마지막 선택 기억 (id-search.json)

/// 접속 · DB 별로 마지막에 쓴 테이블 · 컬럼. 형식: `{"version":1,"last":[{profileID,database,table,column,updatedAt}]}`
public sealed class IdSearchMemory
{
    public sealed record Entry(Guid ProfileId, string Database, string Table, string Column, DateTimeOffset UpdatedAt)
    {
        public Entry(Guid profileId, string database, string table, string column) : this(profileId, database, table, column, DateTimeOffset.Now) { }
    }

    public int Version { get; set; } = 1;
    public List<Entry> Last { get; set; } = [];

    public override bool Equals(object? obj) => obj is IdSearchMemory o && Version == o.Version && Last.SequenceEqual(o.Last);
    public override int GetHashCode() => Last.Count;

    /// 이 접속 · DB 에서 마지막으로 쓴 테이블
    public string? LastTable(Guid profileId, string database) =>
        Last.Where(e => e.ProfileId == profileId && e.Database == database).MaxBy(e => e.UpdatedAt)?.Table;

    /// 이 접속 · DB · 테이블에서 마지막으로 쓴 컬럼
    public string? LastColumn(Guid profileId, string database, string table) =>
        Last.FirstOrDefault(e => e.ProfileId == profileId && e.Database == database && e.Table == table)?.Column;

    /// 기록 (같은 접속 · DB · 테이블은 바꿔 쓴다). 오래된 것부터 지워 최대 maxEntries 개.
    public void Remember(Entry entry, int maxEntries = 500)
    {
        Last.RemoveAll(e => e.ProfileId == entry.ProfileId && e.Database == entry.Database && e.Table == entry.Table);
        Last.Add(entry);
        if (Last.Count > maxEntries)
        {
            Last = [.. Last.OrderBy(e => e.UpdatedAt)];
            Last.RemoveRange(0, Last.Count - maxEntries);
        }
    }
}

/// id-search.json (테이블 · 컬럼 이름만이라 평문 — Mac 과 같음)
public sealed class IdSearchStore(string file)
{
    private static readonly Lazy<IdSearchStore> SharedLazy = new(() => new IdSearchStore(Path.Combine(AppPaths.DataDirectory, "id-search.json")));
    public static IdSearchStore Shared => SharedLazy.Value;

    public string File { get; } = file;

    public IdSearchMemory Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new IdSearchMemory();
            var root = JsonNode.Parse(System.IO.File.ReadAllBytes(File)) as JsonObject;
            var m = new IdSearchMemory { Version = root?["version"]?.GetValue<int>() ?? 1 };
            if (root?["last"] is JsonArray arr)
            {
                foreach (var n in arr.OfType<JsonObject>())
                {
                    var date = DateTimeOffset.Parse(n["updatedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                    m.Last.Add(new IdSearchMemory.Entry(Guid.Parse(n["profileID"]!.GetValue<string>()), n["database"]!.GetValue<string>(),
                                                        n["table"]!.GetValue<string>(), n["column"]!.GetValue<string>(), date));
                }
            }
            return m;
        }
        catch { return new IdSearchMemory(); }
    }

    public void Save(IdSearchMemory memory)
    {
        try
        {
            var arr = new JsonArray();
            foreach (var e in memory.Last)
                arr.Add(new JsonObject
                {
                    ["column"] = e.Column,
                    ["database"] = e.Database,
                    ["profileID"] = e.ProfileId.ToString().ToUpperInvariant(),
                    ["table"] = e.Table,
                    ["updatedAt"] = Iso8601SecondsConverter.Format(e.UpdatedAt),
                });
            var root = new JsonObject { ["last"] = arr, ["version"] = memory.Version };
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), new UTF8Encoding(false));
            System.IO.File.Move(tmp, File, overwrite: true);
        }
        catch { }
    }
}
