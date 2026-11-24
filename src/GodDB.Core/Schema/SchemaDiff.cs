using GodDB.Core.Dialects;
using GodDB.Core.Drivers;
using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.Core.Schema;

public enum SchemaDiffKind
{
    /// 대상에 테이블 없음
    MissingTable,
    /// 대상에만 있는 테이블 (참고용)
    ExtraTable,
    /// 테이블 자체 속성(코멘트)이 다름
    DifferentTable,
    MissingColumn,
    DifferentColumn,
    /// 대상에만 있는 컬럼 (참고용, 자동 삭제 안 함)
    ExtraColumn,
    MissingIndex,
    DifferentIndex,
    /// 대상에만 있는 인덱스 (참고용)
    ExtraIndex,
}

public enum SchemaDiffSeverity { Missing, Different, Extra }

/// 기준(source) 스키마와 대상(target) 스키마의 차이.
/// FixSql: 대상에 적용할 보정 SQL. 참고용 항목은 null.
public sealed record SchemaDiffItem(SchemaDiffKind Kind, string Table, string? Object, string Detail, string? FixSql)
{
    /// Swift rawValue 와 같은 이름 (missingTable …)
    public static string RawValue(SchemaDiffKind k)
    {
        var s = k.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }

    public string Id => $"{RawValue(Kind)}:{Table}.{Object ?? ""}";

    public SchemaDiffSeverity Severity => Kind switch
    {
        SchemaDiffKind.MissingTable or SchemaDiffKind.MissingColumn or SchemaDiffKind.MissingIndex => SchemaDiffSeverity.Missing,
        SchemaDiffKind.DifferentTable or SchemaDiffKind.DifferentColumn or SchemaDiffKind.DifferentIndex => SchemaDiffSeverity.Different,
        _ => SchemaDiffSeverity.Extra,
    };
}

public sealed record SchemaDiffReport(IReadOnlyList<SchemaDiffItem> Items, int MatchedTables)
{
    public int MissingCount => Items.Count(i => i.Severity == SchemaDiffSeverity.Missing);
    public int DifferentCount => Items.Count(i => i.Severity == SchemaDiffSeverity.Different);
    public int ExtraCount => Items.Count(i => i.Severity == SchemaDiffSeverity.Extra);

    /// 실행 가능한 보정 SQL 전체 (DROP TABLE/COLUMN 은 생성하지 않는다)
    public string FixScript => Grouped(Items);

    /// 고른 항목들의 보정 SQL 을 목록(`items`) 순서대로 테이블별로 묶어 이어 붙인다.
    /// 고른 순서와 관계없이 목록 순서를 따르고, 참고용(SQL 없음) 항목은 건너뛴다.
    public static string FixSql(IEnumerable<SchemaDiffItem> items, IReadOnlySet<string> selected) =>
        Grouped(items.Where(i => selected.Contains(i.Id)));

    /// 테이블마다 "-- 테이블" 한 줄 뒤에 그 테이블의 SQL 을 줄바꿈으로 잇고, 테이블 사이는 빈 줄로 띄운다
    public static string Grouped(IEnumerable<SchemaDiffItem> items)
    {
        var order = new List<string>();
        var byTable = new Dictionary<string, List<string>>();
        foreach (var item in items)
        {
            if (item.FixSql is not { } sql) continue;
            if (!byTable.TryGetValue(item.Table, out var list))
            {
                order.Add(item.Table);
                byTable[item.Table] = list = [];
            }
            list.Add(sql);
        }
        return string.Join("\n\n", order.Select(t => string.Join("\n", new[] { $"-- {t}" }.Concat(byTable[t]))));
    }
}

public static class SchemaDiff
{
    /// 스키마 비교 창이 쓰는 경로. 두 접속에서 비교할 DB(PostgreSQL 은 스키마)로 옮겨 읽고,
    /// 대상 DB 문법으로 보정 SQL 을 만든다. 대상 접속도 대상 DB 로 옮겨 두므로 보정 SQL 을 그 접속에서 바로 실행할 수 있다.
    public static async Task<SchemaDiffReport> CompareAsync(DbSession ss, string sdb, DbSession ts, string tdb,
                                                            CancellationToken ct = default)
    {
        await ss.UseAsync(sdb, ct);
        // PostgreSQL 은 search_path 에 따라 기본값 표기가 달라진다 (예: nextval('스키마.시퀀스')). 양쪽 모두 비교할 곳으로 옮긴다.
        await ts.UseAsync(tdb, ct);
        var source = await ss.LoadSchemaAsync(sdb, ct);
        var target = await ts.LoadSchemaAsync(tdb, ct);
        var creates = new Dictionary<string, string>();
        // 대상에 없는 테이블은 MySQL 이면 기준 DB 의 SHOW CREATE TABLE 을 쓴다. 종류가 다르면 대상 문법으로 만든다.
        if (ss.Profile.Kind == ts.Profile.Kind)
        {
            var targetNames = target.Select(x => x.Name.ToLowerInvariant()).ToHashSet();
            foreach (var table in source.Where(x => !targetNames.Contains(x.Name.ToLowerInvariant())))
                if (await ss.ShowCreateTableAsync(table.Name, ct) is { } ddl) creates[table.Name] = ddl;
        }
        return Compare(source, target, creates, ts.Dialect);
    }

    /// <param name="createStatements">대상에 없는 테이블의 CREATE 문 (MySQL 은 기준 DB 의 SHOW CREATE TABLE). 없으면 메타데이터로 만든다.</param>
    /// <param name="dialect">보정 SQL 을 만들 대상 DB 의 문법 (기본 MySQL)</param>
    public static SchemaDiffReport Compare(IReadOnlyList<TableMeta> source, IReadOnlyList<TableMeta> target,
                                           IReadOnlyDictionary<string, string>? createStatements = null,
                                           ISqlDialect? dialect = null)
    {
        dialect ??= new MySqlDialect();
        var targetByName = new Dictionary<string, TableMeta>();
        foreach (var t in target) targetByName.TryAdd(t.Name.ToLowerInvariant(), t);
        var sourceNames = source.Select(s => s.Name.ToLowerInvariant()).ToHashSet();
        var items = new List<SchemaDiffItem>();
        int matched = 0;

        foreach (var s in source)
        {
            if (!targetByName.TryGetValue(s.Name.ToLowerInvariant(), out var t))
            {
                var create = createStatements != null && createStatements.TryGetValue(s.Name, out var cs) ? cs : dialect.CreateTableSql(s);
                if (!create.EndsWith(';')) create += ";";
                items.Add(new(SchemaDiffKind.MissingTable, s.Name, null, Tr("테이블 없음"), create));
                continue;
            }
            var tableItems = CompareTable(s, t, dialect);
            if (tableItems.Count == 0) matched++;
            items.AddRange(tableItems);
        }
        foreach (var t in target.Where(t => !sourceNames.Contains(t.Name.ToLowerInvariant())))
            items.Add(new(SchemaDiffKind.ExtraTable, t.Name, null, Tr("대상에만 있음"), null));
        return new SchemaDiffReport(items, matched);
    }

    internal static List<SchemaDiffItem> CompareTable(TableMeta s, TableMeta t, ISqlDialect dialect)
    {
        var items = new List<SchemaDiffItem>();
        var tCols = new Dictionary<string, ColumnMeta>();
        foreach (var c in t.Columns) tCols.TryAdd(c.Name.ToLowerInvariant(), c);
        var sColNames = s.Columns.Select(c => c.Name.ToLowerInvariant()).ToHashSet();
        string Sql(SchemaChange change) => dialect.Render([change], t.Name);

        if (s.Comment != t.Comment)
            items.Add(new(SchemaDiffKind.DifferentTable, s.Name, null, Tr("테이블 코멘트 변경"),
                          Sql(new SchemaChange.TableComment(t.Comment, s.Comment))));

        string? previous = null;
        foreach (var c in s.Columns.OrderBy(c => c.Position))
        {
            if (tCols.TryGetValue(c.Name.ToLowerInvariant(), out var tc))
            {
                var diffs = ColumnDifferences(c, tc);
                if (diffs.Count > 0)
                    items.Add(new(SchemaDiffKind.DifferentColumn, s.Name, c.Name, string.Join(", ", diffs),
                                  Sql(new SchemaChange.ModifyColumn(tc, c))));
            }
            else
            {
                items.Add(new(SchemaDiffKind.MissingColumn, s.Name, c.Name, Tr("컬럼 없음"),
                              Sql(new SchemaChange.AddColumn(c, previous))));
            }
            previous = c.Name;
        }
        foreach (var c in t.Columns.Where(c => !sColNames.Contains(c.Name.ToLowerInvariant())))
            items.Add(new(SchemaDiffKind.ExtraColumn, s.Name, c.Name, Tr("대상에만 있음"), null));

        // 기본 키는 DB 마다 이름이 달라서 이름이 아니라 "기본 키" 끼리 비교한다.
        static string Key(IndexMeta i) => i.IsPrimary ? "#primary" : i.Name.ToLowerInvariant();
        var tIdx = new Dictionary<string, IndexMeta>();
        foreach (var i in t.Indexes) tIdx.TryAdd(Key(i), i);
        var sIdxKeys = s.Indexes.Select(Key).ToHashSet();
        foreach (var i in s.Indexes)
        {
            if (tIdx.TryGetValue(Key(i), out var ti))
            {
                if (ti.Unique != i.Unique || !ti.Columns.Select(x => x.ToLowerInvariant()).SequenceEqual(i.Columns.Select(x => x.ToLowerInvariant())))
                {
                    var detail = $"({string.Join(", ", ti.Columns)}) → ({string.Join(", ", i.Columns)})";
                    var fixedIndex = i with { Name = ti.Name, IsConstraint = ti.IsConstraint };
                    items.Add(new(SchemaDiffKind.DifferentIndex, s.Name, i.Name, detail,
                                  dialect.Render([new SchemaChange.DropIndex(ti), new SchemaChange.AddIndex(fixedIndex)], t.Name)));
                }
            }
            else
            {
                items.Add(new(SchemaDiffKind.MissingIndex, s.Name, i.Name, Tr("인덱스 없음"), Sql(new SchemaChange.AddIndex(i))));
            }
        }
        foreach (var i in t.Indexes.Where(i => !sIdxKeys.Contains(Key(i))))
            items.Add(new(SchemaDiffKind.ExtraIndex, s.Name, i.Name, Tr("대상에만 있음"), null));
        return items;
    }

    public static List<string> ColumnDifferences(ColumnMeta s, ColumnMeta t)
    {
        var d = new List<string>();
        if (s.ColumnType.ToLowerInvariant() != t.ColumnType.ToLowerInvariant()) d.Add(Tr("타입 %@ → %@", t.ColumnType, s.ColumnType));
        if (s.Nullable != t.Nullable) d.Add(s.Nullable ? "NOT NULL → NULL" : "NULL → NOT NULL");
        if (s.DefaultValue != t.DefaultValue) d.Add(Tr("기본값 %@ → %@", t.DefaultValue ?? Tr("없음"), s.DefaultValue ?? Tr("없음")));
        if (NormalizedExtra(s.Extra) != NormalizedExtra(t.Extra)) d.Add($"EXTRA {t.Extra} → {s.Extra}");
        if (s.Comment != t.Comment) d.Add(Tr("코멘트 변경"));
        return d;
    }

    internal static string NormalizedExtra(string extra) =>
        SqlText.TrimSpaces(extra.Replace("DEFAULT_GENERATED", "", StringComparison.OrdinalIgnoreCase)).ToLowerInvariant();

    /// MySQL 컬럼 정의 (기존 호출부 호환용)
    public static string ColumnDefinition(ColumnMeta c) => new MySqlDialect().ColumnDefinition(c);
}
