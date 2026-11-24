using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 쿼리 화면에서 코드로 만드는 작은 조각 도우미
public static class Q
{
    public static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF9500"));
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#34C759"));
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF3B30"));
    public static readonly IBrush Purple = new SolidColorBrush(Color.Parse("#AF52DE"));
    public static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#0A84FF"));
    public static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#FFCC00"));
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    public static TextBlock T(string text, params string[] classes)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in classes) t.Classes.Add(c);
        return t;
    }

    public static Button B(string text, Action onClick, string? tip = null, params string[] classes)
    {
        var b = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in classes) b.Classes.Add(c);
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// 아이콘 + 글자 단추. 도구 막대가 좁으면(:compact) 글자("label")만 숨고 아이콘은 남는다 (Mac Label + AdaptiveBar)
    public static Button B(GodDB.App.Controls.Symbol symbol, string text, Action onClick, string? tip = null, params string[] classes)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Classes = { "label" } };
        var b = new Button
        {
            Content = H(5, new GodDB.App.Controls.SymbolIcon(symbol) { VerticalAlignment = VerticalAlignment.Center }, label),
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var c in classes) b.Classes.Add(c);
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static StackPanel H(double spacing, params Control[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static Border Divider(bool vertical = false) => vertical
        ? new Border { Width = 1, Height = 16, Classes = { "divider" }, Margin = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center }
        : new Border { Height = 1, Classes = { "divider" } };

    /// 끌어서 크기를 바꾸는 경계선 (Mac ResizeHandle): 1pt 구분선 + 양쪽 3pt 잡는 여백.
    /// Fluent 기본 GridSplitter 는 배경이 창 배경과 같은 색이라 경계가 보이지 않아 직접 그린다.
    public static GridSplitter ResizeHandle(bool rows)
    {
        var s = new GridSplitter
        {
            ResizeDirection = rows ? GridResizeDirection.Rows : GridResizeDirection.Columns,
            Background = Brushes.Transparent,
            Cursor = new Cursor(rows ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast),
            Template = new Avalonia.Controls.Templates.FuncControlTemplate<GridSplitter>((_, _) => new Border
            {
                Background = Brushes.Transparent,
                Child = rows
                    ? new Border { Height = 1, Classes = { "divider" }, VerticalAlignment = VerticalAlignment.Center }
                    : new Border { Width = 1, Classes = { "divider" }, HorizontalAlignment = HorizontalAlignment.Center },
            }),
        };
        if (rows) s.Height = s.MinHeight = 7;
        else s.Width = s.MinWidth = 7;
        return s;
    }

    public static Border Pill(string text, IBrush color)
    {
        var tb = new TextBlock { Text = text, Foreground = color, FontSize = 12 };
        var bg = color is ISolidColorBrush s ? new SolidColorBrush(s.Color, 0.12) : null;
        return new Border { Background = bg, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 2), Child = tb, VerticalAlignment = VerticalAlignment.Center };
    }
}

/// 여러 문장을 실행했을 때 문장별 결과 탭 (Mac ResultTabsBar)
public sealed class ResultTabsBar : Border
{
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 4) };
    public Action<int>? OnSelect { get; set; }

    public ResultTabsBar()
    {
        BorderBrush = null;
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,   // Mac showsIndicators: false
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _panel,
        };
    }

    /// 탭 글자 (테스트용)
    public IReadOnlyList<string> Titles { get; private set; } = [];

    public void Update(IReadOnlyList<QueryResult> results, int selected)
    {
        _panel.Children.Clear();
        var titles = new List<string>();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var (n, kw, summary) = QueryUi.ResultTab(r, i);
            titles.Add($"{n} {kw} {summary}");
            var index = i;
            var b = new Button
            {
                Classes = { "hover" },
                Content = Q.H(4,
                    new TextBlock { Text = n, FontSize = 11, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = kw, FontSize = 11 },
                    Q.T(summary, "caption")),
                Background = i == selected ? new SolidColorBrush(Color.FromArgb(0x2E, 0x0A, 0x84, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
                Padding = new Thickness(8, 3),
            };
            ToolTip.SetTip(b, r.Sql ?? "");
            b.Click += (_, _) => OnSelect?.Invoke(index);
            _panel.Children.Add(b);
        }
        Titles = titles;
    }
}

/// 실행 중 표시: 경과 시간과 취소 버튼 (Mac RunningView)
public sealed class RunningView : StackPanel
{
    private readonly TextBlock _text = Q.T("", "secondary");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    /// Mac ProgressView() (기본 크기 동그란 스피너)
    public ActivitySpinner Spinner { get; } = ActivitySpinner.Regular();
    private DateTime? _started;

    public RunningView(Action onCancel)
    {
        Spacing = 12;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        Spinner.HorizontalAlignment = HorizontalAlignment.Center;
        Children.Add(Spinner);
        Children.Add(_text);
        var cancel = Q.B(Tr("취소"), onCancel, Tr("실행 중인 쿼리 멈추기 (Ctrl+.)"));
        cancel.HorizontalAlignment = HorizontalAlignment.Center;
        Children.Add(cancel);
        _timer.Tick += (_, _) => Tick();
    }

    public string Text => _text.Text ?? "";

    public void Start(DateTime? started)
    {
        _started = started;
        Tick();
        _timer.Start();
        Spinner.IsVisible = true;
    }

    /// 숨길 때 스피너도 멈춘다 (부모가 숨어도 스피너 타이머는 따로 돈다)
    public void Stop()
    {
        _timer.Stop();
        Spinner.IsVisible = false;
    }

    private void Tick()
    {
        var seconds = _started is { } s ? (int)(DateTime.Now - s).TotalSeconds : 0;
        _text.Text = Tr("실행 중… %@초", Math.Max(0, seconds));
    }
}

/// 결과 표 위 빠른 필터 (받아 온 행 안에서만 거른다)
public sealed class QuickFilterBar : Border
{
    public TextBox Box { get; }
    private readonly TextBlock _count = Q.T("", "secondary");
    private readonly Button _clear;
    public event Action<string>? FilterChanged;

    public QuickFilterBar()
    {
        Padding = new Thickness(10, 2);
        Box = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            MinHeight = 22,
            Padding = new Thickness(4, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Box.Bind(TextBox.PlaceholderTextProperty, Loc.Bind("결과 안에서 찾기 (여러 단어는 모두 포함)"));
        Box.TextChanged += (_, _) =>
        {
            UpdateClear();
            FilterChanged?.Invoke(Box.Text ?? "");
        };
        _clear = Q.B("✕", () => Box.Text = "", Tr("필터 지우기"), "borderless");
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        var icon = FilterIcon();
        icon.Margin = new Thickness(0, 0, 4, 0);
        g.Children.Add(icon);
        Grid.SetColumn(Box, 1);
        g.Children.Add(Box);
        Grid.SetColumn(_count, 2);
        g.Children.Add(_count);
        Grid.SetColumn(_clear, 3);
        g.Children.Add(_clear);
        Child = g;
        UpdateClear();
    }

    public string Text
    {
        get => Box.Text ?? "";
        set { if (Box.Text != value) Box.Text = value; }
    }

    /// Mac SF Symbol line.3.horizontal.decrease (거르기 아이콘)
    private static Control FilterIcon()
    {
        var s = new StackPanel { Spacing = 2.5, VerticalAlignment = VerticalAlignment.Center, Classes = { "secondary" } };
        foreach (var w in new[] { 12.0, 8.0, 4.0 })
            s.Children.Add(new Border { Width = w, Height = 1.5, CornerRadius = new CornerRadius(0.75), Background = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        return s;
    }

    public void SetCounts(int shown, int total) => _count.Text = Tr("%@ / %@행", shown, total);

    private void UpdateClear()
    {
        var on = !string.IsNullOrEmpty(Box.Text);
        _clear.IsVisible = on;
        _count.IsVisible = on;
    }
}

/// 실행 계획 주의사항 + 트리 형태 계획 (Mac PlanWarningsView)
public sealed class PlanWarningsView : Border
{
    private readonly StackPanel _panel = new() { Spacing = 4 };
    private bool _showTree;

    public PlanWarningsView()
    {
        Padding = new Thickness(10);
        BorderThickness = new Thickness(0, 0, 0, 1);
        Child = _panel;
    }

    public void Update(QueryResult result, string? tree)
    {
        BorderBrush = this.TryFindResource("DividerBrush", ActualThemeVariant, out var d) ? d as IBrush : null;
        _panel.Children.Clear();
        var warnings = PlanAnalyzer.Warnings(result);
        Background = warnings.Count == 0 ? null : new SolidColorBrush(Color.Parse("#FF9500"), 0.06);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var title = Q.H(8, Q.T("⋔", "headline"), Q.T(Tr("실행 계획"), "headline"),
            warnings.Count == 0
                ? new TextBlock { Text = "✓ " + Tr("특별한 주의사항 없음"), Foreground = Q.Green, VerticalAlignment = VerticalAlignment.Center }
                : new TextBlock { Text = "⚠ " + Tr("주의 %@건", warnings.Count), Foreground = Q.Orange, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(title);
        var right = Q.H(8);
        if (tree != null)
        {
            var toggle = new ToggleSwitch { IsChecked = _showTree, OnContent = Tr("트리로 보기"), OffContent = Tr("트리로 보기") };
            toggle.IsCheckedChanged += (_, _) =>
            {
                _showTree = toggle.IsChecked == true;
                Update(result, tree);
            };
            right.Children.Add(toggle);
        }
        Grid.SetColumn(right, 2);
        head.Children.Add(right);
        _panel.Children.Add(head);
        foreach (var w in warnings)
            _panel.Children.Add(Q.H(6,
                new TextBlock { Text = w.Table, FontFamily = Q.Mono, Foreground = Q.Orange },
                new TextBlock { Text = w.Message, TextWrapping = TextWrapping.Wrap }));
        if (_showTree && tree != null)
            _panel.Children.Add(new Border
            {
                Classes = { "section" },
                Padding = new Thickness(8),
                MaxHeight = 200,
                Child = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new SelectableTextBlock { Text = tree, FontFamily = Q.Mono },
                },
            });
    }
}

/// 결과 위 편집 막대 (Mac EditBar)
public sealed class EditBar : Border
{
    private readonly AppState _app;
    private readonly Func<QueryTab?> _tab;
    private readonly Func<IReadOnlyList<int>> _selection;
    private readonly Action<int> _select;

    public EditBar(AppState app, Func<QueryTab?> tab, Func<IReadOnlyList<int>> selection, Action<int> select)
    {
        _app = app;
        _tab = tab;
        _selection = selection;
        _select = select;
        Padding = new Thickness(8, 3);
        Classes.Add("editbar");
    }

    /// "편집 불가: 이유" 등 지금 보이는 글자 (테스트용)
    public string Message { get; private set; } = "";

    public void Update((EditTarget? Target, Exception? Error) target, PendingEdits edits)
    {
        Background = this.TryFindResource("BarBackground", ActualThemeVariant, out var b) ? b as IBrush : null;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        if (target.Target is { } t)
        {
            Message = Tr("%@ 편집 가능 · 행을 고르면 오른쪽에서 값을 고칠 수 있어요", t.Table.Name);
            var msg = Q.H(6, new TextBlock { Text = "✎", Foreground = Q.Blue, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = Message, Classes = { "caption" }, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(msg);
            var right = Q.H(6,
                Q.B("＋ " + Tr("행 추가"), () =>
                {
                    if (_tab() is { } tab && _app.AddRow(tab.Id) is { } id) _select(id);
                }, null, "hover"));
            var del = Q.B("－ " + Tr("선택 행 삭제"), () =>
            {
                if (_tab() is { } tab) _app.ToggleDelete(tab.Id, _selection());
            }, Tr("한 번 더 누르면 삭제 표시를 풀어요"), "hover");
            del.IsEnabled = _selection().Count > 0;
            right.Children.Add(del);
            if (!edits.IsEmpty)
            {
                right.Children.Add(Q.Divider(vertical: true));
                right.Children.Add(new TextBlock { Text = Tr("변경 %@건", edits.Count), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Q.Orange, VerticalAlignment = VerticalAlignment.Center });
                right.Children.Add(Q.B(Tr("되돌리기"), () => { if (_tab() is { } tab) _app.RevertEdits(tab.Id); }, null, "hover"));
                var apply = Q.B("✓ " + Tr("적용…"), () => { if (_tab() is { } tab) _app.RequestApplyEdits(tab.Id); }, null, "hover");
                apply.Foreground = Q.Blue;
                right.Children.Add(apply);
            }
            Grid.SetColumn(right, 2);
            g.Children.Add(right);
        }
        else
        {
            var reason = target.Error != null ? ErrorText.Describe(target.Error).Message : "";
            Message = Tr("편집 불가: %@", reason);
            var text = new TextBlock { Text = Message, Classes = { "caption" }, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(text, reason);
            g.Children.Add(Q.H(6, Q.T("🔒", "secondary"), text));
        }
        Child = g;
    }
}

/// 결과 아래 줄: 행 범위, 페이지 이동, 전체 행 수, 내보내기 (Mac StatusBar)
public sealed class ResultStatusBar : Border
{
    public Action<int>? OnPage { get; set; }
    public Action? OnCount { get; set; }
    public Action? OnExport { get; set; }
    public Action? OnCancelExport { get; set; }

    /// 지금 보이는 글자들 (테스트용)
    public IReadOnlyList<string> Texts { get; private set; } = [];

    public ResultStatusBar()
    {
        Padding = new Thickness(12, 6);
    }

    public void Update(QueryResult result, int selectedCount, bool loading, ExportProgress? export, bool appendCapped)
    {
        var texts = new List<string>();
        TextBlock Txt(string s, bool primary = false)
        {
            texts.Add(s);
            // 좁아져도 두 줄로 깨지지 않게 (Mac .fixedSize() · lineLimit(1))
            var t = new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextWrapping = TextWrapping.NoWrap };
            if (!primary) t.Classes.Add("secondary");
            return t;
        }
        Button Nav(string glyph, int page, bool enabled, string tip)
        {
            var b = Q.B(glyph, () => OnPage?.Invoke(page), tip, "hover");
            b.IsEnabled = enabled && !loading;
            b.FontSize = 11;
            return b;
        }
        // Mac ProgressView().controlSize(.small)
        Control Spinner() => new ActivitySpinner(16) { HorizontalAlignment = HorizontalAlignment.Center };

        var left = Q.H(12);
        if (result.Columns.Count > 0)
        {
            if (result.AppendMode)
            {
                var s = Q.H(6, Txt(Tr("%@행", QueryUi.N(result.Rows.Count)), primary: true));
                if (loading) s.Children.Add(Spinner());
                s.Children.Add(Txt(QueryUi.AppendInfo(result, appendCapped)));
                left.Children.Add(s);
            }
            else if (result.IsPaged)
            {
                var lastPage = QueryUi.LastPage(result);
                var pager = Q.H(2,
                    Nav(Glyphs.First, 0, result.Page > 0, Tr("첫 페이지")),
                    Nav("‹", result.Page - 1, result.Page > 0, Tr("이전 페이지")));   // Mac chevron.left
                var range = Txt(QueryUi.PageRange(result), primary: true);
                range.Margin = new Thickness(4, 0);
                range.Opacity = loading ? 0.3 : 1;
                var rangeBox = new Panel { Children = { range } };
                if (loading) rangeBox.Children.Add(Spinner());
                pager.Children.Add(rangeBox);
                pager.Children.Add(Nav("›", result.Page + 1, result.HasMore, Tr("다음 페이지")));   // Mac chevron.right
                if (lastPage is { } lp) pager.Children.Add(Nav(Glyphs.Last, lp, result.Page < lp, Tr("마지막 페이지")));
                left.Children.Add(pager);
                left.Children.Add(Txt(QueryUi.PageInfo(result)));
                if (result.TotalCount == null)
                {
                    var count = Q.B(Tr("전체 행 수"), () => OnCount?.Invoke(), Tr("SELECT COUNT(*) 로 전체 행 수를 세요 (큰 테이블은 오래 걸릴 수 있어요)"), "hover");
                    count.IsEnabled = !loading;
                    left.Children.Add(count);
                }
            }
            else
            {
                left.Children.Add(Txt(Tr("%@행", QueryUi.N(result.Rows.Count))));
            }
            if (selectedCount > 0) left.Children.Add(Txt(Tr("%@행 선택", selectedCount)));
        }
        left.Children.Add(Txt(QueryUi.Elapsed(result)));
        if (result.Columns.Count > 0 && selectedCount == 0 && !result.IsPaged)
        {
            var hint = Txt(Tr("행을 클릭하면 상세가 나와요"));
            hint.Classes.Remove("secondary");
            hint.Foreground = this.TryFindResource("TertiaryText", ActualThemeVariant, out var t) ? t as IBrush : Brushes.Gray;
            hint.TextTrimming = TextTrimming.CharacterEllipsis;
            left.Children.Add(hint);
        }

        var right = Q.H(6);
        if (export is { } p)
        {
            right.Children.Add(Spinner());
            right.Children.Add(Txt(QueryUi.ExportText(p)));
            right.Children.Add(Q.B(Tr("취소"), () => OnCancelExport?.Invoke(), null, "hover"));
        }
        else if (result.Columns.Count > 0)
        {
            var exp = Q.B(GodDB.App.Controls.Symbol.Download, Tr("내보내기"), () => OnExport?.Invoke(),
                Tr("결과를 파일로 저장: CSV · TSV · Excel · JSON · SQL INSERT · Markdown (보이는 행 또는 쿼리 전체 데이터)"), "hover");
            exp.IsEnabled = !loading;
            right.Children.Add(exp);
        }
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(new ScrollViewer { Content = left, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        Child = g;
        Texts = texts;
    }
}

/// 선택한 행의 모든 컬럼 값을 전체 텍스트로 보여준다. 편집 가능한 결과면 값을 고칠 수 있다. (Mac RowDetailView)
public sealed class RowDetailPanel : Border
{
    /// 행 상세를 편집기로 쓸 때 필요한 것 (Mac RowEditor)
    public sealed record RowEditor(EditTarget Target, PendingEdits Edits, Action<int, string?> Set, Action<int> ResetToDefault);

    private readonly StackPanel _fields = new() { Spacing = 12, Margin = new Thickness(8) };
    private readonly TextBlock _title = Q.T("", "headline");
    private readonly TextBlock _number = Q.T("", "secondary");
    private readonly TextBlock _deleted = new() { Text = "🗑 " + Tr("삭제 표시된 행이에요. 적용하면 삭제돼요."), FontSize = 11, Foreground = Q.Red, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private string? _key;
    private readonly List<(int Column, TextBlock Name, TextBlock? Count, TextBox? Box)> _views = [];
    private bool _updatingBox;

    public RowDetailPanel(Action onClose)
    {
        Background = null;
        var close = Q.B("✕", onClose, Tr("닫기"), "hover");
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(8) };
        head.Children.Add(_title);
        _number.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_number, 1);
        head.Children.Add(_number);
        Grid.SetColumn(close, 3);
        head.Children.Add(close);
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        var div = Q.Divider();
        DockPanel.SetDock(div, Dock.Top);
        dock.Children.Add(div);
        DockPanel.SetDock(_deleted, Dock.Top);
        dock.Children.Add(_deleted);
        dock.Children.Add(new ScrollViewer { Content = _fields, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Child = dock;
    }

    public string Title => _title.Text ?? "";
    public string NumberText => _number.Text ?? "";
    /// 컬럼별 글자 수 표시 (테스트용)
    public IReadOnlyList<string> CountTexts => _views.Select(v => v.Count?.Text ?? "").ToList();

    public void Update(QueryResult result, GridRow row, int? rowNumber, RowEditor? editor)
    {
        Background = this.TryFindResource("PanelBackground", ActualThemeVariant, out var bg) ? bg as IBrush : null;
        _title.Text = editor == null ? Tr("행 상세") : Tr("행 편집");
        _number.Text = rowNumber is { } n ? Tr("%@번째 행", n) : Tr("새 행");
        var deleted = editor?.Edits.Deletes.Contains(row.Id) == true;
        _deleted.IsVisible = deleted;
        // 구조(편집 가능 여부, NULL, 기본값)가 같으면 글자 · 색만 고친다 (입력 중인 칸의 포커스를 잃지 않게)
        var shape = string.Concat(result.Columns.Select(c =>
        {
            var v = c.Id < row.Values.Count ? row.Values[c.Id] : null;
            var st = editor?.Edits.State(row.Id, c.Id) ?? CellState.Normal;
            return (v == null ? "n" : "v") + (st == CellState.InsertedDefault ? "d" : "");
        }));
        var key = $"{result.Id}|{row.Id}|{editor != null}|{deleted}|{shape}";
        if (key != _key)
        {
            _key = key;
            Build(result, row, editor, deleted);
            return;
        }
        foreach (var (col, name, count, box) in _views)
        {
            var value = col < row.Values.Count ? row.Values[col] : null;
            var state = editor?.Edits.State(row.Id, col) ?? CellState.Normal;
            Color(name, state);
            if (count != null && value != null) count.Text = Tr("%@자", value.Length);
            if (box != null && !box.IsFocused && box.Text != (value ?? ""))
            {
                _updatingBox = true;
                box.Text = value ?? "";
                _updatingBox = false;
            }
        }
    }

    private static void Color(TextBlock name, CellState state)
    {
        if (state == CellState.Modified) name.Foreground = Q.Orange;
        else if (state == CellState.Inserted) name.Foreground = Q.Green;
        else name.ClearValue(TextBlock.ForegroundProperty);
        if (state is not (CellState.Modified or CellState.Inserted))
        {
            if (!name.Classes.Contains("secondary")) name.Classes.Add("secondary");
        }
        else name.Classes.Remove("secondary");
    }

    private void Build(QueryResult result, GridRow row, RowEditor? editor, bool deleted)
    {
        _fields.Children.Clear();
        _views.Clear();
        foreach (var column in result.Columns)
        {
            var col = column.Id;
            var value = col < row.Values.Count ? row.Values[col] : null;
            var editable = editor?.Target.ColumnMap.ContainsKey(col) == true && !deleted;
            var state = editor?.Edits.State(row.Id, col) ?? CellState.Normal;
            var name = new TextBlock { Text = column.Name, FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Color(name, state);
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto") };
            head.Children.Add(name);
            if (editor?.Target.PrimaryKey.Any(k => string.Equals(k, column.Name, StringComparison.OrdinalIgnoreCase)) == true)
            {
                var key = new TextBlock { Text = Glyphs.Key, FontSize = 10, Foreground = Q.Yellow, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(key, 1);
                head.Children.Add(key);
            }
            TextBlock? count = null;
            if (value != null)
            {
                count = new TextBlock { Text = Tr("%@자", value.Length), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
                count.Foreground = this.TryFindResource("TertiaryText", ActualThemeVariant, out var t) ? t as IBrush : Brushes.Gray;
                Grid.SetColumn(count, 3);
                head.Children.Add(count);
                var v = value;
                var copy = new CopyButton { Text = v };
                ToolTip.SetTip(copy, Tr("값 복사"));
                Grid.SetColumn(copy, 4);
                head.Children.Add(copy);
            }
            var field = new StackPanel { Spacing = 4 };
            field.Children.Add(head);
            TextBox? box = null;
            if (editable && editor != null)
            {
                if (state == CellState.InsertedDefault)
                {
                    field.Children.Add(Q.B(Tr("값 입력 (지금은 DB 기본값)"), () => editor.Set(col, ""), null, "hover"));
                }
                else
                {
                    box = new TextBox
                    {
                        Text = value ?? "",
                        FontFamily = Q.Mono,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        MaxHeight = 160,
                        IsEnabled = value != null,
                    };
                    var b = box;
                    box.TextChanged += (_, _) =>
                    {
                        if (_updatingBox || !b.IsEnabled) return;
                        editor.Set(col, LineEndings.Normalize(b.Text ?? ""));
                    };
                    field.Children.Add(box);
                    var nullBox = new CheckBox { Content = "NULL", IsChecked = value == null, FontSize = 11 };
                    nullBox.IsCheckedChanged += (_, _) => editor.Set(col, nullBox.IsChecked == true ? null : "");
                    var opts = Q.H(10, nullBox);
                    if (row.Id >= PendingEdits.NewRowBase)
                        opts.Children.Add(Q.B(Tr("DB 기본값 사용"), () => editor.ResetToDefault(col), null, "hover"));
                    field.Children.Add(opts);
                }
            }
            else
            {
                var text = new SelectableTextBlock
                {
                    Text = value ?? "NULL",
                    FontFamily = Q.Mono,
                    TextWrapping = TextWrapping.Wrap,
                };
                if (value == null) text.Foreground = this.TryFindResource("TertiaryText", ActualThemeVariant, out var tt) ? tt as IBrush : Brushes.Gray;
                field.Children.Add(new Border
                {
                    Background = this.TryFindResource("CodeBackground", ActualThemeVariant, out var cb) ? cb as IBrush : null,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6),
                    Child = text,
                });
            }
            _fields.Children.Add(field);
            _views.Add((col, name, count, box));
        }
    }
}

/// 결과 영역 (Mac ResultPane): 결과 탭 · 실행 중 · 오류 · 실행 계획 · 편집 막대 · 빠른 필터 · 표 · 행 상세 · 아래 줄
public sealed class ResultPane : UserControl
{
    private readonly AppState _app;
    private QueryTab? _tab;

    public ResultTabsBar TabsBar { get; } = new();
    public RunningView Running { get; }
    public ResultGrid Grid { get; } = new();
    public RowDetailPanel Detail { get; }
    public EditBar EditBar { get; }
    public QuickFilterBar Filter { get; } = new();
    public ResultStatusBar Status { get; } = new();
    public PlanWarningsView Plan { get; } = new();
    /// ID 로 찾기 결과 아래 "없는 ID" 줄 (Mac 1.4.0)
    public IdSearchMissingBar IdMissing { get; } = new();
    // Mac .font(.system(.body, design: .monospaced)) = 13pt
    public SelectableTextBlock ErrorText { get; } = new() { Foreground = Q.Red, FontFamily = Q.Mono, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
    private readonly ScrollViewer _errorScroll;
    private readonly TextBlock _message = new() { Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _body;
    private readonly Grid _layout;
    private readonly Border _tabsDivider = Q.Divider();
    private readonly Border _statusDivider = Q.Divider();
    private readonly Border _filterDivider = Q.Divider();
    private readonly Border _editDivider = Q.Divider();
    private readonly GridSplitter _detailSplitter;
    /// 행 상세 폭: 끌어서 바꾸면 저장 (Mac Layout.rowDetailWidth)
    private static double DetailWidth => Layout.Get(Layout.RowDetailWidth);

    // 결과 표에 마지막으로 보여 준 것 (같으면 다시 맞추지 않는다)
    private QueryResult? _shownResult;
    private List<QueryResult.Row>? _shownRows;
    private int _shownCount;
    private string _shownFilter = "";
    private bool _editsDirty;
    private Guid? _lastResultId;
    private string? _lastSql;

    public ResultPane(AppState app)
    {
        _app = app;
        Running = new RunningView(() => { if (_tab is { } t) _app.Fire(() => _app.CancelQueryAsync(t.Id)); });
        Detail = new RowDetailPanel(() => Grid.Select([]));
        EditBar = new EditBar(app, () => _tab, () => Grid.SelectedIds, id => Grid.Select([id]));
        // 오류 글이 160 을 넘으면 Mac 처럼 스크롤 (자동 숨김 스크롤 막대면 아래 줄이 그냥 잘려 보여서 막대를 늘 보인다)
        _errorScroll = new ScrollViewer
        {
            Content = ErrorText,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AllowAutoHide = false,
        };
        TabsBar.OnSelect = i =>
        {
            if (_tab is not { } t) return;
            var id = t.Id;
            _app.ConfirmDiscardingEdits([id], () => _app.SelectResult(i, id));
        };
        Status.OnPage = page =>
        {
            if (_tab is not { } t) return;
            var id = t.Id;
            _app.ConfirmDiscardingEdits([id], () => _app.Fire(() => _app.LoadPageAsync(page, id)));
        };
        Status.OnCount = () => { if (_tab is { } t) _app.Fire(() => _app.CountRowsAsync(t.Id)); };
        Status.OnExport = () => { if (_tab is { } t) _app.ExportResult(t.Id); };
        Status.OnCancelExport = () => { if (_tab is { } t) _app.CancelExport(t.Id); };
        Grid.OnSort = (column, ascending) => { if (_tab is { } t) _app.Fire(() => _app.SortResultAsync(t.Id, column, ascending)); };
        Grid.OnAddRow = () => { if (_tab is { } t && _app.AddRow(t.Id) is { } id) Grid.Select([id]); };
        Grid.OnDeleteRows = ids => { if (_tab is { } t) _app.ToggleDelete(t.Id, ids); };
        Grid.SetClipboard = text => _app.Dialogs.SetClipboardTextAsync(text);
        Grid.SelectedIdsChanged += OnSelectionChanged;
        Filter.FilterChanged += _ => Refresh();

        _detailSplitter = Q.ResizeHandle(rows: false);
        _detailSplitter.DragCompleted += (_, _) =>
        {
            if (_body!.ColumnDefinitions[2].ActualWidth > 0) Layout.Set(Layout.RowDetailWidth, _body.ColumnDefinitions[2].ActualWidth);
        };
        _body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,0") };
        _body.ColumnDefinitions[2].Width = new GridLength(DetailWidth);
        Layout.OnReset(this, () => { if (Detail.IsVisible) _body.ColumnDefinitions[2].Width = new GridLength(DetailWidth); });
        _body.ColumnDefinitions[0].MinWidth = 200;
        _body.ColumnDefinitions[2].MinWidth = 240;
        _body.Children.Add(Grid);
        Avalonia.Controls.Grid.SetColumn(_detailSplitter, 1);
        _body.Children.Add(_detailSplitter);
        Avalonia.Controls.Grid.SetColumn(Detail, 2);
        _body.Children.Add(Detail);

        // 줄: 0 결과 탭, 1 구분선, 2 오류, 3 실행 계획, 4 편집 막대, 5 구분선, 6 필터, 7 구분선, 8 본문(실행 중 · 메시지 · 표),
        //     9 없는 ID (ID 로 찾기), 10 구분선, 11 아래 줄
        _layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,*,Auto,Auto,Auto") };
        void Put(Control c, int row) { Avalonia.Controls.Grid.SetRow(c, row); _layout.Children.Add(c); }
        Put(TabsBar, 0);
        Put(_tabsDivider, 1);
        Put(_errorScroll, 2);
        Put(Plan, 3);
        Put(EditBar, 4);
        Put(_editDivider, 5);
        Put(Filter, 6);
        Put(_filterDivider, 7);
        Put(_body, 8);
        Put(Running, 8);
        Put(_message, 8);
        Put(IdMissing, 9);
        Put(_statusDivider, 10);
        Put(Status, 11);
        IdMissing.SetClipboard = text => _app.Dialogs.SetClipboardTextAsync(text);
        Content = _layout;
    }

    /// 행 상세 칸 폭 (테스트용)
    public double DetailColumnWidth => _body.ColumnDefinitions[2].Width.Value;

    public QueryTab? Tab => _tab;

    public void Bind(QueryTab? tab)
    {
        if (ReferenceEquals(tab, _tab)) { Refresh(); return; }
        if (_tab != null)
        {
            _tab.PropertyChanged -= OnTabChanged;
            _tab.RowsAppended -= OnRowsAppended;
        }
        _tab = tab;
        _shownResult = null;
        _shownRows = null;
        _lastResultId = null;
        _lastSql = null;
        Filter.Text = "";
        if (tab != null)
        {
            tab.PropertyChanged += OnTabChanged;
            tab.RowsAppended += OnRowsAppended;
        }
        Refresh();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(QueryTab.Text) or nameof(QueryTab.SavedText) or nameof(QueryTab.FilePath) or nameof(QueryTab.IsDirty)
                or nameof(QueryTab.Title) or nameof(QueryTab.Number) or nameof(QueryTab.CustomTitle) or nameof(QueryTab.LastSql):
                return;
            case nameof(QueryTab.Edits):
                _editsDirty = true;
                break;
        }
        Refresh();
    }

    private void OnRowsAppended(QueryTab tab, int start, int count)
    {
        if (!ReferenceEquals(tab.Result, _shownResult) || tab.Result is not { } r) return;
        Grid.Append(start, count);
        _shownCount = r.Rows.Count;
        _shownRows = r.Rows;
    }

    private void OnSelectionChanged()
    {
        UpdateDetail();
        if (_tab?.Result is { } r && r.Columns.Count > 0)
        {
            Status.Update(r, Grid.SelectedIds.Count, _tab.PageLoading || !_tab.Edits.IsEmpty, _tab.ExportProgress, _tab.AppendCapped);
            if (EditBar.IsVisible && _app.EditTarget(_tab) is { } target) EditBar.Update(target, _tab.Edits);
        }
    }

    private readonly HashSet<Control> _visible = [];
    private bool _refreshing;
    private bool _bodyShown;

    /// 탭 상태로 화면을 다시 맞춘다 (보일 조각을 모은 뒤 한 번에 보이기/숨기기 — 표가 잠깐 숨었다 나타나지 않게)
    public void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        _visible.Clear();
        try { RefreshCore(); }
        finally
        {
            foreach (var c in _layout.Children) c.IsVisible = _visible.Contains(c);
            _bodyShown = _visible.Contains(_body);
            _refreshing = false;
        }
        UpdateDetail();
    }

    private void Vis(params Control[] controls)
    {
        foreach (var c in controls) _visible.Add(c);
    }

    private void RefreshCore()
    {
        var tab = _tab;
        if (tab == null)
        {
            Running.Stop();
            _message.Text = Tr("쿼리를 실행하면 결과가 여기 나와요.");
            Vis(_message);
            return;
        }
        var result = tab.Result;
        // 결과가 바뀌면 (새 실행) 1행이면 상세를 바로 열고, SQL 이 바뀌면 필터를 지운다
        if (result?.Sql != _lastSql)
        {
            _lastSql = result?.Sql;
            if (Filter.Text.Length > 0) Filter.Text = "";
        }
        var resultChanged = result?.Id != _lastResultId;
        _lastResultId = result?.Id;

        if (!tab.Running && tab.Results.Count > 1)
        {
            TabsBar.Update(tab.Results, tab.ResultIndex);
            Vis(TabsBar, _tabsDivider);
        }
        if (tab.Running)
        {
            Vis(Running);
            Running.Start(tab.RunStarted);
            return;
        }
        Running.Stop();
        if (tab.Error is { } error)
        {
            ErrorText.Text = error;
            Vis(_errorScroll);
            var hasResults = tab.Results.Count > 0 && result != null;
            _errorScroll.MaxHeight = hasResults ? 160 : double.PositiveInfinity;
            _layout.RowDefinitions[2].Height = hasResults ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            if (!hasResults) return;
            _layout.RowDefinitions[2].Height = GridLength.Auto;
            if (result!.Columns.Count == 0)
            {
                _message.Text = result.Message ?? Tr("실행 완료");
                Vis(_message);
            }
            else
            {
                Grid.Editable = false;
                ShowGrid(tab, result, new PendingEdits(), "", resultChanged, showDetail: false);
            }
            return;
        }
        _layout.RowDefinitions[2].Height = GridLength.Auto;
        if (result == null)
        {
            _message.Text = Tr("쿼리를 실행하면 결과가 여기 나와요.");
            Vis(_message);
            return;
        }
        if (tab.IsPlan)
        {
            Plan.Update(result, tab.PlanTree);
            Vis(Plan);
        }
        if (result.Columns.Count == 0)
        {
            _message.Text = result.Message ?? Tr("실행 완료");
            Vis(_message);
        }
        else
        {
            var target = _app.EditTarget(tab);
            if (target is { } t)
            {
                EditBar.Update(t, tab.Edits);
                Vis(EditBar, _editDivider);
            }
            Grid.Editable = target is { Target: not null };
            Grid.CopyTableName = target is { Target: { } et } ? et.Table.Name : "table";
            Grid.Dialect = _app.Connections.TryGetValue(tab.ProfileId, out var info) ? info.Session?.Dialect : null;
            Grid.OnReachEnd = result.AppendMode ? () => _app.Fire(() => _app.LoadMoreAsync(tab.Id)) : null;
            Vis(Filter, _filterDivider);
            ShowGrid(tab, result, tab.Edits, Filter.Text, resultChanged, showDetail: true);
            Filter.SetCounts(Grid.Rows.Count, result.Rows.Count + tab.Edits.Inserts.Count);
            if (tab.IdSearch is { } run && IdSearchMissingBar.Applies(run, result))
            {
                IdMissing.Update(run, result);
                Vis(IdMissing);
            }
        }
        Status.Update(result, Grid.SelectedIds.Count, tab.PageLoading || !tab.Edits.IsEmpty, tab.ExportProgress, tab.AppendCapped);
        Vis(Status, _statusDivider);
    }

    private bool _showDetail;

    private void ShowGrid(QueryTab tab, QueryResult result, PendingEdits edits, string filter, bool resultChanged, bool showDetail)
    {
        _showDetail = showDetail;
        Vis(_body);
        var need = !ReferenceEquals(result, _shownResult) || !ReferenceEquals(result.Rows, _shownRows) || _editsDirty
                   || filter != _shownFilter || result.Rows.Count != _shownCount;
        if (need)
        {
            Grid.Show(result, edits, filter);
            _shownResult = result;
            _shownRows = result.Rows;
            _shownCount = result.Rows.Count;
            _shownFilter = filter;
            _editsDirty = false;
        }
        if (resultChanged)
        {
            // 결과가 1행이면 (SHOW CREATE 등) 바로 상세를 연다.
            if (result.Rows.Count == 1) Grid.Select([result.Rows[0].Id]);
            else Grid.Select([]);
        }
    }

    private void UpdateDetail()
    {
        var tab = _tab;
        var row = Grid.SelectedIds.Count == 1 && _showDetail && tab?.Result is { } ? Grid.Rows.FirstOrDefault(r => r.Id == Grid.SelectedIds[0]) : null;
        var show = row != null && tab?.Result is { } && _bodyShown;
        Detail.IsVisible = _detailSplitter.IsVisible = show;
        _body.ColumnDefinitions[2].Width = show ? new GridLength(DetailWidth) : new GridLength(0);
        _body.ColumnDefinitions[2].MinWidth = show ? 240 : 0;
        if (!show || tab?.Result is not { } result) return;
        RowDetailPanel.RowEditor? editor = null;
        if (_app.EditTarget(tab) is { Target: { } target })
        {
            var tabId = tab.Id;
            var id = row!.Id;
            editor = new RowDetailPanel.RowEditor(target, tab.Edits,
                (col, value) => _app.SetCell(tabId, id, col, value),
                col => _app.ResetInsertedCell(tabId, id, col));
        }
        Detail.Update(result, row!, row!.Id >= PendingEdits.NewRowBase ? null : result.RowOffset + row.Id + 1, editor);
    }
}
