using CommunityToolkit.Mvvm.ComponentModel;

namespace GodDB.App.ViewModels;

// 다른 화면(Redis · 다이어그램 · AI 어시스턴트) 포팅에서 채울 상태 클래스들.
// AppState 가 쓰는 멤버만 먼저 둔다. 채우는 쪽은 이 파일의 클래스를 자기 파일로 옮겨 넓히면 된다
// (이름 · 이미 있는 멤버의 뜻은 바꾸지 말 것).

// RedisWorkspace 는 RedisWorkspace.cs 로 옮김 (Redis 화면 포팅)

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
