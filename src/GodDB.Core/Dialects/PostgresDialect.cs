using GodDB.Core.Schema;

namespace GodDB.Core.Dialects;

/// PostgreSQL. 사이드바의 "DB" 선택은 스키마(search_path)를 뜻한다. 접속 DB 는 접속 설정에서 고른다.
/// Amazon Redshift 도 PostgreSQL 접속으로 붙는다. 연결할 때 `version()` 으로 알아내 `Redshift` 를 켜고,
/// PostgreSQL 8.0 기반 카탈로그에서 동작하는 메타데이터 조회를 쓴다 (identity · prokind · unnest · LATERAL 없음).
public sealed partial class PostgresDialect(bool redshift = false) : SqlDialect
{
    /// Amazon Redshift 접속
    public bool Redshift { get; } = redshift;

    public override DatabaseKind Kind => DatabaseKind.Postgres;

    public override string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public override string QuoteString(string value) => "'" + value.Replace("'", "''") + "'";

    public override string? ReadOnlySessionSql => "SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY";
    /// 큰 테이블의 순차 스캔은 기본으로 직전 스캔이 멈춘 곳부터 시작한다 (synchronize_seqscans).
    /// 그러면 ORDER BY 없는 결과를 LIMIT/OFFSET 으로 나눠 받을 때 페이지끼리 행이 겹치거나 빠진다. 항상 처음부터 읽게 한다.
    public override string? SessionSetupSql => Redshift ? null : "SET synchronize_seqscans = off";
    public override string SessionIdSql => "SELECT pg_backend_pid()";
    public override string CancelSql(int sessionId) => $"SELECT pg_cancel_backend({sessionId})";

    public override string? AllTablesSql => Redshift ? RedshiftAllTablesSql : """
        SELECT table_schema, table_name, table_type FROM information_schema.tables
        WHERE table_schema NOT IN ('pg_catalog', 'information_schema') AND table_schema NOT LIKE 'pg_toast%'
        ORDER BY table_schema, table_name LIMIT 20000
        """;

    // Redshift: 사용 권한(USAGE) 없는 스키마는 고르면 search_path 가 비어 "테이블 없음" 이 되므로 빼고 보여준다
    public override string DatabasesSql => $"""
        SELECT nspname FROM pg_namespace
         WHERE nspname NOT IN ('pg_catalog', 'information_schema')
           AND nspname NOT LIKE 'pg_toast%' AND nspname NOT LIKE 'pg_temp%'{(Redshift ? "\n   AND has_schema_privilege(nspname, 'USAGE')" : "")}
         ORDER BY nspname
        """;

    public override string UseSql(string name) => $"SET search_path TO {QuoteIdent(name)}";

    public override string InitialContainer(ConnectionProfile profile, IReadOnlyList<string> available) =>
        available.Contains("public") ? "public" : (available.Count > 0 ? available[0] : "public");

    public override string TablesSql(string schema) => $"""
        SELECT c.relname, COALESCE(obj_description(c.oid, 'pg_class'), '')
          FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE n.nspname = {QuoteString(schema)} AND c.relkind IN ('r', 'p')
         ORDER BY c.relname
        """;

    public override string ColumnsSql(string schema) => Redshift ? RedshiftColumnsSql(schema) : $"""
        SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod),
               CASE WHEN a.attnotnull THEN 'NO' ELSE 'YES' END,
               pg_get_expr(d.adbin, d.adrelid),
               CASE a.attidentity WHEN 'a' THEN 'identity always' WHEN 'd' THEN 'identity' ELSE '' END,
               CASE WHEN EXISTS (SELECT 1 FROM pg_index i
                                  WHERE i.indrelid = c.oid AND i.indisprimary AND a.attnum = ANY(i.indkey))
                    THEN 'PRI' ELSE '' END,
               COALESCE(col_description(c.oid, a.attnum), ''), a.attnum
          FROM pg_attribute a
          JOIN pg_class c ON c.oid = a.attrelid
          JOIN pg_namespace n ON n.oid = c.relnamespace
          LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
         WHERE n.nspname = {QuoteString(schema)} AND c.relkind IN ('r', 'p')
           AND a.attnum > 0 AND NOT a.attisdropped
         ORDER BY c.relname, a.attnum
        """;

    public override string IndexesSql(string schema) => Redshift ? RedshiftPrimaryKeysSql(schema) : $"""
        SELECT t.relname, i.relname, CASE WHEN ix.indisunique THEN '0' ELSE '1' END, a.attname,
               CASE WHEN ix.indisprimary THEN '1' ELSE '0' END,
               CASE WHEN EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = ix.indexrelid)
                    THEN '1' ELSE '0' END
          FROM pg_index ix
          JOIN pg_class t ON t.oid = ix.indrelid
          JOIN pg_class i ON i.oid = ix.indexrelid
          JOIN pg_namespace n ON n.oid = t.relnamespace
          JOIN LATERAL unnest(ix.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) ON true
          JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
         WHERE n.nspname = {QuoteString(schema)} AND t.relkind IN ('r', 'p')
         ORDER BY t.relname, i.relname, k.ord
        """;

    public override string RoutinesSql(string schema)
    {
        if (Redshift) return RedshiftRoutinesSql(schema);
        var s = QuoteString(schema);
        return $"""
            SELECT 'VIEW', c.relname, COALESCE(obj_description(c.oid, 'pg_class'), '')
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = {s} AND c.relkind IN ('v', 'm')
            UNION ALL
            SELECT CASE p.prokind WHEN 'p' THEN 'PROCEDURE' ELSE 'FUNCTION' END, p.proname,
                   COALESCE(obj_description(p.oid, 'pg_proc'), '')
              FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname = {s} AND p.prokind IN ('f', 'p')
            UNION ALL
            SELECT 'TRIGGER', t.tgname, 'ON ' || c.relname
              FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = {s} AND NOT t.tgisinternal
            ORDER BY 1, 2
            """;
    }

    public override string DefinitionSql(RoutineMeta routine, string schema)
    {
        string s = QuoteString(schema), name = QuoteString(routine.Name);
        return routine.Kind switch
        {
            RoutineKind.View => $"""
                SELECT pg_get_viewdef(c.oid, true) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = {s} AND c.relname = {name}
                """,
            // Redshift 는 pg_get_functiondef 가 없다 → 본문만.
            // Mac #175: Swift `case .procedure, .function where redshift` 의 where 가 .function 에만 붙어
            // PostgreSQL 프로시저도 본문만 나왔다. 여기서는 when 이 두 종류 모두에 걸린다 (괄호).
            RoutineKind.Procedure or RoutineKind.Function when Redshift => $"""
                SELECT p.prosrc FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                 WHERE n.nspname = {s} AND p.proname = {name} ORDER BY p.oid LIMIT 1
                """,
            RoutineKind.Procedure or RoutineKind.Function => $"""
                SELECT pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                 WHERE n.nspname = {s} AND p.proname = {name} ORDER BY p.oid LIMIT 1
                """,
            _ => $"""
                SELECT pg_get_triggerdef(t.oid, true) FROM pg_trigger t
                  JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = {s} AND t.tgname = {name} LIMIT 1
                """,
        };
    }

    public override string FormatDefinition(RoutineMeta routine, string schema, string body)
    {
        if (routine.Kind != RoutineKind.View) return body;
        return $"CREATE OR REPLACE VIEW {QuoteIdent(schema)}.{QuoteIdent(routine.Name)} AS\n" + body;
    }

    public override string CreateTableSql(TableMeta table)
    {
        if (Redshift) return RedshiftCreateTableSql(table);
        var lines = table.Columns.OrderBy(c => c.Position).Select(c => "  " + ColumnDefinition(c)).ToList();
        if (table.Indexes.FirstOrDefault(i => i.IsPrimary) is IndexMeta pk)
            lines.Add($"  CONSTRAINT {QuoteIdent(pk.Name)} PRIMARY KEY {IndexColumns(pk)}");
        var sequences = table.Columns.Select(c => (Column: c, Name: SequenceName(c))).Where(x => x.Name != null).ToList();
        var output = sequences.Select(x => CreateSequenceSql(x.Name!, x.Column)).ToList();
        output.Add($"CREATE TABLE {QuoteIdent(table.Name)} (\n" + string.Join(",\n", lines) + "\n);");
        // serial 처럼 테이블을 지우면 시퀀스도 함께 지워지게
        output.AddRange(sequences.Select(x => $"ALTER SEQUENCE {x.Name} OWNED BY {QuoteIdent(table.Name)}.{QuoteIdent(x.Column.Name)};"));
        foreach (var i in table.Indexes.Where(i => !i.IsPrimary))
            output.Add($"CREATE {(i.Unique ? "UNIQUE " : "")}INDEX {QuoteIdent(i.Name)} ON {QuoteIdent(table.Name)} {IndexColumns(i)};");
        if (table.Comment.Length > 0)
            output.Add($"COMMENT ON TABLE {QuoteIdent(table.Name)} IS {QuoteString(table.Comment)};");
        foreach (var c in table.Columns.Where(c => c.Comment.Length > 0))
            output.Add($"COMMENT ON COLUMN {QuoteIdent(table.Name)}.{QuoteIdent(c.Name)} IS {QuoteString(c.Comment)};");
        return string.Join("\n", output);
    }

    public override ExplainSteps Explain(string sql) => new() { Main = $"EXPLAIN {sql}", TreeColumn = "QUERY PLAN" };

    public override string ColumnDefinition(ColumnMeta c)
    {
        var parts = new List<string> { QuoteIdent(c.Name), c.ColumnType };
        var extra = c.Extra.ToLowerInvariant();
        if (extra.Contains("identity"))
            parts.Add(extra.Contains("always") ? "GENERATED ALWAYS AS IDENTITY" : "GENERATED BY DEFAULT AS IDENTITY");
        if (!c.Nullable) parts.Add("NOT NULL");
        if (c.DefaultValue is string d) parts.Add($"DEFAULT {DefaultLiteral(d, c.Extra)}");
        return string.Join(" ", parts);
    }

    /// PostgreSQL 기본값은 SQL 식 그대로 쓴다 (문자열은 'A' 처럼 따옴표 포함, 예: 'A'::bpchar, now()).
    public override string DefaultLiteral(string value, string extra) => value;

    /// PostgreSQL 은 변경마다 별도 문장으로 만든다.
    public override string Render(IReadOnlyList<SchemaChange> changes, string table)
    {
        var t = QuoteIdent(table);
        var output = new List<string>();
        foreach (var change in changes)
        {
            switch (change)
            {
                case SchemaChange.DropIndex(var i):
                    output.Add(i.IsConstraint
                        ? $"ALTER TABLE {t} DROP CONSTRAINT {QuoteIdent(i.Name)};"
                        : $"DROP INDEX {QuoteIdent(i.Name)};");
                    break;
                case SchemaChange.DropColumn(var name):
                    output.Add($"ALTER TABLE {t} DROP COLUMN {QuoteIdent(name)};");
                    break;
                case SchemaChange.RenameColumn(var old, var @new):
                    output.Add($"ALTER TABLE {t} RENAME COLUMN {QuoteIdent(old.Name)} TO {QuoteIdent(@new.Name)};");
                    output.AddRange(AlterColumn(t, old, @new));
                    break;
                case SchemaChange.ModifyColumn(var old, var @new):
                    output.AddRange(AlterColumn(t, old, @new));
                    break;
                case SchemaChange.AddColumn(var c, _):
                    var sequence = SequenceName(c);
                    if (sequence != null) output.Add(CreateSequenceSql(sequence, c));
                    output.Add($"ALTER TABLE {t} ADD COLUMN {ColumnDefinition(c)};");
                    if (sequence != null) output.Add($"ALTER SEQUENCE {sequence} OWNED BY {t}.{QuoteIdent(c.Name)};");
                    if (c.Comment.Length > 0)
                        output.Add($"COMMENT ON COLUMN {t}.{QuoteIdent(c.Name)} IS {QuoteString(c.Comment)};");
                    break;
                case SchemaChange.AddIndex(var i):
                    output.Add(i.IsPrimary
                        ? $"ALTER TABLE {t} ADD PRIMARY KEY {IndexColumns(i)};"
                        : $"CREATE {(i.Unique ? "UNIQUE " : "")}INDEX {QuoteIdent(i.Name)} ON {t} {IndexColumns(i)};");
                    break;
                case SchemaChange.TableComment(_, var @new):
                    output.Add($"COMMENT ON TABLE {t} IS {(@new.Length == 0 ? "NULL" : QuoteString(@new))};");
                    break;
            }
        }
        return string.Join("\n", output);
    }

    /// 기본값이 `nextval('시퀀스'::regclass)` (serial 컬럼)이면 시퀀스 이름 (SQL 에 그대로 쓸 수 있는 형태).
    /// 다른 스키마에 테이블을 만들 때 시퀀스가 없으면 CREATE TABLE 이 실패하므로 먼저 만든다.
    public static string? SequenceName(ColumnMeta c)
    {
        if (c.DefaultValue is not { } d || !NextvalRegex().IsMatch(d)) return null;
        var inner = d["nextval('".Length..];
        var end = inner.LastIndexOf('\'');
        return end < 0 ? null : inner[..end].Replace("''", "'");
    }

    public string CreateSequenceSql(string name, ColumnMeta c)
    {
        var type = c.ColumnType.ToLowerInvariant();
        var asType = type is "smallint" or "integer" or "bigint" ? $" AS {type}" : "";
        return $"CREATE SEQUENCE IF NOT EXISTS {name}{asType};";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^nextval\('((?:[^']|'')+)'(::regclass)?\)$")]
    private static partial System.Text.RegularExpressions.Regex NextvalRegex();

    private List<string> AlterColumn(string t, ColumnMeta old, ColumnMeta @new)
    {
        var c = $"{t} ALTER COLUMN {QuoteIdent(@new.Name)}";
        var output = new List<string>();
        if (old.ColumnType.ToLowerInvariant() != @new.ColumnType.ToLowerInvariant())
            output.Add($"ALTER TABLE {c} TYPE {@new.ColumnType} USING {QuoteIdent(@new.Name)}::{@new.ColumnType};");
        if (old.Nullable != @new.Nullable)
            output.Add($"ALTER TABLE {c} {(@new.Nullable ? "DROP" : "SET")} NOT NULL;");
        if (old.DefaultValue != @new.DefaultValue)
            output.Add(@new.DefaultValue is string d ? $"ALTER TABLE {c} SET DEFAULT {d};" : $"ALTER TABLE {c} DROP DEFAULT;");
        if (old.Comment != @new.Comment)
            output.Add($"COMMENT ON COLUMN {t}.{QuoteIdent(@new.Name)} IS {(@new.Comment.Length == 0 ? "NULL" : QuoteString(@new.Comment))};");
        return output;
    }
}
