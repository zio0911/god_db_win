using System.Diagnostics;
using GodDB.Core.Schema;
using static GodDB.Core.L10n;

namespace GodDB.Core.Ai;

/// AI 대화 상태 하나 (다이어그램 AI 패널 · AI 어시스턴트 패널 공용). Mac 판 DiagramAIPanel.swift 의 DiagramChat 에서
/// 화면과 무관한 부분을 옮겼다. 상태가 바뀌면 Changed 가 불린다 — UI 스레드에서 AskAsync 를 부르면 변경도 UI 스레드에서 일어난다.
public sealed class AiChat
{
    public sealed class Message
    {
        public Guid Id { get; } = Guid.NewGuid();
        /// "user" / "assistant"
        public string Role { get; }
        public string Text { get; set; }
        public bool Failed { get; set; }

        public Message(string role, string text, bool failed = false)
        {
            Role = role;
            Text = text;
            Failed = failed;
        }
    }

    /// 감사 로그 · 상태창 연결 (모두 선택). 질문·답변 내용은 감사 로그에 넣지 않는다.
    public sealed record Hooks
    {
        /// 시작할 때 상태창 한 줄 (model, 첫 질문인지) → 문장 (null 이면 남기지 않음). 상세는 질문.
        public Func<string, bool, string?>? LogMessage { get; init; }
        /// 상태창에 남기기 (문장, 상세)
        public Action<string, string>? Log { get; init; }
        /// 답변 완료 (기능 이름, 걸린 초, 모델) — 감사 로그 한 줄 요약용
        public Action<string, int, string>? Answered { get; init; }
        /// 실패: 오류를 기록하고 화면에 보일 문장을 돌려준다 (예외, 실패 문맥, 기능 이름). 없으면 예외 메시지.
        public Func<Exception, string, string, string>? Failed { get; init; }
    }

    public List<Message> Messages { get; private set; } = [];
    public bool Running { get; private set; }
    public int ThinkingChars { get; private set; }

    /// 대화 기록 (범위마다, 90일 보관)
    public string Scope { get; }
    public DiagramChatStore Store { get; }
    public Guid ConversationId { get; private set; } = Guid.NewGuid();
    private DateTime _created = DateTime.UtcNow;
    public List<DiagramConversation> History { get; private set; }

    /// 쓸 Ollama (기본: 설정의 주소). 테스트에서 바꾼다.
    public Func<OllamaClient> ClientFactory { get; set; } = () => AISettings.Client;
    /// 쓸 모델 (빈 문자열이면 설치된 추천 모델 → 첫 모델). 기본: 설정
    public Func<string> ModelSetting { get; set; } = () => AISettings.Model;

    public event Action? Changed;

    private CancellationTokenSource? _cts;

    /// 지금 작업 번호: 멈춘 작업이 늦게 끝나면서 새 작업 상태를 건드리지 않게 (Mac 1.4.3 #177)
    public int Generation { get; private set; }

    public AiChat(string scope, DiagramChatStore? store = null)
    {
        Scope = scope;
        Store = store ?? DiagramChatStore.Shared;
        History = Store.Load(scope);
        // 열 때 마지막 대화를 이어서 보여준다
        if (History.FirstOrDefault() is { } last) Show(last);
    }

    private void Notify() => Changed?.Invoke();

    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        Running = false;
        Notify();
    }

    /// 새 작업 시작: 답하는 중이면 그 답은 멈춘다 (#177 — 답하는 중에 보낸 글이 사라지지 않게).
    /// 작업 번호와 이 작업의 취소 토큰을 돌려준다.
    public (int Generation, CancellationToken Token) Begin()
    {
        if (Running) Cancel();
        Generation++;
        Running = true;
        ThinkingChars = 0;
        var cts = new CancellationTokenSource();
        _cts = cts;
        return (Generation, cts.Token);
    }

    /// 답 말풍선 고치기 (id 로 찾는다 — 그 사이 새 대화로 바꿨으면 아무것도 하지 않는다)
    public void Edit(Guid id, Action<Message> change)
    {
        if (Messages.FirstOrDefault(m => m.Id == id) is not { } m) return;
        change(m);
        Notify();
    }

    /// 작업 끝: 그 사이 새 작업이 시작됐으면 상태는 새 작업 것이라 그대로 둔다
    public void Finish(int generation)
    {
        if (generation != Generation) return;
        Running = false;
        _cts = null;
        Persist();
        Notify();
    }

    /// 메시지 더하기 (데이터 찾기처럼 AskAsync 밖에서 대화를 만들 때)
    public Message Add(string role, string text)
    {
        var m = new Message(role, text);
        Messages.Add(m);
        Notify();
        return m;
    }

    private void Show(DiagramConversation c)
    {
        ConversationId = c.Id;
        _created = c.Created;
        Messages = c.Messages.Select(m => new Message(m.Role, m.Text, m.Failed)).ToList();
    }

    /// 지금 대화를 저장 (답이 끝났을 때)
    public void Persist()
    {
        if (Messages.Count == 0) return;
        var c = new DiagramConversation(ConversationId, _created, Messages.Select(m => new DiagramConversation.Message(m.Role, m.Text, m.Failed)))
        {
            Updated = DateTime.UtcNow,
        };
        Store.Save(c, Scope);
        History = Store.Load(Scope);
        Notify();
    }

    /// 새 대화 시작 (이전 대화는 기록에 남는다)
    public void StartNew()
    {
        Cancel();
        Persist();
        Messages = [];
        ConversationId = Guid.NewGuid();
        _created = DateTime.UtcNow;
        Notify();
    }

    public void Open(DiagramConversation c)
    {
        if (c.Id == ConversationId) return;
        Cancel();
        Persist();
        Show(c);
        Notify();
    }

    public void Delete(Guid id)
    {
        if (id == ConversationId) Cancel();
        Store.Delete(id, Scope);
        History = Store.Load(Scope);
        if (id == ConversationId)
        {
            Messages = [];
            ConversationId = Guid.NewGuid();
            _created = DateTime.UtcNow;
        }
        Notify();
    }

    public void DeleteAll()
    {
        Cancel();
        Store.DeleteAll(Scope);
        History = [];
        Messages = [];
        ConversationId = Guid.NewGuid();
        _created = DateTime.UtcNow;
        Notify();
    }

    /// "AI 어시스턴트 실패" → "AI 어시스턴트" (감사 로그 요약용)
    public static string FeatureName(string failureContext) =>
        failureContext.Replace(" 실패", "").Replace(" failed", "");

    /// 다이어그램: 질문을 보내고 답을 스트리밍으로 받는다 (테이블 구조와 관계만 보냄).
    public Task AskDiagramAsync(string question, DatabaseKind kind, IReadOnlyList<TableMeta> tables, IReadOnlyList<ForeignKeyMeta> inferred,
                                Hooks? hooks = null)
    {
        var count = tables.Count;
        hooks ??= new Hooks();
        hooks = hooks with
        {
            LogMessage = hooks.LogMessage ?? ((model, first) => first ? Tr("다이어그램 AI 분석 · %@ · 테이블 %@개", model, count) : null),
        };
        return AskAsync(question, (history, _) =>
        {
            var schema = DiagramAI.SchemaText(tables, inferred);
            return Task.FromResult(DiagramAI.Messages(kind, schema, history, IsEnglish));
        }, Tr("다이어그램 AI 분석 실패"), hooks);
    }

    /// 공통: 질문을 보내고 답을 스트리밍으로 받는다. build 는 대화 기록으로 보낼 메시지를 만든다 (문맥 수집 포함).
    public async Task AskAsync(string question,
                               Func<IReadOnlyList<(string Role, string Content)>, CancellationToken, Task<List<OllamaMessage>>> build,
                               string failureContext, Hooks? hooks = null, double temperature = 0.3)
    {
        hooks ??= new Hooks();
        var q = question.Trim();
        if (q.Length == 0) return;
        // 답하는 중이면 그 답은 멈추고 이 질문을 먼저 (#177)
        var (gen, ct) = Begin();
        Messages.Add(new Message("user", q));
        var answer = new Message("assistant", "");
        Messages.Add(answer);
        var answerId = answer.Id;
        var history = Messages.Take(Messages.Count - 1).Where(m => !m.Failed).Select(m => (m.Role, Content: m.Text)).ToList();
        Notify();

        var client = ClientFactory();
        var started = Stopwatch.StartNew();
        try
        {
            if (await client.StatusAsync(ct) is not OllamaStatus.Running running) throw new OllamaException(OllamaErrorKind.NotRunning);
            var model = TuningSettings.ResolveModel(running.Models, ModelSetting());
            if (model.Length == 0) throw new OllamaException(OllamaErrorKind.NoModel);
            var msgs = await build(history, ct);
            if (hooks.LogMessage?.Invoke(model, history.Count == 1) is { } line) hooks.Log?.Invoke(line, q);
            var raw = "";
            await foreach (var chunk in client.ChatAsync(model, msgs, temperature, ct))
            {
                ct.ThrowIfCancellationRequested();
                switch (chunk)
                {
                    case ChatChunk.Thinking t: if (gen == Generation) ThinkingChars += t.Text.Length; break;
                    case ChatChunk.Content c:
                        raw += c.Text;
                        answer.Text = DiagramAI.Clean(raw);
                        break;
                }
                Notify();
            }
            // 멈추면 스트림이 오류 없이 끝날 수 있다 → 답이 끝난 것으로 보지 않게
            ct.ThrowIfCancellationRequested();
            Edit(answerId, m => { if (m.Text.Length == 0) m.Text = Tr("(답변 없음)"); });
            hooks.Answered?.Invoke(FeatureName(failureContext), (int)started.Elapsed.TotalSeconds, model);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 취소로 끊긴 연결 오류도 실패가 아니라 취소로
            Edit(answerId, m => m.Text += (m.Text.Length == 0 ? "" : "\n\n") + Tr("(취소했어요)"));
        }
        catch (Exception e)
        {
            var text = hooks.Failed?.Invoke(e, failureContext, FeatureName(failureContext)) ?? e.Message;
            Edit(answerId, m => { m.Failed = true; m.Text = text; });
        }
        Finish(gen);
    }

    /// 진행 표시 (생각 중 / 답변 쓰는 중)
    public string ProgressText => ThinkingChars > 0 && Messages.LastOrDefault()?.Text.Length == 0
        ? Tr("모델이 생각하는 중… (%@자)", ThinkingChars)
        : Tr("답변을 쓰는 중…");

    /// 다이어그램 AI 패널의 예시 질문
    public static List<string> DiagramExampleQuestions =>
        [Tr("핵심 테이블과 데이터 흐름을 설명해 줘"),
         Tr("FK 가 빠진 것 같은 관계를 찾아 줘"),
         Tr("이 테이블들을 JOIN 하는 대표 쿼리를 만들어 줘"),
         Tr("인덱스가 더 필요한 컬럼이 있어?")];
}
