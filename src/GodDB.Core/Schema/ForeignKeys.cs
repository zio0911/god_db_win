namespace GodDB.Core.Schema;

/// 외래 키 (여러 컬럼 가능)
public sealed record ForeignKeyMeta
{
    public string Id => $"{Table}.{Name}";
    public string Name { get; init; }
    public string Table { get; init; }
    public List<string> Columns { get; init; }
    public string ReferencedTable { get; init; }
    public List<string> ReferencedColumns { get; init; }

    public ForeignKeyMeta(string name, string table, IEnumerable<string> columns, string referencedTable, IEnumerable<string> referencedColumns)
    {
        Name = name;
        Table = table;
        Columns = [.. columns];
        ReferencedTable = referencedTable;
        ReferencedColumns = [.. referencedColumns];
    }

    public bool Equals(ForeignKeyMeta? other) =>
        other is not null && Name == other.Name && Table == other.Table && ReferencedTable == other.ReferencedTable
        && Columns.SequenceEqual(other.Columns) && ReferencedColumns.SequenceEqual(other.ReferencedColumns);

    public override int GetHashCode() => HashCode.Combine(Name, Table, ReferencedTable, Columns.Count);
}

public static partial class SchemaBuilder
{
    /// 외래 키 조회 결과 [테이블, 외래키 이름, 컬럼, 참조 테이블, 참조 컬럼, 순서] 를 테이블에 붙인다.
    /// 입력 테이블은 바꾸지 않고 새 목록을 돌려준다 (Swift 값 복사와 같게).
    public static List<TableMeta> AttachForeignKeys(IReadOnlyList<TableMeta> tables, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var byName = new Dictionary<string, int>();
        for (int i = 0; i < tables.Count; i++) byName.TryAdd(tables[i].Name, i);
        var output = tables.Select(t => t with
        {
            ForeignKeys = [.. t.ForeignKeys.Select(f => f with { Columns = [.. f.Columns], ReferencedColumns = [.. f.ReferencedColumns] })],
        }).ToList();
        foreach (var r in rows)
        {
            if (r.Count < 5 || r[0] is not string t || r[1] is not string name || r[2] is not string col
                || r[3] is not string refT || r[4] is not string refC || !byName.TryGetValue(t, out var i)) continue;
            var existing = output[i].ForeignKeys.FirstOrDefault(k => k.Name == name);
            if (existing != null)
            {
                existing.Columns.Add(col);
                existing.ReferencedColumns.Add(refC);
            }
            else
            {
                output[i].ForeignKeys.Add(new ForeignKeyMeta(name, t, [col], refT, [refC]));
            }
        }
        return output;
    }
}

/// 다이어그램 카드 위치 (CoreGraphics CGRect 대신)
public readonly record struct DiagramRect(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;

    /// 두 사각형이 겹치는지 (변이 맞닿기만 하면 겹치지 않음)
    public bool Intersects(DiagramRect o) => X < o.MaxX && o.X < MaxX && Y < o.MaxY && o.Y < MaxY;
}

/// ER 다이어그램 배치
public static class DiagramLayout
{
    public const double CardWidth = 250;
    public const double HeaderHeight = 34;
    public const double RowHeight = 20;
    public const int MaxRows = 24;

    public static double CardHeight(TableMeta t) =>
        HeaderHeight + (Math.Min(t.Columns.Count, MaxRows) + (t.Columns.Count > MaxRows ? 1 : 0)) * RowHeight + 10;

    /// 테이블과, 외래 키(+ 추정 관계)로 바로 이어진 테이블 (참조하는 쪽 + 참조되는 쪽)
    public static List<TableMeta> Related(string name, IReadOnlyList<TableMeta> tables, IEnumerable<ForeignKeyMeta>? extra = null)
    {
        if (!tables.Any(t => t.Name == name)) return [];
        var all = tables.SelectMany(t => t.ForeignKeys).Concat(extra ?? []).ToList();
        var names = new List<string> { name };
        foreach (var fk in all) if (fk.Table == name && !names.Contains(fk.ReferencedTable)) names.Add(fk.ReferencedTable);
        foreach (var fk in all) if (fk.ReferencedTable == name && !names.Contains(fk.Table)) names.Add(fk.Table);
        return [.. names.Select(n => tables.FirstOrDefault(t => t.Name == n)).OfType<TableMeta>()];
    }

    /// 고른 테이블(관계 없어도 모두) + 관계로 이어진 테이블. 고른 순서(스키마 순서)가 먼저.
    public static List<TableMeta> WithRelated(IReadOnlySet<string> names, IReadOnlyList<TableMeta> tables, IEnumerable<ForeignKeyMeta>? extra = null)
    {
        var extraList = extra?.ToList() ?? [];
        var ordered = tables.Select(t => t.Name).Where(names.Contains).ToList();
        // Swift 는 처음 목록만 돈다 (덧붙인 테이블의 이웃은 보지 않음)
        foreach (var name in ordered.ToList())
            foreach (var t in Related(name, tables, extraList))
                if (!ordered.Contains(t.Name)) ordered.Add(t.Name);
        return [.. ordered.Select(n => tables.FirstOrDefault(t => t.Name == n)).OfType<TableMeta>()];
    }

    private static readonly HashSet<string> Generic = ["id", "seq", "no", "idx", "num", "code", "key", "uid", "sn"];

    private static string? PrimaryKey(TableMeta t)
    {
        var cols = t.Indexes.FirstOrDefault(i => i.IsPrimary)?.Columns
                   ?? [.. t.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name)];
        return cols.Count == 1 ? cols[0] : null;
    }

    private static string Stem(string s)
    {
        var x = s.ToLowerInvariant();
        foreach (var p in new[] { "tb_", "tbl_", "t_", "tm_", "th_" })
            if (x.StartsWith(p, StringComparison.Ordinal)) x = x[p.Length..];
        foreach (var suf in new[] { "_id", "_no", "_seq", "_cd", "_code", "id" })
        {
            if (x.EndsWith(suf, StringComparison.Ordinal) && x.Length > suf.Length)
            {
                x = x[..^suf.Length];
                break;
            }
        }
        return x;
    }

    /// 외래 키 제약이 없는 DB 를 위한 관계 추정 (DataGrip 의 가상 외래 키와 비슷)
    /// 규칙: 어떤 테이블의 컬럼 이름이 다른 테이블의 "단일 컬럼 기본 키" 이름과 같으면 그 테이블을 참조한다고 본다.
    ///  - 자기 자신의 기본 키 컬럼은 제외, 이미 진짜 외래 키가 있는 컬럼은 제외
    ///  - 같은 이름의 기본 키를 가진 테이블이 여러 개면, 이름이 컬럼과 가장 비슷한 하나만 (애매하면 건너뜀)
    ///  - 흔한 이름(id, seq, no …)은 너무 많이 이어지므로 제외
    public static List<ForeignKeyMeta> InferredRelations(IReadOnlyList<TableMeta> tables)
    {
        var byPK = new Dictionary<string, List<TableMeta>>();
        foreach (var t in tables)
        {
            if (PrimaryKey(t)?.ToLowerInvariant() is string k && !Generic.Contains(k))
            {
                if (!byPK.TryGetValue(k, out var list)) byPK[k] = list = [];
                list.Add(t);
            }
        }
        var output = new List<ForeignKeyMeta>();
        foreach (var t in tables)
        {
            var own = PrimaryKey(t)?.ToLowerInvariant();
            var real = t.ForeignKeys.SelectMany(f => f.Columns).Select(c => c.ToLowerInvariant()).ToHashSet();
            foreach (var c in t.Columns)
            {
                var name = c.Name.ToLowerInvariant();
                if (name == own || real.Contains(name) || !byPK.TryGetValue(name, out var candidates)) continue;
                var others = candidates.Where(o => o.Name != t.Name).ToList();
                if (others.Count == 0) continue;
                TableMeta? target;
                if (others.Count == 1)
                {
                    target = others[0];
                }
                else
                {
                    var s = Stem(c.Name);
                    var close = others.Where(o => Stem(o.Name) == s || Stem(o.Name).EndsWith(s, StringComparison.Ordinal)).ToList();
                    target = close.Count == 1 ? close[0] : null;
                }
                if (target is null || PrimaryKey(target) is not string refPK) continue;
                output.Add(new ForeignKeyMeta($"~{t.Name}.{c.Name}", t.Name, [c.Name], target.Name, [refPK]));
            }
        }
        return output;
    }

    /// 참조되는 테이블(부모)을 왼쪽, 참조하는 테이블(자식)을 오른쪽 열에 놓는다. 관계 없는 테이블은 격자로 아래에.
    public static Dictionary<string, DiagramRect> Frames(IReadOnlyList<TableMeta> tables, IEnumerable<ForeignKeyMeta>? extra = null,
                                                         double gap = 70, double cardWidth = CardWidth)
    {
        var names = tables.Select(t => t.Name).ToHashSet();
        var all = tables.SelectMany(t => t.ForeignKeys).Concat(extra ?? []).ToList();
        var parents = new Dictionary<string, List<string>>();
        foreach (var t in tables)
            parents[t.Name] = [.. all.Where(f => f.Table == t.Name).Select(f => f.ReferencedTable).Where(r => names.Contains(r) && r != t.Name)];
        var connected = tables.Where(t =>
                (parents.TryGetValue(t.Name, out var p) && p.Count > 0)
                || all.Any(f => f.ReferencedTable == t.Name && f.Table != t.Name && names.Contains(f.Table)))
            .Select(t => t.Name).ToHashSet();

        // 단계 = 1 + 부모 단계의 최대값 (순환은 횟수 제한으로 끊음)
        var level = new Dictionary<string, int>();
        foreach (var t in tables) if (connected.Contains(t.Name)) level[t.Name] = 0;
        for (int iter = 0; iter < tables.Count; iter++)
        {
            bool changed = false;
            foreach (var t in tables)
            {
                if (!connected.Contains(t.Name)) continue;
                var ls = (parents.GetValueOrDefault(t.Name) ?? []).Where(level.ContainsKey).Select(p => level[p]).ToList();
                int l = ls.Count > 0 ? ls.Max() + 1 : 0;
                if (l > level.GetValueOrDefault(t.Name) && l < tables.Count) { level[t.Name] = l; changed = true; }
            }
            if (!changed) break;
        }

        var result = new Dictionary<string, DiagramRect>();
        double bottomOfGraph = 0;
        int maxLevel = level.Count > 0 ? level.Values.Max() : -1;
        for (int l = 0; l <= maxLevel; l++)
        {
            double y = 0;
            foreach (var t in tables)
            {
                if (!level.TryGetValue(t.Name, out var tl) || tl != l) continue;
                var h = CardHeight(t);
                result[t.Name] = new DiagramRect(l * (cardWidth + gap * 1.6), y, cardWidth, h);
                y += h + gap;
            }
            bottomOfGraph = Math.Max(bottomOfGraph, y);
        }
        // 관계 없는 테이블: 격자
        var loose = tables.Where(t => !connected.Contains(t.Name)).ToList();
        if (loose.Count > 0)
        {
            int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(loose.Count)));
            double rowTop = bottomOfGraph;
            for (int index = 0; index < loose.Count; index += columns)
            {
                double rowHeight = 0;
                var row = loose.Skip(index).Take(columns).ToList();
                for (int c = 0; c < row.Count; c++)
                {
                    var h = CardHeight(row[c]);
                    result[row[c].Name] = new DiagramRect(c * (cardWidth + gap), rowTop, cardWidth, h);
                    rowHeight = Math.Max(rowHeight, h);
                }
                rowTop += rowHeight + gap;
            }
        }
        return result;
    }
}
