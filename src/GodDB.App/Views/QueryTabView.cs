using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Document;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 쿼리 도구 막대의 자동 커밋 체크박스 + 열린 트랜잭션 표시 + 커밋/롤백 (Mac TransactionControls)
public sealed class TransactionControls : StackPanel
{
    private readonly AppState _app;
    private readonly CheckBox _auto = new();
    private readonly Border _pill = Q.Pill("", Q.Orange);
    private readonly Button _commit;
    private readonly Button _rollback;
    private Guid _profileId;
    private bool _syncing;

    public TransactionControls(AppState app)
    {
        _app = app;
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        VerticalAlignment = VerticalAlignment.Center;
        _auto.Bind(ContentControl.ContentProperty, Loc.Bind("자동 커밋"));
        ToolTip.SetTip(_auto, Tr("끄면 INSERT·UPDATE·DELETE 가 트랜잭션 안에서 실행되고 [커밋] 할 때 반영돼요. (MySQL 은 CREATE·ALTER·DROP 이 자동 커밋돼요)"));
        _auto.IsCheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            var on = _auto.IsChecked == true;
            var id = _profileId;
            _app.Fire(async () => { await _app.SetAutoCommitAsync(id, on); Update(id); });
        };
        _commit = Q.B(Tr("커밋"), () => { var id = _profileId; _app.Fire(() => _app.CommitTxAsync(id)); }, Tr("열린 트랜잭션의 변경을 DB 에 반영해요"));
        _commit.Background = Q.Green;
        _commit.Foreground = Brushes.White;
        _rollback = Q.B(Tr("롤백"), () => { var id = _profileId; _app.Fire(() => _app.RollbackTxAsync(id)); }, Tr("열린 트랜잭션의 변경을 모두 되돌려요"));
        _rollback.Foreground = Q.Red;
        Children.Add(_auto);
        Children.Add(_pill);
        Children.Add(_commit);
        Children.Add(_rollback);
    }

    /// 트랜잭션 표시 글자 (테스트용)
    public string PillText => (_pill.Child as TextBlock)?.Text ?? "";
    public bool AutoCommitChecked => _auto.IsChecked == true;

    public void Update(Guid profileId)
    {
        _profileId = profileId;
        var tx = _app.TxState(profileId);
        _syncing = true;
        _auto.IsChecked = tx.AutoCommit;
        _syncing = false;
        ((TextBlock)_pill.Child!).Text = "⟲ " + Tr("트랜잭션 · 변경 %@건", tx.Changes);
        _pill.IsVisible = _commit.IsVisible = _rollback.IsVisible = tx.Open;
    }
}

/// 쿼리 탭 하나의 화면: 도구 막대 · SQL 편집기 · 결과 (Mac QueryTabView).
/// 탭을 바꿔도 같은 화면을 쓰고, 탭마다 편집기 문서(글 · 되돌리기 기록)와 선택 · 커서 · 스크롤 위치를 따로 기억한다.
public sealed class QueryTabView : UserControl
{
    private sealed class EditorState(TextDocument doc)
    {
        public TextDocument Doc { get; } = doc;
        public int SelectionStart, SelectionLength, Caret;
        public double ScrollY;
    }

    private readonly AppState _app;
    private QueryTab? _tab;
    private readonly Dictionary<Guid, EditorState> _states = [];
    private bool _pushing;

    public SqlEditor Editor { get; } = new();
    public ResultPane Results { get; }
    public TransactionControls Tx { get; }
    private readonly Border _banner;
    private readonly TextBlock _bannerText = new() { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeight.SemiBold };
    private readonly Button _run;
    private readonly Button _runAll;
    private readonly Button _cancel;
    private readonly Button _explain;
    private readonly Button _refreshSource;
    private readonly ComboBox _limit;
    private readonly Border _txDivider = Q.Divider(vertical: true);
    private readonly Grid _split;

    private static readonly (int Value, string Label)[] Limits = [(100, "100"), (500, "500"), (1000, "1000"), (5000, "5000"), (0, "제한 없음")];

    public QueryTabView(AppState app)
    {
        _app = app;
        Results = new ResultPane(app);
        Tx = new TransactionControls(app);

        _banner = new Border
        {
            Background = Q.Red,
            Padding = new Thickness(10, 3),
            Child = Q.H(6, new TextBlock { Text = "⚠", Foreground = Brushes.White, FontSize = 11 }, _bannerText),
        };

        _run = Q.B(Symbol.Play, Tr("실행"), () => Run(explain: false), Tr("선택 영역만 실행, 선택이 없으면 커서가 있는 문장(강조된 문장)만 실행 (Ctrl+Enter)"), "accent");
        _runAll = Q.B(Symbol.Forward, Tr("전체 실행"), () => Run(explain: false, all: true), Tr("편집기의 모든 문장을 위에서부터 실행 (Ctrl+Alt+Enter)"));
        _cancel = Q.B(Symbol.Stop, Tr("취소"), Cancel, Tr("실행 중인 쿼리 멈추기 (Ctrl+.)"));
        _cancel.Background = Q.Red;
        _cancel.Foreground = Brushes.White;
        _explain = Q.B(Symbol.Plan, Tr("실행 계획"), () => Run(explain: true), Tr("EXPLAIN 으로 실행 계획 보기 (Ctrl+Shift+Enter)"));
        _refreshSource = Q.B(Symbol.Refresh, Tr("생성문 새로고침"), () => { if (_tab is { } t) _app.Fire(() => _app.RefreshSourceAsync(t.Id)); },
            Tr("DB 에서 생성문(정의)을 다시 읽어 편집기 내용을 바꿔요"));
        var history = Q.B(Symbol.History, Tr("히스토리"), () => _app.ShowHistory = true, Tr("실행한 쿼리 다시 찾기 (Ctrl+Y)"));
        var saved = Q.B(Symbol.Bookmark, Tr("저장 쿼리"), _app.OpenSavedQueries, Tr("저장 쿼리 목록 (Ctrl+Shift+K) · 지금 쿼리 저장은 Ctrl+Alt+S"));
        SavedQueriesButton = saved;

        // 좁아져도 선택한 값(500 등)은 항상 보이게: 이름표만 compact 에서 숨기고 선택 상자는 줄이지 않는다 (Mac minWidth 72)
        _limit = new ComboBox { MinWidth = 72, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (_, label) in Limits)
        {
            var item = new ComboBoxItem();
            if (label == "제한 없음") item.Bind(ContentControl.ContentProperty, Loc.Bind(label));
            else item.Content = label;
            _limit.Items.Add(item);
        }
        _limit.SelectionChanged += (_, _) =>
        {
            if (_limit.SelectedIndex >= 0 && _limit.SelectedIndex < Limits.Length) _app.RowLimit = Limits[_limit.SelectedIndex].Value;
        };
        ToolTip.SetTip(_limit, Tr("한 번에 가져올 행 수"));
        var pageLabel = Q.T(Tr("페이지"));
        pageLabel.Classes.Add("label");

        var left = Q.H(10, _run, _runAll, _cancel, _explain, _refreshSource, history, saved, Q.H(4, pageLabel, _limit), _txDivider, Tx);
        var hint = Q.T(Tr("Tab 자동완성 · Ctrl+Enter 커서 문장 실행 · Ctrl+Alt+Enter 전체 실행 · Ctrl+Shift+Enter 실행 계획 · Ctrl+/ 주석 · Ctrl+Alt+L 정렬"), "caption");
        hint.Classes.Add("label");
        hint.TextWrapping = TextWrapping.NoWrap;
        hint.Margin = new Thickness(10, 0, 0, 0);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(8) };
        row.Children.Add(left);
        Grid.SetColumn(hint, 2);
        row.Children.Add(hint);
        var toolbar = new AdaptiveBar { Content = row };
        Toolbar = toolbar;

        var top = new DockPanel();
        DockPanel.SetDock(_banner, Dock.Top);
        top.Children.Add(_banner);
        DockPanel.SetDock(toolbar, Dock.Top);
        top.Children.Add(toolbar);
        var div = Q.Divider();
        DockPanel.SetDock(div, Dock.Top);
        top.Children.Add(div);
        top.Children.Add(Editor);

        // 편집기 높이 (결과 창과의 경계): 조절하면 모든 탭 · 다음 실행에 쓴다 (Mac Layout.queryEditorHeight)
        _split = new Grid { RowDefinitions = new RowDefinitions("220,Auto,*") };
        _split.RowDefinitions[0].Height = new GridLength(Layout.Get(Layout.QueryEditorHeight));
        _split.RowDefinitions[0].MinHeight = 80;
        _split.RowDefinitions[2].MinHeight = 140;
        _split.Children.Add(top);
        // Mac ResizeHandle(axis: .horizontal): 편집기 기본 높이 220, 80 ~ (전체 - 140)
        var splitter = Q.ResizeHandle(rows: true);
        Layout.SaveOnDrag(splitter, _split.RowDefinitions[0], Layout.QueryEditorHeight);
        Layout.OnReset(this, () => _split.RowDefinitions[0].Height = new GridLength(Layout.Get(Layout.QueryEditorHeight)));
        Grid.SetRow(splitter, 1);
        _split.Children.Add(splitter);
        Grid.SetRow(Results, 2);
        _split.Children.Add(Results);
        Content = _split;

        Editor.SqlTextChanged += OnEditorTextChanged;
        // 편집기 우클릭 > 쿼리 저장… (Mac: goddbSaveQuery 알림)
        Editor.SaveQueryRequested += _app.OpenSaveQuery;
        // 저장 쿼리 (Ctrl+Alt+S) 가 선택 영역 · 커서 문장을 읽는다 (Mac: app.activeEditor = controller)
        _app.ActiveEditorSql = () => Editor.SqlToRun();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        _app.PropertyChanged += OnAppChanged;
        _app.ConnectionChanged += OnConnectionChanged;
    }

    public QueryTab? Tab => _tab;

    /// 편집기 높이 · 전체 실행 버튼 · 페이지 크기 선택 (테스트용)
    public double EditorHeight => _split.RowDefinitions[0].Height.Value;
    public Button RunAllButton => _runAll;
    public Button RunButton => _run;
    public ComboBox LimitPicker => _limit;
    /// 도구 막대 저장 쿼리 버튼 (테스트용)
    public Button SavedQueriesButton { get; }

    // MARK: - 탭 바꾸기

    /// 이 화면에 보일 탭 (탭을 바꾸면 앞 탭의 선택 · 커서 · 스크롤을 기억해 둔다)
    public void Bind(QueryTab? tab)
    {
        if (ReferenceEquals(tab, _tab)) { RefreshToolbar(); return; }
        SaveEditorState();
        if (_tab != null) _tab.PropertyChanged -= OnTabChanged;
        _tab = tab;
        // 닫힌 탭의 문서는 버린다
        foreach (var id in _states.Keys.Where(id => _app.Tab(id) == null).ToList()) _states.Remove(id);
        if (tab != null)
        {
            NormalizeTabText(tab);
            if (!_states.TryGetValue(tab.Id, out var st))
            {
                st = new EditorState(new TextDocument(tab.Text));
                _states[tab.Id] = st;
            }
            else if (st.Doc.Text != tab.Text)
            {
                st.Doc.Text = tab.Text;
            }
            Editor.SchemaProvider = () => _app.Connections.TryGetValue(tab.ProfileId, out var c) ? (c.Tables, c.Routines) : ([], []);
            var tabId = tab.Id;
            Editor.LintContext = () => _app.LintContext(tabId);
            Editor.ServerChecker = sql => _app.ServerCheckAsync(sql, tabId);
            Editor.Kind = _app.Profile(tab.ProfileId)?.Kind ?? DatabaseKind.Mysql;
            _pushing = true;
            Editor.Document = st.Doc;
            _pushing = false;
            var len = st.Doc.TextLength;
            Editor.CaretOffset = Math.Min(st.Caret, len);
            if (st.SelectionLength > 0 && st.SelectionStart + st.SelectionLength <= len) Editor.Select(st.SelectionStart, st.SelectionLength);
            Editor.ScrollToVerticalOffset(st.ScrollY);
            Editor.RefreshDecorations();
            tab.PropertyChanged += OnTabChanged;
            // 탭을 열거나 바꾸면 편집기에 포커스 (Mac QueryTabView.onAppear controller.focus())
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_tab, tab)) Editor.TextArea.Focus(); });
        }
        Results.Bind(tab);
        RefreshToolbar();
        RunPendingRequest();
    }

    /// 저장 쿼리 [실행]: 넣은 내용을 평소 실행(Ctrl+Alt+Enter 와 같은 확인)으로 실행 (Mac onChange(of: runAllRequest, initial: true))
    private void RunPendingRequest()
    {
        if (_tab is not { } tab || _app.RunAllRequest != tab.Id) return;
        _app.RunAllRequest = null;
        Run(explain: false, all: true);
    }

    /// 도구 막대 (테스트용). 접속 환경 · 읽기전용 표시는 Mac 처럼 창 위 막대(ConnectionBadge)에만 있다.
    public Control Toolbar { get; private set; } = null!;

    private void SaveEditorState()
    {
        if (_tab == null || !_states.TryGetValue(_tab.Id, out var st) || !ReferenceEquals(Editor.Document, st.Doc)) return;
        st.Caret = Editor.CaretOffset;
        st.SelectionStart = Editor.TextArea.Selection.IsEmpty ? Editor.CaretOffset : Editor.SelectionStart;
        st.SelectionLength = Editor.TextArea.Selection.IsEmpty ? 0 : Editor.SelectionLength;
        st.ScrollY = Editor.VerticalOffset;
    }

    /// 탭 글이 CRLF 면 LF 로 (편집기 · Core 오프셋을 맞춘다). 파일에서 읽은 그대로면 저장된 글도 같이 바꿔 '바뀜' 표시가 생기지 않게 한다.
    private static void NormalizeTabText(QueryTab tab)
    {
        if (!tab.Text.Contains('\r')) return;
        var n = LineEndings.Normalize(tab.Text);
        if (tab.SavedText == tab.Text) tab.SavedText = n;
        tab.Text = n;
    }

    private void OnEditorTextChanged()
    {
        if (_pushing || _tab == null) return;
        var text = Editor.SqlText;
        if (_tab.Text == text) return;
        _pushing = true;
        try { _tab.Text = text; }
        finally { _pushing = false; }
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not QueryTab tab || !ReferenceEquals(tab, _tab)) return;
        switch (e.PropertyName)
        {
            case nameof(QueryTab.Text):
                if (_pushing) return;
                NormalizeTabText(tab);
                if (tab.Text != Editor.SqlText)
                {
                    _pushing = true;
                    try { Editor.SetSqlText(tab.Text); }
                    finally { _pushing = false; }
                }
                break;
            case nameof(QueryTab.Running) or nameof(QueryTab.Source) or nameof(QueryTab.Edits):
                RefreshToolbar();
                break;
        }
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.RowLimit) or nameof(AppState.SelectedTabId)) RefreshToolbar();
        if (e.PropertyName == nameof(AppState.RunAllRequest)) RunPendingRequest();
    }

    private void OnConnectionChanged(Guid id)
    {
        if (_tab?.ProfileId != id) return;
        RefreshToolbar();
        Editor.RefreshDecorations();
        Results.Refresh();
    }

    /// 도구 막대 · 배너를 탭 상태에 맞춘다
    public void RefreshToolbar()
    {
        var tab = _tab;
        var profile = tab != null ? _app.Profile(tab.ProfileId) : null;
        _banner.IsVisible = profile?.Environment == ConnectionEnvironment.Production;
        _bannerText.Text = Tr("운영 접속 · %@", profile?.Name ?? "");
        var running = tab?.Running == true;
        _run.IsVisible = _runAll.IsVisible = !running;
        _cancel.IsVisible = running;
        _explain.IsEnabled = !running && tab != null;
        _refreshSource.IsVisible = tab?.Source != null;
        var index = Array.FindIndex(Limits, l => l.Value == _app.RowLimit);
        if (_limit.SelectedIndex != index) _limit.SelectedIndex = index;
        var showTx = tab != null && profile is { Kind: not DatabaseKind.Redis, ReadOnly: false } && _app.ConnectionState(tab.ProfileId).IsConnected;
        Tx.IsVisible = _txDivider.IsVisible = showTx;
        if (showTx) Tx.Update(tab!.ProfileId);
    }

    // MARK: - 실행

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        if (e.Key == Key.Enter && mods == KeyModifiers.Control) { Run(explain: false); e.Handled = true; }
        // Mac ⌥⌘↩ 전체 실행
        else if (e.Key == Key.Enter && mods == (KeyModifiers.Control | KeyModifiers.Alt)) { Run(explain: false, all: true); e.Handled = true; }
        else if (e.Key == Key.Enter && mods == (KeyModifiers.Control | KeyModifiers.Shift)) { Run(explain: true); e.Handled = true; }
        else if (mods == KeyModifiers.Control && (e.Key == Key.OemPeriod || e.PhysicalKey == PhysicalKey.Period)) { Cancel(); e.Handled = true; }
    }

    private void Cancel()
    {
        if (_tab is { Running: true } t) _app.Fire(() => _app.CancelQueryAsync(t.Id));
    }

    /// 실행 버튼 / Ctrl+Enter: 선택 영역이 있으면 그 안의 문장만, 없으면 커서가 있는 문장 하나 (편집기가 강조하는 문장).
    /// 전체 실행 / Ctrl+Alt+Enter (all): 편집기의 모든 문장을 위에서부터. 실행 계획은 선택 영역 또는 커서 문장 하나.
    public void Run(bool explain, bool all = false)
    {
        if (_tab is not { Running: false } tab) return;
        RunConfirm? confirm;
        if (explain)
        {
            if (Editor.SqlToRun() is not { } sql) return;
            confirm = RunConfirm.ForExplain(sql, tab.Edits.Count);
        }
        else
        {
            var profile = _app.Profile(tab.ProfileId);
            var kind = profile?.Kind ?? DatabaseKind.Mysql;
            confirm = all
                ? RunConfirm.ForRunAll(Editor.SqlText, tab.Edits.Count, profile?.Environment == ConnectionEnvironment.Production, kind)
                : RunConfirm.ForRun(Editor.SelectedSql(), Editor.SqlToRun(), tab.Edits.Count,
                    profile?.Environment == ConnectionEnvironment.Production, kind);
        }
        if (confirm == null) return;
        var c = confirm;
        _app.Fire(async () =>
        {
            if (c.NeedsConfirm && !await _app.Dialogs.ConfirmAsync(c.Title, c.Message, c.ConfirmButton, Tr("취소"), destructive: true)) return;
            await _app.StartAsync(c);
        });
    }
}
