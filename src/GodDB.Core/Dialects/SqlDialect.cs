using System.Text.RegularExpressions;
using GodDB.Core.Results;
using GodDB.Core.Schema;
using GodDB.Core.Sql;

namespace GodDB.Core.Dialects;

/// ISqlDialect 공통 구현 (Swift `extension SQLDialect` 의 기본 구현들).
/// DB 마다 다른 부분은 abstract, 기본값이 있는 요구사항은 virtual, 확장 메서드는 일반 메서드다.
public abstract partial class SqlDialect : ISqlDialect
{
    public abstract DatabaseKind Kind { get; }

    public abstract string QuoteIdent(string name);
    public virtual string QuoteTable(string name) => QuoteIdent(name);
    public virtual string QuoteString(string value) => SqlText.QuoteString(value);

    public virtual string ApplyingLimit(string sql, int limit) => SqlText.ApplyingLimit(sql, limit);
    public abstract string? ReadOnlySessionSql { get; }
    public virtual string? SessionSetupSql => null;
    public virtual string? StatementTimeoutSql(int milliseconds) => null;
    public abstract string SessionIdSql { get; }
    public abstract string CancelSql(int sessionId);

    public abstract string DatabasesSql { get; }
    public abstract string UseSql(string name);
    public virtual bool MetadataNeedsUse => false;

    public virtual string InitialContainer(ConnectionProfile profile, IReadOnlyList<string> available) =>
        profile.Database.Length == 0 ? (available.Count > 0 ? available[0] : "") : profile.Database;

    public abstract string TablesSql(string schema);
    public abstract string ColumnsSql(string schema);
    public abstract string IndexesSql(string schema);
    public abstract string RoutinesSql(string schema);
    public abstract string DefinitionSql(RoutineMeta routine, string schema);
    public abstract string FormatDefinition(RoutineMeta routine, string schema, string body);

    public virtual string? ShowCreateTableSql(string table) => null;
    public abstract string CreateTableSql(TableMeta table);

    public abstract ExplainSteps Explain(string sql);

    public abstract string ColumnDefinition(ColumnMeta c);
    public abstract string DefaultLiteral(string value, string extra);
    public abstract string Render(IReadOnlyList<SchemaChange> changes, string table);
    public abstract string? AllTablesSql { get; }

    /// 인덱스 컬럼 목록 `(a, b)`
    internal string IndexColumns(IndexMeta i) => "(" + string.Join(", ", i.Columns.Select(QuoteIdent)) + ")";

    // MARK: - 화면에서 쓰는 DB 별 SQL

    /// 테이블 데이터 보기
    public string SelectAllSql(string table) => $"SELECT * FROM {QuoteTable(table)};";

    /// 인덱스 상세 (카디널리티/정의 등)
    public string IndexDetailSql(string table, string schema) => Kind switch
    {
        DatabaseKind.Postgres => $"""
            SELECT i.indexname, i.indexdef, s.idx_scan AS scans, pg_size_pretty(pg_relation_size(c.oid)) AS size
              FROM pg_indexes i
              JOIN pg_class c ON c.relname = i.indexname
              JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = i.schemaname
              LEFT JOIN pg_stat_user_indexes s ON s.indexrelid = c.oid
             WHERE i.schemaname = {QuoteString(schema)} AND i.tablename = {QuoteString(table)}
             ORDER BY i.indexname;
            """,
        DatabaseKind.Mssql => $"EXEC sp_helpindex {QuoteString(table)};",
        _ => $"SHOW INDEX FROM {QuoteIdent(table)};",
    };

    /// 서버 버전 한 줄
    public string VersionSql => Kind switch
    {
        DatabaseKind.Postgres => "SHOW server_version",
        DatabaseKind.Mssql => "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) + ' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(100))",
        _ => "SELECT VERSION()",
    };

    /// 프로시저 호출 문 틀
    public string CallSql(string name) => Kind == DatabaseKind.Mssql ? $"EXEC {QuoteTable(name)};" : $"CALL {QuoteTable(name)}();";

    // MARK: - 테이블 이름 변경 / 삭제

    public string RenameTableSql(string table, string newName)
    {
        switch (Kind)
        {
            case DatabaseKind.Postgres:
                return $"ALTER TABLE {QuoteTable(table)} RENAME TO {QuoteIdent(newName)};";
            case DatabaseKind.Mssql:
                // sp_rename 의 새 이름에는 스키마를 붙이지 않는다
                var parts = newName.Split('.', StringSplitOptions.RemoveEmptyEntries);
                var bare = parts.Length > 0 ? parts[^1] : newName;
                return $"EXEC sp_rename {QuoteString(QuoteTable(table))}, {QuoteString(bare)};";
            default:
                return $"RENAME TABLE {QuoteTable(table)} TO {QuoteIdent(newName)};";
        }
    }

    public string DropTableSql(string table) => $"DROP TABLE {QuoteTable(table)};";

    // MARK: - 외래 키 (Mac 판 ForeignKeys.swift)

    /// [테이블, 외래키 이름, 컬럼, 참조 테이블, 참조 컬럼, 순서] — 같은 DB/스키마 안의 참조만
    public string ForeignKeysSql(string schema)
    {
        if (this is PostgresDialect { Redshift: true } rs) return rs.RedshiftForeignKeysSql(schema);
        var s = QuoteString(schema);
        return Kind switch
        {
            DatabaseKind.Postgres => $"""
                SELECT c.relname, con.conname, a.attname, rc.relname, ra.attname, k.ord
                  FROM pg_constraint con
                  JOIN pg_class c ON c.oid = con.conrelid
                  JOIN pg_namespace n ON n.oid = c.relnamespace
                  JOIN pg_class rc ON rc.oid = con.confrelid
                  JOIN pg_namespace rn ON rn.oid = rc.relnamespace
                  JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS k(col, refcol, ord) ON true
                  JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.col
                  JOIN pg_attribute ra ON ra.attrelid = con.confrelid AND ra.attnum = k.refcol
                 WHERE con.contype = 'f' AND n.nspname = {s} AND rn.nspname = {s}
                 ORDER BY 1, 2, 6
                """,
            DatabaseKind.Mssql => """
                SELECT CASE WHEN s.name = 'dbo' THEN t.name ELSE s.name + '.' + t.name END, fk.name, c.name,
                       CASE WHEN rs.name = 'dbo' THEN rt.name ELSE rs.name + '.' + rt.name END, rc.name, fkc.constraint_column_id
                  FROM sys.foreign_keys fk
                  JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
                  JOIN sys.tables t ON t.object_id = fk.parent_object_id
                  JOIN sys.schemas s ON s.schema_id = t.schema_id
                  JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
                  JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
                  JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
                  JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                 ORDER BY 1, 2, 6
                """,
            _ => $"""
                SELECT TABLE_NAME, CONSTRAINT_NAME, COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME, ORDINAL_POSITION
                  FROM information_schema.KEY_COLUMN_USAGE
                 WHERE TABLE_SCHEMA = {s} AND REFERENCED_TABLE_NAME IS NOT NULL AND REFERENCED_TABLE_SCHEMA = {s}
                 ORDER BY 1, 2, 6
                """,
        };
    }

    // MARK: - 결과 페이징

    [GeneratedRegex(@"\bLIMIT\s+\d|\bOFFSET\b|\bFETCH\s+(FIRST|NEXT)\b|\bTOP\s*\(?\s*\d|\bINTO\b|\bFOR\s+UPDATE\b|\bFOR\s+SHARE\b")]
    private static partial Regex PagingBlocked();

    [GeneratedRegex(@"\bDISTINCT\b|\bUNION\b|\bINTERSECT\b|\bEXCEPT\b|\bOPTION\s*\(|\bFOR\s+(XML|JSON|BROWSE)\b")]
    private static partial Regex NoOffset();

    [GeneratedRegex(@"\bLIMIT\s+\d|\bTOP\s*\(?\s*\d|\bOFFSET\b|\bFETCH\s+(FIRST|NEXT)\b")]
    private static partial Regex Limited();

    /// SELECT 를 페이지 단위로 가져오는 SQL. 다음 페이지가 있는지 알기 위해 limit + 1 행을 요청한다.
    /// 이미 LIMIT/TOP/OFFSET/INTO/FOR UPDATE 가 있거나 SELECT 가 아니면 null (그대로 실행).
    public PagedSql? PagedSql(string sql, int limit, int offset)
    {
        if (limit <= 0) return null;
        var body = SqlText.TrimmedBody(sql);
        var kw = SqlText.FirstKeyword(body);
        var upper = SqlText.StripComments(body).ToUpperInvariant();
        if (PagingBlocked().IsMatch(upper)) return null;
        var fetch = limit + 1;

        if (Kind != DatabaseKind.Mssql)
        {
            if (kw is not ("SELECT" or "WITH")) return null;
            return new PagedSql($"{body}\nLIMIT {fetch}" + (offset > 0 ? $" OFFSET {offset}" : ""), 0);
        }
        if (SqlText.HasTopLevelOrderBy(upper))
        {
            if (kw is not ("SELECT" or "WITH")) return null;
            return new PagedSql($"{body}\nOFFSET {offset} ROWS FETCH NEXT {fetch} ROWS ONLY", 0);
        }
        // ORDER BY 가 없으면 순서 없는 정렬을 붙여 이번 페이지만 받는다.
        // (TOP(앞 페이지 + 이번 페이지) 로 받아 앞을 버리면 뒤 페이지일수록 다시 받는 행이 늘어 느려진다)
        // 순서 보장은 TOP 방식과 같다 (둘 다 없음).
        if (kw is "SELECT" or "WITH" && !NoOffset().IsMatch(upper))
            return new PagedSql($"{body}\nORDER BY (SELECT NULL) OFFSET {offset} ROWS FETCH NEXT {fetch} ROWS ONLY", 0);
        // DISTINCT · UNION 등은 ORDER BY (SELECT NULL) 을 쓸 수 없어 TOP(앞 페이지 + 이번 페이지) 로 받고 앞부분을 버린다.
        if (kw != "SELECT") return null;
        var top = ApplyingLimit(body, offset + fetch);
        return top == body ? null : new PagedSql(top, offset);
    }

    /// 쿼리 결과의 전체 행 수
    public string? CountSql(string sql)
    {
        var body = SqlText.TrimmedBody(sql);
        var kw = SqlText.FirstKeyword(body);
        if (!(kw == "SELECT" || (kw == "WITH" && Kind != DatabaseKind.Mssql))) return null;
        // SQL Server 는 서브쿼리 안의 ORDER BY 를 허용하지 않는다.
        if (Kind == DatabaseKind.Mssql) body = SqlText.RemovingTopLevelOrderBy(body);
        return $"SELECT COUNT(*) FROM (\n{body}\n) AS goddb_cnt";
    }

    // MARK: - 결과 정렬 (Mac 판 ResultSort.swift)

    /// 원래 쿼리를 감싸 정렬한 SQL. SELECT 가 아니면 null.
    /// 맨 바깥 ORDER BY 는 바깥 정렬로 대신하므로 뺀다 (단, LIMIT/TOP 이 있으면 결과 집합이 바뀌므로 둔다).
    public string? SortedSql(string sql, ResultSort sort)
    {
        var body = SqlText.TrimmedBody(sql);
        var kw = SqlText.FirstKeyword(body);
        if (!(kw == "SELECT" || (kw == "WITH" && Kind != DatabaseKind.Mssql))) return null;
        var upper = SqlText.StripComments(body).ToUpperInvariant();
        if (!Limited().IsMatch(upper)) body = SqlText.RemovingTopLevelOrderBy(body);
        return $"SELECT * FROM (\n{body}\n) AS goddb_sort ORDER BY {QuoteIdent(sort.Column)} {(sort.Ascending ? "ASC" : "DESC")}";
    }
}
