using System.Diagnostics;
using GodDB.Core.Dialects;
using GodDB.Core.Drivers;
using GodDB.Core.Schema;
using static GodDB.Core.L10n;

namespace GodDB.Core.Ai;

public enum TuningPhase { Collecting, Thinking, Writing, Comparing, Done, Failed }

/// 쿼리 튜닝 작업 하나 (모으기 → 모델 응답 → 개선 SQL 실행 계획 비교). Mac 판 TuningView.swift 의 TuningJob · runTuning 에서
/// 화면과 무관한 부분. 개선 SQL 과 추천 인덱스는 실행하지 않는다 (개선 SQL 은 EXPLAIN 만).
public sealed class TuningJob
{
    /// 감사 로그 · 상태창 연결 (모두 선택)
    public sealed record Hooks
    {
        /// 시작 (상태창 문장, 상세 = Ollama 에 보낸 내용)
        public Action<string, string>? Started { get; init; }
        /// 완료 (상태창 문장, 상세 = 받은 답, 감사 로그 요약 "튜닝 완료 · N초 · 모델")
        public Action<string, string, string>? Finished { get; init; }
        /// 실패: 오류를 기록하고 화면에 보일 문장을 돌려준다 (예외, 실패 문맥). 없으면 예외 메시지.
        public Func<Exception, string, string>? Failed { get; init; }
    }

    public Guid Id { get; } = Guid.NewGuid();
    public Guid TabId { get; }
    public string Sql { get; }
    /// 설정의 모델 (빈 문자열이면 설치된 첫 모델 — 실제로 쓴 모델은 UsedModel)
    public string Model { get; }
    public string ConnectionName { get; }
    public TuningPhase Phase { get; private set; } = TuningPhase.Collecting;
    public string? FailureMessage { get; private set; }
    public TuningContext? Context { get; private set; }
    /// Ollama 에 보낸 내용 ("Ollama 에 보낸 정보 보기")
    public string Prompt { get; private set; } = "";
    public string Streamed { get; private set; } = "";
    public int ThinkingChars { get; private set; }
    public TuningResult? Result { get; private set; }
    public IReadOnlyList<PlanAnalyzer.Warning>? AfterWarnings { get; private set; }
    public string? AfterError { get; private set; }
    public string UsedModel { get; private set; } = "";
    public DateTime Started { get; } = DateTime.Now;
    public DateTime? Finished { get; private set; }

    public event Action? Changed;

    private readonly CancellationTokenSource _cts = new();

    public TuningJob(Guid tabId, string sql, string model, string connectionName)
    {
        TabId = tabId;
        Sql = sql;
        Model = model;
        ConnectionName = connectionName;
    }

    public bool IsRunning => Phase is not (TuningPhase.Done or TuningPhase.Failed);

    /// 튜닝을 쓸 수 없는 이유 (쓸 수 있으면 null)
    public static string? UnavailableReason(bool hasQueryTab, bool? enabled = null)
    {
        if (!(enabled ?? TuningSettings.Enabled)) return Tr("설정 > 쿼리 튜닝에서 켜 주세요.");
        if (!hasQueryTab) return Tr("쿼리 탭을 먼저 여세요.");
        return null;
    }

    /// 대상 SQL 정리 (앞뒤 공백, 끝의 ; 제거). 비면 null — 튜닝하지 않는다.
    public static string? Normalize(string sql)
    {
        var s = sql.Trim();
        while (s.EndsWith(';')) s = s[..^1];
        return s.Length == 0 ? null : s;
    }

    /// 진행 표시
    public string PhaseText => Phase switch
    {
        TuningPhase.Collecting => Tr("테이블 구조와 실행 계획을 모으는 중…"),
        TuningPhase.Thinking => Tr("모델이 생각하는 중… (%@자)", ThinkingChars),
        TuningPhase.Writing => Tr("답변을 쓰는 중…"),
        TuningPhase.Comparing => Tr("개선 SQL 의 실행 계획을 확인하는 중…"),
        TuningPhase.Failed => FailureMessage ?? "",
        _ => "",
    };

    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        if (IsRunning) Fail(Tr("취소했어요."));
    }

    private void Fail(string message)
    {
        Phase = TuningPhase.Failed;
        FailureMessage = message;
        Changed?.Invoke();
    }

    /// 튜닝 실행. database 가 비어 있지 않고 세션의 현재 DB 와 다르면 먼저 바꾼다.
    public async Task RunAsync(DbSession session, string database, IReadOnlyList<TableMeta> knownTables,
                               OllamaClient? client = null, Hooks? hooks = null, bool? english = null)
    {
        hooks ??= new Hooks();
        client ??= TuningSettings.Client;
        var ct = _cts.Token;
        var sw = Stopwatch.StartNew();
        try
        {
            // 1) 모델 확인
            if (await client.StatusAsync(ct) is not OllamaStatus.Running running) throw new OllamaException(OllamaErrorKind.NotRunning);
            var model = TuningSettings.ResolveModel(running.Models, Model);
            if (model.Length == 0) throw new OllamaException(OllamaErrorKind.NoModel);
            UsedModel = model;

            // 2) 정보 모으기 (쿼리, 테이블 구조, 예상 행 수, 실행 계획)
            if (database.Length > 0 && session.Current != database) await session.UseAsync(database, ct);
            var context = await session.TuningContextAsync(Sql, knownTables, ct);
            Context = context;
            var messages = TuningPrompt.Messages(context, english ?? IsEnglish);
            Prompt = messages.LastOrDefault()?.Content ?? "";
            hooks.Started?.Invoke(Tr("쿼리 튜닝 시작 · %@ · 테이블 %@개", model, context.Tables.Count), Prompt);
            Changed?.Invoke();

            // 3) 모델 응답 (스트리밍)
            Phase = TuningPhase.Thinking;
            Changed?.Invoke();
            await foreach (var chunk in client.ChatAsync(model, messages, ct: ct))
            {
                ct.ThrowIfCancellationRequested();
                switch (chunk)
                {
                    case ChatChunk.Thinking t: ThinkingChars += t.Text.Length; break;
                    case ChatChunk.Content c:
                        Phase = TuningPhase.Writing;
                        Streamed += c.Text;
                        break;
                }
                Changed?.Invoke();
            }
            var result = TuningResult.Parse(Streamed);
            Result = result;

            // 4) 개선 SQL 의 실행 계획을 다시 떠서 비교 (실행은 하지 않음)
            if (result.ImprovedSql is { } improved)
            {
                Phase = TuningPhase.Comparing;
                Changed?.Invoke();
                var s = improved;
                while (s.EndsWith(';')) s = s[..^1];
                try
                {
                    var (plan, _) = await session.ExplainAsync(s.Trim(), ct);
                    AfterWarnings = PlanAnalyzer.Warnings(plan);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    AfterError = e.Message;
                }
            }
            Phase = TuningPhase.Done;
            Finished = DateTime.Now;
            var secs = (int)sw.Elapsed.TotalSeconds;
            hooks.Finished?.Invoke(Tr("쿼리 튜닝 완료 · %@초", secs), Streamed, $"튜닝 완료 · {secs}초 · {model}");
            Changed?.Invoke();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            Fail(Tr("취소했어요."));
        }
        catch (Exception e)
        {
            Fail(hooks.Failed?.Invoke(e, Tr("쿼리 튜닝 실패")) ?? e.Message);
        }
    }

    /// 실행 계획 주의사항 요약 ("주의 없음" / "주의 N건")
    public static string WarningSummary(IReadOnlyList<PlanAnalyzer.Warning> list) =>
        list.Count == 0 ? Tr("주의 없음") : Tr("주의 %@건", list.Count);
}
