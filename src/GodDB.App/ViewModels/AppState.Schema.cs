using GodDB.App.Services;

namespace GodDB.App.ViewModels;

// ER 다이어그램 · 모델링 · 쿼리 튜닝 · AI 어시스턴트 · 시작 화면 · 새로운 기능
public sealed partial class AppState
{
    // MARK: - ER 다이어그램

    /// 고른 테이블들로 다이어그램: 고른 테이블은 모두(관계가 없어도) 보이고,
    /// 관계(외래 키·추정)가 있으면 이어진 테이블도 함께 그린다. 고른 테이블은 보라색으로 구분.
    public Guid? MakeDiagram(IReadOnlySet<string> names)
    {
        var list = DiagramLayout.WithRelated(names, Tables, DiagramLayout.InferredRelations(Tables));
        return MakeDiagram(null, list, names);
    }

    /// 다이어그램 문서를 만들고 창 id 를 돌려준다. focus 가 있으면 그 테이블과 바로 이어진 테이블만.
    public Guid? MakeDiagram(string? focus, IReadOnlyList<TableMeta>? only = null, IReadOnlySet<string>? highlighted = null)
    {
        highlighted ??= new HashSet<string>();
        if (SelectedProfile is not { } profile || Tables.Count == 0)
        {
            Log(LogLevel.Warning, LogSource.Schema, Tr("먼저 접속을 연결하고 스키마를 불러오세요."));
            return null;
        }
        // 추정 관계는 스키마 전체 기준으로 찾는다 (고른 테이블 밖과의 관계는 그리지 않음)
        var inferredAll = DiagramLayout.InferredRelations(Tables);
        List<TableMeta> list;
        string title;
        if (only != null)
        {
            list = [.. only];
            title = $"{profile.Name} / {CurrentDatabase} / " + Tr("선택 %@개", highlighted.Count == 0 ? only.Count : highlighted.Count);
        }
        else if (focus != null)
        {
            list = DiagramLayout.Related(focus, Tables, inferredAll);
            title = $"{profile.Name} / {CurrentDatabase} / {focus}";
        }
        else
        {
            list = [.. Tables];
            title = $"{profile.Name} / {CurrentDatabase}";
        }
        const int limit = 200;
        if (list.Count > limit)
        {
            Log(LogLevel.Warning, LogSource.Schema, Tr("테이블이 %@개라 앞의 %@개만 그려요.", list.Count, limit));
            list = list.Take(limit).ToList();
        }
        var names = list.Select(t => t.Name).ToHashSet();
        var doc = new DiagramDoc
        {
            Title = title,
            Tables = list,
            Focus = focus,
            Highlighted = focus != null ? new HashSet<string> { focus } : highlighted.Where(names.Contains).ToHashSet(),
            Inferred = inferredAll.Where(f => names.Contains(f.Table) && names.Contains(f.ReferencedTable)).ToList(),
            Kind = profile.Kind,
            ChatScope = DiagramChatStore.Scope(profile.Id, CurrentDatabase, list.Select(t => t.Name)),
        };
        Diagrams[doc.Id] = doc;
        var fkCount = list.Sum(t => t.ForeignKeys.Count(f => names.Contains(f.ReferencedTable)));
        Log(LogLevel.Info, LogSource.Schema, Tr("ER 다이어그램 · 테이블 %@개 · 관계 %@개 · 추정 %@개", list.Count, fkCount, doc.Inferred.Count));
        return doc.Id;
    }

    /// 다이어그램 창 열기 (Mac: openWindow(id: "diagram", value: id))
    public void OpenDiagram(Guid? id)
    {
        if (id is { } d) RequestWindow(WindowKinds.Diagram, d);
    }

    // MARK: - 모델링

    /// 리버스 엔지니어링은 연결된 SQL DB 가 있어야 한다
    public bool CanReverseEngineer => State.IsConnected && SelectedProfile?.Kind.IsSql() == true && Tables.Count > 0;

    // MARK: - 쿼리 튜닝

    /// 튜닝을 쓸 수 없는 이유 (쓸 수 있으면 null)
    public string? TuningUnavailableReason => GodDB.Core.Ai.TuningJob.UnavailableReason(SelectedTab != null);

    public void StartTuning(string sql)
    {
        if (SelectedTab is not { } tab || GodDB.Core.Ai.TuningJob.Normalize(sql) is not { } trimmed) return;
        var job = new GodDB.Core.Ai.TuningJob(tab.Id, trimmed, TuningSettings.Model, Profile(tab.ProfileId)?.Name ?? "");
        TuningJob = job;
        if (!Connections.TryGetValue(tab.ProfileId, out var info) || info.Session is not { } session)
        {
            Fire(() => job.RunAsync(null!, tab.Database, [], hooks: TuningHooks(tab)));
            return;
        }
        var known = info.Tables;
        var db = tab.Database;
        var hooks = TuningHooks(tab);
        Fire(() => job.RunAsync(session, db, known, hooks: hooks));
    }

    private GodDB.Core.Ai.TuningJob.Hooks TuningHooks(QueryTab tab) => new()
    {
        Started = (message, prompt) => RunOnUi(() => Log(LogLevel.Info, LogSource.Query, message, detail: prompt)),
        Finished = (message, streamed, auditResult) => RunOnUi(() =>
            Log(LogLevel.Success, LogSource.Query, message, detail: streamed,
                audit: new AuditRecord(AuditAction.Ai, AuditTarget(tab), auditResult))),
        Failed = (e, context) =>
        {
            string? msg = null;
            void Do() => msg = LogError(e is NullReferenceException ? new GodDbException(DbErrorKind.NotConnected) : e, LogSource.Query, context,
                                        audit: new AuditRecord(AuditAction.Ai, AuditTarget(tab), Statement: "쿼리 튜닝"));
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Do();
            else Avalonia.Threading.Dispatcher.UIThread.Invoke(Do);
            return msg ?? e.Message;
        },
    };

    public void CancelTuning() => TuningJob?.Cancel();

    // MARK: - AI 어시스턴트

    /// 지금 화면의 대화 범위
    public string AssistantScope =>
        AssistantAI.Scope(Mode == MainMode.Modeling ? Modeling.SelectedId : null,
                          SelectedProfileId is { } p && State.IsConnected ? p : null, CurrentDatabase);

    public string AssistantTitle =>
        AssistantAI.ScopeTitle(Mode == MainMode.Modeling ? Modeling.Selected?.Name : null,
                               State.IsConnected ? SelectedProfile?.Name : null,
                               SelectedProfile?.Kind == DatabaseKind.Redis, CurrentDatabase);

    /// 자세히 볼 테이블: 질문에 나온 것 + 사이드바에서 고른 것 + 지금 탭의 테이블
    public List<TableMeta> AssistantFocus(string question) =>
        AssistantAI.Focus(question, Tables, SelectedTables,
                          SelectedTab?.Source is TabSource.Table t ? t.Name : null,
                          Assistant.IncludeSql ? SelectedTab?.Text : null);

    /// 테이블 통계 (DB 마다 한 번 가져와 둔다; 실패하면 없이 진행)
    public async Task<Dictionary<string, TableStats>> AssistantStatsAsync()
    {
        if (SelectedProfile is not { } p || Session is not { } session || !State.IsConnected) return [];
        var key = $"{p.Id.ToString().ToUpperInvariant()}|{CurrentDatabase}";
        if (Assistant.Stats.TryGetValue(key, out var cached)) return cached;
        if (AssistantAI.StatsSql(p.Kind, CurrentDatabase) is not { } sql) return [];
        try
        {
            var r = await Bg(() => session.RawAsync(sql));
            var s = AssistantAI.ParseStats(r.Rows);
            Assistant.Stats[key] = s;
            return s;
        }
        catch (Exception e)
        {
            Log(LogLevel.Warning, LogSource.Schema, AssistantAI.StatsFailedMessage, detail: ErrorText.Describe(e).Message);
            Assistant.Stats[key] = [];
            return [];
        }
    }

    /// 지금 앱 상태 (질문 "지금 어디 붙어있어?" 같은 것에 답하도록)
    public string AssistantAppState()
    {
        var model = Mode == MainMode.Modeling ? Modeling.Selected : null;
        var p = SelectedProfile;
        return AssistantAI.AppStateText(new AssistantAI.AppState(
            Version, Mode.RawValue(), Profiles.Count, Profiles.Select(x => x.Group).ToList(),
            p?.Name, p?.Kind ?? DatabaseKind.Mysql, p?.ReadOnly ?? false, State.IsConnected, CurrentDatabase, Tables.Count,
            SelectedTables.ToList(), Tabs.Count, model?.Name, model?.Tables.Count ?? 0, model?.Relations.Count ?? 0));
    }

    public string AssistantGuideText(string question) => AssistantAI.GuideText(question, AssistantAppState());

    /// 질문 하나에 보낼 메시지 (문맥은 질문할 때마다 새로 모은다)
    public async Task<List<OllamaMessage>> AssistantMessagesAsync(IReadOnlyList<(string Role, string Content)> history)
    {
        var english = IsEnglish;
        var question = history.Count > 0 ? history[^1].Content : "";
        var guide = AssistantGuideText(question);
        if (Mode == MainMode.Modeling && Modeling.Selected is { } m)
            return AssistantAI.ModelingMessages(m.Kind, guide, ModelAiBridge.ModelText(m), history, english);
        if (SelectedProfile is not { } p || !State.IsConnected)
            return AssistantAI.Messages(SelectedProfile?.Kind ?? DatabaseKind.Mysql, guide, history, english);
        if (p.Kind == DatabaseKind.Redis)
            return AssistantAI.Messages(DatabaseKind.Redis, AssistantAI.RedisContextText(guide, p.Name, p.RedisMode.Title()), history, english);
        var stats = await AssistantStatsAsync();
        var tab = SelectedTab;
        var ctx = new AssistantAI.Context(p.Kind, p.Name, CurrentDatabase, Tables, AssistantFocus(question), stats,
                                          Assistant.IncludeSql ? tab?.Text : null, Assistant.IncludeSql ? tab?.Error : null);
        return AssistantAI.Messages(p.Kind, guide + "\n" + AssistantAI.ContextText(ctx), history, english);
    }

    /// AI 대화(어시스턴트 · 다이어그램 AI)의 상태창 · 감사 로그 연결 (Mac DiagramChat.ask 의 app.log / logError)
    public AiChat.Hooks AiChatHooks(Func<string, bool, string?>? logMessage = null) => new()
    {
        LogMessage = logMessage,
        Log = (line, q) => RunOnUi(() => Log(LogLevel.Info, LogSource.Schema, line, detail: q)),
        // 감사 로그에는 질문·답변 내용 없이 한 줄 요약만
        Answered = (feature, secs, model) => RunOnUi(() => AuditOnly(new AuditRecord(AuditAction.Ai, Result: $"{feature} 답변 · {secs}초 · {model}"), LogSource.Schema)),
        Failed = (e, context, feature) =>
        {
            string? msg = null;
            void Do() => msg = LogError(e, LogSource.Schema, context, audit: new AuditRecord(AuditAction.Ai, Statement: feature));
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Do();
            else Avalonia.Threading.Dispatcher.UIThread.Invoke(Do);
            return msg ?? e.Message;
        },
    };

    public void AskAssistant(string question)
    {
        var chat = Assistant.Chat(AssistantScope);
        var scopeTitle = AssistantTitle;
        Fire(() => chat.AskAsync(question, (history, _) => AssistantMessagesAsync(history), Tr("AI 어시스턴트 실패"),
            AiChatHooks((model, first) => first ? Tr("AI 어시스턴트 · %@ · %@", model, scopeTitle) : null)));
    }

    // MARK: - 시작 화면 · 새로운 기능

    /// 업데이트 (메인 창이 시작할 때 넣는다. 테스트는 null)
    public UpdateService? Updates { get; set; }

    /// 앱을 켤 때 한 번. 오래 걸릴 수 있는 일(네트워크·Ollama·업데이트)은 시간 제한을 두고, 실패해도 앱은 열린다.
    public async Task RunStartupAsync()
    {
        var st = Startup;
        if (st.Finished || st.Steps.Count > 0) return;
        var plan = new List<(string Title, Func<Task<string?>> Work)>
        {
            (Tr("설정과 접속 목록 불러오는 중"), () => Task.FromResult<string?>(Tr("접속 %@개", Profiles.Count))),
            (Tr("PC · 네트워크 정보 확인 중 (감사 로그)"), async () => { await LogStartupAsync(); return null; }),
            (Tr("AI(Ollama) 연결 확인 중"), async () =>
            {
                if (!TuningSettings.Enabled && !TuningSettings.AssistantEnabled) return Tr("꺼짐");
                var status = await WithTimeout(2.5, ct => TuningSettings.Client.StatusAsync(ct));
                return status switch
                {
                    OllamaStatus.Running r => Tr("모델 %@개", r.Models.Count),
                    OllamaStatus.InstalledNotRunning => Tr("실행 안 됨"),
                    OllamaStatus.NotInstalled => Tr("설치 안 됨"),
                    _ => Tr("응답 없음"),
                };
            }),
            (Tr("모델 · 대화 기록 정리 중"), async () =>
            {
                await Task.Run(() => DiagramChatStore.Shared.PruneAll());
                Modeling.LoadIfNeeded();
                return null;
            }),
            (Tr("업데이트 확인 중"), () =>
            {
                if (Updates is not { } u || !u.Available) return Task.FromResult<string?>(Tr("개발 실행"));
                if (UpdateService.Mode == UpdateMode.Manual) return Task.FromResult<string?>(Tr("수동"));
                Fire(() => CheckForUpdatesAsync(userInitiated: false));
                RestartDailyUpdateCheck();
                return Task.FromResult<string?>(null);
            }),
        };
        foreach (var (title, _) in plan) st.Steps.Add(new StartupState.Step(title));
        for (var i = 0; i < plan.Count; i++)
        {
            st.Current = plan[i].Title;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string? note;
            try { note = await plan[i].Work(); }
            catch (Exception e) { note = ErrorText.Describe(e).Message; }
            // 단계가 순서대로 진행되는 게 보이도록 단계마다 최소 0.15초
            var took = sw.Elapsed.TotalSeconds;
            if (took < 0.15) await Task.Delay(TimeSpan.FromSeconds(0.15 - took));
            st.Steps[i].Done = true;
            st.Steps[i].Note = note;
            st.UpdateProgress();
        }
        await Task.Delay(200);   // 마지막 체크가 보이도록 잠깐
        st.Finished = true;
        ShowReleaseNotesIfUpdated();
    }

    internal static async Task<T?> WithTimeout<T>(double seconds, Func<CancellationToken, Task<T>> work) where T : class
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            var task = Task.Run(() => work(cts.Token));
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
            return done == task && task.IsCompletedSuccessfully ? task.Result : null;
        }
        catch { return null; }
    }

    /// 지금까지의 변경 이력 (지금 버전까지)
    public static IReadOnlyList<ReleaseNotes.Entry> ReleaseNotesEntries => ReleaseNotes.Released(ReleaseNotes.Bundled, Version);

    /// 새 버전을 처음 켰으면 그 사이 변경 내용을 한 번 보여준다
    public void ShowReleaseNotesIfUpdated()
    {
        if (ReleaseNotes.CheckUpdated(Version, ReleaseNotesEntries) is not { } last) return;
        ReleaseNotesSince = last;
        ShowReleaseNotes = true;
    }

    /// 도움말 > 새로운 기능
    public void OpenReleaseNotes()
    {
        ReleaseNotesSince = null;
        ShowReleaseNotes = true;
    }

    /// 앱 시작 감사 로그 (PC·IP 정보)
    public async Task LogStartupAsync()
    {
        var (identity, summary) = await Task.Run(() => (ClientInfo.Identity, ClientInfo.Summary()));
        Log(LogLevel.Info, LogSource.App, Tr("GodDB %@ 시작 · 사용자 %@", Version, identity), detail: summary,
            audit: new AuditRecord(AuditAction.AppStart, Result: $"GodDB {Version}", Statement: string.Join(" · ", summary.Split('\n'))));
    }

    // MARK: - 업데이트 (Mac: Sparkle UpdateController)

    /// 메뉴 "업데이트 확인…" 또는 시작 / 하루 한 번 확인. 새 버전이 있으면 방식에 따라 알리거나 받아서 설치한다.
    public async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (Updates is not { } u) return;
        if (!u.Available)
        {
            if (userInitiated) await Dialogs.AlertAsync(Tr("업데이트 확인…"), Tr("설치된 앱에서만 업데이트를 확인할 수 있어요. (개발 실행)"));
            return;
        }
        string? version;
        try { version = await u.CheckAsync(); }
        catch (Exception e)
        {
            LogError(e, LogSource.App, Tr("업데이트 확인 실패"));
            return;
        }
        if (version == null)
        {
            if (userInitiated) await Dialogs.AlertAsync(Tr("업데이트 확인…"), Tr("지금 최신 버전이에요 (%@).", Version));
            return;
        }
        if (UpdateService.Mode == UpdateMode.Automatic && !userInitiated)
        {
            try { await u.DownloadAsync(); }
            catch (Exception e) { LogError(e, LogSource.App, Tr("업데이트 받기 실패")); return; }
            Log(LogLevel.Info, LogSource.App, Tr("새 버전 %@ 을 받았어요. 종료할 때 설치돼요.", version));
            var choice = await Dialogs.ChooseAsync(Tr("GodDB %@ 을 받았어요", version), Tr("지금 다시 시작해서 설치할까요? 나중에 하면 앱을 종료할 때 설치돼요."),
                                                   [Tr("지금 다시 시작"), Tr("종료할 때 설치")]);
            if (choice == 0) ApplyUpdateAndRestart(version);
            else u.ApplyOnExit();
            return;
        }
        var pick = await Dialogs.ChooseAsync(Tr("새 버전 %@ 이 있어요", version), Tr("지금 받아서 설치할까요? (지금 버전 %@)", Version),
                                             [Tr("받아서 설치"), Tr("나중에")]);
        if (pick != 0) return;
        try { await u.DownloadAsync(); }
        catch (Exception e) { LogError(e, LogSource.App, Tr("업데이트 받기 실패")); return; }
        ApplyUpdateAndRestart(version);
    }

    private Avalonia.Threading.DispatcherTimer? _dailyUpdateTimer;

    /// 하루 한 번 업데이트 확인 (Mac Sparkle 의 자동 확인 주기). 방식이 수동이면 끈다.
    public void RestartDailyUpdateCheck()
    {
        _dailyUpdateTimer?.Stop();
        if (Updates is not { Available: true } || UpdateService.Mode == UpdateMode.Manual) return;
        _dailyUpdateTimer ??= new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _dailyUpdateTimer.Tick -= OnDailyUpdateTick;
        _dailyUpdateTimer.Tick += OnDailyUpdateTick;
        _dailyUpdateTimer.Start();
    }

    private void OnDailyUpdateTick(object? sender, EventArgs e) => Fire(() => CheckForUpdatesAsync(userInitiated: false));

    /// 설정 > AI > AI 어시스턴트 사용 (끄면 도구 막대 버튼과 오른쪽 패널이 사라진다)
    public bool AssistantEnabled => TuningSettings.AssistantEnabled;

    public void NotifyAssistantEnabled() => OnPropertyChanged(nameof(AssistantEnabled));

    private void ApplyUpdateAndRestart(string version)
    {
        Log(LogLevel.Info, LogSource.App, Tr("업데이트 설치 · %@ → %@", Version, version),
            audit: new AuditRecord(AuditAction.Settings, Result: $"업데이트 설치 {Version} → {version}"));
        SaveTabsNow();
        _logFile?.Flush();
        Updates?.ApplyAndRestart();
    }
}

/// AppState.RequestWindow 의 창 종류
public static class WindowKinds
{
    public const string Diagram = "diagram";
    public const string Settings = "settings";
    public const string ReleaseNotes = "release-notes";
}
