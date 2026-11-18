using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 쿼리 히스토리: 실행한 SQL 을 접속별 · 시간순으로 찾아 편집기로 다시 연다 (Ctrl+Y). Mac HistoryView.swift
[SheetFor(nameof(AppState.ShowHistory))]
public sealed class HistoryWindow : Window
{
    private readonly AppState _app;
    private List<HistoryEntry> _all;
    public TextBox SearchBox { get; }
    public CheckBox OnlyThisConnection { get; }
    public ListBox List { get; }
    private readonly TextBlock _empty = new() { Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = Ui.Caption("");
    private readonly Button _clear;
    private readonly Button _insert;

    public HistoryWindow(AppState app, object value)
    {
        _app = app;
        _all = app.History.All();
        Title = Tr("쿼리 히스토리");
        Width = 760;
        Height = 520;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        SearchBox = new TextBox { PlaceholderText = Tr("SQL · DB 검색 (여러 단어는 모두 포함)") };
        SearchBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refresh(); };
        OnlyThisConnection = new CheckBox { Content = Tr("이 접속만"), IsChecked = true, IsEnabled = app.SelectedProfileId != null, VerticalAlignment = VerticalAlignment.Center };
        OnlyThisConnection.IsCheckedChanged += (_, _) => Refresh();
        var top = new DockPanel { Margin = new Thickness(10) };
        var icon = new GodDB.App.Controls.SymbolIcon(GodDB.App.Controls.Symbol.History) { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(OnlyThisConnection, Dock.Right);
        OnlyThisConnection.Margin = new Thickness(8, 0, 0, 0);
        top.Children.Add(icon);
        top.Children.Add(OnlyThisConnection);
        top.Children.Add(SearchBox);

        List = new ListBox { ItemTemplate = new FuncDataTemplate<HistoryEntry>((e, _) => e == null ? new TextBlock() : Row(e)) };
        List.SelectionChanged += (_, _) => UpdateButtons();
        List.DoubleTapped += (_, _) => { if (List.SelectedItem is HistoryEntry e) Open(e); };
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && List.SelectedItem is HistoryEntry h) { Open(h); e.Handled = true; }
        };
        var menuInsert = new MenuItem { Header = Tr("편집기에 넣기") };
        menuInsert.Click += (_, _) => { if (List.SelectedItem is HistoryEntry e) Open(e); };
        var menuCopy = new MenuItem { Header = Tr("SQL 복사") };
        menuCopy.Click += (_, _) => { if (List.SelectedItem is HistoryEntry e) _app.Fire(() => _app.Dialogs.SetClipboardTextAsync(e.Sql)); };
        List.ContextMenu = new ContextMenu { ItemsSource = new[] { menuInsert, menuCopy } };

        _clear = Ui.Button(Tr("기록 지우기…"), () => _app.Fire(ClearAsync));
        _clear.Classes.Add("danger");
        _insert = Ui.Button(Tr("편집기에 넣기"), () => { if (List.SelectedItem is HistoryEntry e) Open(e); }, isDefault: true);
        var bottom = Ui.ButtonRow([_clear, Ui.Button(Tr("닫기"), Close, isCancel: true), _insert], [_count]);
        bottom.Margin = new Thickness(10);
        _count.VerticalAlignment = VerticalAlignment.Center;

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(Divider(Dock.Top));
        root.Children.Add(bottom);
        root.Children.Add(Divider(Dock.Bottom));
        root.Children.Add(new Panel { Children = { List, _empty } });
        Content = root;
        Refresh();
        Opened += (_, _) => SearchBox.Focus();
    }

    private static Border Divider(Dock dock)
    {
        var b = new Border { Classes = { "divider" }, Height = 1 };
        DockPanel.SetDock(b, dock);
        return b;
    }

    /// 지금 보이는 기록 (검색어 · 이 접속만)
    public List<HistoryEntry> Visible =>
        QueryHistory.Filter(_all, SearchBox.Text ?? "", OnlyThisConnection.IsChecked == true ? _app.SelectedProfileId : null);

    public void Refresh()
    {
        var visible = Visible;
        var selected = List.SelectedItem as HistoryEntry;
        List.ItemsSource = visible;
        if (selected != null && visible.Contains(selected)) List.SelectedItem = selected;
        List.IsVisible = visible.Count > 0;
        _empty.IsVisible = visible.Count == 0;
        _empty.Text = _all.Count == 0 ? Tr("아직 실행한 쿼리가 없어요") : Tr("찾는 쿼리가 없어요");
        _count.Text = Tr("최근 %@건 · 두 번 누르면 편집기에 넣어요", visible.Count);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _clear.IsEnabled = _all.Count > 0;
        _insert.IsEnabled = List.SelectedItem != null;
    }

    private static Control Row(HistoryEntry e)
    {
        var info = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (e.Summary is { } s) right.Children.Add(new TextBlock { Text = s, Classes = { "caption" } });
        if (e.Ok) right.Children.Add(new TextBlock { Text = $"{e.ElapsedMs}ms", Classes = { "caption" } });
        DockPanel.SetDock(right, Dock.Right);
        info.Children.Add(right);
        info.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = e.Ok ? "✓" : "⛔", FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse(e.Ok ? "#34C759" : "#FF3B30")),
                },
                new TextBlock { Text = e.Date.ToString("G", System.Globalization.CultureInfo.CurrentCulture), FontSize = 11 },
                new TextBlock
                {
                    Text = e.Database.Length == 0 ? e.ProfileName : $"{e.ProfileName} / {e.Database}",
                    Classes = { "caption" }, TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        });
        return new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(0, 2),
            Children =
            {
                info,
                new TextBlock { Text = e.Sql, Classes = { "mono" }, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap },
            },
        };
    }

    public void Open(HistoryEntry e)
    {
        _app.Fire(() => _app.OpenFromHistoryAsync(e));
        Close();
    }

    private async Task ClearAsync()
    {
        if (!await _app.Dialogs.ConfirmAsync(Tr("쿼리 히스토리를 모두 지울까요?"), Tr("되돌릴 수 없어요. 감사 로그는 지우지 않아요."),
                                             Tr("지우기"), Tr("취소"), destructive: true)) return;
        _app.History.Clear();
        _all = [];
        Refresh();
    }
}
