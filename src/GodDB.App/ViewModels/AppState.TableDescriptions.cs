namespace GodDB.App.ViewModels;

/// 테이블 설명 관리 창 요청 (사이드바 말풍선 아이콘 · DB 머리 우클릭 > 테이블 설명 (AI 참고)…) — Mac 1.4.3 #178
public sealed record TableDescriptionsRequest(Guid ProfileId, string Database)
{
    public Guid Id { get; } = Guid.NewGuid();
}

// 테이블 설명 (AI 참고) — Mac 1.4.3 TableDescriptionsView.swift · AssistantPanel.swift 의 AppState 쪽
public sealed partial class AppState
{
    private TableDescriptionLibrary? _tableDescriptions;

    /// table-descriptions.json 저장소 (테스트에서 임시 파일로 바꾼다)
    public TableDescriptionStore DescriptionStore { get; set; } = TableDescriptionStore.Shared;

    /// 접속 · DB 별 테이블 설명 (처음 쓸 때 읽는다)
    public TableDescriptionLibrary TableDescriptions
    {
        get => _tableDescriptions ??= DescriptionStore.Load() ?? new TableDescriptionLibrary();
        set
        {
            _tableDescriptions = value;
            NotifyTableDescriptions();
        }
    }

    /// 테이블 설명 창 요청 (값이 있으면 메인 창이 띄운다)
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private TableDescriptionsRequest? _tableDescriptionsRequest;

    /// 테이블 설명이 바뀌었을 때 (사이드바 아이콘 · 어시스턴트 칩)
    public event Action? TableDescriptionsChanged;

    private void NotifyTableDescriptions()
    {
        OnPropertyChanged(nameof(CurrentDescriptions));
        TableDescriptionsChanged?.Invoke();
    }

    public void SaveTableDescriptions()
    {
        DescriptionStore.Save(TableDescriptions);
        NotifyTableDescriptions();
    }

    public void OpenTableDescriptions()
    {
        if (SelectedProfileId is not { } id || !State.IsConnected) return;
        TableDescriptionsRequest = new TableDescriptionsRequest(id, CurrentDatabase);
    }

    /// 지금 접속 · DB 의 테이블 설명 (#178)
    public TableDescriptionDoc? CurrentDescriptions =>
        SelectedProfileId is { } id && State.IsConnected ? TableDescriptions.Doc(id, CurrentDatabase) : null;

    /// 테이블 설명의 title 을 빈 코멘트에 채운 테이블 목록 (AI 에 보낼 때)
    public IReadOnlyList<TableMeta> DescribedTables => CurrentDescriptions?.ApplyTitles(Tables) ?? Tables;

    /// 사이드바 말풍선 아이콘 도움말
    public string TableDescriptionsTip => CurrentDescriptions is { } d
        ? Tr("테이블 설명 (AI 참고) — 테이블 %@개", d.Tables.Count)
        : Tr("테이블 설명 (AI 참고) — 아직 없어요");
}
