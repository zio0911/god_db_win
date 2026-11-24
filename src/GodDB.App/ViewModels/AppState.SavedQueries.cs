using CommunityToolkit.Mvvm.ComponentModel;

namespace GodDB.App.ViewModels;

/// 저장 쿼리 창 하나 (같은 자리에서 목록 → 빈칸 입력으로 바꿔 띄운다). Mac SavedQuerySheet
public abstract record SavedQuerySheet
{
    /// 쿼리 저장 (Ctrl+Alt+S)
    public sealed record Save(string Sql) : SavedQuerySheet;
    /// 저장 쿼리 목록 (Ctrl+Shift+K)
    public sealed record List : SavedQuerySheet;
    /// 빈칸 입력
    public sealed record Fill(Guid QueryId) : SavedQuerySheet;
}

// 저장 쿼리 (Mac SavedQueriesView.swift 의 AppState 확장)
public sealed partial class AppState
{
    /// 저장 쿼리 창 (Ctrl+Alt+S 저장 · Ctrl+Shift+K 목록 · 빈칸 입력)
    [ObservableProperty] private SavedQuerySheet? _savedQuerySheet;
    /// 이 탭의 편집기 내용 전체를 평소 실행 경로(확인 창 포함)로 실행해 달라는 요청 (저장 쿼리 [실행])
    [ObservableProperty] private Guid? _runAllRequest;

    /// saved-queries.json (테스트는 임시 파일로 바꾼다)
    public SavedQueryStore SavedQueryFile { get; set; } = SavedQueryStore.Shared;

    private SavedQueryLibrary? _savedQueries;
    /// 저장 쿼리 목록 (처음 쓸 때 읽는다)
    public SavedQueryLibrary SavedQueries
    {
        get => _savedQueries ??= SavedQueryFile.Load();
        set => _savedQueries = value;
    }

    /// 보고 있는 쿼리 탭의 편집기에서 저장할 SQL (선택 영역 → 커서가 있는 문장). 쿼리 화면이 넣는다.
    /// (Mac: AppState.activeEditor?.sqlToRun())
    public Func<string?>? ActiveEditorSql { get; set; }

    /// 저장 쿼리를 쓸 수 있는지 (Redis 가 아닌 접속을 보고 있을 때)
    public bool CanOpenSavedQueries => SelectedProfile is { } p && p.Kind != DatabaseKind.Redis;

    /// Ctrl+Alt+S 가 가능한지 (SQL 쿼리 탭을 보고 있을 때)
    public bool CanSaveQuery => Mode == MainMode.Query && SelectedTab is { } t && Profile(t.ProfileId)?.Kind is { } k && k != DatabaseKind.Redis;

    /// 보고 있는 접속의 SQL 문법 (빈칸 · 값 변환)
    public ISqlDialect SavedQueryDialect
    {
        get
        {
            if (SelectedProfileId is { } id && Connections.TryGetValue(id, out var c) && c.Session?.Dialect is { } d) return d;
            return SelectedProfile is { } p ? Dialects.Of(p.Kind) : Dialect;
        }
    }

    /// 쿼리 저장 창: 선택 영역 → 커서가 있는 문장 → 편집기 전체
    public void OpenSaveQuery()
    {
        if (!CanSaveQuery || SelectedTab is not { } tab) return;
        var picked = ActiveEditorSql?.Invoke()?.Trim() ?? "";
        var sql = picked.Length == 0 ? tab.Text.Trim() : picked;
        if (sql.Length == 0)
        {
            Log(LogLevel.Warning, LogSource.App, Tr("저장할 쿼리가 없어요. 편집기에 쿼리를 쓰고 다시 저장하세요."));
            return;
        }
        SavedQuerySheet = new SavedQuerySheet.Save(sql);
    }

    public void OpenSavedQueries()
    {
        if (!CanOpenSavedQueries) return;
        SavedQueries = SavedQueryFile.Load();
        SavedQuerySheet = new SavedQuerySheet.List();
    }

    /// 저장 쿼리 목록을 바꾸고 바로 파일에 쓴다
    public void ChangeSavedQueries(Action<SavedQueryLibrary> change)
    {
        change(SavedQueries);
        SavedQueryFile.Save(SavedQueries);
    }

    /// 목록에서 실행 · 편집기에 넣기: 빈칸이 없으면 바로, 있으면 입력 창
    public void UseSavedQuery(Guid id, bool run)
    {
        if (SavedQueries[id] is not { } q) return;
        if (SqlParameters.Names(q.Sql, SavedQueryDialect.Kind).Count == 0)
        {
            SavedQuerySheet = null;
            ChangeSavedQueries(l => l.MarkUsed(id));
            PlaceSavedQuery(q.Sql, q.Name, run);
        }
        else
        {
            SavedQuerySheet = new SavedQuerySheet.Fill(id);
        }
    }

    /// 빈칸을 채워 넣는다 (입력값은 다음을 위해 기억)
    public void ApplySavedQuery(Guid id, IReadOnlyDictionary<string, SqlParameters.Input> inputs, bool run)
    {
        if (SavedQueries[id] is not { } q) return;
        var sql = SqlParameters.Fill(q.Sql, inputs, SavedQueryDialect);
        SavedQuerySheet = null;
        ChangeSavedQueries(l => l.MarkUsed(id, inputs.ToDictionary(kv => kv.Key, kv => kv.Value.Value)));
        PlaceSavedQuery(sql, q.Name, run);
    }

    /// 지금 탭이 비었으면 그 탭, 아니면 새 탭(제목 = 저장 쿼리 이름)에 넣는다. run 이면 평소 실행 경로로 실행.
    private void PlaceSavedQuery(string sql, string title, bool run)
    {
        if (SelectedProfileId is not { } profileId) return;
        Guid tabId;
        if (SelectedTab is { } t && t.ProfileId == profileId && !t.Running && t.Source == null && t.Text.Trim().Length == 0)
        {
            t.Text = sql;
            tabId = t.Id;
        }
        else
        {
            if (AddTab(sql, title, profileId) is not { } id) return;
            tabId = id;
        }
        if (run) RunAllRequest = tabId;
    }
}
