using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 객체 빠른 찾기 (Ctrl+O): 테이블 · 뷰 · 프로시저 · 함수 · 컬럼 · DB 이름으로 찾아 바로 연다. Mac QuickOpenView.swift
[SheetFor(nameof(AppState.ShowQuickOpen))]
public sealed class QuickOpenWindow : Window
{
    private readonly AppState _app;
    private List<QuickOpen.Item> _others = [];
    private List<QuickOpen.Item> _results = [];
    private bool _syncing;
    public TextBox SearchBox { get; }
    public ListBox List { get; }
    private readonly GodDB.App.Controls.ActivitySpinner _loading = new() { Width = 16, Height = 16, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _empty = new() { Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    /// 고른 항목 Id (↑↓ 로 움직인다)
    public string? SelectedId { get; private set; }
    /// 연 항목 (테스트용)
    public QuickOpen.Item? OpenedItem { get; private set; }

    public QuickOpenWindow(AppState app, object value)
    {
        _app = app;
        Title = Tr("객체 빠른 찾기");
        Width = 620;
        Height = 460;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        SearchBox = new TextBox
        {
            PlaceholderText = Tr("테이블 · 뷰 · 프로시저 · 컬럼 이름 (예: ord_it, shop.orders)"),
            FontSize = 16,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
        };
        SearchBox.PropertyChanged += (_, e) => { if (e.Property != TextBox.TextProperty) return; Refresh(); SelectedId = _results.FirstOrDefault()?.Id; SyncSelection(); };
        // TextBox 가 ↑↓ · Enter 를 먼저 먹지 않도록 터널 단계에서 받는다
        SearchBox.AddHandler(KeyDownEvent, OnSearchKey, RoutingStrategies.Tunnel);

        var top = new DockPanel { Margin = new Thickness(12) };
        var icon = new TextBlock { Text = "⌕", FontSize = 18, Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(_loading, Dock.Right);
        top.Children.Add(icon);
        top.Children.Add(_loading);
        top.Children.Add(SearchBox);

        List = new ListBox { ItemTemplate = new FuncDataTemplate<QuickOpen.Item>((i, _) => i == null ? new TextBlock() : Row(i)) };
        List.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            if (List.SelectedItem is QuickOpen.Item i) SelectedId = i.Id;
        };
        List.DoubleTapped += (_, _) => { if (List.SelectedItem is QuickOpen.Item i) Open(i); };
        List.KeyDown += (_, e) => { if (e.Key == Key.Enter && List.SelectedItem is QuickOpen.Item i) { Open(i); e.Handled = true; } };

        var hint = new TextBlock
        {
            Text = Tr("↑↓ 고르기 · Enter 열기 · esc 닫기") + (app.Dialect.AllTablesSql == null ? Tr(" · SQL Server 는 지금 DB 만 찾아요") : ""),
            Classes = { "caption" },
            Margin = new Thickness(12, 6),
        };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(Divider(Dock.Top));
        root.Children.Add(hint);
        root.Children.Add(Divider(Dock.Bottom));
        root.Children.Add(new Panel { Children = { List, _empty } });
        Content = root;

        // Esc 는 어디에 포커스가 있어도 닫는다 (Mac onExitCommand)
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } }, RoutingStrategies.Tunnel);
        Refresh();
        SelectedId = _results.FirstOrDefault()?.Id;
        SyncSelection();
        Opened += (_, _) =>
        {
            SearchBox.Focus();
            _app.Fire(LoadOthersAsync);
        };
    }

    private static Border Divider(Dock dock)
    {
        var b = new Border { Classes = { "divider" }, Height = 1 };
        DockPanel.SetDock(b, dock);
        return b;
    }

    /// 찾을 대상 (지금 DB 의 테이블·컬럼·루틴, 다른 DB, 다른 스키마 객체)
    public List<QuickOpen.Item> Items =>
        QuickOpen.BuildItems(_app.CurrentDatabase, _app.Tables, _app.Routines, _app.Databases, _others);

    /// 지금 목록에 보이는 결과
    public IReadOnlyList<QuickOpen.Item> Results => _results;

    public void Refresh()
    {
        _results = QuickOpen.Results(SearchBox.Text ?? "", Items);
        _syncing = true;
        List.ItemsSource = _results;
        _syncing = false;
        List.IsVisible = _results.Count > 0;
        _empty.IsVisible = _results.Count == 0;
        _empty.Text = _app.State.IsConnected ? Tr("찾는 객체가 없어요") : Tr("먼저 접속을 연결하세요");
    }

    private void SyncSelection()
    {
        _syncing = true;
        var item = _results.FirstOrDefault(i => i.Id == SelectedId);
        List.SelectedItem = item;
        if (item != null) List.ScrollIntoView(item);
        _syncing = false;
    }

    public void Move(int delta)
    {
        SelectedId = QuickOpen.Move(SelectedId, delta, _results);
        SyncSelection();
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if ((_results.FirstOrDefault(i => i.Id == SelectedId) ?? _results.FirstOrDefault()) is { } item) Open(item);
                e.Handled = true;
                break;
        }
    }

    private Control Row(QuickOpen.Item item)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("18,Auto,*,Auto"), ColumnSpacing = 8 };
        var icon = new TextBlock { Text = KindIcon(item.Kind), Foreground = new SolidColorBrush(Color.Parse(ColorOf(item.Kind))), TextAlignment = TextAlignment.Center };
        var name = new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock? detail = null;
        if (item.Kind == QuickOpen.Kind.Column) detail = new TextBlock { Text = item.Detail, Classes = { "secondary" } };
        else if (item.Detail.Length > 0) detail = new TextBlock { Text = item.Detail, Classes = { "caption" }, Opacity = 0.7 };
        Grid.SetColumn(name, 1);
        g.Children.Add(icon);
        g.Children.Add(name);
        if (detail != null)
        {
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            detail.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(detail, 2);
            g.Children.Add(detail);
        }
        if (item.Kind != QuickOpen.Kind.Database)
        {
            var schema = new TextBlock { Text = item.Schema, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            if (item.Schema == _app.CurrentDatabase) schema.Classes.Add("secondary");
            Grid.SetColumn(schema, 3);
            g.Children.Add(schema);
        }
        return g;
    }

    private static string KindIcon(QuickOpen.Kind k) => k switch
    {
        QuickOpen.Kind.Table => "▦",
        QuickOpen.Kind.View => "👁",
        QuickOpen.Kind.Procedure => "⚙",
        QuickOpen.Kind.Function => "ƒ",
        QuickOpen.Kind.Trigger => "⚡",
        QuickOpen.Kind.Column => "≡",
        _ => "🛢",
    };

    private static string ColorOf(QuickOpen.Kind k) => k switch
    {
        QuickOpen.Kind.Table => "#0A84FF",
        QuickOpen.Kind.View => "#30B0C7",
        QuickOpen.Kind.Procedure or QuickOpen.Kind.Function or QuickOpen.Kind.Trigger => "#AF52DE",
        QuickOpen.Kind.Column => "#8A8A8E",
        _ => "#FF9500",
    };

    /// 다른 스키마의 테이블 · 뷰 (MySQL · PostgreSQL)
    private async Task LoadOthersAsync()
    {
        if (_app.Session is not { } session || !_app.State.IsConnected) return;
        _loading.IsVisible = true;
        try
        {
            var current = _app.CurrentDatabase;
            List<(string Schema, string Name, bool IsView)> rows;
            try { rows = await Task.Run(() => session.AllTablesAsync()); }
            catch { rows = []; }
            _others = QuickOpen.OtherSchemaItems(rows, current);
            Refresh();
            if (_results.All(i => i.Id != SelectedId)) SelectedId = _results.FirstOrDefault()?.Id;
            SyncSelection();
        }
        finally { _loading.IsVisible = false; }
    }

    public void Open(QuickOpen.Item item)
    {
        OpenedItem = item;
        Close();
        _app.Fire(() => _app.OpenQuickItemAsync(item));
    }
}
