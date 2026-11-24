using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 저장 쿼리 창 (Mac SavedQueriesView.swift): 값(AppState.SavedQuerySheet)에 따라 저장 · 목록 · 빈칸 입력 중 하나.
/// 목록에서 빈칸 입력으로 넘어가면 요청이 바뀌어 메인 창이 이 창을 닫고 새로 띄운다 (Mac 은 같은 시트 자리에서 바꿈).
[SheetFor(nameof(AppState.SavedQuerySheet))]
public sealed class SavedQueriesWindow : Window
{
    private readonly AppState _app;

    /// 안에 보이는 화면 (SaveQueryView / SavedQueryListView / SavedQueryFillView)
    public Control View { get; }

    public SavedQueriesWindow(AppState app, object value)
    {
        _app = app;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Title = Tr("저장 쿼리");
        switch (value)
        {
            case SavedQuerySheet.Save s:
                Width = 520;
                SizeToContent = SizeToContent.Height;
                CanResize = false;
                Title = Tr("쿼리 저장");
                View = new SaveQueryView(app, s.Sql, Close);
                break;
            case SavedQuerySheet.Fill f:
                Width = 600;
                SizeToContent = SizeToContent.Height;
                CanResize = false;
                Title = app.SavedQueries[f.QueryId]?.Name ?? Tr("저장 쿼리");
                View = new SavedQueryFillView(app, f.QueryId, Close);
                break;
            default:
                Width = 640;
                Height = 480;
                View = new SavedQueryListView(app, Close);
                break;
        }
        Content = View;
        Opened += (_, _) => (View as IFocusFirst)?.FocusFirst();
    }

    internal interface IFocusFirst { void FocusFirst(); }

    internal static TextBlock Mono(string text, double size = 12) =>
        new() { Text = text, FontFamily = Q.Mono, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
}

// MARK: - 저장 창

/// 쿼리 저장 (Ctrl+Alt+S): 이름 · 폴더 · 범위
public sealed class SaveQueryView : Border, SavedQueriesWindow.IFocusFirst
{
    private readonly AppState _app;
    private readonly string _sql;
    private readonly Action _close;
    public TextBox NameBox { get; }
    public AutoCompleteBox FolderBox { get; }
    public RadioButton AllConnections { get; }
    public RadioButton ThisConnection { get; }
    public Button SaveButton { get; }
    public TextBlock ParamsText { get; }

    public SaveQueryView(AppState app, string sql, Action close)
    {
        _app = app;
        _sql = sql;
        _close = close;
        Padding = new Thickness(20);
        var names = SqlParameters.Names(sql, app.SavedQueryDialect.Kind);
        var preview = new Border
        {
            Height = 110,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new ScrollViewer { Content = new SelectableTextBlock { Text = sql, FontFamily = Q.Mono, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) } },
        };
        preview.Bind(BorderBrushProperty, preview.GetResourceObservable("DividerBrush"));
        NameBox = new TextBox { Text = SavedQuery.DefaultName(sql) };
        FolderBox = new AutoCompleteBox
        {
            ItemsSource = app.SavedQueries.Folders,
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            MinimumPrefixLength = 0,
            PlaceholderText = Tr("선택 사항"),
        };
        AllConnections = new RadioButton { Content = Tr("모든 접속"), GroupName = "scope", IsChecked = true };
        ThisConnection = new RadioButton { Content = Tr("이 접속만"), GroupName = "scope" };
        ThisConnection.IsEnabled = app.SelectedProfileId != null;
        ParamsText = new TextBlock
        {
            Text = names.Count == 0 ? Tr("빈칸 없음") : Tr("빈칸: %@", string.Join(", ", names.Select(n => ":" + n))),
            Classes = { "secondary" },
            TextWrapping = TextWrapping.Wrap,
        };
        SaveButton = Ui.Button(Tr("저장"), Save, isDefault: true);
        NameBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) SaveButton.IsEnabled = TrimmedName.Length > 0; };
        SaveButton.IsEnabled = TrimmedName.Length > 0;

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 8, ColumnSpacing = 10 };
        void Row(int r, string label, Control c)
        {
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(t, r);
            Grid.SetRow(c, r);
            Grid.SetColumn(c, 1);
            form.Children.Add(t);
            form.Children.Add(c);
        }
        Row(0, Tr("이름"), NameBox);
        Row(1, Tr("폴더"), FolderBox);
        Row(2, Tr("보이는 곳"), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { AllConnections, ThisConnection } });

        Child = Ui.Stack(12,
            Ui.Headline("🔖 " + Tr("쿼리 저장")),
            preview,
            form,
            ParamsText,
            Ui.ButtonRow([Ui.Button(Tr("취소"), close, isCancel: true), SaveButton]));
    }

    private string TrimmedName => (NameBox.Text ?? "").Trim();

    public void FocusFirst() { NameBox.Focus(); NameBox.SelectAll(); }

    public void Save()
    {
        if (TrimmedName.Length == 0) return;
        var q = new SavedQuery(TrimmedName, _sql, (FolderBox.Text ?? "").Trim(),
                               ThisConnection.IsChecked == true ? SavedQuery.QueryScope.Profile : SavedQuery.QueryScope.All,
                               _app.SelectedProfileId);
        _app.ChangeSavedQueries(l => l.Add(q));
        _app.Log(LogLevel.Info, LogSource.App, Tr("저장 쿼리에 넣었어요: %@", q.Name));
        _close();
    }
}

// MARK: - 목록 창

/// 저장 쿼리 목록 (Ctrl+Shift+K): 검색 · 폴더별 · 최근 사용 순
public sealed class SavedQueryListView : DockPanel, SavedQueriesWindow.IFocusFirst
{
    private readonly AppState _app;
    private readonly Action _close;
    public TextBox SearchBox { get; }
    public ListBox List { get; }
    private readonly StackPanel _empty;
    private readonly TextBlock _emptyTitle = new() { FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private List<SavedQuery> _ordered = [];
    private bool _syncing;

    /// 이름 · 폴더 바꾸기 입력 (제목, 안내, 처음 값 → 새 값 · 취소면 null). 테스트에서 바꾼다.
    public Func<string, string?, string, Task<string?>> Prompt { get; set; }

    public SavedQueryListView(AppState app, Action close)
    {
        _app = app;
        _close = close;
        Prompt = (title, message, initial) => TextPromptWindow.AskAsync(TopLevel.GetTopLevel(this) as Window, title, message, initial);
        SearchBox = new TextBox { PlaceholderText = Tr("저장 쿼리 검색 (이름 · SQL · 폴더)"), FontSize = 15, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        SearchBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refresh(selectFirst: true); };
        SearchBox.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down: Move(1); e.Handled = true; break;
                case Key.Up: Move(-1); e.Handled = true; break;
                case Key.Enter:
                    if ((Selected ?? _ordered.FirstOrDefault()) is { } q) _app.UseSavedQuery(q.Id, run: true);
                    e.Handled = true;
                    break;
            }
        };
        var top = new DockPanel { Margin = new Thickness(12) };
        var icon = new TextBlock { Text = "⌕", Classes = { "secondary" }, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        top.Children.Add(SearchBox);

        List = new ListBox { Background = Brushes.Transparent };
        List.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            // 폴더 머리글은 고르지 않는다
            if (List.SelectedItem is ListBoxItem { Tag: not SavedQuery }) List.SelectedItem = null;
        };
        List.DoubleTapped += (_, _) => { if (Selected is { } q) _app.UseSavedQuery(q.Id, run: true); };
        List.KeyDown += (_, e) => { if (e.Key == Key.Enter && Selected is { } q) { _app.UseSavedQuery(q.Id, run: true); e.Handled = true; } };
        List.ContextMenu = BuildMenu();

        _empty = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "🔖", FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.5 },
                _emptyTitle,
                new TextBlock { Text = Tr("편집기에서 Ctrl+Alt+S 로 지금 쿼리를 저장하세요."), Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Center },
            },
        };

        var bottom = Ui.ButtonRow([Ui.Button(Tr("닫기"), close, isCancel: true)],
                                  [new TextBlock { Text = Tr("Enter · 더블클릭 실행 · 우클릭 메뉴 · Esc 닫기"), Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center }]);
        bottom.Margin = new Thickness(12, 8);
        SetDock(top, Dock.Top);
        SetDock(bottom, Dock.Bottom);
        Children.Add(top);
        Children.Add(Divider(Dock.Top));
        Children.Add(bottom);
        Children.Add(Divider(Dock.Bottom));
        Children.Add(new Panel { Children = { List, _empty } });
        Refresh(selectFirst: true);
    }

    private static Border Divider(Dock dock)
    {
        var b = new Border { Classes = { "divider" }, Height = 1 };
        SetDock(b, dock);
        return b;
    }

    public void FocusFirst() => SearchBox.Focus();

    /// 고른 저장 쿼리
    public SavedQuery? Selected => (List.SelectedItem as ListBoxItem)?.Tag as SavedQuery;
    /// 지금 보이는 순서 (폴더 묶음 순)
    public IReadOnlyList<SavedQuery> Ordered => _ordered;

    public void Select(Guid? id)
    {
        _syncing = true;
        List.SelectedItem = List.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is SavedQuery q && q.Id == id);
        if (List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem);
        _syncing = false;
    }

    public void Refresh(bool selectFirst = false)
    {
        var keep = Selected?.Id;
        var found = _app.SavedQueries.Search(SearchBox.Text ?? "", _app.SelectedProfileId);
        var groups = SavedQueryLibrary.Grouped(found);
        _ordered = groups.SelectMany(g => g.Queries).ToList();
        var items = new List<ListBoxItem>();
        var dialectKind = _app.SavedQueryDialect.Kind;
        foreach (var g in groups)
        {
            items.Add(new ListBoxItem
            {
                Tag = g.Folder,
                Focusable = false,
                IsHitTestVisible = false,
                Content = new TextBlock { Text = g.Folder.Length == 0 ? Tr("폴더 없음") : g.Folder, Classes = { "caption" }, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
            });
            foreach (var q in g.Queries) items.Add(new ListBoxItem { Tag = q, Content = Row(q, dialectKind) });
        }
        _syncing = true;
        List.ItemsSource = items;
        _syncing = false;
        List.IsVisible = found.Count > 0;
        _empty.IsVisible = found.Count == 0;
        _emptyTitle.Text = _app.SavedQueries.Queries.Count == 0 ? Tr("저장한 쿼리가 없어요") : Tr("찾는 저장 쿼리가 없어요");
        Select(selectFirst || keep == null ? _ordered.FirstOrDefault()?.Id : keep);
    }

    private static Control Row(SavedQuery q, DatabaseKind kind)
    {
        var names = SqlParameters.Names(q.Sql, kind);
        var head = new DockPanel();
        if (q.LastUsedAt is { } used)
        {
            var t = new TextBlock { Text = ModelRow.Relative(used.LocalDateTime), Classes = { "caption" }, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            SetDock(t, Dock.Right);
            head.Children.Add(t);
        }
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        left.Children.Add(new TextBlock { Text = q.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        if (q.Scope == SavedQuery.QueryScope.Profile)
        {
            var link = new TextBlock { Text = "🔗", Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(link, Tr("이 접속만"));
            left.Children.Add(link);
        }
        if (names.Count > 0)
        {
            var p = SavedQueriesWindow.Mono(string.Join(" ", names.Select(n => ":" + n)), 11);
            p.Foreground = Q.Orange;
            left.Children.Add(p);
        }
        head.Children.Add(left);
        var sql = SavedQueriesWindow.Mono(AppState.OneLine(q.Sql, 100), 11);
        sql.Classes.Add("secondary");
        sql.TextTrimming = TextTrimming.CharacterEllipsis;
        return new StackPanel { Spacing = 2, Margin = new Thickness(0, 2), Children = { head, sql } };
    }

    private ContextMenu BuildMenu()
    {
        MenuItem Item(string title, Action<SavedQuery> action)
        {
            var m = new MenuItem { Header = title };
            m.Click += (_, _) => { if (Selected is { } q) action(q); };
            return m;
        }
        return new ContextMenu
        {
            ItemsSource = new Control[]
            {
                Item(Tr("실행"), q => _app.UseSavedQuery(q.Id, run: true)),
                Item(Tr("편집기에 넣기"), q => _app.UseSavedQuery(q.Id, run: false)),
                new Separator(),
                Item(Tr("이름 바꾸기"), q => _app.Fire(() => RenameAsync(q))),
                Item(Tr("폴더 바꾸기"), q => _app.Fire(() => ChangeFolderAsync(q))),
                Item(Tr("복제"), Duplicate),
                new Separator(),
                Item(Tr("삭제…"), q => _app.Fire(() => DeleteAsync(q))),
            },
        };
    }

    public async Task RenameAsync(SavedQuery q)
    {
        if (await Prompt(Tr("이름 바꾸기"), null, q.Name) is not { } name) return;
        _app.ChangeSavedQueries(l => l.Rename(q.Id, name));
        Refresh();
    }

    public async Task ChangeFolderAsync(SavedQuery q)
    {
        var folders = _app.SavedQueries.Folders;
        var message = Tr("비우면 폴더 없음. 있는 폴더: %@", folders.Count == 0 ? "—" : string.Join(", ", folders));
        if (await Prompt(Tr("폴더 바꾸기"), message, q.Folder) is not { } folder) return;
        _app.ChangeSavedQueries(l => l.SetFolder(q.Id, folder));
        Refresh();
    }

    public void Duplicate(SavedQuery q)
    {
        _app.ChangeSavedQueries(l => l.Duplicate(q.Id, Tr(" 사본")));
        Refresh();
    }

    public async Task DeleteAsync(SavedQuery q)
    {
        if (!await _app.Dialogs.ConfirmAsync(Tr("저장 쿼리를 지울까요?"), Tr("'%@' 을(를) 지워요. 되돌릴 수 없어요.", q.Name), Tr("삭제"), Tr("취소"), destructive: true)) return;
        _app.ChangeSavedQueries(l => l.Delete(q.Id));
        Refresh();
    }

    public void Move(int delta)
    {
        if (_ordered.Count == 0) return;
        var i = _ordered.FindIndex(q => q.Id == Selected?.Id);
        if (i < 0) i = delta > 0 ? -1 : _ordered.Count;
        Select(_ordered[Math.Max(0, Math.Min(_ordered.Count - 1, i + delta))].Id);
    }
}

// MARK: - 빈칸 입력 창

/// 빈칸 입력: 값 · NULL · 그대로 넣기. [넣기만] [취소] [실행 (Ctrl+Enter)]
public sealed class SavedQueryFillView : Border, SavedQueriesWindow.IFocusFirst
{
    private readonly AppState _app;
    private readonly Guid _queryId;
    private readonly Action _close;
    private readonly Dictionary<string, (TextBox Value, CheckBox Null, CheckBox Raw)> _rows = [];
    public IReadOnlyList<string> Names { get; }
    public SelectableTextBlock PreviewText { get; } = new() { FontFamily = Q.Mono, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };

    public SavedQueryFillView(AppState app, Guid queryId, Action close)
    {
        _app = app;
        _queryId = queryId;
        _close = close;
        Padding = new Thickness(20);
        var query = app.SavedQueries[queryId];
        Names = query == null ? [] : SqlParameters.Names(query.Sql, app.SavedQueryDialect.Kind);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), RowSpacing = 8, ColumnSpacing = 10 };
        for (var r = 0; r < Names.Count; r++)
        {
            var name = Names[r];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = SavedQueriesWindow.Mono(":" + name, 13);
            var value = new TextBox { Text = query!.LastValues.TryGetValue(name, out var v) ? v : "", PlaceholderText = ":" + name, MinWidth = 220 };
            Avalonia.Automation.AutomationProperties.SetName(value, ":" + name);
            var isNull = new CheckBox { Content = "NULL", VerticalAlignment = VerticalAlignment.Center };
            var raw = new CheckBox { Content = Tr("그대로 넣기"), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(raw, Tr("입력을 SQL 조각으로 그대로 넣어요 (예: NOW(), 1,2,3)"));
            value.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdatePreview(); };
            isNull.IsCheckedChanged += (_, _) =>
            {
                value.IsEnabled = raw.IsEnabled = isNull.IsChecked != true;
                UpdatePreview();
            };
            raw.IsCheckedChanged += (_, _) => UpdatePreview();
            _rows[name] = (value, isNull, raw);
            Control[] cells = [label, value, isNull, raw];
            for (var c = 0; c < cells.Length; c++)
            {
                Grid.SetRow(cells[c], r);
                Grid.SetColumn(cells[c], c);
                grid.Children.Add(cells[c]);
            }
        }
        var preview = new Border
        {
            Height = 90,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new ScrollViewer { Content = PreviewText },
        };
        preview.Bind(BorderBrushProperty, preview.GetResourceObservable("DividerBrush"));

        var insertOnly = Ui.Button(Tr("넣기만"), () => Apply(run: false));
        ToolTip.SetTip(insertOnly, Tr("값을 채운 쿼리를 편집기에 넣고 실행하지 않아요"));
        var run = Ui.Button(Tr("실행"), () => Apply(run: true), accent: true);
        ToolTip.SetTip(run, Tr("값을 채워 실행 (Ctrl+Enter)"));
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter, KeyModifiers.Control), Command = new RelayCommand(() => Apply(run: true)) });

        Child = Ui.Stack(12,
            Ui.Headline("🔖 " + (query?.Name ?? Tr("저장 쿼리"))),
            grid,
            Ui.Caption(Tr("숫자는 그대로, 그 밖은 따옴표로 감싸요. 쉼표로 나누면 목록(IN (…))이 돼요.")),
            preview,
            Ui.ButtonRow([Ui.Button(Tr("취소"), close, isCancel: true), run], [insertOnly]));
        UpdatePreview();
    }

    public void FocusFirst()
    {
        if (Names.Count > 0) _rows[Names[0]].Value.Focus();
    }

    /// 칸 하나 (테스트용)
    public (TextBox Value, CheckBox Null, CheckBox Raw) Field(string name) => _rows[name];

    /// 지금 입력값
    public Dictionary<string, SqlParameters.Input> Inputs =>
        _rows.ToDictionary(kv => kv.Key, kv => new SqlParameters.Input(kv.Value.Value.Text ?? "", kv.Value.Null.IsChecked == true, kv.Value.Raw.IsChecked == true));

    private void UpdatePreview()
    {
        if (_app.SavedQueries[_queryId] is { } q) PreviewText.Text = SqlParameters.Fill(q.Sql, Inputs, _app.SavedQueryDialect);
    }

    public void Apply(bool run) => _app.ApplySavedQuery(_queryId, Inputs, run);
}

/// 글 한 줄 입력 (Mac .alert 의 TextField). 확인이면 입력, 취소면 null.
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _box;
    private string? _result;

    private TextPromptWindow(string title, string? message, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _box = new TextBox { Text = initial };
        var children = new List<Control> { Ui.Headline(title), _box };
        if (message != null) children.Add(Ui.Caption(message));
        children.Add(Ui.ButtonRow([Ui.Button(Tr("취소"), Close, isCancel: true), Ui.Button(Tr("바꾸기"), () => { _result = _box.Text ?? ""; Close(); }, isDefault: true)]));
        Content = new Border { Padding = new Thickness(20), Child = Ui.Stack(12, [.. children]) };
        Opened += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static async Task<string?> AskAsync(Window? owner, string title, string? message, string initial)
    {
        var w = new TextPromptWindow(title, message, initial);
        if (owner is { IsVisible: true }) await w.ShowDialog(owner);
        else
        {
            var done = new TaskCompletionSource();
            w.Closed += (_, _) => done.TrySetResult();
            w.Show();
            await done.Task;
        }
        return w._result;
    }
}
