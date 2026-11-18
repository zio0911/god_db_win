using System.Globalization;
using System.Text.RegularExpressions;
using GodDB.Core.Ai;
using GodDB.Core.Dialects;
using GodDB.Core.Schema;

namespace GodDB.Core.Drivers;

public sealed partial class DbSession
{
    /// 쿼리에 나온 테이블의 구조·인덱스·예상 행 수와 실행 계획을 모은다. (Mac: Tuning.swift 의 tuningContext)
    /// 실제 데이터 행은 읽지 않는다 — 통계상 예상 행 수와 EXPLAIN 만.
    public async Task<TuningContext> TuningContextAsync(string sql, IReadOnlyList<TableMeta> knownTables, CancellationToken ct = default)
    {
        string version;
        try { version = await ServerVersionAsync(ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { version = Profile.Kind.Title(); }

        var byName = new Dictionary<string, TableMeta>();
        foreach (var t in knownTables) byName.TryAdd(t.Name.ToLowerInvariant(), t);
        var seen = new HashSet<string>();
        var tables = new List<TuningContext.Table>();
        foreach (var name in TuningSqlAliases.Aliases(sql).Values)
        {
            if (!byName.TryGetValue(name.ToLowerInvariant(), out var meta) || !seen.Add(meta.Name)) continue;
            int? rows = null;
            try
            {
                var v = (await RawAsync(Dialect.RowEstimateSql(meta.Name, Current), ct)).Rows.FirstOrDefault()?.FirstOrDefault();
                var d = v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : -1;
                if (d >= 0 && d <= int.MaxValue) rows = (int)d;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            tables.Add(new TuningContext.Table(meta, Dialect.CreateTableSql(meta), rows));
        }

        QueryResult? plan = null;
        string? tree = null;
        string? planError = null;
        try
        {
            (plan, tree) = await ExplainAsync(sql, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Mac: ErrorText.describe(error).message — 오류 문장 정리기는 아직 포팅 전이라 예외 메시지를 그대로 쓴다
            planError = GodDB.Core.Audit.ErrorText.Describe(e).Message;
        }
        return new TuningContext(Profile.Kind, version, sql, tables, plan, tree,
                                 plan != null ? PlanAnalyzer.Warnings(plan) : [], planError);
    }
}

/// 구문에서 "알리아스/테이블명(소문자) → 실제 테이블명" 을 찾는다.
/// Mac 판 SQLCompleter.aliases 와 같은 규칙 (자동 완성 포팅 전까지 튜닝용으로 여기 둔다). 처음 나온 순서를 지킨다.
internal static partial class TuningSqlAliases
{
    private static readonly HashSet<string> Reserved =
    [
        "WHERE", "ON", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "GROUP", "ORDER", "HAVING",
        "LIMIT", "SET", "VALUES", "UNION", "USING", "STRAIGHT_JOIN", "NATURAL", "FOR", "WINDOW", "AS",
    ];

    [GeneratedRegex(@"\b(?:FROM|JOIN|UPDATE|INTO)\s+`?([A-Za-z0-9_$.]+)`?(?:\s+(?:AS\s+)?`?([A-Za-z0-9_$]+)`?)?", RegexOptions.IgnoreCase)]
    private static partial Regex Main();

    // 쉼표로 이어진 FROM a x, b y 도 처리
    [GeneratedRegex(@",\s*`?([A-Za-z0-9_$.]+)`?(?:\s+(?:AS\s+)?`?([A-Za-z0-9_$]+)`?)?", RegexOptions.IgnoreCase)]
    private static partial Regex Comma();

    public static Dictionary<string, string> Aliases(string statement)
    {
        var map = new Dictionary<string, string>();
        foreach (var regex in new[] { Main(), Comma() })
        {
            foreach (Match m in regex.Matches(statement))
            {
                if (!m.Groups[1].Success) continue;
                var table = m.Groups[1].Value;
                var dot = table.LastIndexOf('.');
                if (dot >= 0) table = table[(dot + 1)..];
                map[table.ToLowerInvariant()] = table;
                if (m.Groups[2].Success)
                {
                    var alias = m.Groups[2].Value;
                    if (!Reserved.Contains(alias.ToUpperInvariant())) map[alias.ToLowerInvariant()] = table;
                }
            }
        }
        return map;
    }
}
