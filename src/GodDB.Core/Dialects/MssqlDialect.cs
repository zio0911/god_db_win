using System.Text.RegularExpressions;
using GodDB.Core.Schema;
using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.Core.Dialects;

/// SQL Server. 테이블 이름은 dbo 스키마면 `table`, 그 외는 `schema.table` 로 다룬다.
public sealed partial class MssqlDialect : SqlDialect
{
    public override DatabaseKind Kind => DatabaseKind.Mssql;

    public override string QuoteIdent(string name) => "[" + name.Replace("]", "]]") + "]";

    public override string QuoteTable(string name)
    {
        var (schema, table) = Split(name);
        return schema != null ? $"{QuoteIdent(schema)}.{QuoteIdent(table)}" : QuoteIdent(table);
    }

    public override string QuoteString(string value) => "N'" + value.Replace("'", "''") + "'";

    /// `sales.orders` → ("sales", "orders"), `orders` → (null, "orders")
    internal static (string? Schema, string Table) Split(string name)
    {
        var dot = name.IndexOf('.');
        return dot < 0 ? (null, name) : (name[..dot], name[(dot + 1)..]);
    }

    /// SQL Server 는 세션 단위 읽기전용 설정이 없어서 앱 검사만 한다.
    public override string? ReadOnlySessionSql => null;
    public override string SessionIdSql => "SELECT @@SPID";
    /// SQL Server 는 쿼리만 멈추는 명령이 없어 세션을 끊는다 (DBSession 이 다시 연결한다)
    public override string CancelSql(int sessionId) => $"KILL {sessionId}";

    public override string? AllTablesSql => null;

    public override string DatabasesSql =>
        "SELECT name FROM sys.databases WHERE name NOT IN ('master', 'tempdb', 'model', 'msdb') ORDER BY name";

    public override string UseSql(string name) => $"USE {QuoteIdent(name)}";
    public override bool MetadataNeedsUse => true;

    [GeneratedRegex(@"\bTOP\s*\(?\s*\d|\bOFFSET\b|\bFETCH\b|\bINTO\b")] private static partial Regex HasLimit();
    [GeneratedRegex(@"^((?:\s|--[^\n]*\n|/\*[\s\S]*?\*/)*)(SELECT)(\s+(?:DISTINCT|ALL)\b)?", RegexOptions.IgnoreCase)]
    private static partial Regex SelectHead();

    /// LIMIT 대신 SELECT 뒤에 TOP n 을 넣는다.
    public override string ApplyingLimit(string sql, int limit)
    {
        if (limit <= 0 || SqlText.FirstKeyword(sql) != "SELECT") return sql;
        var upper = SqlText.StripComments(sql).ToUpperInvariant();
        if (HasLimit().IsMatch(upper)) return sql;
        var m = SelectHead().Match(sql);
        if (!m.Success) return sql;
        var head = m.Value;
        var trimmed = sql[(m.Index + m.Length)..].Trim();
        while (trimmed.EndsWith(';')) trimmed = trimmed[..^1];
        return $"{head} TOP {limit} {trimmed}";
    }

    private const string TableName = "CASE WHEN s.name = 'dbo' THEN t.name ELSE s.name + '.' + t.name END";

    public override string TablesSql(string schema) => $"""
        SELECT {TableName}, ISNULL(CAST(ep.value AS nvarchar(4000)), '')
          FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
          LEFT JOIN sys.extended_properties ep
            ON ep.class = 1 AND ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'
         ORDER BY 1
        """;

    public override string ColumnsSql(string schema) => $"""
        SELECT {TableName}, c.name,
               CASE WHEN ty.name IN ('varchar', 'char', 'varbinary', 'binary')
                      THEN ty.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                    WHEN ty.name IN ('nvarchar', 'nchar')
                      THEN ty.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                    WHEN ty.name IN ('decimal', 'numeric')
                      THEN ty.name + '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
                    WHEN ty.name IN ('datetime2', 'time', 'datetimeoffset')
                      THEN ty.name + '(' + CAST(c.scale AS varchar(10)) + ')'
                    ELSE ty.name END,
               CASE WHEN c.is_nullable = 1 THEN 'YES' ELSE 'NO' END,
               dc.definition,
               CASE WHEN c.is_identity = 1 THEN 'identity' ELSE '' END,
               CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic
                                   JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                                  WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id)
                    THEN 'PRI' ELSE '' END,
               ISNULL(CAST(ep.value AS nvarchar(4000)), ''), c.column_id
          FROM sys.columns c
          JOIN sys.tables t ON t.object_id = c.object_id
          JOIN sys.schemas s ON s.schema_id = t.schema_id
          JOIN sys.types ty ON ty.user_type_id = c.user_type_id
          LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
          LEFT JOIN sys.extended_properties ep
            ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
         ORDER BY 1, c.column_id
        """;

    public override string IndexesSql(string schema) => $"""
        SELECT {TableName}, i.name, CASE WHEN i.is_unique = 1 THEN '0' ELSE '1' END, c.name,
               CASE WHEN i.is_primary_key = 1 THEN '1' ELSE '0' END,
               CASE WHEN i.is_primary_key = 1 OR i.is_unique_constraint = 1 THEN '1' ELSE '0' END
          FROM sys.indexes i
          JOIN sys.tables t ON t.object_id = i.object_id
          JOIN sys.schemas s ON s.schema_id = t.schema_id
          JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
         WHERE i.type > 0
         ORDER BY 1, i.name, ic.key_ordinal
        """;

    public override string RoutinesSql(string schema) => """
        SELECT CASE o.type WHEN 'V' THEN 'VIEW' WHEN 'P' THEN 'PROCEDURE' WHEN 'TR' THEN 'TRIGGER' ELSE 'FUNCTION' END,
               CASE WHEN s.name = 'dbo' THEN o.name ELSE s.name + '.' + o.name END,
               CASE WHEN o.type = 'TR' THEN 'ON ' + OBJECT_NAME(o.parent_object_id) ELSE '' END
          FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
         WHERE o.type IN ('V', 'P', 'FN', 'IF', 'TF', 'TR') AND o.is_ms_shipped = 0
         ORDER BY 1, 2
        """;

    public override string DefinitionSql(RoutineMeta routine, string schema) =>
        $"SELECT OBJECT_DEFINITION(OBJECT_ID({QuoteString(QuoteTable(routine.Name))}))";

    public override string FormatDefinition(RoutineMeta routine, string schema, string body) => body;

    public override string CreateTableSql(TableMeta table)
    {
        var t = QuoteTable(table.Name);
        var lines = table.Columns.OrderBy(c => c.Position).Select(c => "  " + ColumnDefinition(c)).ToList();
        if (table.Indexes.FirstOrDefault(i => i.IsPrimary) is IndexMeta pk)
            lines.Add($"  CONSTRAINT {QuoteIdent(pk.Name)} PRIMARY KEY {IndexColumns(pk)}");
        var output = new List<string> { $"CREATE TABLE {t} (\n" + string.Join(",\n", lines) + "\n);" };
        foreach (var i in table.Indexes.Where(i => !i.IsPrimary))
        {
            if (i.IsConstraint)
                output.Add($"ALTER TABLE {t} ADD CONSTRAINT {QuoteIdent(i.Name)} UNIQUE {IndexColumns(i)};");
            else
                output.Add($"CREATE {(i.Unique ? "UNIQUE " : "")}INDEX {QuoteIdent(i.Name)} ON {t} {IndexColumns(i)};");
        }
        if (table.Comment.Length > 0) output.Add(CommentSql(table.Name, null, table.Comment, exists: false));
        foreach (var c in table.Columns.Where(c => c.Comment.Length > 0))
            output.Add(CommentSql(table.Name, c.Name, c.Comment, exists: false));
        return string.Join("\n", output);
    }

    /// SHOWPLAN_ALL 은 같은 연결에서 따로 켜고 꺼야 한다.
    public override ExplainSteps Explain(string sql) =>
        new() { Before = ["SET SHOWPLAN_ALL ON"], Main = sql, After = ["SET SHOWPLAN_ALL OFF"], TreeColumn = "StmtText" };

    public override string ColumnDefinition(ColumnMeta c)
    {
        var parts = new List<string> { QuoteIdent(c.Name), c.ColumnType };
        if (c.Extra.ToLowerInvariant().Contains("identity")) parts.Add("IDENTITY(1,1)");
        parts.Add(c.Nullable ? "NULL" : "NOT NULL");
        if (c.DefaultValue is string d) parts.Add($"DEFAULT {DefaultLiteral(d, c.Extra)}");
        return string.Join(" ", parts);
    }

    /// SQL Server 기본값도 식 그대로 쓴다 (예: ('A'), (getdate())).
    public override string DefaultLiteral(string value, string extra) => value;

    /// MS_Description 확장 속성으로 코멘트를 단다.
    internal string CommentSql(string table, string? column, string comment, bool exists)
    {
        var (schema, name) = Split(table);
        var proc = comment.Length == 0 ? "sp_dropextendedproperty" : (exists ? "sp_updateextendedproperty" : "sp_addextendedproperty");
        var args = new List<string> { "@name = N'MS_Description'" };
        if (comment.Length > 0) args.Add($"@value = {QuoteString(comment)}");
        args.AddRange(["@level0type = N'SCHEMA'", $"@level0name = {QuoteString(schema ?? "dbo")}",
                       "@level1type = N'TABLE'", $"@level1name = {QuoteString(name)}"]);
        if (column != null) args.AddRange(["@level2type = N'COLUMN'", $"@level2name = {QuoteString(column)}"]);
        return $"EXEC sys.{proc} " + string.Join(", ", args) + ";";
    }

    public override string Render(IReadOnlyList<SchemaChange> changes, string table)
    {
        var t = QuoteTable(table);
        var (schema, name) = Split(table);
        var output = new List<string>();
        foreach (var change in changes)
        {
            switch (change)
            {
                case SchemaChange.DropIndex(var i):
                    output.Add(i.IsConstraint
                        ? $"ALTER TABLE {t} DROP CONSTRAINT {QuoteIdent(i.Name)};"
                        : $"DROP INDEX {QuoteIdent(i.Name)} ON {t};");
                    break;
                case SchemaChange.DropColumn(var col):
                    output.Add($"ALTER TABLE {t} DROP COLUMN {QuoteIdent(col)};");
                    break;
                case SchemaChange.RenameColumn(var old, var @new):
                    var path = QuoteString($"{schema ?? "dbo"}.{name}.{old.Name}");
                    output.Add($"EXEC sp_rename {path}, {QuoteString(@new.Name)}, 'COLUMN';");
                    output.AddRange(AlterColumn(t, table, old, @new));
                    break;
                case SchemaChange.ModifyColumn(var old, var @new):
                    output.AddRange(AlterColumn(t, table, old, @new));
                    break;
                case SchemaChange.AddColumn(var c, _):
                    output.Add($"ALTER TABLE {t} ADD {ColumnDefinition(c)};");
                    if (c.Comment.Length > 0) output.Add(CommentSql(table, c.Name, c.Comment, exists: false));
                    break;
                case SchemaChange.AddIndex(var i):
                    output.Add(i.IsPrimary
                        ? $"ALTER TABLE {t} ADD CONSTRAINT {QuoteIdent(i.Name)} PRIMARY KEY {IndexColumns(i)};"
                        : $"CREATE {(i.Unique ? "UNIQUE " : "")}INDEX {QuoteIdent(i.Name)} ON {t} {IndexColumns(i)};");
                    break;
                case SchemaChange.TableComment(var old, var @new):
                    output.Add(CommentSql(table, null, @new, exists: old.Length > 0));
                    break;
            }
        }
        return string.Join("\n", output);
    }

    /// 컬럼의 기본값 제약조건을 이름을 몰라도 지운다. 기본값 제약조건은 서버가 붙인 이름(DF__…)인 경우가 많다.
    /// 쿼리 탭은 세미콜론으로 문장을 나누므로 세미콜론 없이 한 문장(한 배치)으로 만든다.
    internal string DropDefaultSql(string t, string column)
    {
        var v = "@df_" + new string(column.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        var alter = $"ALTER TABLE {t} DROP CONSTRAINT ".Replace("'", "''");
        return $"DECLARE {v} nvarchar(300) = (SELECT QUOTENAME(dc.name) FROM sys.default_constraints dc "
             + "JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id "
             + $"WHERE dc.parent_object_id = OBJECT_ID({QuoteString(t)}) AND c.name = {QuoteString(column)}) "
             + $"IF {v} IS NOT NULL EXEC (N'{alter}' + {v});";
    }

    private List<string> AlterColumn(string t, string table, ColumnMeta old, ColumnMeta @new)
    {
        var output = new List<string>();
        if (old.ColumnType.ToLowerInvariant() != @new.ColumnType.ToLowerInvariant() || old.Nullable != @new.Nullable)
            output.Add($"ALTER TABLE {t} ALTER COLUMN {QuoteIdent(@new.Name)} {@new.ColumnType} {(@new.Nullable ? "NULL" : "NOT NULL")};");
        if (old.DefaultValue != @new.DefaultValue)
        {
            if (old.DefaultValue != null) output.Add(DropDefaultSql(t, @new.Name));
            if (@new.DefaultValue is string d)
                output.Add($"ALTER TABLE {t} ADD DEFAULT {d} FOR {QuoteIdent(@new.Name)};");
        }
        if (old.Comment != @new.Comment)
            output.Add(CommentSql(table, @new.Name, @new.Comment, exists: old.Comment.Length > 0));
        return output;
    }
}
