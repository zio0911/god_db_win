using System.Collections;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace GodDB.App.Views;

/// 칸 너비 — Mac `.width(min:ideal:max:)` (Min == Max 면 `.width(n)` 고정, 크기 조절 안 됨)
public readonly record struct RedisWidth(double Min, double Ideal, double Max)
{
    public static RedisWidth Fixed(double w) => new(w, w, w);
    public bool IsFixed => Min.Equals(Max);
}

/// 표 한 칸 정의 — Mac TableColumn. Width 가 없으면 남은 자리를 나눠 갖는 칸 (Mac 기본).
public sealed record RedisColumn(string Title, Func<object, string> Text)
{
    public RedisWidth? Width { get; init; }
    /// 글자 색 (Mac .foregroundStyle(.orange/.red …)); null 이면 기본
    public Func<object, IBrush?>? Brush { get; init; }
    /// 흐린 글자 (Mac .secondary)
    public Func<object, bool>? Secondary { get; init; }
    public bool Mono { get; init; }
    /// Mac .font(.caption)
    public bool Caption { get; init; }
    /// Mac .lineLimit(n)
    public int MaxLines { get; init; } = 1;
    /// Mac .textSelection(.enabled)
    public bool Selectable { get; init; }
    /// 아래 작은 줄 (Mac VStack { Text; Text.caption.secondary })
    public Func<object, string?>? Sub { get; init; }
}

/// 표의 행 선택 — Mac Table(selection:) 이 무엇에 묶였는지
public enum RedisSelection { None, Single, Multiple }

/// Mac SwiftUI Table 대응: 칸 너비 조절(최소·최대), 머리글 눌러 정렬 없음, 행 선택(없음/하나/여럿),
/// 오른쪽 클릭 메뉴(Mac contextMenu(forSelectionType:) — 고른 행 밖을 누르면 그 행만), 값이 바뀌어도
/// 같은 행은 그대로 두어 스크롤 · 선택이 유지된다.
public sealed class RedisTable : UserControl
{
    private readonly ObservableCollection<object> _rows = [];
    private readonly TextBlock _empty;
    private readonly Func<object, object> _key;
    private readonly RedisSelection _selection;
    private Func<IReadOnlyList<object>, IReadOnlyList<Control>?>? _menu;

    public DataGrid Grid { get; }
    public IReadOnlyList<RedisColumn> Columns { get; }

    /// 다시 채우면서 선택을 되돌리는 중 (SelectionChanged 받는 쪽은 입력 칸을 건드리지 않는다 — Mac 은 선택이 그대로라 onChange 가 안 돈다)
    public bool Restoring { get; private set; }

    public event EventHandler? SelectionChanged;

    public RedisTable(IReadOnlyList<RedisColumn> columns, RedisSelection selection, string empty = "", Func<object, object>? key = null)
    {
        Columns = columns;
        _selection = selection;
        _key = key ?? (o => o);
        Grid = new DataGrid
        {
            ItemsSource = _rows,
            IsReadOnly = true,
            CanUserSortColumns = false,
            CanUserResizeColumns = true,
            CanUserReorderColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            SelectionMode = selection == RedisSelection.Multiple ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single,
            ClipboardCopyMode = DataGridClipboardCopyMode.None,
            Background = Brushes.Transparent,
        };
        foreach (var c in columns) Grid.Columns.Add(MakeColumn(c));
        Grid.SelectionChanged += (_, _) =>
        {
            // Mac Table(rows) — selection 없이 그린 표는 행이 골라지지 않는다
            if (_selection == RedisSelection.None)
            {
                if (Grid.SelectedItem != null) Grid.SelectedItem = null;
                return;
            }
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        Grid.ContextRequested += OnContextRequested;
        _empty = new TextBlock
        {
            Text = empty, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false, IsVisible = false, Classes = { "secondary" },
        };
        Content = new Panel { Children = { Grid, _empty } };
    }

    /// 오른쪽 클릭 메뉴 — 받은 행들(행 순서)로 메뉴 항목을 만든다. 빈 목록/null 이면 메뉴 없음.
    public Func<IReadOnlyList<object>, IReadOnlyList<Control>?>? Menu
    {
        get => _menu;
        set
        {
            _menu = value;
            // 행 메뉴가 있는 표는 글 선택 칸의 '복사' 메뉴 대신 행 메뉴가 뜬다 (Mac 과 같게)
            Grid.Columns.Clear();
            foreach (var c in Columns) Grid.Columns.Add(MakeColumn(c));
        }
    }

    public IReadOnlyList<object> Rows => _rows;
    public int Count => _rows.Count;

    public string EmptyText
    {
        get => _empty.Text ?? "";
        set { _empty.Text = value; UpdateEmpty(); }
    }

    /// 빈 표 안내를 강제로 보이거나 숨긴다 (null 이면 행이 없고 안내 글이 있을 때)
    public bool? ShowEmpty { get; set; }

    /// 고른 행 (행 순서)
    public List<object> Selected
    {
        get
        {
            if (Grid.SelectedItems is not { Count: > 0 } sel) return [];
            var set = new HashSet<object>(sel.Cast<object>(), ReferenceEqualityComparer.Instance);
            return _rows.Where(set.Contains).ToList();
        }
    }

    public List<T> SelectedAs<T>() => Selected.OfType<T>().ToList();

    public void ClearSelection()
    {
        if (Grid.SelectedItems is { Count: > 0 } s) s.Clear();
    }

    public void Select(IEnumerable<object> items)
    {
        var list = items.ToList();
        if (_selection == RedisSelection.Single) { Grid.SelectedItem = list.FirstOrDefault(); return; }
        Grid.SelectedItems.Clear();
        foreach (var o in list) Grid.SelectedItems.Add(o);
    }

    /// 행을 바꾼다. 같은 행(Equals)은 그대로 두고 바뀐 곳만 고쳐서 스크롤 위치를 지키고,
    /// 선택은 키(Mac Table 의 id)가 남아 있으면 되살린다.
    public void SetItems(IEnumerable items)
    {
        var src = items.Cast<object>().ToList();
        var selectedKeys = Selected.Select(_key).ToHashSet();
        Restoring = true;
        try
        {
            Sync(_rows, src);
            if (_selection != RedisSelection.None)
            {
                var want = selectedKeys.Count == 0 ? [] : _rows.Where(r => selectedKeys.Contains(_key(r))).ToList();
                var now = Selected;
                if (!want.SequenceEqual(now, ReferenceEqualityComparer.Instance)) Select(want);
            }
        }
        finally { Restoring = false; }
        UpdateEmpty();
    }

    private void UpdateEmpty() => _empty.IsVisible = ShowEmpty ?? (_rows.Count == 0 && _empty.Text is { Length: > 0 });

    /// 최소한으로 고치기: 앞뒤 같은 부분은 그대로, 위에 새로 붙은 행(실시간)은 끼워 넣고, 가운데는 자리마다 바꿔 넣는다.
    internal static void Sync(ObservableCollection<object> dst, IReadOnlyList<object> src)
    {
        if (dst.Count == 0 || src.Count == 0)
        {
            if (dst.Count == 0 && src.Count == 0) return;
            dst.Clear();
            foreach (var o in src) dst.Add(o);
            return;
        }
        // 위에 새 행이 붙은 경우 (Pub/Sub · 실시간 스트림: 최신이 위)
        if (!Equals(dst[0], src[0]))
        {
            var k = -1;
            for (var i = 1; i < src.Count; i++) if (Equals(src[i], dst[0])) { k = i; break; }
            if (k > 0) for (var i = k - 1; i >= 0; i--) dst.Insert(0, src[i]);
        }
        var p = 0;
        while (p < dst.Count && p < src.Count && Equals(dst[p], src[p])) p++;
        var s = 0;
        while (s < dst.Count - p && s < src.Count - p && Equals(dst[dst.Count - 1 - s], src[src.Count - 1 - s])) s++;
        var oldMid = dst.Count - p - s;
        var newMid = src.Count - p - s;
        var common = Math.Min(oldMid, newMid);
        for (var i = 0; i < common; i++)
            if (!Equals(dst[p + i], src[p + i])) dst[p + i] = src[p + i];
        for (var i = common; i < oldMid; i++) dst.RemoveAt(p + common);
        for (var i = common; i < newMid; i++) dst.Insert(p + i, src[p + i]);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_menu == null) return;
        var clicked = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext;
        var sel = Selected;
        // Mac contextMenu(forSelectionType:): 고른 행을 누르면 고른 행 모두, 아니면 누른 행만
        IReadOnlyList<object> ids = clicked != null && !sel.Contains(clicked, ReferenceEqualityComparer.Instance) ? [clicked] : sel;
        if (ids.Count == 0) return;
        if (_menu(ids) is not { Count: > 0 } items) return;
        e.Handled = true;
        new ContextMenu { ItemsSource = items }.Open(Grid);
    }

    private DataGridTemplateColumn MakeColumn(RedisColumn c)
    {
        var col = new DataGridTemplateColumn
        {
            Header = c.Title,
            CanUserSort = false,
            CellTemplate = new FuncDataTemplate<object>((_, _) => Cell(c), supportsRecycling: true),
        };
        if (c.Width is { } w)
        {
            col.Width = new DataGridLength(w.Ideal);
            col.MinWidth = w.Min;
            col.MaxWidth = w.Max;
            col.CanUserResize = !w.IsFixed;
        }
        else
        {
            col.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            col.MinWidth = 40;
        }
        return col;
    }

    private Control Cell(RedisColumn c)
    {
        TextBlock main = c.Selectable ? new CellText() : new TextBlock();
        // 행 메뉴가 있으면 글 칸 자체의 '복사' 메뉴를 끄고 행 메뉴가 뜨게 (Mac contextMenu(forSelectionType:))
        if (c.Selectable && _menu != null) main.ContextFlyout = null;
        main.TextTrimming = TextTrimming.CharacterEllipsis;
        main.MaxLines = c.MaxLines;
        main.TextWrapping = c.MaxLines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap;
        main.VerticalAlignment = VerticalAlignment.Center;
        if (c.Mono) main.FontFamily = RedisUi.Mono;
        if (c.Caption) main.FontSize = 11;
        TextBlock? sub = null;
        Control root = main;
        if (c.Sub != null)
        {
            sub = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "secondary" } };
            root = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { main, sub } };
        }
        root.Margin = new Thickness(8, 3);
        void Apply()
        {
            var item = root.DataContext;
            if (item == null) { main.Text = null; if (sub != null) sub.IsVisible = false; return; }
            main.Text = c.Text(item);
            main.Classes.Set("secondary", c.Secondary?.Invoke(item) == true);
            if (c.Brush?.Invoke(item) is { } b) main.Foreground = b;
            else main.ClearValue(TextBlock.ForegroundProperty);
            if (sub != null)
            {
                var t = c.Sub!(item);
                sub.Text = t;
                sub.IsVisible = t != null;
            }
        }
        root.DataContextChanged += (_, _) => Apply();
        return root;
    }

    /// 글을 고를 수 있는 칸 (Mac .textSelection(.enabled)). 눌러도 행 선택은 표가 하도록 넘긴다.
    public sealed class CellText : SelectableTextBlock
    {
        protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.ClickCount == 1) e.Handled = false;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            // 글을 골랐으면 Ctrl+C 가 이 칸으로 오게
            if (SelectionStart != SelectionEnd && !IsFocused) Focus();
        }
    }
}
