using System.Text.RegularExpressions;
using GodDB.Core.Dialects;
using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.Core.Schema;

/// 테이블 수정 화면에서 편집 중인 컬럼. OriginalName 이 null 이면 새 컬럼.
public sealed record EditableColumn
{
    public Guid Id { get; init; }
    public string? OriginalName { get; set; }
    public string Name { get; set; }
    public string ColumnType { get; set; }
    public bool Nullable { get; set; }
    /// null 이면 DEFAULT 없음
    public string? DefaultValue { get; set; }
    public string Extra { get; set; }
    public string Comment { get; set; }

    public EditableColumn(Guid? id = null, string? originalName = null, string name = "", string columnType = "varchar(100)",
                          bool nullable = true, string? defaultValue = null, string extra = "", string comment = "")
    {
        Id = id ?? Guid.NewGuid();
        OriginalName = originalName;
        Name = name;
        ColumnType = columnType;
        Nullable = nullable;
        DefaultValue = defaultValue;
        Extra = extra;
        Comment = comment;
    }

    public EditableColumn(ColumnMeta c)
        : this(originalName: c.Name, name: c.Name, columnType: c.ColumnType, nullable: c.Nullable,
               defaultValue: c.DefaultValue, extra: c.Extra, comment: c.Comment) { }

    /// 화면에서 바뀜 표시용
    public ColumnMeta MetaForCompare => Meta;

    internal ColumnMeta Meta => new(Name, ColumnType, Nullable, DefaultValue, extra: Extra, comment: Comment);
}

public sealed record EditableIndex
{
    public Guid Id { get; init; }
    public string? OriginalName { get; set; }
    public string Name { get; set; }
    public bool Unique { get; set; }
    public List<string> Columns { get; set; }

    public EditableIndex(Guid? id = null, string? originalName = null, string name = "", bool unique = false, IEnumerable<string>? columns = null)
    {
        Id = id ?? Guid.NewGuid();
        OriginalName = originalName;
        Name = name;
        Unique = unique;
        Columns = columns?.ToList() ?? [];
    }

    public EditableIndex(IndexMeta i) : this(originalName: i.Name, name: i.Name, unique: i.Unique, columns: i.Columns) { }

    internal IndexMeta Meta => new(Name, Unique, Columns);
}

/// 테이블 수정 입력 오류 (Swift TableAlterError)
public abstract record TableAlterError
{
    private TableAlterError() { }

    public sealed record EmptyColumnName : TableAlterError;
    public sealed record EmptyColumnType(string Column) : TableAlterError;
    public sealed record DuplicateColumn(string Column) : TableAlterError;
    public sealed record EmptyIndex(string Index) : TableAlterError;
    public sealed record UnknownIndexColumn(string Index, string Column) : TableAlterError;
    public sealed record NoColumns : TableAlterError;

    public string Description => this switch
    {
        EmptyColumnName => Tr("이름이 비어 있는 컬럼이 있어요."),
        EmptyColumnType(var c) => Tr("%@ 컬럼의 타입이 비어 있어요.", c),
        DuplicateColumn(var c) => Tr("컬럼 이름 %@ 이(가) 중복돼요.", c),
        EmptyIndex(var i) => Tr("%@ 인덱스에 이름 또는 컬럼이 없어요.", i.Length == 0 ? Tr("이름 없는") : i),
        UnknownIndexColumn(var i, var c) => Tr("%@ 인덱스의 컬럼 %@ 이(가) 테이블에 없어요.", i, c),
        _ => Tr("컬럼이 최소 1개는 있어야 해요."),
    };
}

public sealed class TableAlterException(TableAlterError error) : Exception(error.Description)
{
    public TableAlterError Error { get; } = error;
}

/// 원래 테이블과 편집 결과를 비교해 하나의 ALTER TABLE 문을 만든다.
public static class TableAlter
{
    /// 변경 사항이 없으면 null.
    /// <exception cref="TableAlterException">입력이 잘못된 경우</exception>
    public static string? Statement(TableMeta original, IReadOnlyList<EditableColumn> columns, IReadOnlyList<EditableIndex> indexes,
                                    string comment, ISqlDialect? dialect = null)
    {
        var list = Changes(original, columns, indexes, comment);
        return list.Count == 0 ? null : (dialect ?? new MySqlDialect()).Render(list, original.Name);
    }

    /// 원래 테이블과 편집 결과를 비교해 변경 목록을 만든다. (순서: 인덱스 삭제 → 컬럼 삭제 → 컬럼 변경/추가 → 인덱스 추가 → 코멘트)
    public static List<SchemaChange> Changes(TableMeta original, IReadOnlyList<EditableColumn> columns, IReadOnlyList<EditableIndex> indexes,
                                             string comment)
    {
        Validate(columns, indexes);
        var output = new List<SchemaChange>();
        var originalCols = new Dictionary<string, ColumnMeta>();
        foreach (var c in original.Columns) originalCols.TryAdd(c.Name, c);
        var originalIdx = new Dictionary<string, IndexMeta>();
        foreach (var i in original.Indexes) originalIdx.TryAdd(i.Name, i);

        // 1) 인덱스 삭제 (삭제됐거나 바뀐 것) — 컬럼 삭제보다 먼저
        var keptIndexNames = indexes.Select(i => i.OriginalName).OfType<string>().ToHashSet();
        var droppedIndexNames = new HashSet<string>();
        foreach (var i in original.Indexes.Where(i => !keptIndexNames.Contains(i.Name)))
        {
            output.Add(new SchemaChange.DropIndex(i));
            droppedIndexNames.Add(i.Name);
        }
        foreach (var e in indexes)
        {
            if (e.OriginalName is not string on || !originalIdx.TryGetValue(on, out var o)) continue;
            if (o.Name != e.Name || o.Unique != e.Unique || !o.Columns.SequenceEqual(e.Columns))
            {
                output.Add(new SchemaChange.DropIndex(o));
                droppedIndexNames.Add(o.Name);
            }
        }

        // 2) 컬럼 삭제
        var keptColumnNames = columns.Select(c => c.OriginalName).OfType<string>().ToHashSet();
        foreach (var c in original.Columns.Where(c => !keptColumnNames.Contains(c.Name)))
            output.Add(new SchemaChange.DropColumn(c.Name));

        // 3) 컬럼 변경 / 추가 (편집 순서대로, 새 컬럼은 앞 컬럼 뒤에)
        string? previous = null;
        foreach (var c in columns)
        {
            if (c.OriginalName is string on && originalCols.TryGetValue(on, out var o))
            {
                if (on != c.Name)
                    output.Add(new SchemaChange.RenameColumn(o, c.Meta));
                else if (SchemaDiff.ColumnDifferences(c.Meta, o).Count > 0)
                    output.Add(new SchemaChange.ModifyColumn(o, c.Meta));
            }
            else
            {
                output.Add(new SchemaChange.AddColumn(c.Meta, previous));
            }
            previous = c.Name;
        }

        // 4) 인덱스 추가 (새 것 + 바뀐 것)
        foreach (var e in indexes)
        {
            IndexMeta? orig = e.OriginalName is string on && originalIdx.TryGetValue(on, out var oi) ? oi : null;
            var changed = e.OriginalName is string n && droppedIndexNames.Contains(n);
            if (orig == null || changed)
            {
                var isPrimary = orig?.IsPrimary ?? (e.Name == "PRIMARY");
                var meta = e.Meta with { IsPrimary = isPrimary, IsConstraint = orig?.IsConstraint ?? isPrimary };
                output.Add(new SchemaChange.AddIndex(meta));
            }
        }

        // 5) 테이블 코멘트
        if (comment != original.Comment)
            output.Add(new SchemaChange.TableComment(original.Comment, comment));
        return output;
    }

    internal static void Validate(IReadOnlyList<EditableColumn> columns, IReadOnlyList<EditableIndex> indexes)
    {
        if (columns.Count == 0) throw new TableAlterException(new TableAlterError.NoColumns());
        var seen = new HashSet<string>();
        foreach (var c in columns)
        {
            var n = SqlText.TrimSpaces(c.Name);
            if (n.Length == 0) throw new TableAlterException(new TableAlterError.EmptyColumnName());
            if (SqlText.TrimSpaces(c.ColumnType).Length == 0) throw new TableAlterException(new TableAlterError.EmptyColumnType(n));
            if (!seen.Add(n.ToLowerInvariant())) throw new TableAlterException(new TableAlterError.DuplicateColumn(n));
        }
        foreach (var i in indexes)
        {
            if (SqlText.TrimSpaces(i.Name).Length == 0 || i.Columns.Count == 0)
                throw new TableAlterException(new TableAlterError.EmptyIndex(i.Name));
            foreach (var col in i.Columns)
                if (!seen.Contains(col.ToLowerInvariant()))
                    throw new TableAlterException(new TableAlterError.UnknownIndexColumn(i.Name, col));
        }
    }
}

/// 컬럼 Extra 를 화면 항목(자동 증가, ON UPDATE, 나머지)으로 나누고 다시 합친다.
/// 값을 고칠 때만 합치므로, 손대지 않으면 원래 Extra 그대로 남는다.
public sealed partial record ColumnExtraParts
{
    public bool AutoIncrement { get; set; }
    public string OnUpdate { get; set; }
    public string Rest { get; set; }

    [GeneratedRegex(@"on update\s+(\S+(?:\(\d*\))?)", RegexOptions.IgnoreCase)] private static partial Regex OnUpdateClause();
    [GeneratedRegex(@"^on update\s+", RegexOptions.IgnoreCase)] private static partial Regex OnUpdatePrefix();

    public ColumnExtraParts(string extra, DatabaseKind kind)
    {
        var s = extra;
        if (kind is DatabaseKind.Mysql or DatabaseKind.Redis)
        {
            AutoIncrement = s.Contains("auto_increment", StringComparison.OrdinalIgnoreCase);
            s = s.Replace("auto_increment", "", StringComparison.OrdinalIgnoreCase);
            var m = OnUpdateClause().Match(s);
            if (m.Success)
            {
                OnUpdate = OnUpdatePrefix().Replace(m.Value, "");
                s = s.Remove(m.Index, m.Length);
            }
            else
            {
                OnUpdate = "";
            }
        }
        else
        {
            AutoIncrement = s.ToLowerInvariant().Contains("identity");
            OnUpdate = "";
        }
        Rest = string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (kind is DatabaseKind.Postgres or DatabaseKind.Mssql && AutoIncrement) Rest = "";
    }

    public string Compose(DatabaseKind kind)
    {
        if (kind is DatabaseKind.Mysql or DatabaseKind.Redis)
        {
            var parts = new List<string>();
            if (Rest.Length > 0) parts.Add(Rest);
            if (AutoIncrement) parts.Add("auto_increment");
            if (SqlText.TrimSpaces(OnUpdate).Length > 0) parts.Add($"on update {SqlText.TrimSpaces(OnUpdate)}");
            return string.Join(" ", parts);
        }
        return AutoIncrement ? "identity" : Rest;
    }
}
