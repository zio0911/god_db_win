using GodDB.Core.Schema;
using GodDB.Core.Sql;

namespace GodDB.Core.Dialects;

public sealed class MySqlDialect : SqlDialect
{
    public override DatabaseKind Kind => DatabaseKind.Mysql;

    public override string QuoteIdent(string name) => SqlText.QuoteIdent(name);

    public override string? ReadOnlySessionSql => "SET SESSION TRANSACTION READ ONLY";
    /// MySQL 5.7.8+: SELECT 에만 적용된다 (MariaDB 는 이 변수가 없어 실패 → 무시)
    public override string? StatementTimeoutSql(int milliseconds) => $"SET SESSION max_execution_time = {milliseconds}";
    public override string SessionIdSql => "SELECT CONNECTION_ID()";
    public override string CancelSql(int sessionId) => $"KILL QUERY {sessionId}";

    public override string? AllTablesSql => """
        SELECT table_schema, table_name, table_type FROM information_schema.tables
        WHERE table_schema NOT IN ('mysql', 'information_schema', 'performance_schema', 'sys')
        ORDER BY table_schema, table_name LIMIT 20000
        """;

    public override string DatabasesSql => """
        SELECT SCHEMA_NAME FROM information_schema.SCHEMATA
         WHERE SCHEMA_NAME NOT IN ('information_schema', 'performance_schema', 'mysql', 'sys')
         ORDER BY SCHEMA_NAME
        """;

    public override string UseSql(string name) => $"USE {QuoteIdent(name)}";

    public override string TablesSql(string schema) => $"""
        SELECT TABLE_NAME, IFNULL(TABLE_COMMENT, '')
          FROM information_schema.TABLES
         WHERE TABLE_SCHEMA = {QuoteString(schema)} AND TABLE_TYPE = 'BASE TABLE'
         ORDER BY TABLE_NAME
        """;

    public override string ColumnsSql(string schema) => $"""
        SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT,
               EXTRA, COLUMN_KEY, IFNULL(COLUMN_COMMENT, ''), ORDINAL_POSITION
          FROM information_schema.COLUMNS
         WHERE TABLE_SCHEMA = {QuoteString(schema)}
         ORDER BY TABLE_NAME, ORDINAL_POSITION
        """;

    public override string IndexesSql(string schema) => $"""
        SELECT TABLE_NAME, INDEX_NAME, NON_UNIQUE, COLUMN_NAME,
               IF(INDEX_NAME = 'PRIMARY', '1', '0'), IF(INDEX_NAME = 'PRIMARY', '1', '0')
          FROM information_schema.STATISTICS
         WHERE TABLE_SCHEMA = {QuoteString(schema)}
         ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX
        """;

    public override string RoutinesSql(string schema)
    {
        var s = QuoteString(schema);
        return $"""
            SELECT 'VIEW', TABLE_NAME, '' FROM information_schema.VIEWS WHERE TABLE_SCHEMA = {s}
            UNION ALL
            SELECT ROUTINE_TYPE, ROUTINE_NAME, IFNULL(ROUTINE_COMMENT, '')
              FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = {s}
            UNION ALL
            SELECT 'TRIGGER', TRIGGER_NAME, CONCAT(ACTION_TIMING, ' ', EVENT_MANIPULATION, ' ON ', EVENT_OBJECT_TABLE)
              FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = {s}
            ORDER BY 1, 2
            """;
    }

    public override string DefinitionSql(RoutineMeta routine, string schema) =>
        $"SHOW CREATE {routine.Kind.RawValue()} {QuoteIdent(routine.Name)}";

    public override string FormatDefinition(RoutineMeta routine, string schema, string body) => body;

    public override string? ShowCreateTableSql(string table) => $"SHOW CREATE TABLE {QuoteIdent(table)}";

    public override string CreateTableSql(TableMeta table)
    {
        var lines = table.Columns.OrderBy(c => c.Position).Select(c => "  " + ColumnDefinition(c)).ToList();
        foreach (var i in table.Indexes)
        {
            if (i.IsPrimary)
                lines.Add($"  PRIMARY KEY {IndexColumns(i)}");
            else
                lines.Add($"  {(i.Unique ? "UNIQUE " : "")}KEY {QuoteIdent(i.Name)} {IndexColumns(i)}");
        }
        var sql = $"CREATE TABLE {QuoteIdent(table.Name)} (\n" + string.Join(",\n", lines) + "\n)";
        if (table.Comment.Length > 0) sql += $" COMMENT={QuoteString(table.Comment)}";
        return sql + ";";
    }

    public override ExplainSteps Explain(string sql) => new() { Main = $"EXPLAIN {sql}", TreeSql = $"EXPLAIN FORMAT=TREE {sql}" };

    public override string ColumnDefinition(ColumnMeta c)
    {
        var parts = new List<string> { QuoteIdent(c.Name), c.ColumnType, c.Nullable ? "NULL" : "NOT NULL" };
        if (c.DefaultValue is string def) parts.Add($"DEFAULT {DefaultLiteral(def, c.Extra)}");
        var extra = SqlText.TrimSpaces(c.Extra.Replace("DEFAULT_GENERATED", "", StringComparison.OrdinalIgnoreCase));
        if (extra.Length > 0) parts.Add(extra);
        if (c.Comment.Length > 0) parts.Add($"COMMENT {QuoteString(c.Comment)}");
        return string.Join(" ", parts);
    }

    public override string DefaultLiteral(string value, string extra)
    {
        var upper = value.ToUpperInvariant();
        if (extra.ToUpperInvariant().Contains("DEFAULT_GENERATED") || upper.StartsWith("CURRENT_TIMESTAMP", StringComparison.Ordinal) || upper == "NULL")
            return value;
        return QuoteString(value);
    }

    /// MySQL 은 하나의 ALTER TABLE 문으로 모은다.
    public override string Render(IReadOnlyList<SchemaChange> changes, string table)
    {
        if (changes.Count == 0) return "";
        var clauses = changes.Select(change => change switch
        {
            SchemaChange.DropIndex(var i) => i.IsPrimary ? "DROP PRIMARY KEY" : $"DROP INDEX {QuoteIdent(i.Name)}",
            SchemaChange.DropColumn(var name) => $"DROP COLUMN {QuoteIdent(name)}",
            SchemaChange.RenameColumn(var old, var @new) => $"CHANGE COLUMN {QuoteIdent(old.Name)} {ColumnDefinition(@new)}",
            SchemaChange.ModifyColumn(_, var @new) => $"MODIFY COLUMN {ColumnDefinition(@new)}",
            SchemaChange.AddColumn(var c, var after) => $"ADD COLUMN {ColumnDefinition(c)} " + (after != null ? $"AFTER {QuoteIdent(after)}" : "FIRST"),
            SchemaChange.AddIndex(var i) => i.IsPrimary
                ? $"ADD PRIMARY KEY {IndexColumns(i)}"
                : $"ADD {(i.Unique ? "UNIQUE " : "")}INDEX {QuoteIdent(i.Name)} {IndexColumns(i)}",
            SchemaChange.TableComment(_, var @new) => $"COMMENT = {QuoteString(@new)}",
            _ => throw new ArgumentOutOfRangeException(nameof(changes)),
        }).ToList();
        // 절이 1~2개면 한 줄, 그보다 많으면 줄마다
        if (clauses.Count <= 2)
            return $"ALTER TABLE {QuoteTable(table)} " + string.Join(", ", clauses) + ";";
        return $"ALTER TABLE {QuoteTable(table)}\n  " + string.Join(",\n  ", clauses) + ";";
    }
}
