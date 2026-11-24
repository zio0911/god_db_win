using System.Text.RegularExpressions;
using GodDB.Core.Schema;

namespace GodDB.Core.Dialects;

/// Amazon Redshift 용 메타데이터 조회 · CREATE 문 (Mac DialectPostgres.swift 의 "MARK: - Redshift")
public sealed partial class PostgresDialect
{
    /// Redshift 의 information_schema.tables 는 권한 있는 테이블만 보여 비어 있을 수 있다
    private const string RedshiftAllTablesSql = """
        SELECT n.nspname, c.relname, CASE c.relkind WHEN 'v' THEN 'VIEW' ELSE 'BASE TABLE' END
          FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE c.relkind IN ('r', 'v') AND n.nspname NOT IN ('pg_catalog', 'information_schema')
           AND n.nspname NOT LIKE 'pg_toast%' AND n.nspname NOT LIKE 'pg_temp%'
           AND has_schema_privilege(n.nspname, 'USAGE')
         ORDER BY 1, 2 LIMIT 20000
        """;

    /// 컬럼 순서는 PostgreSQL 과 같다. 6번째(extra)에 "distkey" · "sortkey N" (N = 정렬 키 안의 순서)
    internal string RedshiftColumnsSql(string schema) => $"""
        SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod),
               CASE WHEN a.attnotnull THEN 'NO' ELSE 'YES' END,
               pg_get_expr(d.adbin, d.adrelid),
               TRIM(CASE WHEN a.attisdistkey THEN 'distkey' ELSE '' END
                    || CASE WHEN a.attsortkeyord <> 0 THEN ' sortkey ' || CAST(ABS(a.attsortkeyord) AS varchar) ELSE '' END),
               CASE WHEN EXISTS (SELECT 1 FROM pg_constraint con
                                  WHERE con.conrelid = c.oid AND con.contype = 'p' AND a.attnum = ANY(con.conkey))
                    THEN 'PRI' ELSE '' END,
               COALESCE(col_description(c.oid, a.attnum), ''), a.attnum
          FROM pg_attribute a
          JOIN pg_class c ON c.oid = a.attrelid
          JOIN pg_namespace n ON n.oid = c.relnamespace
          LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
         WHERE n.nspname = {QuoteString(schema)} AND c.relkind = 'r'
           AND a.attnum > 0 AND NOT a.attisdropped
         ORDER BY c.relname, a.attnum
        """;

    /// Redshift 에는 인덱스가 없다. 기본 키(정보용 제약)만 인덱스 모양으로 돌려준다
    internal string RedshiftPrimaryKeysSql(string schema) => $"""
        SELECT c.relname, con.conname, '0', a.attname, '1', '1'
          FROM pg_constraint con
          JOIN pg_class c ON c.oid = con.conrelid
          JOIN pg_namespace n ON n.oid = c.relnamespace
          CROSS JOIN generate_series(1, 32) AS g(i)
          JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = con.conkey[g.i]
         WHERE con.contype = 'p' AND n.nspname = {QuoteString(schema)}
         ORDER BY c.relname, con.conname, g.i
        """;

    /// 외래 키 (정보용 제약). unnest · LATERAL 대신 배열 첨자
    internal string RedshiftForeignKeysSql(string schema)
    {
        var s = QuoteString(schema);
        return $"""
            SELECT c.relname, con.conname, a.attname, rc.relname, ra.attname, g.i
              FROM pg_constraint con
              JOIN pg_class c ON c.oid = con.conrelid
              JOIN pg_namespace n ON n.oid = c.relnamespace
              JOIN pg_class rc ON rc.oid = con.confrelid
              JOIN pg_namespace rn ON rn.oid = rc.relnamespace
              CROSS JOIN generate_series(1, 32) AS g(i)
              JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = con.conkey[g.i]
              JOIN pg_attribute ra ON ra.attrelid = con.confrelid AND ra.attnum = con.confkey[g.i]
             WHERE con.contype = 'f' AND n.nspname = {s} AND rn.nspname = {s}
             ORDER BY 1, 2, 6
            """;
    }

    /// 프로시저 구분은 pg_proc_info.prokind. 트리거는 없다
    private string RedshiftRoutinesSql(string schema)
    {
        var s = QuoteString(schema);
        return $"""
            SELECT 'VIEW', c.relname, COALESCE(obj_description(c.oid, 'pg_class'), '')
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = {s} AND c.relkind = 'v'
            UNION ALL
            SELECT CASE p.prokind WHEN 'p' THEN 'PROCEDURE' ELSE 'FUNCTION' END, p.proname,
                   COALESCE(obj_description(p.prooid, 'pg_proc'), '')
              FROM pg_proc_info p JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname = {s}
            ORDER BY 1, 2
            """;
    }

    /// "distkey" / "sortkey N" (extra 에서)
    public static (bool Dist, int? Sort) RedshiftKeys(ColumnMeta c)
    {
        var parts = c.Extra.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.IndexOf(parts, "sortkey");
        int? sort = i >= 0 && i + 1 < parts.Length && int.TryParse(parts[i + 1], out var n) ? n : null;
        return (parts.Contains("distkey"), sort);
    }

    /// CREATE TABLE + DISTKEY · SORTKEY. 인덱스 · 시퀀스는 없다
    private string RedshiftCreateTableSql(TableMeta table)
    {
        var columns = table.Columns.OrderBy(c => c.Position).ToList();
        var lines = columns.Select(c => "  " + ColumnDefinition(c)).ToList();
        if (table.Indexes.FirstOrDefault(i => i.IsPrimary) is IndexMeta pk)
            lines.Add($"  CONSTRAINT {QuoteIdent(pk.Name)} PRIMARY KEY {IndexColumns(pk)}");
        var tail = "";
        if (columns.FirstOrDefault(c => RedshiftKeys(c).Dist) is { } dist)
            tail += $"\nDISTKEY ({QuoteIdent(dist.Name)})";
        var sort = columns.Select(c => (Order: RedshiftKeys(c).Sort, c.Name)).Where(x => x.Order != null)
            .OrderBy(x => x.Order).ToList();
        if (sort.Count > 0)
            tail += $"\nSORTKEY ({string.Join(", ", sort.Select(x => QuoteIdent(x.Name)))})";
        var output = new List<string> { $"CREATE TABLE {QuoteIdent(table.Name)} (\n" + string.Join(",\n", lines) + "\n)" + tail + ";" };
        if (table.Comment.Length > 0)
            output.Add($"COMMENT ON TABLE {QuoteIdent(table.Name)} IS {QuoteString(table.Comment)};");
        foreach (var c in columns.Where(c => c.Comment.Length > 0))
            output.Add($"COMMENT ON COLUMN {QuoteIdent(table.Name)}.{QuoteIdent(c.Name)} IS {QuoteString(c.Comment)};");
        return string.Join("\n", output);
    }

    [GeneratedRegex("Redshift [0-9][0-9.]*")]
    private static partial Regex RedshiftVersionRegex();

    /// `version()` 결과에서 "Redshift 1.0.12345" (없으면 null)
    public static string? RedshiftVersion(string version) =>
        RedshiftVersionRegex().Match(version) is { Success: true } m ? m.Value : null;
}

public static class RedshiftDialectExtensions
{
    /// Amazon Redshift 접속인지
    public static bool IsRedshift(this ISqlDialect d) => d is PostgresDialect { Redshift: true };
}
