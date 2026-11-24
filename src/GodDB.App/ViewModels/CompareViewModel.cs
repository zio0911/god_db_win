using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GodDB.App.ViewModels;

/// 스키마 비교 화면 상태 (Mac CompareView.swift 의 @State 와 compare()).
/// 두 접속의 스키마를 비교하고 보정 SQL 을 만든다. SQL 은 자동 실행하지 않는다.
public sealed partial class CompareViewModel : ObservableObject
{
    public AppState App { get; }

    /// 접속 고르기 항목 (Id 가 null 이면 "선택", IsHeader 면 고를 수 없는 그룹 이름 줄 — Mac Picker 의 Section)
    public sealed record Choice(Guid? Id, string Name, DatabaseKind? Kind = null,
                                ConnectionEnvironment Environment = ConnectionEnvironment.None, string Group = "", bool IsHeader = false)
    {
        /// "이름 · 운영" (환경 표시 안 함이면 이름만) — Mac CompareView.label
        public string Label => Kind == null || Environment == ConnectionEnvironment.None ? Name : $"{Name} · {Environment.Title()}";
        /// 고른 접속의 도움말: 이름 · 환경 · 그룹
        public string Tip => Id == null ? "" : Label + (Group.Length == 0 ? "" : " · " + Group);
        public override string ToString() => Label;
    }

    /// 고른 접속이 없을 때 환경 표시 (숨김)
    public static readonly ConnectionEnvironment NoEnvironment = ConnectionEnvironment.None;

    [ObservableProperty] private Choice? _source;
    [ObservableProperty] private Choice? _target;
    [ObservableProperty] private string _sourceDb = "";
    [ObservableProperty] private string _targetDb = "";
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private SchemaDiffReport? _report;
    [ObservableProperty] private bool _showExtra;
    [ObservableProperty] private SchemaDiffItem? _selection;
    [ObservableProperty] private IReadOnlyList<Choice> _choices = [];

    public CompareViewModel(AppState app)
    {
        App = app;
        RefreshChoices();
        app.Profiles.CollectionChanged += (_, _) => RefreshChoices();
    }

    /// Redis 는 스키마가 없어서 고를 수 없다 (Mac: profiles.filter { $0.kind.isSQL })
    public void RefreshChoices()
    {
        var keepS = Source?.Id;
        var keepT = Target?.Id;
        var list = new List<Choice> { new(null, Tr("선택")) };
        var sql = App.Profiles.Where(p => p.Kind.IsSql()).ToList();
        Choice Item(ConnectionProfile p) => new(p.Id, p.Name, p.Kind, p.Environment, p.Group);
        // 그룹 없는 접속 먼저, 그다음 그룹별 (사이드바와 같은 순서 — Mac #101)
        list.AddRange(sql.Where(p => p.Group.Length == 0).Select(Item));
        foreach (var g in App.ProfileGroups.Where(g => sql.Any(p => p.Group == g)))
        {
            list.Add(new Choice(null, g, IsHeader: true));
            list.AddRange(sql.Where(p => p.Group == g).Select(Item));
        }
        Choices = list;
        Source = list.FirstOrDefault(c => c.Id == keepS && keepS != null) ?? list[0];
        Target = list.FirstOrDefault(c => c.Id == keepT && keepT != null) ?? list[0];
    }

    /// 고를 수 있으면 해당 접속을 고른다 (테스트 · 다른 화면에서)
    public void Select(Guid? source, Guid? target)
    {
        if (source != null) Source = Choices.FirstOrDefault(c => c.Id == source) ?? Source;
        if (target != null) Target = Choices.FirstOrDefault(c => c.Id == target) ?? Target;
    }

    public bool CanCompare => Source?.Id != null && Target?.Id != null && !Running;

    partial void OnSourceChanged(Choice? value)
    {
        if (value is { IsHeader: true }) { Source = Choices[0]; return; }
        CompareStateChanged();
    }

    partial void OnTargetChanged(Choice? value)
    {
        if (value is { IsHeader: true }) { Target = Choices[0]; return; }
        CompareStateChanged();
    }
    partial void OnRunningChanged(bool value)
    {
        CompareStateChanged();
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowResult));
    }
    partial void OnErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowResult));
    }

    private void CompareStateChanged()
    {
        OnPropertyChanged(nameof(CanCompare));
        OnPropertyChanged(nameof(CanRetry));
        CompareCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
    }

    partial void OnReportChanged(SchemaDiffReport? value)
    {
        OnPropertyChanged(nameof(Visible));
        OnPropertyChanged(nameof(HasScript));
        OnPropertyChanged(nameof(MatchedText));
        OnPropertyChanged(nameof(DifferentText));
        OnPropertyChanged(nameof(MissingText));
        OnPropertyChanged(nameof(ExtraText));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowResult));
        OnPropertyChanged(nameof(NoDifferences));
        OnPropertyChanged(nameof(HasItems));
        CopyAllCommand.NotifyCanExecuteChanged();
        OpenInTabCommand.NotifyCanExecuteChanged();
    }

    partial void OnShowExtraChanged(bool value)
    {
        OnPropertyChanged(nameof(Visible));
        OnPropertyChanged(nameof(NoDifferences));
        OnPropertyChanged(nameof(HasItems));
        // 숨긴 항목은 고른 것에서 뺀다
        var visible = Visible.Select(i => i.Id).ToHashSet();
        if (_selectedIds.RemoveWhere(id => !visible.Contains(id)) > 0) SelectionUpdated(fromView: false);
    }

    // MARK: - 고르기 (Mac: Set<SchemaDiffItem.ID> — ⌘ · ⇧ 클릭으로 여러 개, ⌘A 모두)

    private readonly HashSet<string> _selectedIds = [];
    private bool _settingSelection;

    /// 고른 항목들의 Id
    public IReadOnlySet<string> SelectedIds => _selectedIds;
    public int SelectionCount => _selectedIds.Count;

    /// 화면(표)의 고른 것을 바꿔야 할 때 (모두 선택 · 비교 다시 하기 등 화면 밖에서 바뀜)
    public event Action? SelectionReset;

    /// 표에서 고른 항목들 (화면이 부른다)
    public void SetSelection(IEnumerable<SchemaDiffItem> items)
    {
        _selectedIds.Clear();
        foreach (var i in items) _selectedIds.Add(i.Id);
        SelectionUpdated(fromView: true);
    }

    /// Ctrl+A · 우클릭 '모두 선택': 보이는 항목 모두
    public void SelectAll() => SetIds(Visible.Select(i => i.Id));

    private void SetIds(IEnumerable<string> ids)
    {
        _selectedIds.Clear();
        foreach (var id in ids) _selectedIds.Add(id);
        SelectionUpdated(fromView: false);
    }

    /// 하나만 고르기 (기존 단일 선택 호출부 · 테스트). 고른 것이 여러 개면 목록 순서로 첫 항목.
    partial void OnSelectionChanged(SchemaDiffItem? value)
    {
        if (_settingSelection) return;
        SetIds(value == null ? [] : [value.Id]);
    }

    private void SelectionUpdated(bool fromView)
    {
        _settingSelection = true;
        Selection = Visible.FirstOrDefault(i => _selectedIds.Contains(i.Id));
        _settingSelection = false;
        OnPropertyChanged(nameof(SelectedIds));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(SelectedSql));
        OnPropertyChanged(nameof(SqlHeader));
        OnPropertyChanged(nameof(CopySelectedLabel));
        OnPropertyChanged(nameof(ContextCopyLabel));
        OnPropertyChanged(nameof(HasSelectedSql));
        CopySelectedCommand.NotifyCanExecuteChanged();
        if (!fromView) SelectionReset?.Invoke();
    }

    // MARK: - 표시

    public bool ShowError => !Running && Error != null;
    public bool ShowResult => !Running && Error == null && Report != null;
    public bool ShowEmpty => !Running && Error == null && Report == null;

    /// "대상에만 있는 항목" 을 끄면 참고 항목을 숨긴다
    public IReadOnlyList<SchemaDiffItem> Visible =>
        Report?.Items.Where(i => ShowExtra || i.Severity != SchemaDiffSeverity.Extra).ToList() ?? [];

    public bool NoDifferences => ShowResult && Visible.Count == 0;
    public bool HasItems => ShowResult && Visible.Count > 0;
    public bool HasScript => !string.IsNullOrEmpty(Report?.FixScript);

    public string MatchedText => Tr("일치 테이블 %@", Report?.MatchedTables ?? 0);
    public string DifferentText => Tr("다름 %@", Report?.DifferentCount ?? 0);
    public string MissingText => Tr("누락 %@", Report?.MissingCount ?? 0);
    public string ExtraText => Tr("대상에만 있는 항목 %@", Report?.ExtraCount ?? 0);

    /// 고른 항목들의 보정 SQL (목록 순서, 테이블마다 "-- 테이블" 머리줄). 참고용만 골랐으면 빈 문자열
    public string JoinedSql => SchemaDiffReport.FixSql(Visible, _selectedIds);
    public bool HasSelectedSql => JoinedSql.Length > 0;

    /// 오른쪽 보정 SQL 칸
    public string SelectedSql
    {
        get
        {
            var joined = JoinedSql;
            return joined.Length > 0 ? joined
                : _selectedIds.Count == 0 ? Tr("왼쪽에서 항목을 선택하세요.") : Tr("-- 참고용 항목이라 SQL을 만들지 않아요.");
        }
    }

    public string SqlHeader => _selectedIds.Count > 1 ? Tr("보정 SQL · %@개 선택", _selectedIds.Count) : Tr("보정 SQL");
    public string CopySelectedLabel => _selectedIds.Count > 1 ? Tr("선택한 %@개 SQL 복사", _selectedIds.Count) : Tr("이 SQL 복사");
    /// 우클릭 메뉴의 복사 (Mac "복사 (%@개, ⌘C)" → Windows 단축키 표기)
    public string ContextCopyLabel => _selectedIds.Count > 1 ? Tr("복사 (%@개, Ctrl+C)", _selectedIds.Count) : Tr("복사 (Ctrl+C)");

    public static string Icon(SchemaDiffSeverity s) => s switch
    {
        SchemaDiffSeverity.Missing => "✕",
        SchemaDiffSeverity.Different => "⚠",
        _ => "ⓘ",
    };

    // MARK: - 동작

    [RelayCommand(CanExecute = nameof(HasScript))]
    private Task CopyAll() => App.Dialogs.SetClipboardTextAsync(Report?.FixScript ?? "");

    /// 고른 항목들의 보정 SQL 복사 (Ctrl+C · 우클릭 복사 · 아래 버튼)
    [RelayCommand(CanExecute = nameof(HasSelectedSql))]
    private Task CopySelected()
    {
        var text = JoinedSql;
        return text.Length == 0 ? Task.CompletedTask : App.Dialogs.SetClipboardTextAsync(text);
    }

    /// 실행하지 않고 새 쿼리 탭에 연다 (검토 후 직접 실행)
    [RelayCommand(CanExecute = nameof(HasScript))]
    private void OpenInTab()
    {
        if (Report is not { } r || r.FixScript.Length == 0) return;
        // Mac app.addTab: 연결된 접속이 없으면 상태창에 안내만 남기고 비교 화면에 그대로 있는다 (AddTab 이 성공해야 쿼리 모드로)
        App.AddTab(Tr("-- 스키마 보정 SQL (실행 전 반드시 검토하세요)\n\n") + r.FixScript);
    }

    private CancellationTokenSource? _cts;

    /// 읽는 중 [취소]
    [RelayCommand]
    public void Cancel() => _cts?.Cancel();

    /// 오류 화면의 [다시 시도] (Mac startCompare)
    public bool CanRetry => Source?.Id != null && Target?.Id != null;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task Retry() => CompareAsync();

    /// 접속 하나를 연다. 서버가 응답하지 않으면 드라이버의 연결 시간 제한(15초)에 걸려 실패한다.
    /// [취소]는 바로 돌아온다: MySqlConnector 는 서버 인사말을 기다리는 중에는 취소를 받지 않아(시간 제한까지 기다림)
    /// 기다림만 끊고, 늦게 열린 연결은 뒤에서 닫는다 (Mac 은 Task 취소로 바로 끝남)
    private static async Task<(DbSession? Session, Exception? Error)> OpenAsync(ConnectionProfile p, string? password, CancellationToken ct)
    {
        var connect = Task.Run(() => DbSession.ConnectAsync(p, password, ct));
        try { return (await connect.WaitAsync(ct), null); }
        catch (OperationCanceledException e) when (ct.IsCancellationRequested)
        {
            _ = connect.ContinueWith(t => { if (t.IsCompletedSuccessfully) _ = t.Result.CloseAsync(); }, TaskScheduler.Default);
            return (null, e);
        }
        catch (Exception e) { return (null, e); }
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    public async Task CompareAsync()
    {
        if (Running) return;
        if (App.Profiles.FirstOrDefault(p => p.Id == Source?.Id) is not { } s
            || App.Profiles.FirstOrDefault(p => p.Id == Target?.Id) is not { } t) return;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        Running = true;
        Error = null;
        Report = null;
        SetIds([]);
        DbSession? ss = null, ts = null;
        try
        {
            // 두 접속을 함께 열고, 한쪽만 실패해도 열린 쪽은 닫는다. 어느 쪽이 실패했는지 알려 준다.
            var sTask = OpenAsync(s, App.Password(s), ct);
            var tTask = OpenAsync(t, App.Password(t), ct);
            var (sr, tr) = (await sTask, await tTask);
            ss = sr.Session;
            ts = tr.Session;
            if (ss == null || ts == null)
            {
                if (ct.IsCancellationRequested)
                {
                    App.Log(LogLevel.Info, LogSource.Compare, Tr("스키마 비교를 취소했어요."));
                    return;
                }
                var messages = new List<string>();
                if (sr.Error is { } se)
                {
                    var context = Tr("기준 접속 %@ 연결 실패", s.Name);
                    messages.Add(context + "\n" + App.LogError(se, LogSource.Compare, context));
                }
                if (tr.Error is { } te)
                {
                    var context = Tr("대상 접속 %@ 연결 실패", t.Name);
                    messages.Add(context + "\n" + App.LogError(te, LogSource.Compare, context));
                }
                Error = string.Join("\n\n", messages);
                return;
            }

            // PostgreSQL 은 DB 가 아니라 스키마를 비교한다 (기본 public)
            var sdb = SourceDb.Length == 0 ? (s.Kind == DatabaseKind.Postgres ? "public" : s.Database) : SourceDb;
            var tdb = TargetDb.Length == 0 ? (t.Kind == DatabaseKind.Postgres ? "public" : t.Database) : TargetDb;
            if (sdb.Length == 0 || tdb.Length == 0)
            {
                Error = Tr("비교할 DB 이름을 입력하거나 접속의 기본 DB를 설정해 주세요.");
                return;
            }
            // 보정 SQL 은 대상 DB 문법으로 만든다. 종류가 다르면 MySQL 의 SHOW CREATE 결과는 쓰지 않는다.
            if (s.Kind != t.Kind)
                App.Log(LogLevel.Warning, LogSource.Compare,
                        Tr("기준(%@)과 대상(%@) DB 종류가 달라요. 타입 이름 차이로 '다름'이 많이 나올 수 있어요.", s.Kind.Title(), t.Kind.Title()));
            var src = ss;
            var dst = ts;
            var result = await Task.Run(() => SchemaDiff.CompareAsync(src, sdb, dst, tdb, ct), ct).WaitAsync(ct);
            Report = result;
            SetIds([]);
            App.Log(LogLevel.Success, LogSource.Compare,
                    Tr("%@/%@ → %@/%@ 비교 완료 · 누락 %@, 다름 %@", s.Name, sdb, t.Name, tdb, result.MissingCount, result.DifferentCount),
                    audit: new AuditRecord(AuditAction.Compare, $"{s.Name}/{sdb} → {t.Name}/{tdb}",
                                           $"누락 {result.MissingCount}, 다름 {result.DifferentCount}"));
        }
        catch (Exception e)
        {
            if (ct.IsCancellationRequested || e is OperationCanceledException)
            {
                App.Log(LogLevel.Info, LogSource.Compare, Tr("스키마 비교를 취소했어요."));
                return;
            }
            Error = App.LogError(e, LogSource.Compare, Tr("스키마 비교 실패"));
        }
        finally
        {
            _cts = null;
            Running = false;
            var a = ss;
            var b = ts;
            _ = Task.Run(async () =>
            {
                if (a != null) await a.CloseAsync();
                if (b != null) await b.CloseAsync();
            });
        }
    }
}
