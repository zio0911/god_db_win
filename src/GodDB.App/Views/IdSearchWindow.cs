using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// ID 로 찾기 (Ctrl+Alt+F): 로그에서 복사한 글을 붙여넣으면 ID 를 뽑아 IN (…) 으로 조회. Mac IDSearchView.swift
[SheetFor(nameof(AppState.IdSearchRequest))]
public sealed class IdSearchWindow : Window
{
    private readonly AppState _app;
    private readonly IdSearchRequest _request;
    private IdSearchMemory _memory;
    private bool _includeText;
    private bool _syncing;

    public ComboBox TableBox { get; } = new() { MinWidth = 200, PlaceholderText = Tr("테이블을 고르세요") };
    public ComboBox ColumnBox { get; } = new() { MinWidth = 200, PlaceholderText = "—" };
    public TextBox PasteBox { get; } = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 140,
        MaxHeight = 260,
        FontFamily = Q.Mono,
    };
    public CheckBox IncludeTextBox { get; } = new();
    public SelectableTextBlock SummaryText { get; } = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 3 };
    public TextBlock OverflowText { get; } = new() { Foreground = Q.Orange, TextWrapping = TextWrapping.Wrap };
    public Button SearchButton { get; }

    public IdSearchWindow(AppState app, object value)
    {
        _app = app;
        _request = (IdSearchRequest)value;
        _memory = app.IdSearchFile.Load();
        Title = Tr("ID 로 찾기");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        TableBox.ItemsSource = Tables.Select(t => t.Name).ToList();
        TableBox.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            FillColumns(DefaultColumn(SelectedTableName));
            Update();
        };
        ColumnBox.SelectionChanged += (_, _) => { if (!_syncing) Update(); };
        PasteBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Update(); };
        Avalonia.Automation.AutomationProperties.SetName(PasteBox, Tr("붙여넣기"));
        IncludeTextBox.Bind(ContentControl.ContentProperty, Loc.Bind("문자 ID 포함 (영문 · 하이픈)"));
        ToolTip.SetTip(IncludeTextBox, Tr("숫자 컬럼에서도 A-77 · UUID 같은 문자 ID 를 뽑아요"));
        IncludeTextBox.IsCheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            _includeText = IncludeTextBox.IsChecked == true;
            Update();
        };

        SearchButton = Ui.Button(Tr("조회"), Submit, accent: true);
        ToolTip.SetTip(SearchButton, "Ctrl+Enter");
        var cancel = Ui.Button(Tr("취소"), Close, isCancel: true);
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter, KeyModifiers.Control), Command = new RelayCommand(Submit) });

        var pickers = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                Ui.Stack(4, Ui.Caption(Tr("테이블")), TableBox),
                Ui.Stack(4, Ui.Caption(Tr("컬럼")), ColumnBox),
            },
        };
        Content = new Border
        {
            Padding = new Thickness(20),
            Child = Ui.Stack(12,
                Ui.Headline("# " + Tr("ID 로 찾기")),
                pickers,
                Ui.Stack(4, Ui.Caption(Tr("붙여넣기")), PasteBox),
                SummaryText,
                OverflowText,
                IncludeTextBox,
                Ui.ButtonRow([cancel, SearchButton])),
        };
        SetDefaults();
        Update();
        Opened += (_, _) => PasteBox.Focus();
    }

    private IReadOnlyList<TableMeta> Tables =>
        _app.Connections.TryGetValue(_request.ProfileId, out var c) ? c.Tables : [];

    private string SelectedTableName => TableBox.SelectedItem as string ?? "";
    private TableMeta? Table => Tables.FirstOrDefault(t => t.Name == SelectedTableName);
    private ColumnMeta? Column => ColumnBox.SelectedIndex >= 0 && Table is { } t && ColumnBox.SelectedIndex < t.Columns.Count ? t.Columns[ColumnBox.SelectedIndex] : null;
    private bool ColumnIsNumeric => Column is { } c && IdSearch.IsNumericType(c.ColumnType);
    /// 숫자만 뽑을지: 숫자 컬럼이고 "문자 ID 포함" 을 켜지 않았을 때
    public bool NumericOnly => ColumnIsNumeric && !_includeText;

    /// 지금 붙여넣은 글에서 뽑은 값
    public IdSearch.Extraction Found => IdSearch.Extract(PasteBox.Text ?? "", NumericOnly);

    private void FillColumns(string select)
    {
        _syncing = true;
        var cols = Table?.Columns ?? [];
        ColumnBox.ItemsSource = cols.Select(c => $"{c.Name} ({c.ColumnType})").ToList();
        ColumnBox.SelectedIndex = cols.FindIndex(c => c.Name == select);
        ColumnBox.IsEnabled = Table != null;
        _syncing = false;
    }

    /// 테이블: 사이드바에서 고른 테이블 → 이 기능에서 마지막으로 쓴 테이블 → 빈칸
    private void SetDefaults()
    {
        var names = Tables.Select(t => t.Name).ToHashSet();
        var sidebar = _request.Table ?? Tables.FirstOrDefault(t => _app.SelectedTables.Contains(t.Name))?.Name;
        var last = _memory.LastTable(_request.ProfileId, _request.Database);
        var table = new[] { sidebar, last }.FirstOrDefault(n => n != null && names.Contains(n)) ?? "";
        _syncing = true;
        TableBox.SelectedItem = table.Length > 0 ? table : null;
        _syncing = false;
        FillColumns(DefaultColumn(table));
    }

    /// 컬럼: 이 테이블에서 마지막으로 쓴 컬럼 → 기본 키(단일 컬럼) → 첫 컬럼
    private string DefaultColumn(string name)
    {
        if (Tables.FirstOrDefault(t => t.Name == name) is not { } t) return "";
        var cols = t.Columns.Select(c => c.Name).ToList();
        if (_memory.LastColumn(_request.ProfileId, _request.Database, name) is { } last && cols.Contains(last)) return last;
        if (t.Indexes.FirstOrDefault(i => i.IsPrimary) is { } pk && pk.Columns.Count == 1 && cols.Contains(pk.Columns[0])) return pk.Columns[0];
        var pkCols = t.Columns.Where(c => c.IsPrimaryKey).ToList();
        if (pkCols.Count == 1) return pkCols[0].Name;
        return cols.FirstOrDefault() ?? "";
    }

    private void Update()
    {
        var found = Found;
        var s = Tr("찾은 값 %@개", found.UniqueCount);
        if (found.Duplicates > 0) s += Tr(" (중복 %@개 제외)", found.Duplicates);
        if (found.Values.Count > 0) s += " : " + IdSearch.Preview(found.Values);
        SummaryText.Text = s;
        SummaryText.Opacity = found.Values.Count == 0 ? 0.6 : 1;
        OverflowText.Text = found.Overflow > 0 ? Tr("1,000개까지만 조회해요 (나머지 %@개는 다시 붙여넣으세요)", found.Overflow.ToString("N0")) : "";
        OverflowText.IsVisible = found.Overflow > 0;
        _syncing = true;
        IncludeTextBox.IsChecked = _includeText || !ColumnIsNumeric;
        IncludeTextBox.IsEnabled = !(Column != null && !ColumnIsNumeric);
        _syncing = false;
        SearchButton.IsEnabled = found.Values.Count > 0 && Column != null;
    }

    public void Submit()
    {
        var found = Found;
        if (Column is not { } column || found.Values.Count == 0) return;
        _memory.Remember(new IdSearchMemory.Entry(_request.ProfileId, _request.Database, SelectedTableName, column.Name));
        _app.IdSearchFile.Save(_memory);
        var (profile, table, numeric) = (_request.ProfileId, SelectedTableName, NumericOnly);
        Close();
        _app.Fire(() => _app.RunIdSearchAsync(profile, table, column.Name, found.Values, numeric));
    }
}

/// 결과 아래 줄: `요청 N · 찾음 N · 없음 N [없는 ID 복사]` (결과가 끝까지 들어온 경우에만). Mac IDSearchMissingBar
public sealed class IdSearchMissingBar : Border
{
    private readonly TextBlock _icon = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _counts = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly SelectableTextBlock _missing = new() { Classes = { "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _copy;
    private IReadOnlyList<string> _missingValues = [];

    /// 없는 ID 를 복사할 때 (줄마다 하나)
    public Func<string, Task>? SetClipboard { get; set; }

    public IdSearchMissingBar()
    {
        Padding = new Thickness(8, 4);
        // Mac: 표와 사이에 Divider()
        BorderThickness = new Thickness(0, 1, 0, 0);
        this.Bind(BorderBrushProperty, this.GetResourceObservable("DividerBrush"));
        _copy = new Button { Content = Tr("없는 ID 복사"), Classes = { "small" }, VerticalAlignment = VerticalAlignment.Center };
        _copy.Click += (_, _) => CopyMissing();
        var row = new DockPanel { LastChildFill = true };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _icon, _counts } };
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(_copy, Dock.Right);
        _missing.Margin = new Thickness(8, 0);
        row.Children.Add(left);
        row.Children.Add(_copy);
        row.Children.Add(_missing);
        Child = row;
    }

    /// 결과가 이 ID 조회의 것이고 잘리지 않았는지
    public static bool Applies(IdSearchRun run, QueryResult result) =>
        result.Sql == run.Sql && !result.HasMore && (result.AppendMode || result.Page == 0)
        && result.Columns.Any(c => string.Equals(c.Name, run.Column, StringComparison.OrdinalIgnoreCase));

    /// 지금 보이는 비교 결과 (테스트용)
    public IdSearch.Comparison? Comparison { get; private set; }
    public string CountsText => _counts.Text ?? "";

    public void Update(IdSearchRun run, QueryResult result)
    {
        if (result.Columns.FirstOrDefault(c => string.Equals(c.Name, run.Column, StringComparison.OrdinalIgnoreCase)) is not { } col) return;
        var cmp = IdSearch.Compare(run.Values, result.Rows.Select(r => col.Id < r.Values.Count ? r.Values[col.Id] : null));
        Comparison = cmp;
        _missingValues = cmp.Missing;
        var ok = cmp.Missing.Count == 0;
        _icon.Text = ok ? "✓" : "!";
        _icon.Foreground = ok ? Q.Green : Q.Orange;
        _counts.Text = Tr("요청 %@ · 찾음 %@ · 없음 %@", cmp.Requested, cmp.Found, cmp.Missing.Count);
        _missing.Text = ok ? "" : IdSearch.Preview(cmp.Missing);
        _missing.IsVisible = _copy.IsVisible = !ok;
    }

    public void CopyMissing()
    {
        if (_missingValues.Count == 0) return;
        _ = SetClipboard?.Invoke(string.Join("\n", _missingValues));
    }
}
