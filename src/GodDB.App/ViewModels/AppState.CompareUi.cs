namespace GodDB.App.ViewModels;

// 스키마 비교 · 테이블 수정 · AI 어시스턴트 화면이 쓰는 AppState 멤버 (화면 포팅에서 더한 것)
public sealed partial class AppState
{
    /// 모델이 화면 밖(AI 어시스턴트 물리명 적용 등)에서 바뀌었을 때 (모델링 화면이 다시 그린다). 인자: 모델 id
    public event Action<Guid>? ModelEditedExternally;

    /// AI 어시스턴트 답의 물리명 JSON 을 지금 모델의 빈 칸에만 넣는다 (Mac: modeling.edit(id) { ModelAI.applyPhysicalNames }).
    /// 되돌리기 기록을 남기고 자동 저장한다. 넣은 개수를 돌려준다.
    public int ApplyAssistantPhysicalNames(string answer)
    {
        if (Modeling.SelectedId is not { } id) return 0;
        var n = 0;
        Modeling.Edit(id, m => n = ModelAiBridge.ApplyPhysicalNames(answer, m));
        Log(n > 0 ? LogLevel.Info : LogLevel.Warning, LogSource.Schema, ModelAI.ApplyResultMessage(n));
        ModelEditedExternally?.Invoke(id);
        return n;
    }

    /// AI 답의 SQL 을 지금 쿼리 탭(없으면 마지막 탭) 편집기 끝에 넣는다 (Mac AIChatTranscript 의 "SQL 편집기에 넣기")
    public bool InsertAiSql(string sql, string label)
    {
        if ((SelectedTab?.Id ?? Tabs.LastOrDefault()?.Id) is not { } tabId) return false;
        if (Mode != MainMode.Query) Mode = MainMode.Query;
        InsertIntoTab(tabId, sql, label);
        return true;
    }
}
