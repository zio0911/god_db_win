using CommunityToolkit.Mvvm.ComponentModel;

namespace GodDB.App.ViewModels;

/// ID 로 찾기 창을 여는 요청 (사이드바 우클릭이면 그 테이블이 미리 선택됨). Mac IDSearchRequest
public sealed record IdSearchRequest(Guid ProfileId, string Database, string? Table)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// ID 로 찾기로 연 탭의 요청 내용 (결과 아래 "없는 ID" 비교용). Mac IDSearchRun
public sealed record IdSearchRun(string Column, IReadOnlyList<string> Values, string Sql);

public sealed partial class QueryTab
{
    /// ID 로 찾기로 연 탭이면 요청한 값 (결과 아래 "없는 ID" 표시)
    [ObservableProperty] private IdSearchRun? _idSearch;
}

// ID 로 찾기 (Mac IDSearchView.swift 의 AppState 확장)
public sealed partial class AppState
{
    /// ID 로 찾기 (Ctrl+Alt+F)
    [ObservableProperty] private IdSearchRequest? _idSearchRequest;

    /// id-search.json (테스트는 임시 파일로 바꾼다)
    public IdSearchStore IdSearchFile { get; set; } = IdSearchStore.Shared;

    /// ID 로 찾기가 가능한지 (연결된 SQL 접속)
    public bool CanSearchIds => State.IsConnected && SelectedProfile is { } p && p.Kind != DatabaseKind.Redis;

    /// ID 로 찾기 창 열기 (메뉴 Ctrl+Alt+F · 사이드바 테이블 우클릭)
    public void OpenIdSearch(string? table = null)
    {
        if (!CanSearchIds || SelectedProfileId is not { } id) return;
        IdSearchRequest = new IdSearchRequest(id, CurrentDatabase, table);
    }

    /// 새 쿼리 탭에 `SELECT * … WHERE col IN (…)` 을 넣고 실행한다. 행 수 제한은 값 개수 이상으로 (최대 5,000).
    public async Task RunIdSearchAsync(Guid profileId, string table, string column, IReadOnlyList<string> values, bool numeric)
    {
        if (!Connections.TryGetValue(profileId, out var info) || info.Session?.Dialect is not { } d || values.Count == 0) return;
        var sql = IdSearch.Sql(table, column, values, numeric, d);
        if (AddTab(sql, IdSearch.TabTitle(table, column, values.Count), profileId) is not { } tabId) return;
        Update(tabId, t => t.IdSearch = new IdSearchRun(column, values, sql));
        var limit = IdSearch.RowLimit(RowLimit, values.Count);
        await RunAsync(sql, rowLimitOverride: limit);
    }
}
