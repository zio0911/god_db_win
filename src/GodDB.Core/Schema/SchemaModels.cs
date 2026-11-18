namespace GodDB.Core.Schema;

public sealed record ColumnMeta
{
    public string Id => Name;
    public string Name { get; init; }
    public string ColumnType { get; init; }
    public bool Nullable { get; init; }
    public string? DefaultValue { get; init; }
    public string Extra { get; init; }
    public string ColumnKey { get; init; }
    public string Comment { get; init; }
    public int Position { get; init; }

    public ColumnMeta(string name, string columnType, bool nullable, string? defaultValue = null,
                      string extra = "", string columnKey = "", string comment = "", int position = 0)
    {
        Name = name;
        ColumnType = columnType;
        Nullable = nullable;
        DefaultValue = defaultValue;
        Extra = extra;
        ColumnKey = columnKey;
        Comment = comment;
        Position = position;
    }

    public bool IsPrimaryKey => ColumnKey == "PRI";
}

public sealed record IndexMeta
{
    public string Id => Name;
    public string Name { get; init; }
    public bool Unique { get; init; }
    public List<string> Columns { get; init; }
    /// 기본 키 인덱스 (MySQL 은 이름이 PRIMARY, PostgreSQL/SQL Server 는 제약조건 이름)
    public bool IsPrimary { get; init; }
    /// PRIMARY KEY / UNIQUE 제약조건이 만든 인덱스 (삭제 시 DROP CONSTRAINT 필요)
    public bool IsConstraint { get; init; }

    public IndexMeta(string name, bool unique, IEnumerable<string> columns, bool? primary = null, bool? constraint = null)
    {
        Name = name;
        Unique = unique;
        Columns = [.. columns];
        var p = primary ?? (name == "PRIMARY");
        IsPrimary = p;
        IsConstraint = constraint ?? p;
    }

    // Swift 구조체처럼 값으로 비교한다 (컬럼 목록 포함)
    public bool Equals(IndexMeta? other) =>
        other is not null && Name == other.Name && Unique == other.Unique && IsPrimary == other.IsPrimary
        && IsConstraint == other.IsConstraint && Columns.SequenceEqual(other.Columns);

    public override int GetHashCode() => HashCode.Combine(Name, Unique, IsPrimary, IsConstraint, Columns.Count);
}

public sealed record TableMeta
{
    public string Id => Name;
    public string Name { get; init; }
    public string Comment { get; init; }
    public List<ColumnMeta> Columns { get; init; }
    public List<IndexMeta> Indexes { get; init; }
    /// 이 테이블이 다른 테이블을 참조하는 외래 키
    public List<ForeignKeyMeta> ForeignKeys { get; init; }

    public TableMeta(string name, string comment = "", IEnumerable<ColumnMeta>? columns = null,
                     IEnumerable<IndexMeta>? indexes = null, IEnumerable<ForeignKeyMeta>? foreignKeys = null)
    {
        Name = name;
        Comment = comment;
        Columns = columns?.ToList() ?? [];
        Indexes = indexes?.ToList() ?? [];
        ForeignKeys = foreignKeys?.ToList() ?? [];
    }

    public bool Equals(TableMeta? other) =>
        other is not null && Name == other.Name && Comment == other.Comment && Columns.SequenceEqual(other.Columns)
        && Indexes.SequenceEqual(other.Indexes) && ForeignKeys.SequenceEqual(other.ForeignKeys);

    public override int GetHashCode() => HashCode.Combine(Name, Comment, Columns.Count, Indexes.Count, ForeignKeys.Count);
}

/// 메타데이터 조회 결과(문자열 행)를 TableMeta 로 조립한다. 컬럼 순서는 ISqlDialect 설명 참고.
/// (Mac 판에서는 DBSession.swift 안에 있다)
public static partial class SchemaBuilder
{
    public static List<TableMeta> Build(IReadOnlyList<IReadOnlyList<string?>> tableRows,
                                        IReadOnlyList<IReadOnlyList<string?>> columnRows,
                                        IReadOnlyList<IReadOnlyList<string?>> indexRows)
    {
        var tables = new Dictionary<string, TableMeta>();
        var order = new List<string>();
        foreach (var r in tableRows)
        {
            if (r[0] is not string name || tables.ContainsKey(name)) continue;
            tables[name] = new TableMeta(name, comment: r.Count > 1 ? (r[1] ?? "") : "");
            order.Add(name);
        }
        foreach (var r in columnRows)
        {
            if (r[0] is not string t || r[1] is not string name || !tables.TryGetValue(t, out var table)) continue;
            table.Columns.Add(new ColumnMeta(
                name: name,
                columnType: r[2] ?? "",
                nullable: (r[3] ?? "") == "YES",
                defaultValue: r[4],
                extra: r[5] ?? "",
                columnKey: r[6] ?? "",
                comment: r[7] ?? "",
                position: int.TryParse(r[8] ?? "", out var p) ? p : 0));
        }
        foreach (var r in indexRows)
        {
            if (r[0] is not string t || r[1] is not string idx || r[3] is not string col || !tables.TryGetValue(t, out var table)) continue;
            var existing = table.Indexes.FirstOrDefault(i => i.Name == idx);
            if (existing != null)
            {
                existing.Columns.Add(col);
            }
            else
            {
                bool? primary = r.Count > 4 && r[4] != null ? r[4] == "1" : null;
                bool? constraint = r.Count > 5 && r[5] != null ? r[5] == "1" : null;
                table.Indexes.Add(new IndexMeta(idx, unique: (r[2] ?? "1") == "0", columns: [col],
                                                primary: primary, constraint: constraint));
            }
        }
        return [.. order.Where(tables.ContainsKey).Select(n => tables[n])];
    }
}
