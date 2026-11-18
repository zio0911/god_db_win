using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using Avalonia.Threading;
using GodDB.App.Services;

namespace GodDB.App.ViewModels;

// 쿼리 탭 · 히스토리 · 탭 자동 저장 · .sql 파일 · 빠른 찾기
public sealed partial class AppState
{
    public int SelectedTabIndex
    {
        get
        {
            for (var i = 0; i < Tabs.Count; i++) if (Tabs[i].Id == SelectedTabId) return i;
            return -1;
        }
    }

    public QueryTab? SelectedTab => Tabs.FirstOrDefault(t => t.Id == SelectedTabId);

    public QueryTab? Tab(Guid id) => Tabs.FirstOrDefault(t => t.Id == id);

    partial void OnSelectedTabIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(SelectedTabIndex));
        OnPropertyChanged(nameof(CanSaveQueryTab));
        ScheduleTabSave();
    }

    partial void OnModeChanged(MainMode value) => OnPropertyChanged(nameof(CanSaveQueryTab));

    /// 새 쿼리 탭. 기본은 사이드바에서 보고 있는 접속 / DB.
    public Guid? AddTab(string text = "", string? title = null, Guid? profileId = null)
    {
        if ((profileId ?? SelectedProfileId) is not { } id || !ConnectionState(id).IsConnected)
        {
            Log(LogLevel.Warning, LogSource.App, Tr("먼저 왼쪽에서 접속을 연결해 주세요. 쿼리 탭은 연결된 접속 기준으로 만들어져요."));
            return null;
        }
        var number = Tabs.Where(t => t.ProfileId == id).Select(t => t.Number).DefaultIfEmpty(0).Max() + 1;
        var tab = new QueryTab(id, Connections.TryGetValue(id, out var c) ? c.CurrentDatabase : "", number, title, text);
        Tabs.Add(tab);
        SelectedTabId = tab.Id;
        SelectedProfileId = id;
        Mode = MainMode.Query;
        return tab.Id;
    }

    /// 탭을 고르면 사이드바도 그 탭의 접속 / DB 로 맞춘다.
    public void SelectTab(Guid id)
    {
        if (Tab(id) is not { } tab) return;
        SelectedTabId = id;
        SelectedProfileId = tab.ProfileId;
        Mode = MainMode.Query;
        if (Connections.TryGetValue(tab.ProfileId, out var info) && info.State.IsConnected
            && tab.Database.Length > 0 && info.CurrentDatabase != tab.Database)
            Fire(() => SelectDatabaseAsync(tab.Database, tab.ProfileId));
    }

    public void CloseTab(Guid id)
    {
        var i = -1;
        for (var n = 0; n < Tabs.Count; n++) if (Tabs[n].Id == id) { i = n; break; }
        if (i < 0) return;
        Tabs.RemoveAt(i);
        if (SelectedTabId == id)
        {
            var next = i < Tabs.Count ? Tabs[i] : Tabs.LastOrDefault();
            if (next != null) SelectTab(next.Id);
            else SelectedTabId = null;
        }
    }

    public void OpenTableQuery(string table)
    {
        var sql = Dialect.SelectAllSql(table);
        if (SelectedTab is { } tab && tab.ProfileId == SelectedProfileId && tab.Text.Trim().Length == 0)
            tab.Text = sql;
        else if (AddTab(sql) == null)
            return;
        Mode = MainMode.Query;
        Fire(() => RunAsync(sql));
    }

    public async Task OpenTableDdlAsync(TableMeta table)
    {
        if (Session is not { } session) return;
        try
        {
            var ddl = await Bg(() => session.TableDdlAsync(table));
            if (AddTab(ddl + "\n", $"DDL · {table.Name}") is { } id) Update(id, t => t.Source = new TabSource.Table(table.Name));
            Log(LogLevel.Info, LogSource.Schema, Tr("%@ CREATE 문 열기", table.Name));
        }
        catch (Exception e)
        {
            LogError(e, LogSource.Schema, Tr("%@ CREATE 문 조회 실패", table.Name));
        }
    }

    /// 뷰/프로시저/함수/트리거 정의를 새 탭에 연다.
    public async Task OpenRoutineAsync(RoutineMeta routine)
    {
        if (Session is not { } session) return;
        try
        {
            var body = await Bg(() => session.DefinitionAsync(routine)) ?? "";
            if (AddTab(body + "\n", $"{routine.Kind.Title()} · {routine.Name}") is { } id)
                Update(id, t => t.Source = new TabSource.Routine(routine));
            Log(LogLevel.Info, LogSource.Schema, Tr("%@ %@ 정의 열기", routine.Kind.Title(), routine.Name));
        }
        catch (Exception e)
        {
            LogError(e, LogSource.Schema, Tr("%@ %@ 정의 조회 실패", routine.Kind.Title(), routine.Name));
        }
    }

    /// 인덱스 상세 (카디널리티 등)
    public void OpenIndexes(string table)
    {
        var sql = Dialect.IndexDetailSql(table, CurrentDatabase);
        if (AddTab(sql, Tr("인덱스 · %@", table)) == null) return;
        Fire(() => RunAsync(sql));
    }

    /// 프로시저 CALL/EXEC 틀을 새 탭에
    public void OpenCallTemplate(RoutineMeta routine) =>
        AddTab(Dialect.CallSql(routine.Name), Tr("호출 · %@", routine.Name));

    public void Update(Guid id, Action<QueryTab> change)
    {
        if (Tab(id) is { } tab) change(tab);
    }

    /// 탭들에 적용하지 않은 편집이 있으면 확인을 받고, 없으면 바로 실행한다
    public void ConfirmDiscardingEdits(IReadOnlyCollection<Guid> tabIds, Action action)
    {
        var n = Tabs.Where(t => tabIds.Contains(t.Id)).Sum(t => t.Edits.Count);
        if (n == 0) action();
        else DiscardPrompt = new DiscardEditsPrompt(n, action);
    }

    /// 확인 창에서 [버리고 계속]
    public void ResolveDiscardPrompt(bool discard)
    {
        var prompt = DiscardPrompt;
        DiscardPrompt = null;
        if (discard) prompt?.Action();
    }

    /// 탭 닫기 (적용하지 않은 편집이 있으면 먼저 묻는다)
    public void RequestCloseTabs(IReadOnlyList<Guid> ids) =>
        ConfirmDiscardingEdits(ids, () => { foreach (var id in ids) CloseTab(id); });

    /// Ctrl+W: 메인 창이 앞에 있고 쿼리 탭을 보고 있으면 그 탭을 닫는다(편집 확인 포함).
    /// 탭을 닫았으면 true, 아니면 false (그때는 부른 쪽이 앞에 있는 창을 닫는다).
    public bool CloseTabOrWindow(bool mainWindowIsKey)
    {
        if (mainWindowIsKey && Mode == MainMode.Query && SelectedRedis == null && SelectedTabId is { } id)
        {
            RequestCloseTabs([id]);
            return true;
        }
        return false;
    }

    // MARK: - 쿼리 히스토리

    public void RecordHistory(QueryTab tab, string sql, int ms, bool ok, string? summary)
    {
        var name = Profile(tab.ProfileId)?.Name ?? "?";
        var entry = new HistoryEntry(tab.ProfileId, name, tab.Database, sql, ms, ok, summary);
        var history = _history;
        _ = Task.Run(() => history.Append(entry));
    }

    /// 히스토리 저장소 (히스토리 창이 읽는다)
    public QueryHistory History => _history;

    /// 히스토리의 SQL 을 편집기로: 보고 있는 탭이 같은 접속이면 끝에 덧붙이고, 아니면 그 접속의 새 탭으로
    public async Task OpenFromHistoryAsync(HistoryEntry entry)
    {
        if (SelectedTab is { } tab && tab.ProfileId == entry.ProfileId)
        {
            var text = tab.Text;
            if (text.Length > 0 && !text.EndsWith('\n')) text += "\n";
            tab.Text = text + (text.Length == 0 ? "" : "\n") + entry.Sql + (entry.Sql.EndsWith(';') ? "" : ";") + "\n";
            Mode = MainMode.Query;
        }
        else if (ConnectionState(entry.ProfileId).IsConnected)
        {
            AddTab(entry.Sql, profileId: entry.ProfileId);
        }
        else
        {
            await Dialogs.SetClipboardTextAsync(entry.Sql);
            Log(LogLevel.Info, LogSource.App, Tr("%@ 접속이 연결되어 있지 않아 SQL 을 클립보드에 복사했어요.", entry.ProfileName));
        }
    }

    // MARK: - 탭 자동 저장

    private DispatcherTimer? _tabSaveTimer;
    private bool _restoring;

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (QueryTab t in e.OldItems) t.PropertyChanged -= OnTabPropertyChanged;
        if (e.NewItems != null) foreach (QueryTab t in e.NewItems) t.PropertyChanged += OnTabPropertyChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset) foreach (var t in Tabs) { t.PropertyChanged -= OnTabPropertyChanged; t.PropertyChanged += OnTabPropertyChanged; }
        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(SelectedTabIndex));
        OnPropertyChanged(nameof(CanSaveQueryTab));
        ScheduleTabSave();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueryTab.Text) or nameof(QueryTab.Database) or nameof(QueryTab.CustomTitle) or nameof(QueryTab.Number))
            ScheduleTabSave();
    }

    /// 탭 내용이 바뀌면 1초 뒤에 저장한다 (앱을 다시 켜도 남게)
    public void ScheduleTabSave()
    {
        if (_restoring) return;
        if (_tabSaveTimer == null)
        {
            _tabSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tabSaveTimer.Tick += (_, _) => SaveTabsNow();
        }
        _tabSaveTimer.Stop();
        _tabSaveTimer.Start();
    }

    /// 저장 대기 중인지 (테스트용)
    public bool TabSavePending => _tabSaveTimer?.IsEnabled == true;

    public void SaveTabsNow()
    {
        _tabSaveTimer?.Stop();
        var saved = Tabs.Select(t => new SavedTab(t.ProfileId, t.Database, t.Number, t.CustomTitle, t.Text, t.Id == SelectedTabId)).ToList();
        _tabStore.Save(saved);
    }

    /// 지난번 탭을 되살린다 (연결은 하지 않음 — 접속을 연결하면 그 탭에서 바로 실행할 수 있다)
    public void RestoreTabs()
    {
        _restoring = true;
        try
        {
            var known = Profiles.Select(p => p.Id).ToHashSet();
            var restored = new List<QueryTab>();
            Guid? selected = null;
            foreach (var t in _tabStore.Load().Where(t => known.Contains(t.ProfileId)))
            {
                var tab = new QueryTab(t.ProfileId, t.Database, t.Number, t.Title, t.Text);
                restored.Add(tab);
                if (t.Selected) selected = tab.Id;
            }
            foreach (var t in Tabs) t.PropertyChanged -= OnTabPropertyChanged;
            Tabs.Clear();
            foreach (var t in restored) { Tabs.Add(t); t.PropertyChanged -= OnTabPropertyChanged; t.PropertyChanged += OnTabPropertyChanged; }
            SelectedTabId = selected ?? restored.LastOrDefault()?.Id;
        }
        finally
        {
            _restoring = false;
            _tabSaveTimer?.Stop();
        }
    }

    // MARK: - .sql 파일 (열기 Ctrl+Shift+O · 저장 Ctrl+S · 다른 이름으로 저장 · 최근 파일 · 끌어 놓기)

    public static readonly FileFilter SqlFilter = new("SQL", ["*.sql"]);
    private static readonly FileFilter TextFilter = new(Tr("텍스트"), ["*.txt", "*.*"]);

    public List<string> RecentSqlFiles => GodDB.Core.Sql.RecentSqlFiles.Paths();

    public void NoteRecent(string path)
    {
        GodDB.Core.Sql.RecentSqlFiles.Note(path);
        OnPropertyChanged(nameof(RecentSqlFiles));
    }

    public void ClearRecentSqlFiles()
    {
        GodDB.Core.Sql.RecentSqlFiles.Clear();
        OnPropertyChanged(nameof(RecentSqlFiles));
    }

    public async Task OpenSqlFilePanelAsync()
    {
        var files = await Dialogs.OpenFilesAsync(Tr("SQL 파일 열기…"), [SqlFilter, TextFilter], multiple: true);
        foreach (var f in files) OpenSqlFile(f);
    }

    /// 보고 있는 접속의 새 탭으로 연다 (이미 연 파일이면 그 탭으로)
    public void OpenSqlFile(string path)
    {
        if (Tabs.FirstOrDefault(t => t.FilePath == path) is { } open)
        {
            SelectTab(open.Id);
            return;
        }
        string text;
        try
        {
            // BOM 이 있으면 그 인코딩, 없으면 UTF-8 (Mac: usedEncoding)
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception e)
        {
            LogError(e, LogSource.App, Tr("%@ 파일을 열지 못했어요", Path.GetFileName(path)));
            return;
        }
        if (AddTab(text, Path.GetFileName(path)) is not { } id) return;
        Update(id, t =>
        {
            t.FilePath = path;
            t.SavedText = text;
        });
        NoteRecent(path);
        Log(LogLevel.Info, LogSource.App, Tr("%@ 파일을 열었어요", Path.GetFileName(path)));
    }

    /// 파일에 저장. 처음이거나 saveAs 면 저장 위치를 묻는다.
    public async Task SaveSelectedTabAsync(bool saveAs = false)
    {
        if (SelectedTab is not { } tab) return;
        var path = tab.FilePath;
        if (path == null || saveAs)
        {
            var name = (tab.FilePath != null ? Path.GetFileNameWithoutExtension(tab.FilePath) : tab.Title) + ".sql";
            path = await Dialogs.SaveFileAsync(Tr("다른 이름으로 저장…"), name, [SqlFilter]);
            if (path == null) return;
        }
        try
        {
            var text = tab.Text;
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
            Update(tab.Id, t =>
            {
                t.FilePath = path;
                t.SavedText = text;
                t.CustomTitle = Path.GetFileName(path);
            });
            NoteRecent(path);
            Log(LogLevel.Success, LogSource.App, Tr("%@ 에 저장했어요", Path.GetFileName(path)));
        }
        catch (Exception e)
        {
            LogError(e, LogSource.App, Tr("%@ 파일을 저장하지 못했어요", Path.GetFileName(path)));
        }
    }

    /// 저장 메뉴를 쓸 수 있는지 (Redis 화면의 Ctrl+S 와 겹치지 않게 쿼리 탭일 때만)
    public bool CanSaveQueryTab => Mode == MainMode.Query && SelectedRedis == null && SelectedTabIndex >= 0;

    // MARK: - 객체 빠른 찾기 (Ctrl+O)

    public async Task OpenQuickItemAsync(QuickOpen.Item item)
    {
        if (item.Kind == QuickOpen.Kind.Database)
        {
            await SelectDatabaseAsync(item.Name);
            return;
        }
        if (item.Schema != CurrentDatabase && item.Schema.Length > 0) await SelectDatabaseAsync(item.Schema);
        switch (item.Kind)
        {
            case QuickOpen.Kind.Table or QuickOpen.Kind.View:
                OpenTableQuery(item.Name);
                break;
            case QuickOpen.Kind.Column:
                OpenTableQuery(item.Detail);
                break;
            case QuickOpen.Kind.Procedure or QuickOpen.Kind.Function or QuickOpen.Kind.Trigger:
                if (Routines.FirstOrDefault(r => r.Name == item.Name) is { } r) await OpenRoutineAsync(r);
                break;
        }
    }

    /// 빠른 찾기를 열 수 있는지 (연결된 SQL 접속)
    public bool CanQuickOpen => State.IsConnected && SelectedProfile?.Kind != DatabaseKind.Redis;
}
