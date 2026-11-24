using System.ComponentModel;
using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.Services;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 메인 창. 화면 배치 · 메뉴 · 단축키 · AppState 의 창 요청(시트)을 맡는다. 화면 내용은 각 View 가 그린다.
public partial class MainWindow : Window
{
    public AppState App { get; }
    public SidebarViewModel SidebarVm { get; }

    private QueryWorkspaceView? _queryView;
    private CompareView? _compareView;
    private ModelingView? _modelingView;
    private readonly Dictionary<Guid, RedisWorkspaceView> _redisViews = [];
    private SettingsWindow? _settings;
    private readonly Dictionary<string, Window> _sheets = [];
    private bool _closingConfirmed;

    public MainWindow() : this(AppState.CreateDefault()) { }

    public MainWindow(AppState app)
    {
        App = app;
        InitializeComponent();
        DataContext = app;
        app.Dialogs = new WindowDialogs(this);
        SidebarVm = new SidebarViewModel(app);
        Sidebar.DataContext = SidebarVm;
        Assistant.DataContext = app;
        LogPanel.DataContext = app;

        ModeQuery.Click += (_, _) => SetMode(MainMode.Query);
        ModeCompare.Click += (_, _) => SetMode(MainMode.Compare);
        ModeModeling.Click += (_, _) => SetMode(MainMode.Modeling);
        AssistantToggle.Click += (_, _) => app.Assistant.Visible = !app.Assistant.Visible;
        LogToggle.Click += (_, e) => { app.ToggleLog(); e.Handled = true; };
        StatusBar.Tapped += (_, _) => app.ToggleLog();

        app.PropertyChanged += OnAppChanged;
        app.Assistant.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AssistantState.Visible)) UpdateAssistant();
            // 레이아웃 초기화 등으로 폭이 바뀌면 열린 패널에 바로 반영
            else if (e.PropertyName == nameof(AssistantState.Width) && Assistant.IsVisible)
                Main.ColumnDefinitions[2].Width = new GridLength(Math.Clamp(app.Assistant.Width, 320, 760));
        };
        // 끌어서 바꾼 상태창 높이 · AI 어시스턴트 폭은 저장해서 다음에도 쓴다 (Mac Layout)
        Layout.SaveOnDrag(LogSplitter, Right.RowDefinitions[2], Layout.LogHeight);
        AssistantSplitter.DragCompleted += (_, _) =>
        {
            if (Main.ColumnDefinitions[2].ActualWidth > 0) app.Assistant.Width = Main.ColumnDefinitions[2].ActualWidth;
        };
        Layout.OnReset(this, () => { if (App.ShowLog) Right.RowDefinitions[2].Height = new GridLength(Layout.Get(Layout.LogHeight)); });
        app.ConnectionChanged += _ => { UpdateBadge(); UpdateContent(); UpdateModeButtons(); };
        app.WindowRequested += OnWindowRequested;
        L10n.LanguageChanged += () => Dispatcher.UIThread.Post(() =>
        {
            UpdateBadge(); UpdateStatus(); BuildMenu();
            // Mac 처럼 화면 전체를 새 언어로 다시 그린다 (코드로 만든 화면은 만들 때 한 번 번역되므로 새로 만든다).
            // 접속 · 탭 · 입력 중인 SQL 은 AppState 에 있어 그대로 남는다.
            _queryView = null; _compareView = null; _modelingView = null; _redisViews.Clear();
            ContentHost.Content = null;
            UpdateContent();
        });

        BuildMenu();
        UpdateBadge();
        UpdateModeButtons();
        UpdateContent();
        UpdateLog();
        UpdateAssistant();
        UpdateStatus();
        Splash.IsVisible = !app.Startup.Finished;
        app.Startup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StartupState.Finished) && app.Startup.Finished)
            {
                Splash.Opacity = 0;
                DispatcherTimer.RunOnce(() => Splash.IsVisible = false, TimeSpan.FromMilliseconds(360));
            }
        };

        AddHandler(DragDrop.DropEvent, OnFileDrop);
        Closing += OnClosing;
    }

    /// 시작 화면 단계 실행 (App 이 창을 띄운 뒤 부른다. 테스트는 부르지 않는다)
    public void StartUp(UpdateService? updates)
    {
        App.Updates = updates;
        App.Fire(App.RunStartupAsync);
    }

    // MARK: - AppState 변화

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Mode):
                UpdateModeButtons();
                UpdateContent();
                break;
            case nameof(AppState.SelectedProfileId) or nameof(AppState.State) or nameof(AppState.SelectedRedis):
                UpdateBadge();
                UpdateModeButtons();
                UpdateContent();
                break;
            case nameof(AppState.ShowLog):
                UpdateLog();
                break;
            case nameof(AppState.LastLog) or nameof(AppState.ErrorCount):
                UpdateStatus();
                break;
            case nameof(AppState.AssistantEnabled):
                UpdateAssistant();
                break;
            case nameof(AppState.ShowSettings):
                if (App.ShowSettings) { App.ShowSettings = false; OpenSettings(); }
                break;
            case nameof(AppState.ShowReleaseNotes):
                // 새로운 기능은 별도 창으로 (설정 창 위에서 눌러도 앞에 보이도록)
                if (App.ShowReleaseNotes)
                {
                    App.ShowReleaseNotes = false;
                    OpenWindow(WindowKinds.ReleaseNotes, App.ReleaseNotesSince);
                }
                break;
            case nameof(AppState.TxPrompt):
                if (App.TxPrompt is { } tx) _ = ShowTxPromptAsync(tx);
                break;
            case nameof(AppState.DiscardPrompt):
                if (App.DiscardPrompt is { } d) _ = ShowDiscardPromptAsync(d);
                break;
        }
        if (e.PropertyName != null && SheetRegistry.Sheets.ContainsKey(e.PropertyName)) UpdateSheet(e.PropertyName);
    }

    private void SetMode(MainMode mode)
    {
        // Redis 는 스키마가 없어서 스키마 비교를 쓸 수 없다
        if (App.SelectedProfile?.Kind == DatabaseKind.Redis && mode == MainMode.Compare) { UpdateModeButtons(); return; }
        App.Mode = mode;
        UpdateModeButtons();
    }

    private void UpdateModeButtons()
    {
        ModeQuery.IsChecked = App.Mode == MainMode.Query;
        ModeCompare.IsChecked = App.Mode == MainMode.Compare;
        ModeModeling.IsChecked = App.Mode == MainMode.Modeling;
        var isRedis = App.SelectedProfile?.Kind == DatabaseKind.Redis;
        ModeCompare.Opacity = isRedis ? 0.4 : 1;
        ToolTip.SetTip(ModeCompare, isRedis ? Tr("Redis 는 스키마 비교를 지원하지 않아요.") : Tr("쿼리 / 스키마 비교 / 모델링"));
    }

    private void UpdateContent()
    {
        Control view;
        switch (App.Mode)
        {
            case MainMode.Compare:
                view = _compareView ??= new CompareView { DataContext = App };
                break;
            case MainMode.Modeling:
                view = _modelingView ??= new ModelingView { DataContext = App };
                break;
            default:
                if (App.SelectedRedis is { } ws)
                {
                    if (!_redisViews.TryGetValue(ws.ProfileId, out var rv) || !ReferenceEquals(rv.DataContext, ws))
                        _redisViews[ws.ProfileId] = rv = new RedisWorkspaceView { DataContext = ws };
                    view = rv;
                }
                else view = _queryView ??= new QueryWorkspaceView { DataContext = App };
                break;
        }
        if (!ReferenceEquals(ContentHost.Content, view)) ContentHost.Content = view;
        // 끊긴 Redis 접속의 화면은 버린다
        foreach (var id in _redisViews.Keys.Where(id => !App.RedisWorkspaces.ContainsKey(id)).ToList()) _redisViews.Remove(id);
    }

    private void UpdateLog()
    {
        var show = App.ShowLog;
        var rows = Right.RowDefinitions;
        if (!show && LogPanel.IsVisible && rows[2].ActualHeight > 0) Layout.Set(Layout.LogHeight, rows[2].ActualHeight);
        rows[1].Height = new GridLength(show ? 4 : 0);
        rows[2].Height = new GridLength(show ? Layout.Get(Layout.LogHeight) : 0);
        rows[2].MinHeight = show ? 100 : 0;
        LogSplitter.IsVisible = show;
        LogPanel.IsVisible = show;
        LogChevron.Text = show ? "▼ " : "▲ ";
    }

    private void UpdateAssistant()
    {
        var enabled = App.AssistantEnabled;
        AssistantToggle.IsVisible = enabled;
        var show = enabled && App.Assistant.Visible;
        AssistantToggle.IsChecked = App.Assistant.Visible;
        var cols = Main.ColumnDefinitions;
        if (!show && Assistant.IsVisible && cols[2].ActualWidth > 0) App.Assistant.Width = cols[2].ActualWidth;
        cols[1].Width = new GridLength(show ? 4 : 0);
        cols[2].Width = new GridLength(show ? Math.Clamp(App.Assistant.Width, 320, 760) : 0);
        cols[2].MinWidth = show ? 320 : 0;
        cols[2].MaxWidth = show ? 760 : double.PositiveInfinity;
        AssistantSplitter.IsVisible = show;
        Assistant.IsVisible = show;
    }

    private void UpdateStatus()
    {
        if (App.LastLog is { } last)
        {
            StatusIcon.Text = (string)Conv.LevelIcon.Convert(last.Level, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture)!;
            StatusIcon.Foreground = (IBrush)Conv.LevelBrush.Convert(last.Level, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture)!;
            StatusText.Text = last.Message.Split('\n')[0];
            StatusText.Classes.Set("error", last.Level == LogLevel.Error);
            StatusText.Classes.Set("secondary", last.Level != LogLevel.Error);
        }
        else
        {
            StatusIcon.Text = "";
            StatusText.Text = Tr("준비됨");
            StatusText.Classes.Set("error", false);
        }
        var n = App.ErrorCount;
        ErrorCountText.IsVisible = n > 0;
        ErrorCountText.Text = $"⛔ {n}";
        ToolTip.SetTip(ErrorCountText, Tr("오류 %@건", n));
    }

    /// 위 막대 왼쪽: 보고 있는 접속 (Mac ConnectionBadge)
    private void UpdateBadge()
    {
        Badge.Children.Clear();
        var p = App.SelectedProfile;
        TextBlock T(string s, string? cls = null) { var t = new TextBlock { Text = s, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }; if (cls != null) t.Classes.Add(cls); return t; }
        Control Dot(string color) => new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Color.Parse(color)), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Border Pill(string text, string color, double alpha) => new()
        {
            Classes = { "badge" },
            Background = new SolidColorBrush(Color.Parse(color), alpha),
            Child = new TextBlock { Text = text, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(color)) },
        };
        switch (App.State.Kind)
        {
            case ConnectionStateKind.Connected:
                Badge.Children.Add(Dot("#34C759"));
                Badge.Children.Add(T(p?.Name ?? ""));
                if (p?.Ssh != null) Badge.Children.Add(Pill("SSH", "#34C759", 0.15));
                Badge.Children.Add(new EnvironmentBadge { Environment = p?.Environment ?? ConnectionEnvironment.None });
                if (p?.ReadOnly == true) Badge.Children.Add(Pill(Tr("읽기전용"), "#FF3B30", 0.12));
                break;
            case ConnectionStateKind.Connecting:
                Badge.Children.Add(new GodDB.App.Controls.ActivitySpinner(16));   // Mac ProgressView().controlSize(.small)
                Badge.Children.Add(T(Tr("연결 중…")));
                break;
            case ConnectionStateKind.Failed:
                Badge.Children.Add(Dot("#FF3B30"));
                Badge.Children.Add(T(Tr("연결 실패")));
                break;
            default:
                Badge.Children.Add(Dot("#8A8A8E"));
                Badge.Children.Add(T(Tr("연결 안 됨"), "secondary"));
                break;
        }
        if (App.ConnectedCount > 1) Badge.Children.Add(T(Tr("· 열린 접속 %@개", App.ConnectedCount), "secondary"));
    }

    // MARK: - 창 요청 (시트)

    private void UpdateSheet(string property)
    {
        var value = typeof(AppState).GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(App);
        var active = value is not null and not false;
        if (_sheets.TryGetValue(property, out var open))
        {
            if (active && ReferenceEquals(open.Tag, value)) return;
            _sheets.Remove(property);
            open.Close();
        }
        if (!active) return;
        if (SheetRegistry.CreateSheet(property, App, value!) is not { } sheet) return;
        sheet.Tag = value;
        _sheets[property] = sheet;
        sheet.Closed += (_, _) =>
        {
            if (_sheets.TryGetValue(property, out var w) && ReferenceEquals(w, sheet)) _sheets.Remove(property);
            // 창을 닫았는데 요청이 그대로면 지운다 (다른 요청으로 바뀌었으면 그대로)
            var now = typeof(AppState).GetProperty(property)?.GetValue(App);
            if (ReferenceEquals(now, value) || (now is true && value is true))
                typeof(AppState).GetProperty(property)?.SetValue(App, now is bool ? false : null);
        };
        if (IsVisible) _ = sheet.ShowDialog(this);
        else sheet.Show();
    }

    /// 지금 떠 있는 시트 (테스트용)
    public IReadOnlyDictionary<string, Window> OpenSheets => _sheets;

    private void OnWindowRequested(string kind, object? argument)
    {
        if (kind == WindowKinds.Settings) { OpenSettings(); return; }
        OpenWindow(kind, argument);
    }

    private void OpenWindow(string kind, object? argument)
    {
        if (SheetRegistry.CreateWindow(kind, App, argument) is not { } w) return;
        if (IsVisible) w.Show(this); else w.Show();
    }

    public void OpenSettings()
    {
        if (_settings is { IsVisible: true }) { _settings.Activate(); return; }
        _settings = new SettingsWindow(App);
        _settings.Closed += (_, _) => _settings = null;
        if (IsVisible) _settings.Show(this); else _settings.Show();
    }

    private async Task ShowTxPromptAsync(TxPrompt prompt)
    {
        var choice = await MessageDialog.Show(this, Tr("커밋하지 않은 변경이 있어요"),
            Tr("열린 트랜잭션에 변경 %@건이 있어요. %@ 전에 커밋하거나 롤백해 주세요.", App.TxState(prompt.ProfileId).Changes, prompt.Reason.ActionText()),
            [Tr("커밋"), Tr("롤백"), Tr("취소")], destructiveIndex: 1, cancelIndex: 2);
        if (!ReferenceEquals(App.TxPrompt, prompt)) return;
        if (choice is 0 or 1) await App.ResolveTxPromptAsync(prompt, commit: choice == 0);
        else App.TxPrompt = null;
    }

    private async Task ShowDiscardPromptAsync(DiscardEditsPrompt prompt)
    {
        var ok = await MessageDialog.Show(this, Tr("적용하지 않은 편집이 있어요"), Tr("결과 표에서 고치고 적용하지 않은 %@건이 사라져요.", prompt.Count),
            [Tr("버리고 계속"), Tr("취소")], destructiveIndex: 0, cancelIndex: 1) == 0;
        if (!ReferenceEquals(App.DiscardPrompt, prompt)) return;
        App.ResolveDiscardPrompt(ok);
    }

    // MARK: - .sql 파일 끌어 놓기

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { } files) return;
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path && (path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                App.OpenSqlFile(path);
    }

    // MARK: - 종료

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed) return;
        App.SaveTabsNow();
        var open = App.OpenTransactions;
        if (open.Count == 0) { LogFile.Shared.Flush(); return; }
        // 커밋하지 않은 트랜잭션이 있으면 묻는다
        e.Cancel = true;
        var ok = await MessageDialog.Show(this, Tr("커밋하지 않은 트랜잭션이 있어요"),
            Tr("접속 %@개에 커밋하지 않은 변경 %@건이 있어요. 종료하면 되돌려져요(롤백).", open.Count, open.Sum(o => o.Changes)),
            [Tr("롤백하고 종료"), Tr("취소")], destructiveIndex: 0, cancelIndex: 1) == 0;
        if (!ok) return;
        foreach (var (id, _) in open) await App.RollbackTxAsync(id);
        LogFile.Shared.Flush();
        _closingConfirmed = true;
        Close();
    }

    // MARK: - 메뉴 · 단축키 (Mac ⌘ → Ctrl)

    private readonly List<(MenuItem Item, Func<bool> Enabled)> _enablers = [];

    private void BuildMenu()
    {
        _enablers.Clear();
        KeyBindings.Clear();
        var app = App;
        MenuItem Item(string key, Action action, string? gesture = null, Func<bool>? enabled = null)
        {
            var m = new MenuItem();
            m.Bind(MenuItem.HeaderProperty, Loc.Bind(key));
            if (gesture != null)
            {
                var g = KeyGesture.Parse(gesture);
                m.InputGesture = g;
                KeyBindings.Add(new KeyBinding
                {
                    Gesture = g,
                    Command = new RelayCommand(() => { if (enabled?.Invoke() != false) action(); }),
                });
            }
            m.Click += (_, _) => action();
            if (enabled != null) _enablers.Add((m, enabled));
            return m;
        }
        static MenuItem Tip(MenuItem m, string tip)
        {
            ToolTip.SetTip(m, Tr(tip));
            return m;
        }
        MenuItem Top(string key, params Control[] items)
        {
            var m = new MenuItem();
            m.Bind(MenuItem.HeaderProperty, Loc.Bind(key));
            foreach (var i in items) m.Items.Add(i);
            m.SubmenuOpened += (_, _) => RefreshEnabled();
            return m;
        }

        var recent = new MenuItem();
        recent.Bind(MenuItem.HeaderProperty, Loc.Bind("최근 SQL 파일"));
        recent.SubmenuOpened += (_, _) => FillRecent(recent);
        FillRecent(recent);

        var file = Top("파일",
            Item("새 쿼리 탭", () => app.AddTab(), "Ctrl+T"),
            Item("새 접속…", app.NewProfile, "Ctrl+Shift+N"),
            // Ctrl+W: 쿼리 탭을 보고 있으면 그 탭을 닫는다. Windows 에서는 메인 창을 닫으면 앱이 끝나므로
            // (Mac 은 앞에 있는 창을 닫는다) 탭이 없을 때는 아무것도 하지 않는다. 다른 창은 Esc 로 닫힌다.
            Item("탭 닫기", () => app.CloseTabOrWindow(mainWindowIsKey: true), "Ctrl+W"),
            new Separator(),
            Item("SQL 파일 열기…", () => app.Fire(app.OpenSqlFilePanelAsync), "Ctrl+Shift+O"),
            recent,
            Item("저장", () => app.Fire(() => app.SaveSelectedTabAsync()), "Ctrl+S", () => app.CanSaveQueryTab),
            Item("다른 이름으로 저장…", () => app.Fire(() => app.SaveSelectedTabAsync(saveAs: true)), "Ctrl+Shift+S", () => app.CanSaveQueryTab),
            new Separator(),
            Item("설정…", OpenSettings, "Ctrl+OemComma"),
            new Separator(),
            Item("끝내기", Close));

        var edit = Top("편집",
            Item("실행 취소", () => EditCommand("Undo")),
            Item("다시 실행", () => EditCommand("Redo")),
            new Separator(),
            Item("잘라내기", () => EditCommand("Cut")),
            Item("복사", () => EditCommand("Copy")),
            Item("붙여넣기", () => EditCommand("Paste")),
            Item("모두 선택", () => EditCommand("SelectAll")));
        // 편집 메뉴는 표시만 (입력 칸이 직접 Ctrl+Z/X/C/V/A 를 처리한다)
        foreach (var (m, g) in new[] { (0, "Ctrl+Z"), (1, "Ctrl+Shift+Z"), (3, "Ctrl+X"), (4, "Ctrl+C"), (5, "Ctrl+V"), (6, "Ctrl+A") })
            ((MenuItem)edit.Items[m]!).InputGesture = KeyGesture.Parse(g);

        var db = Top("데이터베이스",
            Item("스키마 새로고침", () => app.Fire(() => app.RefreshSchemaAsync()), "Ctrl+Shift+R"),
            Item("보고 있는 접속 끊기", () => { if (app.SelectedProfileId is { } id) app.Fire(() => app.DisconnectAsync(id)); }),
            Item("모든 접속 끊기", () => app.Fire(app.DisconnectAllAsync)),
            new Separator(),
            Item("객체 빠른 찾기…", () => app.ShowQuickOpen = true, "Ctrl+O", () => app.CanQuickOpen),
            // Mac 1.4.0: ⌥⌘F · ⌥⌘S · ⇧⌘K
            Item("ID 로 찾기…", () => app.OpenIdSearch(), "Ctrl+Alt+F", () => app.CanSearchIds),
            Item("쿼리 저장…", app.OpenSaveQuery, "Ctrl+Alt+S", () => app.CanSaveQuery),
            Item("저장 쿼리…", app.OpenSavedQueries, "Ctrl+Shift+K", () => app.CanOpenSavedQueries),
            Item("쿼리 히스토리…", () => app.ShowHistory = true, "Ctrl+Y"));

        var view = Top("보기",
            Item("쿼리", () => SetMode(MainMode.Query)),
            Item("스키마 비교", () => SetMode(MainMode.Compare), enabled: () => app.SelectedProfile?.Kind != DatabaseKind.Redis),
            Item("모델링", () => SetMode(MainMode.Modeling)),
            new Separator(),
            Item("상태창", app.ToggleLog, "Ctrl+Shift+L"),
            Item("AI 어시스턴트", () => { if (app.AssistantEnabled) app.Assistant.Visible = !app.Assistant.Visible; }, "Ctrl+Shift+I", () => app.AssistantEnabled),
            new Separator(),
            // Mac 보기 > 레이아웃 초기화 (CommandGroup(after: .sidebar))
            Tip(Item("레이아웃 초기화", () => Layout.Reset(app)), "끌어서 바꾼 상태창 · 편집기 · 패널 크기를 처음 크기로 되돌려요"));

        var help = Top("도움말",
            Item("새로운 기능", app.OpenReleaseNotes),
            Item("문제 보고…", () => app.ShowProblemReport = true),
            new Separator(),
            Item("업데이트 확인…", () => app.Fire(() => app.CheckForUpdatesAsync(userInitiated: true))));

        MainMenu.ItemsSource = new[] { file, edit, db, view, help };
        RefreshEnabled();
    }

    private void RefreshEnabled()
    {
        foreach (var (m, f) in _enablers) m.IsEnabled = f();
    }

    private void FillRecent(MenuItem recent)
    {
        recent.Items.Clear();
        foreach (var path in App.RecentSqlFiles)
        {
            var m = new MenuItem { Header = Path.GetFileName(path) };
            ToolTip.SetTip(m, path);
            m.Click += (_, _) => App.OpenSqlFile(path);
            recent.Items.Add(m);
        }
        recent.Items.Add(new Separator());
        var clear = new MenuItem { Header = Tr("목록 지우기") };
        clear.Click += (_, _) => App.ClearRecentSqlFiles();
        recent.Items.Add(clear);
    }

    /// 편집 메뉴: 포커스가 있는 입력 칸(TextBox · 편집기)에 Undo/Redo/Cut/Copy/Paste/SelectAll 을 보낸다
    private void EditCommand(string name)
    {
        var focused = FocusManager?.GetFocusedElement();
        focused?.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)?.Invoke(focused, null);
    }
}
