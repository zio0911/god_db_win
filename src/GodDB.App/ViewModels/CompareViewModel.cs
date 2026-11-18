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
        CompareCommand.NotifyCanExecuteChanged();
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
        if (Selection != null && !Visible.Contains(Selection)) Selection = null;
    }

    partial void OnSelectionChanged(SchemaDiffItem? value) => OnPropertyChanged(nameof(SelectedSql));

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

    /// 오른쪽 보정 SQL 칸
    public string SelectedSql => Selection?.FixSql
        ?? (Selection == null ? Tr("왼쪽에서 항목을 선택하세요.") : Tr("-- 참고용 항목이라 SQL을 만들지 않아요."));

    public static string Icon(SchemaDiffSeverity s) => s switch
    {
        SchemaDiffSeverity.Missing => "✕",
        SchemaDiffSeverity.Different => "⚠",
        _ => "ⓘ",
    };

    // MARK: - 동작

    [RelayCommand(CanExecute = nameof(HasScript))]
    private Task CopyAll() => App.Dialogs.SetClipboardTextAsync(Report?.FixScript ?? "");

    [RelayCommand]
    private Task CopySelected() => App.Dialogs.SetClipboardTextAsync(SelectedSql);

    /// 실행하지 않고 새 쿼리 탭에 연다 (검토 후 직접 실행)
    [RelayCommand(CanExecute = nameof(HasScript))]
    private void OpenInTab()
    {
        if (Report is not { } r || r.FixScript.Length == 0) return;
        // Mac app.addTab: 연결된 접속이 없으면 상태창에 안내만 남기고 비교 화면에 그대로 있는다 (AddTab 이 성공해야 쿼리 모드로)
        App.AddTab(Tr("-- 스키마 보정 SQL (실행 전 반드시 검토하세요)\n\n") + r.FixScript);
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    public async Task CompareAsync()
    {
        if (App.Profiles.FirstOrDefault(p => p.Id == Source?.Id) is not { } s
            || App.Profiles.FirstOrDefault(p => p.Id == Target?.Id) is not { } t) return;
        Running = true;
        Error = null;
        Report = null;
        DbSession? ss = null, ts = null;
        try
        {
            var sp = App.Password(s);
            var tp = App.Password(t);
            var sTask = Task.Run(() => DbSession.ConnectAsync(s, sp));
            var tTask = Task.Run(() => DbSession.ConnectAsync(t, tp));
            try { await Task.WhenAll(sTask, tTask); }
            catch
            {
                // 한쪽만 열렸으면 닫는다
                if (sTask.IsCompletedSuccessfully) ss = sTask.Result;
                if (tTask.IsCompletedSuccessfully) ts = tTask.Result;
                throw;
            }
            ss = sTask.Result;
            ts = tTask.Result;

            // PostgreSQL 은 DB 가 아니라 스키마를 비교한다 (기본 public)
            var sdb = SourceDb.Length == 0 ? (s.Kind == DatabaseKind.Postgres ? "public" : s.Database) : SourceDb;
            var tdb = TargetDb.Length == 0 ? (t.Kind == DatabaseKind.Postgres ? "public" : t.Database) : TargetDb;
            if (sdb.Length == 0 || tdb.Length == 0)
            {
                Error = Tr("비교할 DB 이름을 입력하거나 접속의 기본 DB를 설정해 주세요.");
                return;
            }
            var src = ss;
            var dst = ts;
            await Task.Run(() => src.UseAsync(sdb));
            var source = await Task.Run(() => src.LoadSchemaAsync(sdb));
            var target = await Task.Run(() => dst.LoadSchemaAsync(tdb));

            var targetNames = target.Select(x => x.Name.ToLowerInvariant()).ToHashSet();
            var creates = new Dictionary<string, string>();
            foreach (var table in source.Where(x => !targetNames.Contains(x.Name.ToLowerInvariant())))
                if (await Task.Run(() => src.ShowCreateTableAsync(table.Name)) is { } ddl) creates[table.Name] = ddl;
            // 보정 SQL 은 대상 DB 문법으로 만든다. 종류가 다르면 MySQL 의 SHOW CREATE 결과는 쓰지 않는다.
            if (s.Kind != t.Kind)
            {
                creates.Clear();
                App.Log(LogLevel.Warning, LogSource.Compare,
                        Tr("기준(%@)과 대상(%@) DB 종류가 달라요. 타입 이름 차이로 '다름'이 많이 나올 수 있어요.", s.Kind.Title(), t.Kind.Title()));
            }
            var result = SchemaDiff.Compare(source, target, creates, dst.Dialect);
            Report = result;
            Selection = null;
            App.Log(LogLevel.Success, LogSource.Compare,
                    Tr("%@/%@ → %@/%@ 비교 완료 · 누락 %@, 다름 %@", s.Name, sdb, t.Name, tdb, result.MissingCount, result.DifferentCount),
                    audit: new AuditRecord(AuditAction.Compare, $"{s.Name}/{sdb} → {t.Name}/{tdb}",
                                           $"누락 {result.MissingCount}, 다름 {result.DifferentCount}"));
        }
        catch (Exception e)
        {
            Error = App.LogError(e, LogSource.Compare, Tr("스키마 비교 실패"));
        }
        finally
        {
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
