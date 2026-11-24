using System.Diagnostics;

namespace GodDB.App.ViewModels;

/// AI 데이터 찾기 답 카드에 보이는 위치 하나 (Mac DataFinderUI.swift)
public sealed class DataFinderAnswer
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Table { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    /// AI 판단의 신뢰도 (테이블 설명 · 다른 후보는 없음)
    public DataFinder.Confidence? Confidence { get; init; }
    public string Explain { get; init; } = "";
    public string Sql { get; init; } = "";
    /// 카드에 보일 값 예 (null = 조회 안 함, 빈 목록 = 샘플 없음)
    public IReadOnlyList<string>? Samples { get; init; }
    /// 샘플 조회 상태 ("값 N개" / "시간 초과" / "조회 실패: …", null = 조회 안 함)
    public string? SampleStatus { get; init; }
    /// 테이블 설명(#178)에서 찾음 — [아니에요] 하면 설명에서 그 찾는 말을 뺀다
    public bool FromDescriptions { get; init; }
    public bool Confirmed { get; set; }
    public TableNote.NoteSource Source { get; init; }

    public string Location => Columns.Count == 0 ? Table : $"{Table}.{string.Join(", ", Columns)}";
}

/// 고른 모델이 실패했을 때 카드에 보일 내용
public sealed class FinderModelProblem
{
    public required string Model { get; init; }
    public required string Reason { get; init; }
    /// 확인해 보니 답하는 다른 모델 (추천 모델 먼저)
    public List<string> Alternatives { get; } = [];
    /// 다른 모델을 확인하는 중
    public bool Checking { get; set; } = true;

    public string Message => Tr("선택한 AI 모델 ‹%@› 이 응답하지 않아요 (Ollama: %@). 설정 > AI 에서 다른 모델을 골라 주세요.", Model, Reason);
}

/// 답 카드 상태 (어시스턴트 대화의 답 메시지 하나에 붙는다; 앱을 다시 열면 글 요약만 남는다)
public sealed class DataFinderCard
{
    public required string Question { get; init; }
    public required IReadOnlyList<string> Terms { get; init; }
    public required Guid ProfileId { get; init; }
    public required string Database { get; init; }
    public DataFinderAnswer? Current { get; set; }
    /// [아니에요] 하면 차례로 보여줄 다른 후보
    public List<DataFinderAnswer> Others { get; set; } = [];
    /// 테이블 설명에서 바로 찾음 (AI 호출 없음)
    public bool FromNotes { get; set; }
    /// 보낸 내용 칩: 테이블 목록 N개 · 후보 컬럼 샘플 M개
    public int? TablesSent { get; set; }
    public int? SamplesSent { get; set; }
    /// 고른 AI 모델이 답하지 않음 (찾기를 멈추고 다른 모델을 권한다)
    public FinderModelProblem? ModelProblem { get; set; }
    /// AI 를 못 쓰고 이름 · 코멘트 · 값으로 찾음 (모델 오류면 그 이유)
    public bool WithoutAI { get; set; }
    public string? AiError { get; set; }

    public string Title => Terms.Count == 0 ? Question : DataFinder.Phrase(Terms);
}

/// 샘플 조회기: 따로 연 짧은 연결로 조회하고(사용자 세션은 그대로), 연결을 못 열면 사용자 세션으로.
public sealed class FinderSampler(DbSession session)
{
    private SideConnection? _side;
    private bool _tried;

    public async Task<RawResult> RunAsync(string sql, CancellationToken ct)
    {
        if (!_tried)
        {
            _tried = true;
            if (session.SideConnection() is { } s)
            {
                try { await s.ConnectAsync(ct); _side = s; }
                catch { _side = null; await s.CloseAsync(); }
            }
        }
        if (_side != null) return await _side.QueryAsync(sql, DataFinder.SampleTimeout, ct);
        // 따로 연결할 수 없으면 사용자 세션으로: 시간이 넘으면 서버에 취소를 보낸다
        // (SQL Server 는 취소하면 세션이 끊기므로 DbSession 이 다시 연결하고 보던 DB 로 돌아간다)
        var work = session.RawAsync(sql, ct);
        if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(DataFinder.SampleTimeout), ct)) != work)
        {
            ct.ThrowIfCancellationRequested();
            try { await session.CancelRunningAsync(); } catch { }
            _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            throw new SideConnection.TimedOutException((int)DataFinder.SampleTimeout);
        }
        return await work;
    }

    public async Task CloseAsync()
    {
        if (_side != null) await _side.CloseAsync();
        _side = null;
    }
}

// AI 데이터 찾기 (Mac 1.4.0 DataFinderUI.swift 의 AppState 쪽)
public sealed partial class AppState
{
    /// 데이터 찾기 · 모델 확인에 쓸 로컬 Ollama (기본: 설정의 주소, 테스트에서 바꾼다)
    public Func<OllamaClient> AiClientFactory { get; set; } = () => AISettings.Client;

    /// 데이터 위치 찾기. 테이블 설명의 찾는 말이 맞으면 바로 답, 아니면 로컬 AI 후보 → 샘플 조회 → 판단.
    public void FindData(string question, bool skipNotes = false) => Fire(() => FindDataAsync(question, skipNotes));

    public async Task FindDataAsync(string question, bool skipNotes = false)
    {
        if (SelectedProfile is not { } p || !p.Kind.IsSql() || !State.IsConnected || Session is not { } session) return;
        var chat = Assistant.Chat(AssistantScope);
        var q = question.Trim();
        if (q.Length == 0) return;
        // 답하는 중이면 그 답은 멈추고 이 질문을 먼저 (#177)
        if (chat.Running) chat.Cancel();
        Assistant.FinderMode = false;
        var terms = DataFinder.KeyTerms(q);
        var db = CurrentDatabase;
        var tables = DescribedTables;
        var hints = CurrentDescriptions?.FinderNotes(p.Id, db) ?? [];
        chat.Add("user", q);
        var answerMessage = chat.Add("assistant", "");
        var messageId = answerMessage.Id;
        var dialect = session.Dialect;
        var card = new DataFinderCard { Question = q, Terms = terms, ProfileId = p.Id, Database = db };

        if (terms.Count == 0)
        {
            chat.Edit(messageId, m => m.Text = Tr("무엇을 찾을지 알아듣지 못했어요. 예: \"학년 정보는 어디 있어?\""));
            chat.Persist();
            return;
        }

        // 1) 테이블 설명 먼저
        if (!skipNotes)
        {
            var hits = DataFinder.MatchNotes(terms, hints);
            if (hits.Count > 0)
            {
                var answers = hits.Select(n => new DataFinderAnswer
                {
                    Table = n.Table, Columns = n.Columns, Explain = n.Memo, Sql = DataFinder.DefaultSql(n.Table, dialect),
                    FromDescriptions = true, Confirmed = true, Source = TableNote.NoteSource.User,
                }).ToList();
                card.Current = answers[0];
                card.Others = answers.Skip(1).ToList();
                card.FromNotes = true;
                Assistant.SetFinderCard(messageId, card);
                chat.Edit(messageId, m => m.Text = FinderSummary(card));
                chat.Persist();
                return;
            }
        }

        var (gen, ct) = chat.Begin();
        var target = db.Length == 0 ? p.Name : $"{p.Name}/{db}";
        var sampleQueries = 0;
        var candidateCount = 0;
        var sampler = new FinderSampler(session);
        string model = "";
        try
        {
            var client = AiClientFactory();
            if (await client.StatusAsync(ct) is not OllamaStatus.Running running) throw new OllamaException(OllamaErrorKind.NotRunning);
            var installed = running.Models.Select(m => m.Name).ToList();
            model = AISettings.EffectiveModel(installed) ?? "";
            if (model.Length == 0) throw new OllamaException(OllamaErrorKind.NoModel);

            // 0) 모델 확인 (앱을 켜 둔 동안 모델별로 기억). 실패하면 조용히 넘어가지 않고 알린다.
            chat.Edit(messageId, m => m.Text = Tr("🔎 AI 모델 ‹%@› 확인 중…", model));
            var health = await ModelProbe.CheckAsync(model, client, ct: ct);
            ct.ThrowIfCancellationRequested();
            if (!health.IsOK)
            {
                card.ModelProblem = new FinderModelProblem { Model = model, Reason = health.Reason };
                Assistant.SetFinderCard(messageId, card);
                chat.Edit(messageId, m => m.Text = FinderSummary(card));
                Log(LogLevel.Warning, LogSource.Schema, Tr("AI 데이터 찾기 · 모델 %@ 응답 없음 · %@", model, health.Reason),
                    audit: new AuditRecord(AuditAction.Ai, target, $"실패: 데이터 찾기 · 모델 응답 없음 ({model})"));
                chat.Finish(gen);
                ProbeAlternatives(messageId, model, installed, client);
                return;
            }

            var ai = DataFinderAI.Ollama(client, model, p.Kind, IsEnglish);
            var found = await ai.FindAsync(q, terms, tables, hints,
                sample: async (table, column) =>
                {
                    var sql = DataFinder.SampleSql(table, column, dialect);
                    return await RunSampleAsync(sql, sampler, p.Name, target, ct);
                },
                progress: step =>
                {
                    var text = step switch
                    {
                        DataFinderAI.Step.Picking => Tr("🔎 후보 테이블 · 컬럼을 고르는 중…"),
                        DataFinderAI.Step.Sampling => Tr("🔎 후보 컬럼 값을 확인하는 중…"),
                        _ => Tr("🔎 값을 보고 판단하는 중…"),
                    };
                    RunOnUi(() => chat.Edit(messageId, m => m.Text = text));
                    return Task.CompletedTask;
                },
                allowSamples: () => AllowProdSamplesAsync(p),
                ct: ct);
            await sampler.CloseAsync();
            ct.ThrowIfCancellationRequested();
            candidateCount = found.Picked.Candidates.Count;
            sampleQueries = found.SampleQueries;
            card.TablesSent = found.Picked.TablesSent;
            card.WithoutAI = found.UsedFallback;
            card.AiError = found.AiError;
            if (found.Verdict is not { } verdict)
            {
                card.SamplesSent = 0;
                var text = Tr("🔎 %@: 맞는 테이블 · 컬럼을 찾지 못했어요. 다른 말로 물어보세요.", card.Title);
                if (found.UsedFallback) text += "\n\n" + WithoutAIText(card);
                chat.Edit(messageId, m => m.Text = text);
                LogFinder(target, model, card.Title, null, candidateCount, sampleQueries);
                chat.Finish(gen);
                return;
            }
            card.SamplesSent = found.Sampled.Sum(s => s.Candidate.Columns.Count(c => s.Samples.TryGetValue(c, out var v) && v is { Count: > 0 }));

            // 답 카드 (테이블 설명에는 [맞아요] 를 눌렀을 때만 넣는다)
            var chosen = found.Sampled.FirstOrDefault(s => s.Candidate.Table == verdict.Table);
            var current = new DataFinderAnswer
            {
                Table = verdict.Table, Columns = verdict.Columns, Confidence = verdict.Confidence, Explain = verdict.Explain, Sql = verdict.Sql,
                Samples = chosen?.DisplaySamples, SampleStatus = chosen?.SampleStatus, Source = TableNote.NoteSource.Ai,
            };
            card.Current = current;
            card.Others = found.Sampled.Where(s => !ReferenceEquals(s, chosen)).Select(s => new DataFinderAnswer
            {
                Table = s.Candidate.Table, Columns = s.Candidate.Columns, Explain = s.Candidate.Reason,
                Sql = DataFinder.DefaultSql(s.Candidate.Table, dialect), Samples = s.DisplaySamples, SampleStatus = s.SampleStatus,
                Source = TableNote.NoteSource.Ai,
            }).ToList();
            Assistant.SetFinderCard(messageId, card);
            chat.Edit(messageId, m => m.Text = FinderSummary(card));
            LogFinder(target, model, card.Title, current.Location, candidateCount, sampleQueries);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            await sampler.CloseAsync();
            chat.Edit(messageId, m => m.Text = Tr("(취소했어요)"));
        }
        catch (OperationCanceledException)
        {
            // 운영 접속 확인에서 [취소]
            await sampler.CloseAsync();
            chat.Edit(messageId, m => m.Text = Tr("(취소했어요)"));
        }
        catch (Exception e)
        {
            await sampler.CloseAsync();
            var text = LogError(e, LogSource.Schema, Tr("AI 데이터 찾기 실패"),
                audit: new AuditRecord(AuditAction.Ai, target, Statement: DataFinder.AuditResult(candidateCount, sampleQueries)));
            chat.Edit(messageId, m => { m.Failed = true; m.Text = text; });
        }
        chat.Finish(gen);
    }

    /// 운영 접속이면 접속마다 처음 한 번 묻는다: [이번만] [이 접속은 항상] (앱을 켜 둔 동안) [취소]. 취소면 false.
    private async Task<bool> AllowProdSamplesAsync(ConnectionProfile p)
    {
        if (p.Environment != ConnectionEnvironment.Production || Assistant.ProdSampleAllowed.Contains(p.Id)) return true;
        var choice = Avalonia.Threading.Dispatcher.UIThread.CheckAccess()
            ? await AskProdSamplesAsync(p.Name)
            : await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => AskProdSamplesAsync(p.Name));
        switch (choice)
        {
            case 0: return true;
            case 1:
                Assistant.ProdSampleAllowed.Add(p.Id);
                return true;
            default: return false;
        }
    }

    private Task<int> AskProdSamplesAsync(string profileName) =>
        Dialogs.ChooseAsync(Tr("운영 DB 에서 후보 컬럼 값을 몇 개씩 조회해 로컬 AI 에 보낼까요?"),
                            Tr("%@ · 후보 컬럼마다 최대 20개 값을 이 PC 의 Ollama 에만 보내요. 값은 저장하지 않아요.", profileName),
                            [Tr("이번만"), Tr("이 접속은 항상"), Tr("취소")]);

    /// 고른 모델이 실패했을 때 설치된 다른 모델을 차례로 확인해 답하는 것만 [‹모델› 로 바꾸기] 버튼으로 (추천 모델 먼저)
    private void ProbeAlternatives(Guid messageId, string failing, IReadOnlyList<string> installed, OllamaClient client)
    {
        var others = AISettings.RecommendedFirst(installed.Where(m => m != failing).ToList());
        Fire(async () =>
        {
            foreach (var m in others)
            {
                var h = await ModelProbe.CheckAsync(m, client);
                if (Assistant.FinderCard(messageId) is not { ModelProblem: { } problem } card) return;
                if (h.IsOK) problem.Alternatives.Add(m);
                Assistant.SetFinderCard(messageId, card);
            }
            if (Assistant.FinderCard(messageId) is { ModelProblem: { } last } c)
            {
                last.Checking = false;
                Assistant.SetFinderCard(messageId, c);
            }
        });
    }

    /// [‹모델› 로 바꾸기]: 설정의 모델을 바꾸고 같은 질문을 다시 찾는다
    public void FinderSwitchModel(string model, DataFinderCard card)
    {
        AISettings.Model = model;
        Log(LogLevel.Info, LogSource.Schema, Tr("AI 모델을 %@ 로 바꿨어요", model));
        FindData(card.Question);
    }

    public static string WithoutAIText(DataFinderCard card)
    {
        var s = Tr("AI 없이 이름 · 코멘트 · 값으로 찾은 후보예요");
        if (card.AiError is { } e) s += " (" + Tr("AI 모델 오류: %@", e) + ")";
        return s;
    }

    /// 감사 로그: AI | 접속/DB | 데이터 찾기 · 후보 N · 샘플 조회 M (질문 · 값은 남기지 않는다)
    private void LogFinder(string target, string model, string title, string? result, int candidates, int sampleQueries) =>
        Log(LogLevel.Success, LogSource.Schema,
            result != null ? Tr("AI 데이터 찾기 · %@ → %@ · %@", title, result, model) : Tr("AI 데이터 찾기 · %@ → 못 찾음 · %@", title, model),
            audit: new AuditRecord(AuditAction.Ai, target, DataFinder.AuditResult(candidates, sampleQueries)));

    /// 샘플 조회 하나: 10초 제한, 따로 연 짧은 연결에서 (FinderSampler). 조회 SQL 은 평소처럼 "조회" 감사 줄로 남는다.
    private async Task<DataFinder.SampleOutcome> RunSampleAsync(string sql, FinderSampler sampler, string profileName, string target, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var r = await Bg(() => sampler.RunAsync(sql, ct));
            var ms = (int)sw.Elapsed.TotalMilliseconds;
            var values = DataFinder.CleanSamples(r.Rows.Select(row => row.FirstOrDefault()));
            Log(LogLevel.Info, LogSource.Query, $"[{profileName}] " + Tr("데이터 찾기 샘플 · %@행 · %@ms · %@", r.Rows.Count, ms, OneLine(sql)),
                detail: sql, audit: new AuditRecord(AuditAction.Read, target, $"데이터 찾기 샘플 · {r.Rows.Count}행 · {ms}ms", sql));
            return new DataFinder.SampleOutcome.Values(values);
        }
        catch (SideConnection.TimedOutException)
        {
            Log(LogLevel.Warning, LogSource.Query, $"[{profileName}] " + Tr("데이터 찾기 샘플 · 시간 초과 · %@", OneLine(sql)), detail: sql,
                audit: new AuditRecord(AuditAction.Read, target, $"실패: 데이터 찾기 샘플 시간 초과 ({(int)DataFinder.SampleTimeout}초)", sql));
            return new DataFinder.SampleOutcome.TimedOut();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception e)
        {
            var message = ErrorText.Describe(e).Message;
            Log(LogLevel.Warning, LogSource.Query, $"[{profileName}] " + Tr("데이터 찾기 샘플 · 실패 · %@", OneLine(sql)), detail: sql + "\n" + message,
                audit: new AuditRecord(AuditAction.Read, target, $"실패: 데이터 찾기 샘플 · {message}", sql));
            return new DataFinder.SampleOutcome.Failed(message.Length > 80 ? message[..80] : message);
        }
    }

    /// 테이블 설명 고치기 + 저장
    private void EditDescriptions(DataFinderCard card, Action<TableDescriptionDoc> change)
    {
        TableDescriptions.Edit(card.ProfileId, card.Database, change);
        SaveTableDescriptions();
    }

    // MARK: - 카드 버튼

    /// [데이터 보기]: 새 탭에서 평소 실행 경로로 (LIMIT · 감사 로그 · 히스토리)
    public void FinderViewData(DataFinderCard card)
    {
        if (card.Current is not { } a) return;
        if (AddTab(a.Sql, "🔎 " + card.Title, card.ProfileId) == null) return;
        Fire(() => RunAsync(a.Sql));
    }

    /// [SQL 넣기]: 같은 접속의 지금 탭(없으면 새 탭)에
    public void FinderInsertSql(DataFinderCard card)
    {
        if (card.Current is not { } a) return;
        if (SelectedTab is { } tab && tab.ProfileId == card.ProfileId)
            InsertIntoTab(tab.Id, a.Sql, Tr("AI 데이터 찾기"));
        else
            AddTab(a.Sql + "\n", "🔎 " + card.Title, card.ProfileId);
    }

    /// [맞아요]: 이 질문의 말을 테이블 설명의 그 위치에 찾는 말로 넣는다 (다음엔 AI 없이 바로)
    public void FinderConfirm(Guid messageId)
    {
        if (Assistant.FinderCard(messageId) is not { Current: { } a } card) return;
        if (!a.FromDescriptions)
            EditDescriptions(card, d => d.AddTerms([DataFinder.Phrase(card.Terms)], a.Table, a.Columns));
        a.Confirmed = true;
        UpdateFinderCard(messageId, card);
    }

    /// [아니에요]: 테이블 설명에서 찾은 것이면 그 찾는 말을 빼고, 다음 후보 (없으면 "다른 후보가 없어요")
    public void FinderReject(Guid messageId)
    {
        if (Assistant.FinderCard(messageId) is not { Current: { } a } card) return;
        if (a.FromDescriptions)
            EditDescriptions(card, d => d.RemoveTerms([DataFinder.Phrase(card.Terms), .. card.Terms], a.Table, a.Columns));
        if (card.Others.Count == 0)
        {
            card.Current = null;
        }
        else
        {
            var next = card.Others[0];
            card.Others.RemoveAt(0);
            if (!next.FromDescriptions) next.Confirmed = false;
            card.Current = next;
        }
        UpdateFinderCard(messageId, card);
    }

    private void UpdateFinderCard(Guid messageId, DataFinderCard card)
    {
        Assistant.SetFinderCard(messageId, card);
        var chat = Assistant.Chat(AssistantScope);
        if (chat.Messages.Any(m => m.Id == messageId))
        {
            chat.Edit(messageId, m => m.Text = FinderSummary(card));
            chat.Persist();
        }
    }

    /// 대화 기록에 남는 글 요약 (카드는 앱을 다시 열면 사라지고 이 글만 남는다)
    public static string FinderSummary(DataFinderCard card)
    {
        if (card.ModelProblem is { } m) return $"🔎 **{card.Title}** — " + m.Message;
        if (card.Current is not { } a) return $"🔎 **{card.Title}** — " + Tr("다른 후보가 없어요");
        var s = $"🔎 **{card.Title}** → `{a.Location}`";
        if (a.Confidence is { } c) s += " · " + Tr("신뢰도 %@", c.Label());
        s += " · " + (a.Confirmed ? Tr("확인됨") : Tr("AI · 미확인"));
        if (a.Explain.Length > 0) s += "\n\n" + a.Explain;
        if (card.WithoutAI) s += "\n\n" + WithoutAIText(card);
        return s;
    }
}
