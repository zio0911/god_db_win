namespace GodDB.App.ViewModels;

/// 쿼리 편집기 빨간 밑줄 설정 (Mac EditorLint.swift — UserDefaults 와 같은 키)
public static class EditorLint
{
    public const string EnabledKey = "GodDB.editor.lint";
    public const string ServerKey = "GodDB.editor.serverCheck";

    /// 쿼리 오류에 빨간 밑줄 (기본 켬)
    public static bool Enabled
    {
        get => Prefs.Get(EnabledKey, true);
        set => Prefs.Set(EnabledKey, value);
    }

    /// DB 에 검사 요청 (기본 켬)
    public static bool ServerEnabled
    {
        get => Prefs.Get(ServerKey, true);
        set => Prefs.Set(ServerKey, value);
    }
}

// 쿼리 편집기 빨간 밑줄 (Mac EditorLint.swift 의 AppState 확장)
public sealed partial class AppState
{
    /// 탭의 접속 종류 · DB 와, 그 DB 의 스키마를 알고 있으면 테이블 목록
    public (IReadOnlyList<TableMeta> Tables, DatabaseKind? Kind, string? Schema) LintContext(Guid tabId)
    {
        if (Tab(tabId) is not { } tab) return ([], null, null);
        var kind = Profile(tab.ProfileId)?.Kind;
        if (!Connections.TryGetValue(tab.ProfileId, out var info)) return ([], kind, null);
        var schema = tab.Database.Length == 0 ? info.CurrentDatabase : tab.Database;
        // 사이드바가 다른 DB 를 보고 있으면 컬럼 검사는 하지 않는다 (틀린 경고 방지)
        IReadOnlyList<TableMeta> tables = info.CurrentDatabase == schema ? info.Tables : [];
        return (tables, kind, schema);
    }

    /// 실행하지 않고 DB 에 검사만 맡긴다. 연결이 없거나 탭의 DB 가 세션의 DB 와 다르면 묻지 않는다 (null).
    public async Task<DbSession.ServerCheckOutcome?> ServerCheckAsync(string sql, Guid tabId)
    {
        if (!EditorLint.ServerEnabled || Tab(tabId) is not { } tab
            || !Connections.TryGetValue(tab.ProfileId, out var info) || info.Session is not { } s) return null;
        if (tab.Database.Length > 0 && s.Current != tab.Database) return null;
        try { return await Bg(() => s.ServerCheckAsync(sql)); }
        catch { return null; }
    }
}
