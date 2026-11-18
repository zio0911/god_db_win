using System.Globalization;
using System.Text.RegularExpressions;
using GodDB.Core.Dialects;
using GodDB.Core.Schema;
using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.Core.Modeling;

// Mac: Sources/GodDBCore/DataModel.swift
// Swift 의 값 타입(struct)을 C# 클래스로 옮겼다. 복사가 필요한 곳(되돌리기 스냅샷, 복제)은 Clone() 을 쓰고,
// 같음 비교(==)는 Swift Hashable 처럼 모든 필드를 값으로 비교한다.

/// 데이터 모델 (MySQL Workbench 의 모델처럼): 논리명으로 설계하고, 물리명을 붙여 DDL 로 뽑는다.
public sealed partial class DataModel : IEquatable<DataModel>
{
    public const string FileExtension = "goddbmodel";
    public const string FormatName = "goddb-model";

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = 1;
    public Guid Id { get; set; }
    public string Name { get; set; }
    /// DDL 을 뽑을 대상 DB
    public DatabaseKind Kind { get; set; }
    public List<ModelTable> Tables { get; set; }
    public List<ModelRelation> Relations { get; set; }
    public string Memo { get; set; }
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }

    public DataModel(Guid? id = null, string? name = null, DatabaseKind kind = DatabaseKind.Mysql,
                     IEnumerable<ModelTable>? tables = null, IEnumerable<ModelRelation>? relations = null, string memo = "")
    {
        Id = id ?? Guid.NewGuid();
        Name = name ?? Tr("새 모델");
        Kind = kind;
        Tables = tables?.ToList() ?? [];
        Relations = relations?.ToList() ?? [];
        Memo = memo;
        Created = DateTime.UtcNow;
        Updated = DateTime.UtcNow;
    }

    public ModelTable? Table(Guid id) => Tables.FirstOrDefault(t => t.Id == id);
    public int? TableIndex(Guid id)
    {
        int i = Tables.FindIndex(t => t.Id == id);
        return i < 0 ? null : i;
    }

    /// 깊은 복사 (Swift struct 의 값 복사)
    public DataModel Clone() => new(Id, Name, Kind, Tables.Select(t => t.Clone()), Relations.Select(r => r.Clone()), Memo)
    {
        Format = Format, Version = Version, Created = Created, Updated = Updated,
    };

    public bool Equals(DataModel? o) =>
        o is not null && Format == o.Format && Version == o.Version && Id == o.Id && Name == o.Name && Kind == o.Kind
        && Tables.SequenceEqual(o.Tables) && Relations.SequenceEqual(o.Relations) && Memo == o.Memo
        && Created == o.Created && Updated == o.Updated;

    public override bool Equals(object? obj) => Equals(obj as DataModel);
    public override int GetHashCode() => HashCode.Combine(Id, Name, Tables.Count, Relations.Count);
    public static bool operator ==(DataModel? a, DataModel? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(DataModel? a, DataModel? b) => !(a == b);
}

public sealed class ModelTable : IEquatable<ModelTable>
{
    public Guid Id { get; set; }
    public string LogicalName { get; set; }
    public string PhysicalName { get; set; }
    /// 설명 (논리명 외 추가 설명)
    public string Comment { get; set; }
    public List<ModelColumn> Columns { get; set; }
    public List<ModelIndex> Indexes { get; set; }
    /// 다이어그램 위치 (왼쪽 위)
    public double X { get; set; }
    public double Y { get; set; }
    /// 카드 머리 색 (null 이면 기본). ModelDiagram.CardColors 의 이름
    public string? Color { get; set; }

    public ModelTable(Guid? id = null, string logicalName = "", string physicalName = "", string comment = "",
                      IEnumerable<ModelColumn>? columns = null, IEnumerable<ModelIndex>? indexes = null,
                      double x = 40, double y = 40, string? color = null)
    {
        Id = id ?? Guid.NewGuid();
        LogicalName = logicalName;
        PhysicalName = physicalName;
        Comment = comment;
        Columns = columns?.ToList() ?? [];
        Indexes = indexes?.ToList() ?? [];
        X = x;
        Y = y;
        Color = color;
    }

    public List<ModelColumn> PrimaryKey => [.. Columns.Where(c => c.PrimaryKey)];

    /// 화면 제목: 논리명 → 물리명 → (이름 없음)
    public string DisplayName =>
        LogicalName.Length > 0 ? LogicalName : (PhysicalName.Length > 0 ? PhysicalName : Tr("(이름 없음)"));

    public ModelTable Clone() => new(Id, LogicalName, PhysicalName, Comment, Columns.Select(c => c.Clone()),
                                     Indexes.Select(i => i.Clone()), X, Y, Color);

    public bool Equals(ModelTable? o) =>
        o is not null && Id == o.Id && LogicalName == o.LogicalName && PhysicalName == o.PhysicalName && Comment == o.Comment
        && Columns.SequenceEqual(o.Columns) && Indexes.SequenceEqual(o.Indexes) && X.Equals(o.X) && Y.Equals(o.Y) && Color == o.Color;

    public override bool Equals(object? obj) => Equals(obj as ModelTable);
    public override int GetHashCode() => HashCode.Combine(Id, PhysicalName, Columns.Count);
}

public sealed class ModelColumn : IEquatable<ModelColumn>
{
    public Guid Id { get; set; }
    public string LogicalName { get; set; }
    public string PhysicalName { get; set; }
    public string DataType { get; set; }
    public bool PrimaryKey { get; set; }
    public bool NotNull { get; set; }
    public bool AutoIncrement { get; set; }
    public bool Unique { get; set; }
    /// SQL 식 그대로 (문자열이면 'A' 처럼 따옴표 포함). 비우면 DEFAULT 없음
    public string DefaultValue { get; set; }
    public string Comment { get; set; }

    public ModelColumn(Guid? id = null, string logicalName = "", string physicalName = "", string dataType = "varchar(100)",
                       bool primaryKey = false, bool notNull = false, bool autoIncrement = false, bool unique = false,
                       string defaultValue = "", string comment = "")
    {
        Id = id ?? Guid.NewGuid();
        LogicalName = logicalName;
        PhysicalName = physicalName;
        DataType = dataType;
        PrimaryKey = primaryKey;
        NotNull = notNull;
        AutoIncrement = autoIncrement;
        Unique = unique;
        DefaultValue = defaultValue;
        Comment = comment;
    }

    public string DisplayName =>
        LogicalName.Length > 0 ? LogicalName : (PhysicalName.Length > 0 ? PhysicalName : Tr("(이름 없음)"));

    public ModelColumn Clone() => new(Id, LogicalName, PhysicalName, DataType, PrimaryKey, NotNull, AutoIncrement, Unique,
                                      DefaultValue, Comment);

    public bool Equals(ModelColumn? o) =>
        o is not null && Id == o.Id && LogicalName == o.LogicalName && PhysicalName == o.PhysicalName && DataType == o.DataType
        && PrimaryKey == o.PrimaryKey && NotNull == o.NotNull && AutoIncrement == o.AutoIncrement && Unique == o.Unique
        && DefaultValue == o.DefaultValue && Comment == o.Comment;

    public override bool Equals(object? obj) => Equals(obj as ModelColumn);
    public override int GetHashCode() => HashCode.Combine(Id, PhysicalName, DataType);
}

public sealed class ModelIndex : IEquatable<ModelIndex>
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public bool Unique { get; set; }
    public List<Guid> Columns { get; set; }

    public ModelIndex(Guid? id = null, string name = "", bool unique = false, IEnumerable<Guid>? columns = null)
    {
        Id = id ?? Guid.NewGuid();
        Name = name;
        Unique = unique;
        Columns = columns?.ToList() ?? [];
    }

    public ModelIndex Clone() => new(Id, Name, Unique, Columns);

    public bool Equals(ModelIndex? o) =>
        o is not null && Id == o.Id && Name == o.Name && Unique == o.Unique && Columns.SequenceEqual(o.Columns);

    public override bool Equals(object? obj) => Equals(obj as ModelIndex);
    public override int GetHashCode() => HashCode.Combine(Id, Name, Columns.Count);
}

/// 관계 카디널리티 (Swift ModelRelation.Cardinality, 파일 값 "1:N" / "1:1")
public enum ModelCardinality { OneToMany, OneToOne }

/// FK ON DELETE / ON UPDATE (Swift ModelRelation.Action, 파일 값 "NO ACTION" …)
public enum ModelFkAction { NoAction, Restrict, Cascade, SetNull }

public static class ModelEnumExtensions
{
    public static string RawValue(this ModelCardinality c) => c == ModelCardinality.OneToOne ? "1:1" : "1:N";
    public static string Title(this ModelCardinality c) => c.RawValue();
    public static ModelCardinality? ParseCardinality(string raw) => raw switch
    {
        "1:N" => ModelCardinality.OneToMany,
        "1:1" => ModelCardinality.OneToOne,
        _ => null,
    };

    public static string RawValue(this ModelFkAction a) => a switch
    {
        ModelFkAction.Restrict => "RESTRICT",
        ModelFkAction.Cascade => "CASCADE",
        ModelFkAction.SetNull => "SET NULL",
        _ => "NO ACTION",
    };

    public static ModelFkAction? ParseFkAction(string raw) => raw switch
    {
        "NO ACTION" => ModelFkAction.NoAction,
        "RESTRICT" => ModelFkAction.Restrict,
        "CASCADE" => ModelFkAction.Cascade,
        "SET NULL" => ModelFkAction.SetNull,
        _ => null,
    };

    /// 파일의 kind 값 (Swift DatabaseKind rawValue)
    public static string RawValue(this DatabaseKind k) => k switch
    {
        DatabaseKind.Postgres => "postgres",
        DatabaseKind.Mssql => "mssql",
        DatabaseKind.Redis => "redis",
        _ => "mysql",
    };

    public static DatabaseKind? ParseKind(string raw) => raw switch
    {
        "mysql" => DatabaseKind.Mysql,
        "postgres" => DatabaseKind.Postgres,
        "mssql" => DatabaseKind.Mssql,
        "redis" => DatabaseKind.Redis,
        _ => null,
    };
}

/// 관계의 컬럼 연결: Child = 자식 테이블의 FK 컬럼, Parent = 부모 테이블의 키 컬럼
public sealed record ModelRelationPair(Guid Child, Guid Parent);

/// 관계: 부모(참조되는 쪽) 1 — 자식(참조하는 쪽) N (또는 1)
public sealed class ModelRelation : IEquatable<ModelRelation>
{
    public Guid Id { get; set; }
    /// FK 제약 이름 (비우면 fk_자식_부모)
    public string Name { get; set; }
    public Guid Parent { get; set; }
    public Guid Child { get; set; }
    public ModelCardinality Cardinality { get; set; }
    /// 식별 관계 (자식 PK 에 FK 가 포함) — 실선, 비식별은 점선
    public bool Identifying { get; set; }
    /// 자식 쪽이 없어도 되는지 (FK 컬럼 NULL 허용)
    public bool Optional { get; set; }
    public List<ModelRelationPair> Pairs { get; set; }
    public ModelFkAction OnDelete { get; set; }
    public ModelFkAction OnUpdate { get; set; }
    /// false 면 DDL 에 FK 제약을 만들지 않고 그림에만 (논리 관계)
    public bool CreateConstraint { get; set; }

    public ModelRelation(Guid parent, Guid child, Guid? id = null, string name = "",
                         ModelCardinality cardinality = ModelCardinality.OneToMany, bool identifying = false, bool optional = false,
                         IEnumerable<ModelRelationPair>? pairs = null, ModelFkAction onDelete = ModelFkAction.NoAction,
                         ModelFkAction onUpdate = ModelFkAction.NoAction, bool createConstraint = true)
    {
        Id = id ?? Guid.NewGuid();
        Name = name;
        Parent = parent;
        Child = child;
        Cardinality = cardinality;
        Identifying = identifying;
        Optional = optional;
        Pairs = pairs?.ToList() ?? [];
        OnDelete = onDelete;
        OnUpdate = onUpdate;
        CreateConstraint = createConstraint;
    }

    public ModelRelation Clone() => new(Parent, Child, Id, Name, Cardinality, Identifying, Optional, Pairs, OnDelete, OnUpdate,
                                        CreateConstraint);

    public bool Equals(ModelRelation? o) =>
        o is not null && Id == o.Id && Name == o.Name && Parent == o.Parent && Child == o.Child && Cardinality == o.Cardinality
        && Identifying == o.Identifying && Optional == o.Optional && Pairs.SequenceEqual(o.Pairs) && OnDelete == o.OnDelete
        && OnUpdate == o.OnUpdate && CreateConstraint == o.CreateConstraint;

    public override bool Equals(object? obj) => Equals(obj as ModelRelation);
    public override int GetHashCode() => HashCode.Combine(Id, Parent, Child);
}

// MARK: - 편집 동작

public sealed partial class DataModel
{
    /// 빈 자리에 새 테이블 (기본 키 컬럼 하나 포함). at = 왼쪽 위 위치 (없으면 4열 격자의 다음 칸)
    public Guid AddTable(string logicalName = "", (double X, double Y)? at = null)
    {
        int n = Tables.Count + 1;
        var pos = at ?? (40 + (double)((n - 1) % 4) * 300, 40 + (double)((n - 1) / 4) * 260);
        var t = new ModelTable(logicalName: logicalName.Length == 0 ? Tr("테이블%@", n) : logicalName, x: pos.X, y: pos.Y)
        {
            Columns = [new ModelColumn(logicalName: Tr("ID"), physicalName: "", dataType: "bigint", primaryKey: true, notNull: true,
                                       autoIncrement: true)],
        };
        Tables.Add(t);
        Touch();
        return t.Id;
    }

    public void RemoveTable(Guid id)
    {
        // 이 테이블을 부모로 하는 관계가 자식에 만든 FK 컬럼도 함께 지운다
        foreach (var r in Relations.Where(r => r.Parent == id).ToList()) RemoveRelation(r.Id, dropColumns: true);
        Relations.RemoveAll(r => r.Child == id || r.Parent == id);
        Tables.RemoveAll(t => t.Id == id);
        Touch();
    }

    /// 관계 추가: 부모 기본 키를 자식에 FK 컬럼으로 복사 (이미 같은 물리명/논리명 컬럼이 있으면 그 컬럼을 쓴다)
    public Guid? AddRelation(Guid parent, Guid child, ModelCardinality cardinality = ModelCardinality.OneToMany,
                             bool identifying = false)
    {
        if (Table(parent) is not { } p || TableIndex(child) is not int ci) return null;
        var pk = p.PrimaryKey;
        var keys = pk.Count == 0 ? p.Columns.Take(1).ToList() : pk;
        if (keys.Count == 0) return null;
        var pairs = new List<ModelRelationPair>();
        foreach (var k in keys)
        {
            var fkPhysical = FkColumnName(p, k);
            var fkLogical = k.LogicalName.Length == 0 ? ""
                : (pk.Count == 1 && IsGenericKey(k) ? $"{p.LogicalName} {k.LogicalName}" : k.LogicalName);
            var existing = Tables[ci].Columns.FirstOrDefault(c =>
                (fkPhysical.Length > 0 && c.PhysicalName.ToLowerInvariant() == fkPhysical.ToLowerInvariant())
                || (fkLogical.Length > 0 && c.LogicalName == fkLogical));
            if (existing != null)
            {
                if (identifying)
                {
                    existing.PrimaryKey = true;
                    existing.NotNull = true;
                }
                pairs.Add(new ModelRelationPair(existing.Id, k.Id));
                continue;
            }
            var col = new ModelColumn(logicalName: fkLogical, physicalName: fkPhysical, dataType: FkType(k.DataType),
                                      primaryKey: identifying, notNull: true, autoIncrement: false,
                                      unique: cardinality == ModelCardinality.OneToOne, comment: "");
            // 기본 키 컬럼들 뒤에 넣는다
            var cols = Tables[ci].Columns;
            int insertAt = identifying ? cols.FindLastIndex(c => c.PrimaryKey) + 1 : cols.Count;
            cols.Insert(insertAt, col);
            pairs.Add(new ModelRelationPair(col.Id, k.Id));
        }
        var r = new ModelRelation(parent, child, cardinality: cardinality, identifying: identifying, pairs: pairs);
        Relations.Add(r);
        Touch();
        return r.Id;
    }

    public void RemoveRelation(Guid id, bool dropColumns)
    {
        if (Relations.FirstOrDefault(x => x.Id == id) is not { } r) return;
        Relations.RemoveAll(x => x.Id == id);
        if (dropColumns && TableIndex(r.Child) is int ci)
        {
            // 다른 관계가 쓰지 않는 FK 컬럼만
            var used = Relations.Where(x => x.Child == r.Child).SelectMany(x => x.Pairs.Select(p => p.Child)).ToHashSet();
            var drop = r.Pairs.Select(p => p.Child).Where(c => !used.Contains(c)).ToHashSet();
            var t = Tables[ci];
            t.Columns.RemoveAll(c => drop.Contains(c.Id));
            foreach (var idx in t.Indexes) idx.Columns.RemoveAll(drop.Contains);
            t.Indexes.RemoveAll(i => i.Columns.Count == 0);
        }
        Touch();
    }

    /// 식별/비식별, 1:1/1:N 이 바뀌면 FK 컬럼 속성도 맞춘다
    public void UpdateRelation(ModelRelation r)
    {
        int i = Relations.FindIndex(x => x.Id == r.Id);
        if (i < 0) return;
        Relations[i] = r;
        if (TableIndex(r.Child) is int ci)
        {
            foreach (var p in r.Pairs)
            {
                if (Tables[ci].Columns.FirstOrDefault(c => c.Id == p.Child) is not { } col) continue;
                col.PrimaryKey = r.Identifying;
                col.NotNull = r.Identifying || !r.Optional;
                if (r.Pairs.Count == 1) col.Unique = r.Cardinality == ModelCardinality.OneToOne && !r.Identifying;
            }
        }
        Touch();
    }

    /// 컬럼 삭제 (Mac: ModelTableEditor 의 컬럼 삭제 버튼): 인덱스와 관계 연결에서도 빼고, 연결이 없어진 관계는 지운다
    public void RemoveColumn(int tableIndex, Guid columnId)
    {
        var t = Tables[tableIndex];
        t.Columns.RemoveAll(c => c.Id == columnId);
        foreach (var idx in t.Indexes) idx.Columns.RemoveAll(c => c == columnId);
        foreach (var r in Relations) r.Pairs.RemoveAll(p => p.Child == columnId || p.Parent == columnId);
        Relations.RemoveAll(r => r.Pairs.Count == 0);
    }

    /// 컬럼 순서 바꾸기 (Mac: ModelTableEditor.move) — 위(-1)/아래(+1)
    public void MoveColumn(int tableIndex, int index, int by)
    {
        var cols = Tables[tableIndex].Columns;
        int t = index + by;
        if (t < 0 || t >= cols.Count || index < 0 || index >= cols.Count) return;
        (cols[index], cols[t]) = (cols[t], cols[index]);
    }

    /// 관계로 생긴 FK 컬럼인지 (편집기의 링크 표시)
    public bool IsForeignKeyColumn(Guid tableId, Guid columnId) =>
        Relations.Any(r => r.Child == tableId && r.Pairs.Any(p => p.Child == columnId));

    /// 테이블에 이어진 관계 (편집기의 "관계 %@" 탭)
    public List<ModelRelation> RelationsOf(Guid tableId) =>
        [.. Relations.Where(r => r.Child == tableId || r.Parent == tableId)];

    public void Touch() => Updated = DateTime.UtcNow;

    internal static bool IsGenericKey(ModelColumn c) =>
        new[] { "id", "seq", "no", "sn", "idx" }.Contains(c.PhysicalName.ToLowerInvariant())
        || new[] { "ID", "id", "번호", "일련번호", "순번" }.Contains(c.LogicalName);

    /// 부모 키 → 자식 FK 컬럼 물리명: id 처럼 흔한 이름이면 부모테이블_id
    internal static string FkColumnName(ModelTable parent, ModelColumn key)
    {
        if (key.PhysicalName.Length == 0) return "";
        if (IsGenericKey(key) && parent.PhysicalName.Length > 0)
        {
            var b = parent.PhysicalName.ToLowerInvariant();
            // Swift 는 tb_ / tbl_ / t_ 를 차례로 확인하며 맞으면 떼어낸다 (for … where)
            foreach (var p in new[] { "tb_", "tbl_", "t_" })
                if (b.StartsWith(p, StringComparison.Ordinal)) b = b[p.Length..];
            return $"{b}_{key.PhysicalName}";
        }
        return key.PhysicalName;
    }

    /// 자동 증가 타입은 FK 쪽에선 일반 타입으로 (serial → integer 등)
    internal static string FkType(string t) => t.ToLowerInvariant() switch
    {
        "serial" => "integer",
        "bigserial" => "bigint",
        "smallserial" => "smallint",
        _ => t,
    };
}

// MARK: - 검사

public enum ModelIssueLevel { Error, Warning }

public sealed record ModelIssue(ModelIssueLevel Level, string Message, Guid? Table)
{
    public string Id => $"{(Level == ModelIssueLevel.Error ? "error" : "warning")}-{Message}";
}

public sealed partial class DataModel
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$]*$")]
    private static partial Regex IdentRegex();

    /// Swift trimmingCharacters(in: .whitespaces) — 줄바꿈은 남긴다
    internal static string TrimSpaces(string s)
    {
        static bool Ws(char c) => c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
        int a = 0, b = s.Length;
        while (a < b && Ws(s[a])) a++;
        while (b > a && Ws(s[b - 1])) b--;
        return s[a..b];
    }

    /// DDL 을 만들기 전 검사: 오류(물리명 없음·중복)는 DDL 을 막고, 경고는 알려만 준다.
    public List<ModelIssue> Issues()
    {
        var output = new List<ModelIssue>();
        var tableNames = new HashSet<string>();
        static bool Valid(string s) => IdentRegex().IsMatch(s);
        foreach (var t in Tables)
        {
            var name = TrimSpaces(t.PhysicalName);
            if (name.Length == 0)
            {
                output.Add(new(ModelIssueLevel.Error, Tr("%@ 테이블에 물리명이 없어요.", t.DisplayName), t.Id));
            }
            else
            {
                if (!tableNames.Add(name.ToLowerInvariant()))
                    output.Add(new(ModelIssueLevel.Error, Tr("테이블 물리명 %@ 이(가) 중복돼요.", name), t.Id));
                if (!Valid(name))
                    output.Add(new(ModelIssueLevel.Warning, Tr("%@ 테이블 물리명에 영문·숫자·_ 외 글자가 있어요.", name), t.Id));
            }
            if (t.Columns.Count == 0)
                output.Add(new(ModelIssueLevel.Error, Tr("%@ 테이블에 컬럼이 없어요.", t.DisplayName), t.Id));
            if (t.PrimaryKey.Count == 0 && t.Columns.Count > 0)
                output.Add(new(ModelIssueLevel.Warning, Tr("%@ 테이블에 기본 키가 없어요.", t.DisplayName), t.Id));
            var colNames = new HashSet<string>();
            foreach (var c in t.Columns)
            {
                var cn = TrimSpaces(c.PhysicalName);
                if (cn.Length == 0)
                {
                    output.Add(new(ModelIssueLevel.Error, Tr("%@.%@ 컬럼에 물리명이 없어요.", t.DisplayName, c.DisplayName), t.Id));
                    continue;
                }
                if (!colNames.Add(cn.ToLowerInvariant()))
                    output.Add(new(ModelIssueLevel.Error, Tr("%@ 테이블의 컬럼 %@ 이(가) 중복돼요.", t.DisplayName, cn), t.Id));
                if (!Valid(cn))
                    output.Add(new(ModelIssueLevel.Warning, Tr("%@.%@ 물리명에 영문·숫자·_ 외 글자가 있어요.", t.DisplayName, cn), t.Id));
                if (TrimSpaces(c.DataType).Length == 0)
                    output.Add(new(ModelIssueLevel.Error, Tr("%@.%@ 컬럼의 타입이 비어 있어요.", t.DisplayName, cn), t.Id));
            }
        }
        return output;
    }
}

// MARK: - 물리 변환 / DDL

public sealed partial class DataModel
{
    /// DB 코멘트: 논리명(물리명과 다를 때) + 줄바꿈 + 설명. 리버스 때 첫 줄이 논리명이 된다.
    public static string CommentText(string logical, string physical, string comment)
    {
        var parts = new List<string>();
        var l = TrimSpaces(logical);
        if (l.Length > 0 && l != physical) parts.Add(l);
        var c = comment.Trim();
        if (c.Length > 0) parts.Add(c);
        return string.Join("\n", parts);
    }

    /// DB 코멘트 → (논리명, 설명)
    public static (string Logical, string Comment) SplitComment(string comment)
    {
        var lines = comment.Split('\n', 2);
        var first = TrimSpaces(lines[0]);
        var rest = lines.Length > 1 ? lines[1].Trim() : "";
        return (first, rest);
    }

    /// 자동 증가를 DB 별 Extra 로
    internal static string Extra(ModelColumn c, DatabaseKind kind)
    {
        if (!c.AutoIncrement) return "";
        return kind switch
        {
            DatabaseKind.Mysql or DatabaseKind.Redis => "auto_increment",
            _ => "identity",
        };
    }

    /// 모델 테이블 → TableMeta (DDL 렌더링용)
    public TableMeta ToTableMeta(ModelTable t)
    {
        var cols = t.Columns.Select((c, i) => new ColumnMeta(
            name: c.PhysicalName, columnType: c.DataType, nullable: !(c.NotNull || c.PrimaryKey),
            defaultValue: c.DefaultValue.Length == 0 ? null : c.DefaultValue,
            extra: Extra(c, Kind), columnKey: c.PrimaryKey ? "PRI" : "",
            comment: CommentText(c.LogicalName, c.PhysicalName, c.Comment),
            position: i + 1)).ToList();
        var indexes = new List<IndexMeta>();
        var pk = t.PrimaryKey.Select(c => c.PhysicalName).ToList();
        if (pk.Count > 0)
        {
            var pkName = Kind == DatabaseKind.Mysql ? "PRIMARY" : $"pk_{t.PhysicalName}";
            indexes.Add(new IndexMeta(pkName, unique: true, columns: pk, primary: true));
        }
        var byId = new Dictionary<Guid, string>();
        foreach (var c in t.Columns) byId.TryAdd(c.Id, c.PhysicalName);
        // 단일 컬럼 UNIQUE (PK 가 아닌 것)
        foreach (var c in t.Columns.Where(c => c.Unique && !c.PrimaryKey))
            indexes.Add(new IndexMeta($"uk_{t.PhysicalName}_{c.PhysicalName}", unique: true, columns: [c.PhysicalName]));
        foreach (var i in t.Indexes)
        {
            var ic = i.Columns.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            if (ic.Count == 0) continue;
            indexes.Add(new IndexMeta(i.Name.Length == 0 ? $"idx_{t.PhysicalName}_{string.Join("_", ic)}" : i.Name,
                                      unique: i.Unique, columns: ic));
        }
        return new TableMeta(t.PhysicalName, comment: CommentText(t.LogicalName, t.PhysicalName, t.Comment),
                             columns: cols, indexes: indexes);
    }

    /// 물리 모델 전체 (FK 포함) — DB 와 비교할 때
    public List<TableMeta> PhysicalTables() =>
    [
        .. Tables.Select(t => ToTableMeta(t) with
        {
            ForeignKeys = [.. Relations.Where(r => r.Child == t.Id && r.CreateConstraint).Select(ForeignKey).OfType<ForeignKeyMeta>()],
        }),
    ];

    internal ForeignKeyMeta? ForeignKey(ModelRelation r)
    {
        if (Table(r.Child) is not { } c || Table(r.Parent) is not { } p) return null;
        var childCols = r.Pairs.Select(pr => c.Columns.FirstOrDefault(x => x.Id == pr.Child)?.PhysicalName).OfType<string>().ToList();
        var parentCols = r.Pairs.Select(pr => p.Columns.FirstOrDefault(x => x.Id == pr.Parent)?.PhysicalName).OfType<string>().ToList();
        if (childCols.Count == 0 || childCols.Count != parentCols.Count) return null;
        return new ForeignKeyMeta(r.Name.Length == 0 ? $"fk_{c.PhysicalName}_{p.PhysicalName}" : r.Name,
                                  c.PhysicalName, childCols, p.PhysicalName, parentCols);
    }

    /// Forward Engineering: CREATE TABLE 전부 + FK 는 마지막에 ALTER TABLE 로 (순서·순환 참조 걱정 없음)
    /// <exception cref="ModelException">검사 오류가 있으면</exception>
    public string Ddl(DatabaseKind? kind = null, bool dropFirst = false)
    {
        var errors = Issues().Where(i => i.Level == ModelIssueLevel.Error).ToList();
        if (errors.Count > 0) throw ModelException.Invalid(errors[0].Message, errors.Count);
        var model = Clone();
        if (kind is { } target) model.Kind = target;
        var dialect = ISqlDialect.Of(model.Kind);
        var output = new List<string>
        {
            "-- " + Tr("GodDB 모델: %@", model.Name),
            "-- " + Tr("대상: %@ · 테이블 %@개 · 관계 %@개", model.Kind.Title(), model.Tables.Count, model.Relations.Count),
            "",
        };
        var fks = model.Relations.Where(r => r.CreateConstraint).Select(model.ForeignKey).OfType<ForeignKeyMeta>().ToList();
        if (dropFirst)
        {
            foreach (var fk in Enumerable.Reverse(fks)) output.Add(DropFkSql(fk, dialect));
            foreach (var t in Enumerable.Reverse(model.Tables)) output.Add(dialect.DropTableSql(t.PhysicalName));
            output.Add("");
        }
        foreach (var t in model.Tables)
        {
            output.Add(dialect.CreateTableSql(model.ToTableMeta(t)));
            output.Add("");
        }
        var actions = new Dictionary<string, ModelRelation>();
        foreach (var r in model.Relations)
            if (model.ForeignKey(r) is { } fk) actions.TryAdd(fk.Id, r);
        foreach (var fk in fks)
        {
            var s = $"ALTER TABLE {dialect.QuoteTable(fk.Table)} ADD CONSTRAINT {dialect.QuoteIdent(fk.Name)} FOREIGN KEY ("
                    + string.Join(", ", fk.Columns.Select(dialect.QuoteIdent)) + $") REFERENCES {dialect.QuoteTable(fk.ReferencedTable)} ("
                    + string.Join(", ", fk.ReferencedColumns.Select(dialect.QuoteIdent)) + ")";
            if (actions.TryGetValue(fk.Id, out var r))
            {
                // SQL Server 는 RESTRICT 가 없다 (NO ACTION 과 같음)
                if (r.OnDelete != ModelFkAction.NoAction && !(model.Kind == DatabaseKind.Mssql && r.OnDelete == ModelFkAction.Restrict))
                    s += $" ON DELETE {r.OnDelete.RawValue()}";
                if (r.OnUpdate != ModelFkAction.NoAction && !(model.Kind == DatabaseKind.Mssql && r.OnUpdate == ModelFkAction.Restrict))
                    s += $" ON UPDATE {r.OnUpdate.RawValue()}";
            }
            output.Add(s + ";");
        }
        return string.Join("\n", output).Trim() + "\n";
    }

    internal static string DropFkSql(ForeignKeyMeta fk, ISqlDialect dialect) => dialect.Kind switch
    {
        DatabaseKind.Mysql or DatabaseKind.Redis =>
            $"ALTER TABLE {dialect.QuoteTable(fk.Table)} DROP FOREIGN KEY {dialect.QuoteIdent(fk.Name)};",
        _ => $"ALTER TABLE {dialect.QuoteTable(fk.Table)} DROP CONSTRAINT {dialect.QuoteIdent(fk.Name)};",
    };
}

/// 모델 오류 (Swift ModelError). Message 는 Swift errorDescription 과 같다.
public sealed class ModelException : Exception
{
    public enum ErrorKind { Invalid, BadFile }

    public ErrorKind Kind { get; }
    /// Invalid: 첫 오류 문장
    public string? Detail { get; }
    /// Invalid: 오류 개수
    public int Count { get; }

    private ModelException(ErrorKind kind, string message, string? detail, int count) : base(message)
    {
        Kind = kind;
        Detail = detail;
        Count = count;
    }

    public static ModelException Invalid(string message, int count) =>
        new(ErrorKind.Invalid, count > 1 ? Tr("%@ (외 %@건)", message, count - 1) : message, message, count);

    public static ModelException BadFile() => new(ErrorKind.BadFile, Tr("GodDB 모델 파일이 아니에요."), null, 0);
}

// MARK: - 리버스 엔지니어링

public sealed partial class DataModel
{
    /// DB 테이블 → 모델. 코멘트 첫 줄은 논리명, 나머지는 설명. FK(+원하면 추정 관계)는 관계로.
    public static DataModel Reverse(IReadOnlyList<TableMeta> tables, string name, DatabaseKind kind,
                                    IReadOnlyList<ForeignKeyMeta>? inferred = null)
    {
        inferred ??= [];
        var model = new DataModel(name: name, kind: kind == DatabaseKind.Redis ? DatabaseKind.Mysql : kind);
        var frames = DiagramLayout.Frames(tables, inferred, cardWidth: 260);
        var ids = new Dictionary<string, (Guid Table, Dictionary<string, Guid> Columns)>();
        foreach (var t in tables)
        {
            var pk = (t.Indexes.FirstOrDefault(i => i.IsPrimary)?.Columns ?? t.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList())
                .Select(c => c.ToLowerInvariant()).ToHashSet();
            var tc = SplitComment(t.Comment);
            var cols = new List<ModelColumn>();
            var colIds = new Dictionary<string, Guid>();
            foreach (var c in t.Columns.OrderBy(c => c.Position))
            {
                var cc = SplitComment(c.Comment);
                var extra = c.Extra.ToLowerInvariant();
                bool uniqueSingle = t.Indexes.Any(i => i.Unique && !i.IsPrimary && i.Columns.Count == 1
                                                       && i.Columns[0].ToLowerInvariant() == c.Name.ToLowerInvariant());
                var col = new ModelColumn(logicalName: cc.Logical, physicalName: c.Name, dataType: c.ColumnType,
                                          primaryKey: pk.Contains(c.Name.ToLowerInvariant()), notNull: !c.Nullable,
                                          autoIncrement: extra.Contains("auto_increment") || extra.Contains("identity"),
                                          unique: uniqueSingle,
                                          defaultValue: c.DefaultValue is { } d ? ReverseDefault(d, kind) : "",
                                          comment: cc.Comment);
                colIds[c.Name.ToLowerInvariant()] = col.Id;
                cols.Add(col);
            }
            // 단일 UNIQUE 와 PK 를 뺀 나머지 인덱스
            var indexes = t.Indexes.Where(i => !i.IsPrimary && !(i.Unique && i.Columns.Count == 1))
                .Select(i => new ModelIndex(name: i.Name, unique: i.Unique,
                                            columns: i.Columns.Where(c => colIds.ContainsKey(c.ToLowerInvariant()))
                                                .Select(c => colIds[c.ToLowerInvariant()])));
            var f = frames.TryGetValue(t.Name, out var fr) ? fr : default;
            var mt = new ModelTable(logicalName: tc.Logical, physicalName: t.Name, comment: tc.Comment, columns: cols,
                                    indexes: indexes, x: f.MinX + 40, y: f.MinY + 40);
            ids[t.Name] = (mt.Id, colIds);
            model.Tables.Add(mt);
        }

        ModelRelation? Relation(ForeignKeyMeta fk, bool constraint)
        {
            if (!ids.TryGetValue(fk.Table, out var c) || !ids.TryGetValue(fk.ReferencedTable, out var p)) return null;
            var pairs = fk.Columns.Zip(fk.ReferencedColumns)
                .Select(z => c.Columns.TryGetValue(z.First.ToLowerInvariant(), out var a) && p.Columns.TryGetValue(z.Second.ToLowerInvariant(), out var b)
                    ? new ModelRelationPair(a, b) : null)
                .OfType<ModelRelationPair>().ToList();
            if (pairs.Count == 0 || model.Table(c.Table) is not { } child) return null;
            var fkCols = pairs.Select(pr => child.Columns.FirstOrDefault(x => x.Id == pr.Child)).OfType<ModelColumn>().ToList();
            bool identifying = fkCols.Count > 0 && fkCols.All(x => x.PrimaryKey);
            bool oneToOne = fkCols.Count == 1 && (fkCols[0].Unique || (identifying && child.PrimaryKey.Count == 1));
            return new ModelRelation(p.Table, c.Table, name: constraint ? fk.Name : "",
                                     cardinality: oneToOne ? ModelCardinality.OneToOne : ModelCardinality.OneToMany,
                                     identifying: identifying, optional: fkCols.Any(x => !x.NotNull), pairs: pairs,
                                     createConstraint: constraint);
        }

        model.Relations =
        [
            .. tables.SelectMany(t => t.ForeignKeys).Select(fk => Relation(fk, true)).OfType<ModelRelation>(),
            .. inferred.Select(fk => Relation(fk, false)).OfType<ModelRelation>(),
        ];
        return model;
    }

    /// 리버스 엔지니어링 창의 "모델 만들기" (Mac: ModelReverseSheet.create):
    /// 고른 테이블만 가져오고, inferred 면 컬럼 이름으로 추정한 관계 중 고른 테이블끼리인 것을 논리 관계로 넣는다.
    public static DataModel ReverseSelected(IReadOnlyList<TableMeta> allTables, IReadOnlySet<string> picked, string name,
                                            string currentDatabase, DatabaseKind kind, bool inferred)
    {
        var tables = allTables.Where(t => picked.Contains(t.Name)).ToList();
        var names = tables.Select(t => t.Name).ToHashSet();
        var guesses = inferred
            ? DiagramLayout.InferredRelations(allTables).Where(f => names.Contains(f.Table) && names.Contains(f.ReferencedTable)).ToList()
            : [];
        return Reverse(tables, name.Length == 0 ? currentDatabase : name, kind, guesses);
    }

    /// MySQL 은 기본값을 따옴표 없이 돌려주므로 모델(SQL 식) 형태로 감싼다
    internal static string ReverseDefault(string v, DatabaseKind kind)
    {
        if (kind != DatabaseKind.Mysql && kind != DatabaseKind.Redis) return v;
        var upper = v.ToUpperInvariant();
        if (upper == "NULL" || upper.StartsWith("CURRENT_TIMESTAMP", StringComparison.Ordinal) || upper.StartsWith("NOW(", StringComparison.Ordinal)
            || IsSwiftDouble(v) || (v.StartsWith('(') && v.EndsWith(')')) || v.StartsWith('\'')) return v;
        return SqlText.QuoteString(v);
    }

    /// Swift Double(String) 이 숫자로 읽는지 (앞뒤 공백 불가, inf/nan 허용)
    private static bool IsSwiftDouble(string v)
    {
        if (v.Length == 0 || char.IsWhiteSpace(v[0]) || char.IsWhiteSpace(v[^1])) return false;
        var s = v.TrimStart('+', '-').ToLowerInvariant();
        if (s is "inf" or "infinity" or "nan") return v.Length - s.Length <= 1;
        return double.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                               CultureInfo.InvariantCulture, out _);
    }
}

// MARK: - DB 와 비교 (Synchronize)

public sealed partial class DataModel
{
    /// 모델(원본)과 DB(대상)를 비교해 DB 를 모델에 맞추는 SQL. 기존 스키마 비교 엔진을 그대로 쓴다.
    /// <exception cref="ModelException">검사 오류가 있으면</exception>
    public SchemaDiffReport Compare(IReadOnlyList<TableMeta> db, ISqlDialect? dialect = null)
    {
        var errors = Issues().Where(i => i.Level == ModelIssueLevel.Error).ToList();
        if (errors.Count > 0) throw ModelException.Invalid(errors[0].Message, errors.Count);
        return SchemaDiff.Compare(PhysicalTables(), db, dialect: dialect ?? ISqlDialect.Of(Kind));
    }

    /// 타입 칸 메뉴에 보일 자주 쓰는 타입
    public static IReadOnlyList<string> CommonTypes(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Postgres =>
        [
            "bigint", "integer", "smallint", "numeric(12,2)", "varchar(50)", "varchar(100)", "varchar(255)", "char(1)",
            "text", "jsonb", "boolean", "date", "timestamp", "timestamptz", "uuid",
        ],
        DatabaseKind.Mssql =>
        [
            "bigint", "int", "smallint", "bit", "decimal(12,2)", "varchar(100)", "nvarchar(50)", "nvarchar(100)",
            "nvarchar(255)", "nvarchar(max)", "char(1)", "date", "datetime2(3)", "uniqueidentifier",
        ],
        _ =>
        [
            "bigint", "int", "smallint", "tinyint(1)", "decimal(12,2)", "varchar(50)", "varchar(100)", "varchar(255)",
            "char(1)", "text", "longtext", "json", "date", "datetime", "datetime(3)", "timestamp",
        ],
    };

    /// 모델 도구 막대의 대상 DB 고르기 (Redis 제외)
    public static IReadOnlyList<DatabaseKind> TargetKinds { get; } = [DatabaseKind.Mysql, DatabaseKind.Postgres, DatabaseKind.Mssql];
}
