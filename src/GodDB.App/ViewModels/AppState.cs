using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GodDB.App.Services;

namespace GodDB.App.ViewModels;

/// 앱 전체 상태 (Mac AppState.swift). UI 스레드에서만 고친다 (Mac @MainActor 와 같음).
/// DB 작업은 await 하는 동안 백그라운드에서 돌고, 이어지는 코드는 UI 스레드로 돌아온다.
/// 영역별 파일: AppState.Connections / Tabs / Query / Transactions / Schema
public sealed partial class AppState : ObservableObject
{
    private readonly ProfileStore _store;
    private readonly TabStore _tabStore;
    private readonly QueryHistory _history;
    private readonly LogFile? _logFile;

    /// 비밀번호 보관소 (Windows: 자격 증명 관리자 · Mac 개발 실행: 메모리)
    public ISecretStore Secrets { get; }
    /// 저장된 비밀번호를 보기 전 본인 확인 (Windows Hello)
    public IUserVerifier Verifier { get; }
    /// 파일 고르기 · 알림 · 클립보드 (메인 창이 실제 구현을 넣는다)
    public IAppDialogs Dialogs { get; set; } = new NullDialogs();

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    /// 사이드바에서 보고 있는 접속
    [ObservableProperty] private Guid? _selectedProfileId;
    /// 열려 있는 모든 접속 (고칠 때는 UpdateConnection 을 쓴다)
    public Dictionary<Guid, ConnectionInfo> Connections { get; } = [];
    [ObservableProperty] private MainMode _mode = MainMode.Query;
    [ObservableProperty] private string _tableFilter = "";

    /// 처음에는 탭이 없다. 접속하면 그 접속의 쿼리 탭이 생긴다.
    public ObservableCollection<QueryTab> Tabs { get; } = [];
    [ObservableProperty] private Guid? _selectedTabId;
    /// 쿼리 히스토리 창
    [ObservableProperty] private bool _showHistory;
    [ObservableProperty] private int _rowLimit = 500;
    /// "제한 없음" 일 때 보이지 않게 나눠 받는 크기와 메모리에 둘 최대 행 수
    public const int StreamPageSize = 1_000;
    public const int MaxRowsInMemory = 200_000;

    [ObservableProperty] private ConnectionProfile? _editingProfile;
    [ObservableProperty] private bool _editingIsNew;
    [ObservableProperty] private PasswordPrompt? _passwordPrompt;
    [ObservableProperty] private TableMeta? _alteringTable;
    /// CSV 가져오기 대상 테이블
    [ObservableProperty] private TableMeta? _importingTable;
    /// 진행 중이거나 결과를 보고 있는 쿼리 튜닝
    [ObservableProperty] private TuningJob? _tuningJob;
    [ObservableProperty] private TableActionRequest? _tableAction;
    [ObservableProperty] private EditApplyRequest? _editApply;
    /// 시작 화면 (준비 중 진행 막대)
    public StartupState Startup { get; } = new();
    /// 새로운 기능(릴리스 노트) 창, 어느 버전 이후부터 강조할지
    [ObservableProperty] private bool _showReleaseNotes;
    [ObservableProperty] private string? _releaseNotesSince;
    /// 문제 보고 창
    [ObservableProperty] private bool _showProblemReport;
    /// 설정 창 (Windows: Ctrl+,)
    [ObservableProperty] private bool _showSettings;
    /// 커밋하지 않은 트랜잭션 확인 (자동 커밋 켜기 / 연결 끊기)
    [ObservableProperty] private TxPrompt? _txPrompt;
    /// 객체 빠른 찾기 (Ctrl+O)
    [ObservableProperty] private bool _showQuickOpen;
    /// 적용하지 않은 결과 편집을 버리기 전 확인
    [ObservableProperty] private DiscardEditsPrompt? _discardPrompt;
    /// 결과 내보내기 설정 창
    [ObservableProperty] private TabExportRequest? _exportRequest;
    [ObservableProperty] private bool _showExportConnections;
    [ObservableProperty] private ImportRequest? _importRequest;
    /// 접속 그룹 이름 입력창
    [ObservableProperty] private GroupPrompt? _groupPrompt;
    /// 모델링 (논리/물리 모델, 저장된 모델 목록)
    public ModelWorkspace Modeling { get; }
    [ObservableProperty] private bool _showReverseEngineer;
    /// 오른쪽 AI 어시스턴트
    public AssistantState Assistant { get; } = new();
    /// 연결된 Redis 접속의 화면 상태
    public Dictionary<Guid, RedisWorkspace> RedisWorkspaces { get; } = [];
    /// 사이드바에서 고른 테이블 (Ctrl/Shift 클릭으로 여러 개)
    [ObservableProperty] private IReadOnlySet<string> _selectedTables = new HashSet<string>();
    /// 열린 ER 다이어그램 창들
    public Dictionary<Guid, DiagramDoc> Diagrams { get; } = [];
    /// 마지막으로 저장한 내보내기 파일 (탐색기에서 보기)
    [ObservableProperty] private string? _lastExportPath;
    /// 탭별 내보내기 작업 (취소용)
    internal readonly Dictionary<Guid, CancellationTokenSource> ExportTasks = [];

    // 상태창 (로그 패널)
    public ObservableCollection<LogEntry> Logs { get; } = [];
    [ObservableProperty] private bool _showLog;
    [ObservableProperty] private Guid? _selectedLogId;

    /// 저장하지 않기로 한 비밀번호를 앱 실행 중에만 기억
    public Dictionary<Guid, string> SessionPasswords { get; } = [];

    /// 접속 상태 · 스키마가 바뀌었을 때 (사이드바 · 툴바가 다시 그린다). 인자: 바뀐 접속 id
    public event Action<Guid>? ConnectionChanged;
    /// 다른 창을 열어 달라는 요청 (종류, 인자). 예: ("diagram", 다이어그램 id)
    public event Action<string, object?>? WindowRequested;

    /// 앱 종료 확인·설정 창 등에서 쓰는 현재 인스턴스
    public static AppState? Shared { get; private set; }

    public AppState(ProfileStore? store = null, ISecretStore? secrets = null, IUserVerifier? verifier = null,
                    TabStore? tabStore = null, QueryHistory? history = null, LogFile? logFile = null, ModelStore? modelStore = null)
    {
        _store = store ?? new ProfileStore();
        _tabStore = tabStore ?? TabStore.Shared;
        _history = history ?? QueryHistory.Shared;
        _logFile = logFile;
        Secrets = secrets ?? PlatformServices.Secrets;
        Verifier = verifier ?? PlatformServices.Verifier;
        Modeling = new ModelWorkspace(modelStore)
        {
            Dispatcher = a => RunOnUi(a),
        };
        Modeling.OnError = (context, error) => RunOnUi(() => LogError(error, LogSource.Schema, context));
        foreach (var p in _store.Load()) Profiles.Add(p);
        RestoreTabs();
        Tabs.CollectionChanged += OnTabsChanged;
        Shared = this;
    }

    /// 실제 앱: 공용 저장소 + 감사 로그 파일
    public static AppState CreateDefault() => new(logFile: LogFile.Shared);

    // MARK: - 사이드바에서 보고 있는 접속

    private ConnectionInfo Focused =>
        SelectedProfileId is { } id && Connections.TryGetValue(id, out var c) ? c : new ConnectionInfo();

    public ConnectionState State => Focused.State;
    public DbSession? Session => Focused.Session;
    public IReadOnlyList<string> Databases => Focused.Databases;
    public string CurrentDatabase => Focused.CurrentDatabase;
    public IReadOnlyList<TableMeta> Tables => Focused.Tables;
    public IReadOnlyList<RoutineMeta> Routines => Focused.Routines;
    public bool SchemaLoading => Focused.SchemaLoading;

    /// 사이드바에서 보고 있는 접속의 SQL 문법 (연결 전에는 MySQL)
    public ISqlDialect Dialect => Session?.Dialect ?? Dialects.Of(DatabaseKind.Mysql);

    public ConnectionProfile? SelectedProfile => Profiles.FirstOrDefault(p => p.Id == SelectedProfileId);

    public ConnectionProfile? Profile(Guid id) => Profiles.FirstOrDefault(p => p.Id == id);

    public ConnectionState ConnectionState(Guid id) =>
        Connections.TryGetValue(id, out var c) ? c.State : ViewModels.ConnectionState.Disconnected;

    public int ConnectedCount => Connections.Values.Count(c => c.State.IsConnected);

    public IReadOnlyList<TableMeta> FilteredTables
    {
        get
        {
            var f = TableFilter.Trim().ToLowerInvariant();
            return f.Length == 0 ? Tables : Tables.Where(t => t.Name.ToLowerInvariant().Contains(f)).ToList();
        }
    }

    public List<RoutineMeta> FilteredRoutines(RoutineKind kind)
    {
        var f = TableFilter.Trim().ToLowerInvariant();
        return Routines.Where(r => r.Kind == kind && (f.Length == 0 || r.Name.ToLowerInvariant().Contains(f))).ToList();
    }

    public void UpdateConnection(Guid id, Action<ConnectionInfo> change)
    {
        if (!Connections.TryGetValue(id, out var info))
        {
            info = new ConnectionInfo();
            Connections[id] = info;
        }
        change(info);
        NotifyConnection(id);
    }

    /// 접속 정보를 처음 상태로 (Mac: connections[id] = ConnectionInfo())
    private void ResetConnection(Guid id)
    {
        Connections[id] = new ConnectionInfo();
        NotifyConnection(id);
    }

    private void NotifyConnection(Guid id)
    {
        if (id == SelectedProfileId) NotifyFocused();
        OnPropertyChanged(nameof(ConnectedCount));
        ConnectionChanged?.Invoke(id);
    }

    private void NotifyFocused()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(Databases));
        OnPropertyChanged(nameof(CurrentDatabase));
        OnPropertyChanged(nameof(Tables));
        OnPropertyChanged(nameof(FilteredTables));
        OnPropertyChanged(nameof(Routines));
        OnPropertyChanged(nameof(SchemaLoading));
        OnPropertyChanged(nameof(Dialect));
        OnPropertyChanged(nameof(SelectedRedis));
    }

    partial void OnSelectedProfileIdChanged(Guid? value)
    {
        NotifyFocused();
        OnPropertyChanged(nameof(SelectedProfile));
        // Redis 는 스키마가 없어서 스키마 비교를 쓸 수 없다
        if (SelectedProfile?.Kind == DatabaseKind.Redis && Mode == MainMode.Compare) Mode = MainMode.Query;
    }

    partial void OnTableFilterChanged(string value) => OnPropertyChanged(nameof(FilteredTables));

    // MARK: - 상태창

    public LogEntry? LastLog => Logs.Count > 0 ? Logs[^1] : null;
    public int ErrorCount => Logs.Count(e => e.Level == LogLevel.Error);

    public void Log(LogLevel level, LogSource source, string message, string? detail = null, string? hint = null, AuditRecord? audit = null)
    {
        var entry = new LogEntry(level, source, message, detail, hint, audit);
        Logs.Add(entry);
        _logFile?.Append(entry);
        if (Logs.Count > AppLog.MaxEntries)
            for (var n = Logs.Count - AppLog.MaxEntries; n > 0; n--) Logs.RemoveAt(0);
        OnPropertyChanged(nameof(LastLog));
        OnPropertyChanged(nameof(ErrorCount));
        if (level == LogLevel.Error)
        {
            // 오류가 나면 상태창을 자동으로 열고 해당 항목을 선택한다.
            ShowLog = true;
            SelectedLogId = entry.Id;
        }
    }

    /// 감사 로그 파일에만 남긴다 (Mac: LogFile.shared.append — 상태창에는 따로 요약을 남길 때)
    public void AuditOnly(AuditRecord audit, LogSource source = LogSource.Query) =>
        _logFile?.Append(new LogEntry(LogLevel.Success, source, "", Audit: audit));

    /// 오류를 상태창에 남기고, 화면에 보여줄 메시지를 돌려준다.
    public string LogError(Exception error, LogSource source, string context, string? sql = null, AuditRecord? audit = null)
    {
        var d = ErrorText.Describe(error);
        var detail = Tr("원본 오류: %@", d.Raw);
        if (sql != null) detail = Tr("실행한 SQL:\n%@\n\n", sql) + detail;
        // 감사 로그: 같은 구분으로, 결과 칸에 실패 사유
        var record = audit == null ? null : audit with { Result = "실패: " + d.Message };
        Log(LogLevel.Error, source, $"{context}: {d.Message}", detail, d.Hint, record);
        return d.Hint != null ? Tr("%@\n\n해결 방법: %@", d.Message, d.Hint) : d.Message;
    }

    public void ToggleLog() => ShowLog = !ShowLog;

    /// 감사 로그의 대상 칸: "접속이름/DB"
    public string AuditTarget(QueryTab tab)
    {
        var name = Profile(tab.ProfileId)?.Name ?? "?";
        return tab.Database.Length == 0 ? name : $"{name}/{tab.Database}";
    }

    public void ClearLogs()
    {
        Logs.Clear();
        SelectedLogId = null;
        OnPropertyChanged(nameof(LastLog));
        OnPropertyChanged(nameof(ErrorCount));
    }

    // MARK: - 스레드 도우미

    /// UI 스레드에서 실행 (이미 UI 스레드면 바로)
    public static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// DB·파일 작업을 백그라운드 스레드에서 (await 뒤에는 UI 스레드로 돌아온다)
    internal static Task<T> Bg<T>(Func<Task<T>> work) => Task.Run(work);
    internal static Task Bg(Func<Task> work) => Task.Run(work);

    /// Mac 의 Task { … } — 기다리지 않고 실행하되, 예상 못 한 예외는 상태창에 남긴다
    public void Fire(Func<Task> work)
    {
        async Task Wrapped()
        {
            try { await work(); }
            catch (Exception e) { LogError(e, LogSource.App, Tr("작업 실패")); }
        }
        _ = Wrapped();
    }

    /// 다른 창 열기 요청 (메인 창이 받아서 연다)
    public void RequestWindow(string kind, object? argument = null) => WindowRequested?.Invoke(kind, argument);

    public static string OneLine(string sql, int max = 80)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(
            string.Join(" ", sql.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)), @"\s+", " ");
        return s.Length > max ? s[..max] + "…" : s;
    }

    /// 앱 버전 (Mac: CFBundleShortVersionString)
    public static string Version => AppInfo.Version;
}
