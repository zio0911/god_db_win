using CommunityToolkit.Mvvm.ComponentModel;

namespace GodDB.App.ViewModels;

// 다른 화면(Redis · 다이어그램 · AI 어시스턴트) 포팅에서 채울 상태 클래스들.
// AppState 가 쓰는 멤버만 먼저 둔다. 채우는 쪽은 이 파일의 클래스를 자기 파일로 옮겨 넓히면 된다
// (이름 · 이미 있는 멤버의 뜻은 바꾸지 말 것).

/// Redis 접속 하나의 화면 상태 (키 목록, 선택한 키, 콘솔, Pub/Sub, Streams, 서버 정보) — Redis 화면 포팅에서 채운다.
public partial class RedisWorkspace : ObservableObject
{
    public Guid ProfileId { get; }
    public RedisSession Session { get; }
    public AppState? App { get; }

    public RedisWorkspace(Guid profileId, RedisSession session, AppState? app)
    {
        ProfileId = profileId;
        Session = session;
        App = app;
    }

    public string Name => Session.Profile.Name;
    public bool ReadOnly => Session.Profile.ReadOnly;

    /// Cluster / Sentinel 표시 ("Cluster · 마스터 3개")
    [ObservableProperty] private string _topologyText = "";
    /// Cluster 마스터 노드들 (서버 정보에서 노드 고르기)
    [ObservableProperty] private IReadOnlyList<string> _nodes = [];

    public virtual Task LoadTopologyAsync()
    {
        TopologyText = Session.TopologyText;
        Nodes = Session.MasterNodes;
        return Task.CompletedTask;
    }

    /// 사이드바 DB 목록 (Redis 화면 포팅에서 채운다)
    public virtual Task LoadDatabasesAsync() => Task.CompletedTask;

    /// 키 목록 다시 읽기 (Redis 화면 포팅에서 채운다)
    public virtual Task ReloadKeysAsync() => Task.CompletedTask;

    /// 자동 새로고침 · Pub/Sub 구독 등을 멈춘다 (Redis 화면 포팅에서 채운다)
    public virtual Task CloseAsync() => Task.CompletedTask;
}

/// 다이어그램 창 하나의 내용 (열 때의 스키마 스냅샷)
public sealed record DiagramDoc
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Title { get; init; }
    public required IReadOnlyList<TableMeta> Tables { get; init; }
    /// 중심 테이블 (테이블에서 열었을 때)
    public string? Focus { get; init; }
    /// 보라색으로 강조할 테이블 (사이드바에서 고른 테이블 / 중심 테이블)
    public IReadOnlySet<string> Highlighted { get; init; } = new HashSet<string>();
    /// 컬럼 이름으로 추정한 관계 (외래 키 제약이 없는 DB 용)
    public IReadOnlyList<ForeignKeyMeta> Inferred { get; init; } = [];
    /// DB 종류 (AI 분석 SQL 문법용)
    public DatabaseKind Kind { get; init; } = DatabaseKind.Mysql;
    /// AI 대화 기록을 구분하는 값 (접속 · DB · 테이블)
    public string ChatScope { get; init; } = "";
}

/// 메인 창 오른쪽 AI 어시스턴트 상태: 범위(접속·DB / 모델)별 대화, 테이블 통계 캐시
public sealed partial class AssistantState : ObservableObject
{
    public const string VisibleKey = "GodDB.assistant.visible";

    [ObservableProperty] private bool _visible = Prefs.Get(VisibleKey, false);
    [ObservableProperty] private double _width = 400;
    [ObservableProperty] private bool _includeSql = true;

    partial void OnVisibleChanged(bool value) => Prefs.Set(VisibleKey, value);

    private readonly Dictionary<string, AiChat> _chats = [];
    /// "접속ID|DB" → 테이블 통계
    public Dictionary<string, Dictionary<string, TableStats>> Stats { get; } = [];

    public AiChat Chat(string scope)
    {
        if (_chats.TryGetValue(scope, out var c)) return c;
        c = new AiChat(scope);
        _chats[scope] = c;
        return c;
    }
}

/// 시작 화면 (준비 중 진행 막대)
public sealed partial class StartupState : ObservableObject
{
    public sealed partial class Step(string title) : ObservableObject
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Title { get; } = title;
        [ObservableProperty] private bool _done;
        [ObservableProperty] private string? _note;
    }

    public System.Collections.ObjectModel.ObservableCollection<Step> Steps { get; } = [];
    [ObservableProperty] private string _current = "";
    [ObservableProperty] private bool _finished;
    [ObservableProperty] private double _progress;

    internal void UpdateProgress() =>
        Progress = Steps.Count == 0 ? 0 : (double)Steps.Count(s => s.Done) / Steps.Count;
}
