using System.Text.RegularExpressions;
using GodDB.Core.Dialects;
using GodDB.Core.Results;

namespace GodDB.Core.Schema;

/// 결과 안의 외래 키 관계 하나 (진짜 외래 키 또는 이름으로 추정한 관계). (Mac: FKFollow.swift 의 FKRelation)
public sealed class FKRelation : IEquatable<FKRelation>
{
    public string Id => $"{Table}({string.Join(",", Columns)})→{ReferencedTable}({string.Join(",", ReferencedColumns)})";
    /// 참조하는 테이블 (자식)
    public string Table { get; }
    public IReadOnlyList<string> Columns { get; }
    /// 참조되는 테이블 (부모)
    public string ReferencedTable { get; }
    public IReadOnlyList<string> ReferencedColumns { get; }
    /// 컬럼 이름으로 추정한 관계 (외래 키 제약 없음)
    public bool Inferred { get; }

    public FKRelation(string table, IEnumerable<string> columns, string referencedTable, IEnumerable<string> referencedColumns, bool inferred)
    {
        Table = table;
        Columns = [.. columns];
        ReferencedTable = referencedTable;
        ReferencedColumns = [.. referencedColumns];
        Inferred = inferred;
    }

    public FKRelation(ForeignKeyMeta fk, bool inferred)
        : this(fk.Table, fk.Columns, fk.ReferencedTable, fk.ReferencedColumns, inferred) { }

    /// 이 관계에 그 컬럼이 들어 있는지 (대소문자 무시)
    public bool Contains(string column) => Columns.Any(c => c.Equals(column, StringComparison.OrdinalIgnoreCase));

    public bool Equals(FKRelation? other) =>
        other is not null && Table == other.Table && ReferencedTable == other.ReferencedTable && Inferred == other.Inferred
        && Columns.SequenceEqual(other.Columns) && ReferencedColumns.SequenceEqual(other.ReferencedColumns);

    public override bool Equals(object? obj) => Equals(obj as FKRelation);
    public override int GetHashCode() => HashCode.Combine(Id, Inferred);
    public override string ToString() => Id + (Inferred ? " (inferred)" : "");
}

/// 외래 키 따라가기: 결과 행에서 참조하는 행 / 이 행을 참조하는 행을 새 탭으로 조회한다. (Mac: FKFollow.swift)
public static partial class FKFollow
{
    // MARK: - 결과의 원본 테이블

    /// 결과가 테이블 하나에서 온 조회면 그 테이블 (결과 편집 EditTarget 과 같은 판단: JOIN · 집계 · UNION · 서브쿼리 제외)
    public static TableMeta? SourceTable(string sql, IReadOnlyList<TableMeta> tables) =>
        ResultEditing.SingleTable(sql) is { } name ? ResultEditing.TableNamed(name, tables) : null;

    /// 단일 컬럼 기본 키 이름 (복합 키 · 키 없음은 null)
    public static string? SinglePrimaryKey(TableMeta table)
    {
        var cols = table.Indexes.FirstOrDefault(i => i.IsPrimary)?.Columns
                   ?? table.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        return cols.Count == 1 ? cols[0] : null;
    }

    // MARK: - 관계 찾기

    /// 스키마 전체의 추정 관계 (ER 다이어그램과 같은 규칙). 스키마를 읽을 때 한 번 계산해 둔다.
    public static List<ForeignKeyMeta> InferredRelations(IReadOnlyList<TableMeta> tables) => DiagramLayout.InferredRelations(tables);

    /// 이 테이블이 참조하는 관계 (진짜 외래 키 먼저, 그다음 추정)
    public static List<FKRelation> Outgoing(TableMeta table, IEnumerable<ForeignKeyMeta> inferred) =>
        [.. table.ForeignKeys.Select(f => new FKRelation(f, inferred: false)),
         .. inferred.Where(f => f.Table == table.Name).Select(f => new FKRelation(f, inferred: true))];

    /// 이 컬럼이 들어 있는 관계 (보통 하나)
    public static List<FKRelation> RelationsForColumn(string column, IEnumerable<FKRelation> relations) =>
        [.. relations.Where(r => r.Contains(column))];

    /// 이 테이블의 기본 키(단일 컬럼)를 참조하는 테이블들. 기본 키가 복합이거나 없으면 빈 목록.
    public static List<FKRelation> Incoming(TableMeta table, IReadOnlyList<TableMeta> tables, IEnumerable<ForeignKeyMeta> inferred)
    {
        if (SinglePrimaryKey(table)?.ToLowerInvariant() is not { } pk) return [];
        var real = tables.SelectMany(t => t.ForeignKeys).Select(f => new FKRelation(f, inferred: false));
        var guessed = inferred.Select(f => new FKRelation(f, inferred: true));
        var seen = new HashSet<string>();
        return [.. real.Concat(guessed)
            .Where(r => r.ReferencedTable == table.Name && r.Columns.Count == 1
                        && r.ReferencedColumns.FirstOrDefault()?.ToLowerInvariant() == pk)
            .Where(r => seen.Add(r.Id))
            .OrderBy(r => r.Table.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(r => r.Columns[0].ToLowerInvariant(), StringComparer.Ordinal)];
    }

    // MARK: - 값

    private static int ColumnIndex(string column, IReadOnlyList<string> resultColumns)
    {
        for (var i = 0; i < resultColumns.Count; i++)
            if (resultColumns[i].Equals(column, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// 결과 행에서 관계의 컬럼 값들 (관계의 컬럼 순서). 결과에 없는 컬럼은 null.
    public static List<string?> Values(FKRelation relation, IReadOnlyList<string> resultColumns, IReadOnlyList<string?> row) =>
        [.. relation.Columns.Select(c => Value(c, resultColumns, row))];

    /// 결과 행에서 한 컬럼 값 (결과에 없거나 NULL 이면 null)
    public static string? Value(string column, IReadOnlyList<string> resultColumns, IReadOnlyList<string?> row)
    {
        var i = ColumnIndex(column, resultColumns);
        return i >= 0 && i < row.Count ? row[i] : null;
    }

    // MARK: - SQL

    /// 따라가기: `SELECT * FROM <참조 테이블> WHERE <참조 컬럼> = <값>` (복합 키는 AND). 값이 하나라도 NULL 이면 null (비활성).
    /// 값 인용은 컬럼 타입 기준 (ID 로 찾기와 같음): 참조되는 컬럼 타입, 모르면 참조하는 컬럼 타입.
    public static string? FollowSql(FKRelation relation, IReadOnlyList<string?> values, IReadOnlyList<TableMeta> tables, ISqlDialect dialect)
    {
        if (relation.Columns.Count == 0 || values.Count != relation.Columns.Count
            || relation.ReferencedColumns.Count != relation.Columns.Count) return null;
        if (values.Any(v => v == null)) return null;
        var parent = ResultEditing.TableNamed(relation.ReferencedTable, tables);
        var child = ResultEditing.TableNamed(relation.Table, tables);
        var conditions = relation.ReferencedColumns.Select((refCol, i) =>
        {
            var numeric = IsNumeric(refCol, parent) ?? IsNumeric(relation.Columns[i], child) ?? false;
            return $"{dialect.QuoteIdent(refCol)} = {Literal(values[i]!, numeric, dialect)}";
        });
        return $"SELECT * FROM {dialect.QuoteTable(relation.ReferencedTable)} WHERE {string.Join(" AND ", conditions)}";
    }

    /// 참조하는 행: `SELECT * FROM <참조하는 테이블> WHERE <그 테이블의 FK 컬럼> = <이 행 기본 키 값>`. 값이 NULL 이면 null.
    public static string? ReferencingSql(FKRelation relation, string? primaryKeyValue, IReadOnlyList<TableMeta> tables, ISqlDialect dialect)
    {
        if (primaryKeyValue is not { } value || relation.Columns.Count != 1) return null;
        var col = relation.Columns[0];
        var child = ResultEditing.TableNamed(relation.Table, tables);
        var parent = ResultEditing.TableNamed(relation.ReferencedTable, tables);
        var numeric = IsNumeric(col, child)
                      ?? (relation.ReferencedColumns.FirstOrDefault() is { } rc ? IsNumeric(rc, parent) : null)
                      ?? false;
        return $"SELECT * FROM {dialect.QuoteTable(relation.Table)} WHERE {dialect.QuoteIdent(col)} = " + Literal(value, numeric, dialect);
    }

    /// 컬럼이 숫자 타입인지 (테이블 · 컬럼을 모르면 null)
    internal static bool? IsNumeric(string column, TableMeta? table) =>
        table?.Columns.FirstOrDefault(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)) is { } c
            ? IsNumericType(c.ColumnType)
            : null;

    // 값 인용 규칙은 Mac 의 IDSearch.literal / isNumericType 과 같다.
    // (ID 로 찾기는 다른 작업에서 옮기므로, 겹치지 않게 여기에 같은 규칙을 둔다)
    private static readonly HashSet<string> NumericTypes =
    [
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint",
        "decimal", "numeric", "float", "double", "real", "double precision", "number",
        "int2", "int4", "int8", "float4", "float8", "serial", "smallserial", "bigserial",
        "money", "smallmoney",
    ];

    /// 정수 · 소수 타입인지 (`bigint(20) unsigned`, `numeric(10,2)`, `int4` 등)
    internal static bool IsNumericType(string columnType)
    {
        var t = columnType.ToLowerInvariant();
        var p = t.IndexOf('(');
        if (p >= 0) t = t[..p];
        foreach (var word in new[] { " unsigned", " signed", " zerofill", " identity" }) t = t.Replace(word, "");
        return NumericTypes.Contains(t.Trim(' '));
    }

    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial Regex NumberLiteral();

    /// 숫자 컬럼이고 값이 숫자 꼴이면 그대로, 아니면 DB 별 문자열 인용 (SQL Server 는 N'…')
    internal static string Literal(string value, bool numeric, ISqlDialect dialect) =>
        numeric && NumberLiteral().IsMatch(value) ? value : dialect.QuoteString(value);

    // MARK: - 탭 제목

    /// 따라가기 탭: `↗ <테이블>`
    public static string FollowTitle(FKRelation relation) => "↗ " + relation.ReferencedTable;
    /// 참조하는 행 탭: `↙ <테이블>`
    public static string ReferencingTitle(FKRelation relation) => "↙ " + relation.Table;
}
