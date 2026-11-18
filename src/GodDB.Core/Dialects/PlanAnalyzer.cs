using System.Globalization;
using System.Text.RegularExpressions;
using GodDB.Core.Schema;
using static GodDB.Core.L10n;

namespace GodDB.Core.Dialects;

/// 실행 계획 결과에서 성능상 주의할 점을 찾는다. (MySQL EXPLAIN / PostgreSQL QUERY PLAN / SQL Server SHOWPLAN_ALL)
/// (Mac 판에서는 Routines.swift 안에 있다)
public static partial class PlanAnalyzer
{
    public sealed record Warning(string Table, string Message);

    /// Swift `Int.formatted()` 처럼 현재 언어의 자릿수 구분 기호를 넣는다
    private static string Formatted(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    public static List<Warning> Warnings(QueryResult result)
    {
        int? Col(string name)
        {
            var i = result.Columns.FindIndex(c => c.Name.ToLowerInvariant() == name.ToLowerInvariant());
            return i >= 0 ? i : null;
        }
        if (Col("QUERY PLAN") is int qp)
            return Postgres([.. result.Rows.Select(r => r.Values[qp]).OfType<string>()]);
        if (Col("PhysicalOp") is int op) return Mssql(result, op, Col("EstimateRows"), Col("Argument"));
        return Mysql(result, Col);
    }

    internal static List<Warning> Mysql(QueryResult result, Func<string, int?> col)
    {
        if (col("type") is not int typeIdx) return [];
        int? tableIdx = col("table"), keyIdx = col("key"), possibleIdx = col("possible_keys"),
             rowsIdx = col("rows"), extraIdx = col("extra");
        var output = new List<Warning>();
        foreach (var row in result.Rows)
        {
            var v = row.Values;
            var table = (tableIdx is int ti ? v[ti] : null) ?? "?";
            var type = (v[typeIdx] ?? "").ToUpperInvariant();
            long? rows = rowsIdx is int ri && v[ri] is string rs && long.TryParse(rs, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
            var extra = (extraIdx is int ei ? v[ei] : null) ?? "";
            var key = keyIdx is int ki ? v[ki] : null;
            var possible = possibleIdx is int pi ? v[pi] : null;
            var rowsText = rows is long r ? Tr(" (예상 %@행)", Formatted(r)) : "";

            if (type == "ALL")
                output.Add(new(table, Tr("풀 테이블 스캔%@ — WHERE/JOIN 컬럼에 인덱스가 있는지 확인하세요.", rowsText)));
            else if (type == "INDEX")
                output.Add(new(table, Tr("인덱스 풀 스캔%@", rowsText)));
            if (key == null && !string.IsNullOrEmpty(possible) && type != "ALL")
                output.Add(new(table, Tr("사용 가능한 인덱스(%@)가 있지만 사용하지 않았어요.", possible)));
            if (extra.Contains("Using filesort"))
                output.Add(new(table, Tr("정렬을 위해 filesort 사용 — ORDER BY 컬럼 인덱스를 검토하세요.")));
            if (extra.Contains("Using temporary"))
                output.Add(new(table, Tr("임시 테이블 사용 — GROUP BY / DISTINCT 를 검토하세요.")));
        }
        return output;
    }

    [GeneratedRegex(@"Seq Scan on (\S+).*?rows=(\d+)")] private static partial Regex SeqScan();
    [GeneratedRegex(@"rows=(\d+)")] private static partial Regex RowsEq();
    [GeneratedRegex(@"OBJECT:\(([^)]*)\)")] private static partial Regex ObjectArg();

    internal static List<Warning> Postgres(IEnumerable<string> lines)
    {
        var output = new List<Warning>();
        foreach (var line in lines)
        {
            var m = SeqScan().Match(line);
            if (m.Success)
            {
                var table = m.Groups[1].Value;
                var rows = long.TryParse(m.Groups[2].Value, out var n) ? n : 0;
                output.Add(new(table, Tr("풀 테이블 스캔 (Seq Scan, 예상 %@행) — WHERE/JOIN 컬럼에 인덱스가 있는지 확인하세요.", Formatted(rows))));
            }
            if (line.Contains("Sort Method: external"))
                output.Add(new("-", Tr("정렬이 메모리를 넘어 디스크를 사용해요 — work_mem 또는 ORDER BY 인덱스를 검토하세요.")));
            else if (line.Trim(' ', '\t').StartsWith("->  Sort", StringComparison.Ordinal) || line.StartsWith("Sort ", StringComparison.Ordinal))
                output.Add(new("-", Tr("정렬(Sort) 단계가 있어요 — ORDER BY 컬럼 인덱스를 검토하세요.")));
            if (line.Contains("Nested Loop") && line.Contains("rows="))
            {
                var r = RowsEq().Match(line);
                if (r.Success && long.TryParse(r.Groups[1].Value, out var n) && n > 10_000)
                    output.Add(new("-", Tr("큰 Nested Loop 조인 (예상 %@행) — 조인 조건 인덱스를 검토하세요.", Formatted(n))));
            }
        }
        return output;
    }

    internal static List<Warning> Mssql(QueryResult result, int op, int? rowsIdx, int? argIdx)
    {
        var output = new List<Warning>();
        foreach (var row in result.Rows)
        {
            var physical = row.Values[op] ?? "";
            var argument = (argIdx is int ai ? row.Values[ai] : null) ?? "";
            long? rows = rowsIdx is int ri && row.Values[ri] is string rs
                         && double.TryParse(rs, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : null;
            var rowsText = rows is long r ? Tr(" (예상 %@행)", Formatted(r)) : "";
            var m = ObjectArg().Match(argument);
            var table = m.Success ? m.Value.Replace("OBJECT:(", "").Replace(")", "") : "-";
            switch (physical)
            {
                case "Table Scan" or "Clustered Index Scan":
                    output.Add(new(table, Tr("%@ — 풀 스캔%@. WHERE/JOIN 컬럼 인덱스를 확인하세요.", physical, rowsText)));
                    break;
                case "Index Scan":
                    output.Add(new(table, Tr("인덱스 풀 스캔%@", rowsText)));
                    break;
                case "Sort":
                    output.Add(new("-", Tr("정렬(Sort) 단계%@ — ORDER BY 컬럼 인덱스를 검토하세요.", rowsText)));
                    break;
                case "Key Lookup" or "RID Lookup":
                    output.Add(new(table, Tr("%@%@ — 인덱스에 필요한 컬럼을 INCLUDE 하면 줄일 수 있어요.", physical, rowsText)));
                    break;
            }
        }
        return output;
    }
}
