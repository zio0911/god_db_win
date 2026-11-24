using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GodDB.App.ViewModels;

/// 메인 창 오른쪽 AI 어시스턴트 패널 상태 (Mac AssistantPanel.swift 의 화면 쪽).
/// 대화 범위(접속·DB / 모델)는 AppState.AssistantScope 를 따르고, 범위마다 AiChat 하나를 쓴다.
/// 데이터 행은 보내지 않는다 — 보내는 문맥은 AppState.AssistantMessagesAsync 가 만든다.
public sealed partial class AssistantPanelViewModel : ObservableObject
{
    public AppState App { get; }

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private string _scope = "";
    [ObservableProperty] private AiChat _chat;
    /// 로컬 Ollama 상태 안내 (괜찮으면 null)
    [ObservableProperty] private string? _ollamaHint;
    /// 안내 옆 버튼: 실행 / 내려받기 (없으면 None)
    [ObservableProperty] private OllamaAction _ollamaFix;

    public enum OllamaAction { None, Start, Download }

    /// 대화 내용이 바뀌었을 때 (스트리밍 중에는 자주). UI 스레드에서 불린다.
    public event Action? ChatChanged;
    /// 범위 · 문맥(함께 보낼 정보 · 빠른 질문)이 바뀌었을 때
    public event Action? ContextChanged;
    /// 데이터 찾기 답 카드가 바뀌었을 때 (UI 스레드)
    public event Action? CardsChanged;

    /// 상태 확인에 쓸 Ollama (기본: 설정의 주소). 테스트에서 바꾼다.
    public Func<OllamaClient> ClientFactory { get; set; } = () => AISettings.Client;

    public AssistantPanelViewModel(AppState app)
    {
        App = app;
        _scope = app.AssistantScope;
        _chat = app.Assistant.Chat(_scope);
        _chat.Changed += OnChatChanged;
        app.PropertyChanged += OnAppChanged;
        app.ConnectionChanged += _ => Refresh();
        app.Assistant.PropertyChanged += OnAssistantChanged;
        app.Assistant.FinderCardChanged += _ => CardsChanged?.Invoke();
        app.TableDescriptionsChanged += () => AppState.RunOnUi(() => ContextChanged?.Invoke());
    }

    private void OnChatChanged() => AppState.RunOnUi(() => ChatChanged?.Invoke());

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Mode):
            case nameof(AppState.SelectedProfileId):
            case nameof(AppState.State):
            case nameof(AppState.CurrentDatabase):
            case nameof(AppState.Tables):
            case nameof(AppState.SelectedTabId):
            case nameof(AppState.SelectedTables):
                Refresh();
                break;
        }
    }

    private void OnAssistantChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssistantState.IncludeSql)) ContextChanged?.Invoke();
        if (e.PropertyName == nameof(AssistantState.FinderMode)) OnPropertyChanged(nameof(FinderOn));
        if (e.PropertyName == nameof(AssistantState.Visible) && App.Assistant.Visible) _ = CheckOllamaAsync();
    }

    /// 범위가 바뀌었으면 그 범위의 대화로 바꾼다 (모델 선택처럼 알림이 없는 변화는 화면이 보일 때 부른다)
    public void Refresh()
    {
        var scope = App.AssistantScope;
        if (scope != Scope)
        {
            Chat.Changed -= OnChatChanged;
            Scope = scope;
            Chat = App.Assistant.Chat(scope);
            Chat.Changed += OnChatChanged;
            ChatChanged?.Invoke();
        }
        OnPropertyChanged(nameof(Title));
        ContextChanged?.Invoke();
    }

    public string Title => App.AssistantTitle;

    public bool IsModeling => App.Mode == MainMode.Modeling && App.Modeling.Selected != null;
    public bool IsSql => App.State.IsConnected && App.SelectedProfile?.Kind.IsSql() == true;

    /// 이번 질문에 함께 보낼 것 (아이콘, 문장)
    public List<(string Icon, string Text)> Chips()
    {
        if (IsModeling && App.Modeling.Selected is { } m)
            return [("▦", Tr("모델 %@ · 테이블 %@개", m.Name, m.Tables.Count))];
        if (!App.State.IsConnected || App.SelectedProfile is not { } p)
            return [("⊘", Tr("접속이 없어서 일반 질문만 할 수 있어요"))];
        if (p.Kind == DatabaseKind.Redis) return [("🔑", Tr("Redis 접속 정보 (키 값은 안 보내요)"))];
        var focus = IsSql ? App.AssistantFocus(Input) : [];
        var output = new List<(string, string)> { ("☰", Tr("테이블 목록 %@개", App.Tables.Count)) };
        if (focus.Count == 0)
        {
            output.Add(("?", Tr("질문에 테이블 이름(또는 논리명)을 쓰거나 사이드바에서 고르면 구조를 자세히 보내요")));
        }
        else
        {
            output.Add(("▦", Tr("자세히: %@", string.Join(", ", focus.Select(t => t.Name)))));
            output.Add(("📊", Tr("크기 통계 · 자동 점검")));
        }
        if (App.Assistant.IncludeSql && App.SelectedTab is { } tab && tab.Text.Trim().Length > 0)
            output.Add((">_", Tr("SQL · %@", tab.Title)));
        if (App.TableDescriptions.Doc(p.Id, App.CurrentDatabase) is { } d)
            output.Add(("📄", Tr("테이블 설명 · 테이블 %@개", d.Tables.Count)));
        return output;
    }

    public string IntroText => IsModeling
        ? Tr("지금 모델을 바탕으로 검토하거나 물리명을 추천받을 수 있어요.")
        : Tr("지금 접속의 테이블 구조, 코멘트, 인덱스, 크기 통계, 편집기 SQL 을 바탕으로 답해요. 데이터 행은 보내지 않아요.");

    public List<string> QuickActions =>
        AssistantAI.QuickActions(IsModeling, IsSql, App.SelectedTables, App.Modeling.Selected?.Kind ?? DatabaseKind.Mysql);

    public string Expand(string q) =>
        AssistantAI.ExpandQuickAction(q, IsModeling, App.Modeling.Selected?.Kind ?? DatabaseKind.Mysql);

    // MARK: - 동작

    public bool CanSend => Input.Trim().Length > 0 && !Chat.Running;

    partial void OnInputChanged(string value) => OnPropertyChanged(nameof(CanSend));

    // MARK: - 데이터 찾기 (Mac 1.4.0)

    /// 데이터 찾기는 SQL 접속에서만 (Redis · 모델링 제외)
    public bool CanFind => IsSql && !IsModeling;
    public bool FinderOn => CanFind && App.Assistant.FinderMode;

    /// 🔎 데이터 위치 찾기: 다음 입력을 데이터 찾기로
    public void StartFinder() => App.Assistant.FinderMode = true;
    public void StopFinder() => App.Assistant.FinderMode = false;

    /// 답 메시지에 붙은 데이터 찾기 카드 (없으면 null)
    public DataFinderCard? CardOf(AiChat.Message m) => m.Role == "user" ? null : App.Assistant.FinderCard(m.Id);

    /// 입력한 질문 보내기 (Enter). 답하는 중이면 그 답을 멈추고 이 글을 먼저 (#177).
    /// 데이터 찾기 모드이거나 "…어디 있어?" 꼴이면 데이터 찾기 (SQL 접속만, 애매하면 일반 질문)
    public void Send()
    {
        var q = Input;
        if (q.Trim().Length == 0) return;
        Input = "";
        Refresh();
        if (CanFind && (FinderOn || DataFinder.IsFindQuestion(q))) App.FindData(q);
        else App.AskAssistant(q);
    }

    /// 질문 보내기 (빠른 질문은 Expand 한 문장)
    public void Ask(string question)
    {
        Refresh();
        App.AskAssistant(question);
    }

    public void AskQuick(string q) => Ask(Expand(q));

    public void Cancel() => Chat.Cancel();
    public void StartNew() => Chat.StartNew();
    public void Open(DiagramConversation c) => Chat.Open(c);
    public void Delete(Guid id) => Chat.Delete(id);
    public void DeleteAll() => Chat.DeleteAll();

    /// 답 아래 "SQL 편집기에 넣기"
    public static string? SqlOf(AiChat.Message m) => m.Role == "assistant" && !m.Failed ? AiText.FirstSql(m.Text) : null;

    public bool InsertSql(AiChat.Message m) => SqlOf(m) is { } sql && App.InsertAiSql(sql, Tr("AI 어시스턴트"));

    /// 모델링에서 물리명 JSON 이 있는 답에만 "물리명 적용 (빈 칸만)"
    public bool CanApplyPhysicalNames(AiChat.Message m) =>
        IsModeling && m.Role == "assistant" && m.Text.Contains("\"tables\"", StringComparison.Ordinal) && App.Modeling.SelectedId != null;

    public int ApplyPhysicalNames(AiChat.Message m) => App.ApplyAssistantPhysicalNames(m.Text);

    /// 답이 다 끝났는지 (스트리밍 중인 마지막 답에는 버튼을 숨긴다)
    public bool IsFinished(AiChat.Message m) =>
        m.Text.Length > 0 && !(Chat.Running && ReferenceEquals(Chat.Messages.LastOrDefault(), m));

    /// 로컬 Ollama 상태를 보고 안내 문장을 정한다 (Windows 판: 패널에서 바로 실행 / 내려받기 안내)
    public async Task CheckOllamaAsync()
    {
        OllamaStatus status;
        try { status = await Task.Run(() => ClientFactory().StatusAsync()); }
        catch { return; }
        switch (status)
        {
            case OllamaStatus.Running r:
                OllamaHint = r.Models.Count == 0 || TuningSettings.ResolveModel(r.Models, AISettings.Model).Length == 0
                    ? Tr("AI 에 쓸 모델을 설정 > AI 에서 골라 주세요.") : null;
                OllamaFix = OllamaAction.None;
                break;
            case OllamaStatus.InstalledNotRunning:
                OllamaHint = Tr("Ollama 가 실행 중이 아니에요. 설정 > AI 에서 확인해 주세요.");
                OllamaFix = OllamaAction.Start;
                break;
            default:
                OllamaHint = Tr("Ollama 가 실행 중이 아니에요. 설정 > AI 에서 확인해 주세요.");
                OllamaFix = OllamaAction.Download;
                break;
        }
    }

    public async Task FixOllamaAsync()
    {
        switch (OllamaFix)
        {
            case OllamaAction.Start:
                try { OllamaClient.Start(); }
                catch (Exception e) { App.LogError(e, LogSource.App, Tr("Ollama 실행")); return; }
                await Task.Delay(1500);
                await CheckOllamaAsync();
                break;
            case OllamaAction.Download:
                App.Dialogs.OpenExternal(OllamaClient.DownloadUrl);
                break;
        }
    }
}
