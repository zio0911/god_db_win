using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Controls;

/// 결과 표의 행 하나 (편집이 반영된 값). 값이나 편집 상태가 바뀌면 Touch 로 보이는 셀을 다시 그린다.
public sealed class GridRow(int id, IReadOnlyList<string?> values) : INotifyPropertyChanged
{
    public int Id { get; } = id;
    public IReadOnlyList<string?> Values { get; private set; } = values;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(IReadOnlyList<string?> values)
    {
        Values = values;
        Touch();
    }

    public void Touch() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

/// 결과 표 행 목록. 큰 결과(최대 200,000행)를 이어 붙여도 표 전체를 다시 만들지 않도록 범위로 알린다.
public sealed class RowList : IList<GridRow>, IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    private readonly List<GridRow> _items = [];
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public int Count => _items.Count;
    public GridRow this[int index] { get => _items[index]; set => throw new NotSupportedException(); }
    object? IList.this[int index] { get => _items[index]; set => throw new NotSupportedException(); }
    public bool IsReadOnly => true;
    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    private void Changed(NotifyCollectionChangedEventArgs e)
    {
        CollectionChanged?.Invoke(this, e);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    /// 전부 바꾼다 (Reset 한 번)
    public void ResetTo(IEnumerable<GridRow> rows)
    {
        _items.Clear();
        _items.AddRange(rows);
        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// index 자리에 여러 행을 넣는다 (조금이면 행마다 Add 알림, 많으면 Reset 한 번)
    public const int BulkThreshold = 64;

    public void InsertRange(int index, IReadOnlyList<GridRow> rows)
    {
        if (rows.Count == 0) return;
        _items.InsertRange(index, rows);
        if (CollectionChanged == null) return;
        // 많이 붙일 때는 Reset 한 번이 행마다 알리는 것보다 훨씬 빠르다 (DataGrid 는 Reset 뒤에도 스크롤 위치 · 선택을 유지한다)
        if (rows.Count > BulkThreshold) { Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)); return; }
        for (var i = 0; i < rows.Count; i++)
            CollectionChanged.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, rows[i], index + i));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public void RemoveTail(int count)
    {
        for (var n = 0; n < count && _items.Count > 0; n++)
        {
            var i = _items.Count - 1;
            var item = _items[i];
            _items.RemoveAt(i);
            Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, i));
        }
    }

    public int IndexOf(GridRow item) => _items.IndexOf(item);
    public bool Contains(GridRow item) => _items.Contains(item);
    public void CopyTo(GridRow[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<GridRow> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    int IList.IndexOf(object? value) => value is GridRow r ? _items.IndexOf(r) : -1;
    bool IList.Contains(object? value) => value is GridRow r && _items.Contains(r);
    void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public void Add(GridRow item) => throw new NotSupportedException();
    int IList.Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, GridRow item) => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    public bool Remove(GridRow item) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}

/// 결과 표 (Mac ResultGrid). 가상화된 DataGrid 에 결과 컬럼을 만들고,
/// 헤더 클릭 정렬은 AppState 에 맡기며(쿼리를 다시 실행), 편집 상태에 따라 셀 모양을 바꾼다.
public sealed class ResultGrid : UserControl
{
    public DataGrid Grid { get; }
    public RowList Rows { get; } = new();
    public QueryResult? Result { get; private set; }
    public PendingEdits Edits { get; private set; } = new();
    private string _filter = "";
    private bool _syncingSelection;
    private readonly Dictionary<int, GridRow> _byId = [];

    /// 헤더 클릭 정렬 (컬럼 이름, 오름차순)
    public Action<string, bool>? OnSort { get; set; }
    /// 편집 가능한 결과면 우클릭 메뉴에 편집 항목을 보여준다
    public bool Editable { get; set; }
    public Action? OnAddRow { get; set; }
    public Action<IReadOnlyCollection<int>>? OnDeleteRows { get; set; }
    /// 끝 근처 행이 보이면 (보이지 않는 페이징: 다음 행 이어 붙이기)
    public Action? OnReachEnd { get; set; }
    /// 클립보드에 쓰기
    public Func<string, Task>? SetClipboard { get; set; }
    /// INSERT 로 복사할 때 쓸 테이블 이름 / 문법
    public string CopyTableName { get; set; } = "table";
    public ISqlDialect? Dialect { get; set; }

    /// 고른 행 id (화면 순서)
    public IReadOnlyList<int> SelectedIds { get; private set; } = [];
    public event Action? SelectedIdsChanged;

    public ResultGrid()
    {
        Grid = new DataGrid
        {
            ItemsSource = Rows,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Extended,
            CanUserSortColumns = true,
            CanUserReorderColumns = false,
            CanUserResizeColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            ClipboardCopyMode = DataGridClipboardCopyMode.None,
            RowHeight = 22,
            FontSize = 12,
        };
        // 행 강조 (마우스가 올라간 행)
        var hover = new SolidColorBrush(Color.FromArgb(0x1F, 0x0A, 0x84, 0xFF));
        Grid.Styles.Add(new Style(x => x.OfType<DataGridRow>().Class(":pointerover").Descendant().OfType<DataGridCell>())
        {
            Setters = { new Setter(DataGridCell.BackgroundProperty, hover) },
        });
        Grid.Styles.Add(new Style(x => x.OfType<DataGridCell>())
        {
            Setters = { new Setter(DataGridCell.PaddingProperty, new Thickness(6, 0)), new Setter(DataGridCell.MinHeightProperty, 20d) },
        });
        Grid.Sorting += OnSorting;
        Grid.TemplateApplied += (_, e) => AttachHeaders(e.NameScope.Find<DataGridColumnHeadersPresenter>("PART_ColumnHeadersPresenter"));
        Grid.LoadingRow += OnLoadingRow;
        Grid.SelectionChanged += (_, _) => OnGridSelectionChanged();
        Grid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        Grid.ContextMenu = new ContextMenu();
        Grid.ContextMenu.Opening += (_, _) => FillContextMenu(Grid.ContextMenu);
        Content = Grid;
    }

    // MARK: - 내용

    /// 결과 · 편집 · 필터를 반영한다. 같은 결과면 행만 맞추고(스크롤·선택 유지), 다른 결과면 컬럼부터 다시 만든다.
    public void Show(QueryResult result, PendingEdits edits, string filter)
    {
        var newResult = !ReferenceEquals(result, Result);
        Result = result;
        Edits = edits;
        _filter = filter;
        if (newResult) BuildColumns(result);
        UpdateHeaders();
        Sync(QueryUi.Filter(edits.DisplayRows(result), filter), reset: newResult);
    }

    /// "제한 없음" 결과에 행이 이어 붙었을 때 (start = 첫 행 번호, count = 행 수)
    public void Append(int start, int count)
    {
        if (Result is not { } r || count <= 0) return;
        var words = ResultCopy.Words(_filter);
        var add = new List<GridRow>(count);
        for (var i = start; i < start + count && i < r.Rows.Count; i++)
        {
            var row = r.Rows[i];
            if (_byId.ContainsKey(row.Id)) continue;
            var values = Edits.Updates.TryGetValue(row.Id, out var ch) ? Apply(row.Values, ch) : row.Values;
            if (words.Count > 0 && !ResultCopy.Matches(values, words)) continue;
            var g = new GridRow(row.Id, values);
            _byId[row.Id] = g;
            add.Add(g);
        }
        // 새 행(추가 중인 행)은 항상 끝에 둔다
        var index = Rows.Count;
        while (index > 0 && Rows[index - 1].Id >= PendingEdits.NewRowBase) index--;
        Rows.InsertRange(index, add);
    }

    private static IReadOnlyList<string?> Apply(IReadOnlyList<string?> values, Dictionary<int, string?> changes)
    {
        var v = values.ToArray();
        foreach (var (i, x) in changes) if (i >= 0 && i < v.Length) v[i] = x;
        return v;
    }

    private void Sync(List<QueryResult.Row> rows, bool reset)
    {
        var cur = Rows.Count;
        var same = !reset;
        if (same)
        {
            var n = Math.Min(cur, rows.Count);
            for (var i = 0; i < n && same; i++) if (Rows[i].Id != rows[i].Id) same = false;
        }
        if (!same)
        {
            _byId.Clear();
            var list = new List<GridRow>(rows.Count);
            foreach (var r in rows)
            {
                var g = new GridRow(r.Id, r.Values);
                _byId[r.Id] = g;
                list.Add(g);
            }
            Rows.ResetTo(list);
            OnGridSelectionChanged();
            return;
        }
        // 앞부분이 같으면 값만 바꾸고 (편집 상태가 바뀐 셀도 다시 그림) 꼬리만 늘이거나 줄인다
        var common = Math.Min(cur, rows.Count);
        for (var i = 0; i < common; i++)
        {
            if (!ReferenceEquals(Rows[i].Values, rows[i].Values)) Rows[i].Update(rows[i].Values);
            else Rows[i].Touch();
        }
        if (rows.Count > cur)
        {
            var add = new List<GridRow>(rows.Count - cur);
            for (var i = cur; i < rows.Count; i++)
            {
                var g = new GridRow(rows[i].Id, rows[i].Values);
                _byId[g.Id] = g;
                add.Add(g);
            }
            Rows.InsertRange(cur, add);
        }
        else if (rows.Count < cur)
        {
            for (var i = rows.Count; i < cur; i++) _byId.Remove(Rows[i].Id);
            Rows.RemoveTail(cur - rows.Count);
        }
        OnGridSelectionChanged();
    }

    private void BuildColumns(QueryResult result)
    {
        Grid.Columns.Clear();
        foreach (var c in result.Columns)
        {
            var index = c.Id;
            Grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = new HeaderLabel(c.Name),
                Tag = c,
                CellTemplate = new FuncDataTemplate<GridRow>((_, _) => new ResultCell(this, index), supportsRecycling: true),
                CanUserSort = true,
                SortMemberPath = "Id",
                Width = new DataGridLength(140),
                MinWidth = 50,
            });
        }
    }

    /// 컬럼 머리글 글자. 같은 이름 컬럼이 있어도 머리글 칸 → 컬럼을 찾을 수 있게 컬럼마다 다른 객체로 둔다.
    private sealed class HeaderLabel(string name)
    {
        public override string ToString() => name;
    }

    /// 컬럼의 정렬 방향 (결과의 실제 정렬 ActiveSort 기준, 같은 이름이면 첫 컬럼 — Mac syncSortOrder).
    /// 머리글에는 이름만 쓰고, 방향은 표의 기본 정렬 화살표(머리글 :sortascending / :sortdescending)로 보인다 (Mac Table 의 기본 화살표).
    public ListSortDirection? SortDirection(DataGridColumn column)
    {
        if (Result is not { ActiveSort: { } s } r || column.Tag is not QueryResult.Column c) return null;
        var first = r.Columns.FirstOrDefault(x => x.Name == s.Column);
        if (first == null || first.Id != c.Id) return null;
        return s.Ascending ? ListSortDirection.Ascending : ListSortDirection.Descending;
    }

    private DataGridColumnHeadersPresenter? _headers;

    private void AttachHeaders(DataGridColumnHeadersPresenter? presenter)
    {
        if (ReferenceEquals(presenter, _headers)) return;
        if (_headers != null) _headers.Children.CollectionChanged -= OnHeadersChanged;
        _headers = presenter;
        if (presenter == null) return;
        presenter.Children.CollectionChanged += OnHeadersChanged;
        foreach (var h in presenter.Children.OfType<DataGridColumnHeader>()) HookHeader(h);
        UpdateSortGlyphs();
    }

    private void OnHeadersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (var h in e.NewItems.OfType<DataGridColumnHeader>()) HookHeader(h);
        UpdateSortGlyphs();
    }

    private readonly HashSet<DataGridColumnHeader> _hooked = [];

    private void HookHeader(DataGridColumnHeader h)
    {
        if (!_hooked.Add(h)) return;
        // 표가 자기 정렬 상태(우리는 쓰지 않음)로 화살표를 지우는 때가 있어 다시 맞춘다
        h.TemplateApplied += (_, _) => SetGlyph(h);
        h.AddHandler(PointerReleasedEvent, (_, _) => Dispatcher.UIThread.Post(UpdateSortGlyphs), RoutingStrategies.Bubble, handledEventsToo: true);
        h.PropertyChanged += (_, e) => { if (e.Property == ContentControl.ContentProperty) SetGlyph(h); };
    }

    private void SetGlyph(DataGridColumnHeader h)
    {
        var column = h.Content == null ? null : Grid.Columns.FirstOrDefault(c => ReferenceEquals(c.Header, h.Content));
        var dir = column == null ? null : SortDirection(column);
        var pc = (IPseudoClasses)h.Classes;
        pc.Set(":sortascending", dir == ListSortDirection.Ascending);
        pc.Set(":sortdescending", dir == ListSortDirection.Descending);
    }

    /// 머리글 정렬 화살표를 결과의 실제 정렬과 맞춘다
    public void UpdateSortGlyphs()
    {
        if (_headers == null) return;
        foreach (var h in _headers.Children.OfType<DataGridColumnHeader>()) SetGlyph(h);
    }

    private void UpdateHeaders() => UpdateSortGlyphs();

    // MARK: - 정렬 · 이어 붙이기

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        // 표가 직접 정렬하지 않는다: 쿼리를 다시 실행해 정렬 (AppState.SortResultAsync)
        e.Handled = true;
        if (Result is not { } r || e.Column.Tag is not QueryResult.Column c) return;
        var ascending = !(r.ActiveSort is { } s && s.Column == c.Name && s.Ascending);
        OnSort?.Invoke(c.Name, ascending);
        Dispatcher.UIThread.Post(UpdateSortGlyphs);
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (OnReachEnd is not { } reachEnd || Result is not { } r) return;
        if (e.Row.DataContext is GridRow g && g.Id < PendingEdits.NewRowBase && g.Id >= r.Rows.Count - 40) reachEnd();
    }

    // MARK: - 선택

    private void OnGridSelectionChanged()
    {
        if (_syncingSelection) return;
        var ids = new List<int>();
        foreach (var item in Grid.SelectedItems) if (item is GridRow g) ids.Add(g.Id);
        // 화면 순서로
        if (ids.Count > 1)
        {
            var set = ids.ToHashSet();
            ids = Rows.Where(r => set.Contains(r.Id)).Select(r => r.Id).ToList();
        }
        if (ids.SequenceEqual(SelectedIds)) return;
        SelectedIds = ids;
        // 행을 고르면 표로 키보드 포커스를 옮긴다 (Ctrl+C 가 편집기가 아니라 표에 가도록 — Mac gridFocused)
        if (ids.Count > 0 && !Grid.IsKeyboardFocusWithin) Grid.Focus();
        SelectedIdsChanged?.Invoke();
    }

    /// id 로 행을 고른다 (없는 id 는 무시)
    public void Select(IEnumerable<int> ids)
    {
        _syncingSelection = true;
        try
        {
            Grid.SelectedItems.Clear();
            GridRow? last = null;
            foreach (var id in ids)
                if (_byId.TryGetValue(id, out var g)) { Grid.SelectedItems.Add(g); last = g; }
            if (last != null) Grid.ScrollIntoView(last, null);
        }
        finally
        {
            _syncingSelection = false;
        }
        OnGridSelectionChanged();
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 우클릭한 행이 선택에 없으면 그 행을 고른다 (Mac contextMenu(forSelectionType:))
        if (!e.GetCurrentPoint(Grid).Properties.IsRightButtonPressed) return;
        var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);
        if (row?.DataContext is GridRow g && !Grid.SelectedItems.Contains(g))
        {
            Grid.SelectedItems.Clear();
            Grid.SelectedItem = g;
        }
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+C: 고른 행을 TSV(머리글 포함)로 — 엑셀 · 스프레드시트에 바로 붙여 넣을 수 있다
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
        {
            CopySelection(ExportFormat.Tsv);
            e.Handled = true;
        }
    }

    // MARK: - 복사

    /// 고른 행을 화면 순서대로 글자로
    public string CopyText(ExportFormat format, IReadOnlyCollection<int>? ids = null)
    {
        if (Result is not { } r) return "";
        var set = (ids ?? SelectedIds).ToHashSet();
        var picked = Rows.Where(x => set.Contains(x.Id)).Select(x => x.Values).ToList();
        if (picked.Count == 0) return "";
        return ResultCopy.Text(format, r.Columns.Select(c => c.Name).ToList(), picked, CopyTableName, Dialect);
    }

    /// 한 컬럼 값을 IN 목록으로
    public string InListText(int column, IReadOnlyCollection<int>? ids = null)
    {
        var set = (ids ?? SelectedIds).ToHashSet();
        var values = Rows.Where(x => set.Contains(x.Id)).Select(x => column < x.Values.Count ? x.Values[column] : null);
        return ResultCopy.InList(values, Dialect != null ? Dialect.QuoteString : null);
    }

    private void CopySelection(ExportFormat format)
    {
        var text = CopyText(format);
        if (text.Length > 0) _ = SetClipboard?.Invoke(text);
    }

    private void FillContextMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var ids = SelectedIds;
        MenuItem Item(string text, Action action, bool enabled = true)
        {
            var m = new MenuItem { Header = text, IsEnabled = enabled };
            m.Click += (_, _) => action();
            return m;
        }
        if (Editable)
        {
            menu.Items.Add(Item(Tr("이 행 편집"), () => Select(ids), ids.Count == 1));
            menu.Items.Add(Item(Tr("행 추가"), () => OnAddRow?.Invoke()));
            var existing = ids.Where(i => i < PendingEdits.NewRowBase).ToList();
            var allDeleted = ids.Count > 0 && existing.All(Edits.Deletes.Contains);
            menu.Items.Add(Item(allDeleted ? Tr("삭제 표시 해제") : Tr("선택 행 삭제"), () => OnDeleteRows?.Invoke(ids), ids.Count > 0));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item(Tr("복사 (Ctrl+C)"), () => CopySelection(ExportFormat.Tsv), ids.Count > 0));
        var other = new MenuItem { Header = Tr("다른 형식으로 복사"), IsEnabled = ids.Count > 0 };
        foreach (var f in new[] { ExportFormat.Csv, ExportFormat.Tsv, ExportFormat.Sql, ExportFormat.Json, ExportFormat.Markdown })
            other.Items.Add(Item(f.Title(), () => CopySelection(f)));
        menu.Items.Add(other);
        var inList = new MenuItem { Header = Tr("IN 목록으로 복사"), IsEnabled = ids.Count > 0 };
        if (Result is { } r)
            foreach (var c in r.Columns)
            {
                var col = c.Id;
                inList.Items.Add(Item(c.Name, () => { var t = InListText(col); _ = SetClipboard?.Invoke(t); }));
            }
        menu.Items.Add(inList);
    }

    // MARK: - 셀

    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF9500"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#34C759"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF3B30"));
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    internal static IBrush? ToneBrush(CellTone tone, Control owner) => tone switch
    {
        CellTone.Orange => Orange,
        CellTone.Green => Green,
        CellTone.Red => Red,
        CellTone.Secondary => owner.TryFindResource("SecondaryText", owner.ActualThemeVariant, out var s) ? s as IBrush : Brushes.Gray,
        CellTone.Tertiary => owner.TryFindResource("TertiaryText", owner.ActualThemeVariant, out var t) ? t as IBrush : Brushes.Gray,
        _ => null,
    };

    /// 한 칸 (한 줄 미리보기). 행이 바뀌거나(재활용) 값 · 편집 상태가 바뀌면 다시 그린다.
    private sealed class ResultCell : TextBlock
    {
        private readonly ResultGrid _grid;
        private readonly int _column;
        private GridRow? _row;

        public ResultCell(ResultGrid grid, int column)
        {
            _grid = grid;
            _column = column;
            FontFamily = Mono;
            FontSize = 12;
            TextTrimming = TextTrimming.CharacterEllipsis;
            TextWrapping = TextWrapping.NoWrap;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_row != null) _row.PropertyChanged -= OnRowChanged;
            _row = DataContext as GridRow;
            if (_row != null) _row.PropertyChanged += OnRowChanged;
            Refresh();
        }

        private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

        private void Refresh()
        {
            if (_row == null) { Text = ""; return; }
            var value = _column < _row.Values.Count ? _row.Values[_column] : null;
            var look = QueryUi.Cell(_grid.Edits.State(_row.Id, _column), value);
            Text = look.Text;
            FontWeight = look.Bold ? FontWeight.SemiBold : FontWeight.Normal;
            TextDecorations = look.Strike ? Avalonia.Media.TextDecorations.Strikethrough : null;
            if (ToneBrush(look.Tone, this) is { } b) Foreground = b;
            else ClearValue(ForegroundProperty);
        }
    }
}
