using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 쿼리 화면: 탭 바 · SQL 편집기 · 결과 표 (Mac QueryWorkspaceView · TabStrip · TabChip). DataContext = AppState
public partial class QueryWorkspaceView : UserControl
{
    private AppState? _app;
    private QueryTabView? _tabView;
    /// 탭 칩 (탭 id → 모양 요약, 컨트롤). 모양이 바뀐 칩만 다시 만든다.
    private readonly Dictionary<Guid, (string Signature, Control Chip)> _chips = [];

    public QueryWorkspaceView()
    {
        InitializeComponent();
        AddTabButton.Click += (_, _) => _app?.AddTab();
        EmptyNewTab.Click += (_, _) => _app?.AddTab();
        L10n.LanguageChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { _chips.Clear(); RefreshAll(); });
    }

    /// 지금 쓰는 탭 화면 (테스트용)
    public QueryTabView? TabView => _tabView;
    public IReadOnlyList<Control> Chips => TabStripPanel.Children.ToList();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not AppState app || ReferenceEquals(app, _app)) return;
        _app = app;
        _tabView = new QueryTabView(app);
        app.Tabs.CollectionChanged += OnTabsChanged;
        foreach (var t in app.Tabs) t.PropertyChanged += OnTabPropertyChanged;
        app.PropertyChanged += OnAppChanged;
        app.ConnectionChanged += _ => RefreshAll();
        RefreshAll();
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (QueryTab t in e.OldItems) t.PropertyChanged -= OnTabPropertyChanged;
        if (e.NewItems != null) foreach (QueryTab t in e.NewItems) t.PropertyChanged += OnTabPropertyChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset && _app != null)
            foreach (var t in _app.Tabs) { t.PropertyChanged -= OnTabPropertyChanged; t.PropertyChanged += OnTabPropertyChanged; }
        RefreshAll();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueryTab.Title) or nameof(QueryTab.IsDirty) or nameof(QueryTab.Running)
            or nameof(QueryTab.Database) or nameof(QueryTab.FilePath))
            RefreshStrip();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.SelectedTabId) or nameof(AppState.SelectedProfileId) or nameof(AppState.State)
            or nameof(AppState.CurrentDatabase))
            RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshStrip();
        RefreshBody();
    }

    private void RefreshBody()
    {
        if (_app == null || _tabView == null) return;
        var tab = _app.SelectedTab;
        if (tab != null)
        {
            _tabView.Bind(tab);
            if (!ReferenceEquals(Body.Content, _tabView)) Body.Content = _tabView;
            Body.IsVisible = true;
            EmptyState.IsVisible = false;
            return;
        }
        _tabView.Bind(null);
        Body.IsVisible = false;
        EmptyState.IsVisible = true;
        if (_app.State.IsConnected && _app.SelectedProfile is { } p)
        {
            EmptyIcon.Text = "⌨";
            EmptyTitle.Text = Tr("열린 쿼리 탭이 없어요");
            EmptyText.Text = Tr("%@ / %@ 에서 쿼리를 쓰려면 새 탭을 여세요.", p.Name, _app.CurrentDatabase);
            EmptyNewTab.IsVisible = true;
        }
        else
        {
            EmptyIcon.Text = "⛁";
            EmptyTitle.Text = Tr("접속을 연결하세요");
            EmptyText.Text = Tr("왼쪽에서 접속을 누르면 연결되고, 그 접속의 쿼리 탭이 열려요.\n여러 접속을 동시에 열어 탭마다 다른 DB 에서 쿼리할 수 있어요.");
            EmptyNewTab.IsVisible = false;
        }
    }

    // MARK: - 탭 바

    private void RefreshStrip()
    {
        if (_app == null) return;
        var app = _app;
        var wanted = new List<Control>();
        var alive = new HashSet<Guid>();
        foreach (var tab in app.Tabs)
        {
            alive.Add(tab.Id);
            var profile = app.Profile(tab.ProfileId);
            var connected = app.ConnectionState(tab.ProfileId).IsConnected;
            var selected = tab.Id == app.SelectedTabId;
            var sig = $"{QueryUi.TabTitle(tab)}|{QueryUi.TabSubtitle(profile?.Name, tab.Database)}|{connected}|{tab.Running}|{selected}|{profile?.Kind}|{profile?.Environment}|{tab.FilePath}";
            if (!_chips.TryGetValue(tab.Id, out var c) || c.Signature != sig)
            {
                c = (sig, BuildChip(tab, profile, connected, selected));
                _chips[tab.Id] = c;
            }
            wanted.Add(c.Chip);
        }
        foreach (var id in _chips.Keys.Where(id => !alive.Contains(id)).ToList()) _chips.Remove(id);
        if (!TabStripPanel.Children.SequenceEqual(wanted))
        {
            TabStripPanel.Children.Clear();
            foreach (var w in wanted) TabStripPanel.Children.Add(w);
        }
        AddTabButton.IsEnabled = app.State.IsConnected;
        ToolTip.SetTip(AddTabButton, app.SelectedProfile is { } p ? Tr("%@ / %@ 에 새 쿼리 탭 (Ctrl+T)", p.Name, app.CurrentDatabase) : Tr("접속을 먼저 연결하세요"));
    }

    private Control BuildChip(QueryTab tab, ConnectionProfile? profile, bool connected, bool selected)
    {
        var app = _app!;
        var id = tab.Id;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (tab.Running)
            row.Children.Add(new TextBlock { Text = Glyphs.Refresh, Foreground = Q.Blue, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        else
            row.Children.Add(new Ellipse
            {
                Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center,
                Fill = connected ? Q.Green : new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0x8A, 0x8E)),
            });
        if (profile != null) row.Children.Add(new KindBadge { Kind = profile.Kind, VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = QueryUi.TabTitle(tab), FontSize = 12, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        if (tab.FilePath != null) ToolTip.SetTip(title, tab.FilePath);
        var sub = new TextBlock { Text = QueryUi.TabSubtitle(profile?.Name, tab.Database), FontSize = 10, Classes = { "secondary" }, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        row.Children.Add(new StackPanel { Children = { title, sub }, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = Glyphs.Close, Classes = { "borderless" }, FontSize = 9, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(3, 1) };
        close.Classes.Add("secondary");
        ToolTip.SetTip(close, Tr("탭 닫기"));
        close.Click += (_, e) => { app.RequestCloseTabs([id]); e.Handled = true; };
        row.Children.Add(close);

        var grid = new Grid();
        grid.Children.Add(new Border { Padding = new Thickness(10, 4), Child = row });
        if (profile?.Environment is { } env && env != ConnectionEnvironment.None)
            grid.Children.Add(new Border
            {
                Width = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(EnvironmentBadge.ColorOf(env)),
            });
        if (selected)
            grid.Children.Add(new Border { Height = 2, Background = Q.Blue, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Bottom });
        var chip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = selected ? new SolidColorBrush(Color.FromArgb(0x26, 0x0A, 0x84, 0xFF)) : Brushes.Transparent,
            Child = grid,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = id,
        };
        ToolTip.SetTip(chip, connected ? $"{profile?.Name ?? ""} / {tab.Database}" : Tr("연결이 끊긴 접속이에요. 왼쪽에서 다시 연결하세요."));
        chip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) app.SelectTab(id);
        };
        var menu = new ContextMenu();
        MenuItem Item(string key, Action action)
        {
            var m = new MenuItem();
            m.Bind(MenuItem.HeaderProperty, Loc.Bind(key));
            m.Click += (_, _) => action();
            return m;
        }
        var profileId = tab.ProfileId;
        menu.Items.Add(Item("이 접속에 새 탭", () => app.AddTab(profileId: profileId)));
        menu.Items.Add(Item("탭 닫기", () => app.RequestCloseTabs([id])));
        menu.Items.Add(Item("다른 탭 모두 닫기", () => app.RequestCloseTabs(app.Tabs.Where(t => t.Id != id).Select(t => t.Id).ToList())));
        chip.ContextMenu = menu;
        return chip;
    }
}
