namespace GodDB.App.ViewModels;

/// 결과 하나에 대한 외래 키 따라가기 정보 (Mac: FKFollowUI.swift 의 FKContext)
/// 결과가 테이블 하나에서 온 조회이고, 그 테이블에 관계(진짜 외래 키 · 추정)가 있을 때만 만든다.
public sealed class FKContext
{
    public TableMeta Source { get; }
    public IReadOnlyList<string> ResultColumns { get; }
    /// 이 테이블이 참조하는 관계
    public IReadOnlyList<FKRelation> Outgoing { get; }
    /// 이 테이블(단일 기본 키)을 참조하는 관계
    public IReadOnlyList<FKRelation> Incoming { get; }
    /// 단일 컬럼 기본 키
    public string? PrimaryKey { get; }
    public IReadOnlyList<TableMeta> Tables { get; }
    public ISqlDialect Dialect { get; }
    /// 외래 키가 들어 있는 결과 컬럼 번호 → 관계
    public IReadOnlyDictionary<int, List<FKRelation>> ByColumn { get; }
    /// 새 탭(제목)에 SQL 을 넣고 평소 실행 경로로 실행
    private readonly Action<string, string> _open;

    public FKContext(TableMeta source, IReadOnlyList<string> resultColumns, IReadOnlyList<TableMeta> tables,
                     IReadOnlyList<ForeignKeyMeta> inferred, ISqlDialect dialect, Action<string, string> open)
    {
        Source = source;
        ResultColumns = resultColumns;
        Tables = tables;
        Dialect = dialect;
        _open = open;
        Outgoing = FKFollow.Outgoing(source, inferred);
        Incoming = FKFollow.Incoming(source, tables, inferred);
        PrimaryKey = FKFollow.SinglePrimaryKey(source);
        var map = new Dictionary<int, List<FKRelation>>();
        for (var i = 0; i < resultColumns.Count; i++)
        {
            var rs = FKFollow.RelationsForColumn(resultColumns[i], Outgoing);
            if (rs.Count > 0) map[i] = rs;
        }
        ByColumn = map;
    }

    public bool IsEmpty => Outgoing.Count == 0 && Incoming.Count == 0;

    /// 우클릭 메뉴에 보일 항목이 있는지 (참조 따라가기, 또는 기본 키 값이 있을 때 참조하는 행)
    public bool HasMenu(IReadOnlyList<string?> row) => Outgoing.Count > 0 || (Incoming.Count > 0 && PrimaryKeyValue(row) != null);

    /// 머리글 표시: 진짜 외래 키가 하나라도 있으면 false, 추정만 있으면 true, 관계 없으면 null
    public bool? InferredOnly(int column) => ByColumn.TryGetValue(column, out var rs) ? rs.All(r => r.Inferred) : null;

    public string? FollowSql(FKRelation r, IReadOnlyList<string?> row) =>
        FKFollow.FollowSql(r, FKFollow.Values(r, ResultColumns, row), Tables, Dialect);

    public string? PrimaryKeyValue(IReadOnlyList<string?> row) =>
        PrimaryKey is { } pk ? FKFollow.Value(pk, ResultColumns, row) : null;

    public string? ReferencingSql(FKRelation r, IReadOnlyList<string?> row) =>
        FKFollow.ReferencingSql(r, PrimaryKeyValue(row), Tables, Dialect);

    public void Follow(FKRelation r, IReadOnlyList<string?> row)
    {
        if (FollowSql(r, row) is { } sql) _open(sql, FKFollow.FollowTitle(r));
    }

    public void OpenReferencing(FKRelation r, IReadOnlyList<string?> row)
    {
        if (ReferencingSql(r, row) is { } sql) _open(sql, FKFollow.ReferencingTitle(r));
    }

    /// Ctrl+클릭: 그 칸이 들어 있는 관계 중 값이 있는 첫 관계를 따라간다 (NULL 이면 아무것도 안 함). 따라갔으면 true
    public bool Follow(int column, IReadOnlyList<string?> row)
    {
        if (!ByColumn.TryGetValue(column, out var rs) || rs.FirstOrDefault(r => FollowSql(r, row) != null) is not { } r) return false;
        Follow(r, row);
        return true;
    }

    // MARK: - 글자

    public static string InferredSuffix(FKRelation r) => r.Inferred ? " · " + Tr("추정") : "";

    /// `참조 따라가기 → ui_customers (customer_id)`
    public static string FollowLabel(FKRelation r) =>
        Tr("참조 따라가기 → %@ (%@)", r.ReferencedTable, string.Join(", ", r.Columns)) + InferredSuffix(r);

    /// `ui_orders (customer_id)`
    public static string ReferencingLabel(FKRelation r) => $"{r.Table} ({string.Join(", ", r.Columns)})" + InferredSuffix(r);
}

// 외래 키 따라가기 (Mac: FKFollowUI.swift 의 AppState 확장)
public sealed partial class AppState
{
    /// 이 탭의 결과에 대한 외래 키 따라가기 정보. 한 테이블 조회가 아니거나 관계가 없으면 null.
    /// (결과 편집과 달리 읽기전용 접속 · 기본 키 없는 결과에서도 된다)
    public FKContext? FkContext(QueryTab tab)
    {
        if (tab.Result is not { Columns.Count: > 0 } result || tab.IsPlan || result.Sql is not { } sql
            || !Connections.TryGetValue(tab.ProfileId, out var info) || FkDialect(tab.ProfileId, info) is not { } dialect
            || FKFollow.SourceTable(sql, info.Tables) is not { } source) return null;
        var profileId = tab.ProfileId;
        var context = new FKContext(source, [.. result.Columns.Select(c => c.Name)], info.Tables, info.InferredRelations, dialect,
                                    (q, title) => OpenFkQuery(q, title, profileId));
        return context.IsEmpty ? null : context;
    }

    /// 연결된 접속의 문법 (세션이 있으면 세션 것, 연결 상태만 있으면 접속 종류로 — 화면 테스트처럼 세션 없이 연결 상태만 둔 경우)
    private ISqlDialect? FkDialect(Guid profileId, ConnectionInfo info) =>
        info.Session?.Dialect ?? (info.State.IsConnected && Profile(profileId) is { } p ? ISqlDialect.Of(p.Kind) : null);

    /// 새 탭에 넣고 평소 실행 경로(감사 로그 · 히스토리)로 실행
    public void OpenFkQuery(string sql, string title, Guid profileId)
    {
        if (AddTab(sql, title, profileId) == null) return;
        Fire(() => RunAsync(sql));
    }
}
