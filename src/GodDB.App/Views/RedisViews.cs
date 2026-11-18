using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

// Redis 화면의 탭별 내용 (Mac RedisViews.swift). 바뀌는 부분이 많아 코드로 만든다.

/// 작은 화면 도우미 (표 · 막대 · 색)
internal static class RedisUi
{
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF3B30"));
    public static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF9500"));
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#34C759"));
    public static readonly IBrush Purple = new SolidColorBrush(Color.Parse("#AF52DE"));
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#0A84FF"));

    public static FontFamily Mono => Application.Current?.TryFindResource("MonoFont", out var f) == true && f is FontFamily ff ? ff : FontFamily.Default;

    public static Button Btn(string text, Action onClick, string cls = "hover", string? tip = null)
    {
        var b = new Button { Content = text };
        b.Classes.Add(cls);
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static TextBlock Label(string text, params string[] classes)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in classes) t.Classes.Add(c);
        return t;
    }

    /// 아래 막대 (Mac .background(.bar))
    public static Border Bar(Control child, bool top = true) => new()
    {
        Background = Application.Current?.TryFindResource("BarBackground", Application.Current.ActualThemeVariant, out var b) == true ? b as IBrush : null,
        BorderBrush = Application.Current?.TryFindResource("DividerBrush", Application.Current.ActualThemeVariant, out var d) == true ? d as IBrush : null,
        BorderThickness = top ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1),
        Padding = new Thickness(8),
        Child = child,
    };

    /// 왼쪽은 차례로, 마지막 칸이 남은 자리를 차지하는 가로 줄
    public static Grid Row(double spacing, IEnumerable<Control> left, IEnumerable<Control>? right = null)
    {
        var l = left.ToList();
        var r = right?.ToList() ?? [];
        var g = new Grid { ColumnSpacing = spacing };
        foreach (var _ in l) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        g.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        foreach (var _ in r) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var i = 0;
        foreach (var c in l) { Grid.SetColumn(c, i++); g.Children.Add(c); }
        i++;
        foreach (var c in r) { Grid.SetColumn(c, i++); g.Children.Add(c); }
        return g;
    }

    /// 머리글 없는 목록 (Mac List). 빈 목록이면 empty 글을 가운데에.
    public static (Control View, ListBox List, TextBlock Empty) PlainList(Func<object, Control> row, SelectionMode mode, string empty = "")
    {
        var list = new ListBox { SelectionMode = mode, Background = Brushes.Transparent };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 3)), new Setter(Layoutable.MinHeightProperty, 22d) },
        });
        list.ItemTemplate = new FuncDataTemplate<object>((item, _) => item == null ? new Panel() : row(item), supportsRecycling: false);
        var emptyText = Label(empty, "secondary");
        emptyText.HorizontalAlignment = HorizontalAlignment.Center;
        emptyText.TextWrapping = TextWrapping.Wrap;
        emptyText.IsHitTestVisible = false;
        return (new Panel { Children = { list, emptyText } }, list, emptyText);
    }

    public static void SetItems(ListBox list, TextBlock empty, IEnumerable items)
    {
        var l = items.Cast<object>().ToList();
        list.ItemsSource = l;
        empty.IsVisible = l.Count == 0 && empty.Text is { Length: > 0 };
    }

    public static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var m = new MenuItem { Header = header, IsEnabled = enabled };
        m.Click += (_, _) => action();
        return m;
    }

    public static void Copy(RedisWorkspace ws, string text) =>
        ws.Fire(() => ws.App?.Dialogs.SetClipboardTextAsync(text) ?? Task.CompletedTask);

    public static TextBox Box(string placeholder, double? width = null, bool mono = false)
    {
        var b = new TextBox { PlaceholderText = placeholder };
        if (width is { } w) b.Width = w;
        if (mono) b.Classes.Add("mono");
        return b;
    }

    public static TextBox Editor(bool mono = true)
    {
        var b = new TextBox { AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap };
        if (mono) b.Classes.Add("mono");
        ScrollViewer.SetVerticalScrollBarVisibility(b, ScrollBarVisibility.Auto);
        return b;
    }
}

// MARK: - 키 보기 / 편집

/// 선택한 키: 머리 (타입·TTL·크기, 새로고침·TTL·이름 변경·삭제) + 타입별 내용 — Mac RedisKeyView
public sealed class RedisKeyView : UserControl
{
    private readonly RedisWorkspace _ws;
    private readonly Panel _placeholder;
    private readonly DockPanel _main;
    private readonly Border _typeBadge = new() { CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _typeText = new() { FontSize = 11, FontWeight = FontWeight.SemiBold };
    private readonly SelectableTextBlock _keyText = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _infoText = RedisUi.Label("", "secondary");
    private readonly TextBlock _loading = RedisUi.Label("…", "secondary");
    private readonly Button _ttlButton, _renameButton, _deleteButton;
    private readonly ContentControl _content = new();
    private readonly SelectableTextBlock _error = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };

    // 입력 (키가 바뀌면 지운다, 값만 다시 읽으면 유지 — Mac @State)
    private string _fieldInput = "", _valueInput = "", _draft = "";
    private bool _prettyJson;
    private string? _lastKey, _lastString;
    private (RedisValue, long?)? _shown;
    // 지금 그린 타입별 내용을 새 값으로 고치는 함수 (null 이면 다시 그린다)
    private Action<RedisValue>? _updateBody;
    private Type? _bodyKind;

    public RedisKeyView(RedisWorkspace ws)
    {
        _ws = ws;
        _error.Classes.Add("error");
        _infoText.FontSize = 11;
        _typeBadge.Child = _typeText;
        _placeholder = new StackPanel
        {
            Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = "🔑", FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center }, RedisUi.Label(Tr("왼쪽에서 키를 고르세요."), "secondary") },
        };

        var copy = new CopyButton { Title = "⧉", TextProvider = () => _ws.SelectedKey ?? "" };
        ToolTip.SetTip(copy, Tr("복사"));
        _ttlButton = RedisUi.Btn("⏱", () => { }, tip: Tr("만료(TTL) 설정"));
        _ttlButton.Flyout = TtlFlyout();
        _renameButton = RedisUi.Btn("✎", () => { }, tip: Tr("이름 변경"));
        _renameButton.Flyout = RenameFlyout();
        _deleteButton = RedisUi.Btn("🗑", () => _ws.Fire(ConfirmDeleteAsync), tip: Tr("키 삭제"));
        var header = RedisUi.Row(8,
            [_typeBadge, _keyText, copy],
            [_infoText, _loading, RedisUi.Btn(Glyphs.Refresh, () => _ws.Fire(_ws.ReloadValueAsync), tip: Tr("새로고침")), _ttlButton, _renameButton, _deleteButton]);
        header.Margin = new Thickness(12, 8);
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.Bind(Border.BorderBrushProperty, headerBorder.GetResourceObservable("DividerBrush"));
        _main = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        DockPanel.SetDock(_error, Dock.Bottom);
        _main.Children.Add(headerBorder);
        _main.Children.Add(_error);
        _main.Children.Add(_content);
        Content = new Panel { Children = { _placeholder, _main } };

        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, KeyModifiers.Control), Command = new RelayCommand(SaveString) });
        ws.PropertyChanged += OnWsChanged;
        Refresh();
    }

    private void OnWsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RedisWorkspace.SelectedKey) or nameof(RedisWorkspace.Value) or nameof(RedisWorkspace.ValueError)
                or nameof(RedisWorkspace.ValueTruncatedTotal):
                Refresh();
                break;
            case nameof(RedisWorkspace.KeyInfo) or nameof(RedisWorkspace.LoadingValue):
                UpdateHeader();
                break;
        }
    }

    /// 지금 보이는 오류 (쓰기 실패)
    public string? Error
    {
        get => _error.IsVisible ? _error.Text : null;
        set { _error.Text = value; _error.IsVisible = value != null; }
    }

    private void Refresh()
    {
        var key = _ws.SelectedKey;
        _placeholder.IsVisible = key == null;
        _main.IsVisible = key != null;
        if (key != _lastKey)
        {
            _lastKey = key;
            _lastString = null;
            _shown = null;
            _fieldInput = _valueInput = "";
            _prettyJson = false;
            _updateBody = null;
            Error = null;
        }
        // 값이 실제로 바뀌었을 때만 편집 중인 글을 바꾼다 (Mac onChange(of: stringValue) — 자동 새로고침에 편집이 날아가지 않게)
        var str = (_ws.Value as RedisValue.Str)?.Value;
        if (str != null && str != _lastString) _draft = str;
        _lastString = str;
        UpdateHeader();
        if (key == null) { _content.Content = null; _shown = null; _updateBody = null; return; }
        if (_ws.ValueError is { } err)
        {
            _shown = null;
            _updateBody = null;
            var t = new SelectableTextBlock { Text = err, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
            t.Classes.Add("error");
            _content.Content = t;
        }
        else if (_ws.Value is { } v)
        {
            // 같은 문자열을 다시 읽은 것이면 편집 칸을 그대로 둔다 (커서 · 포커스 유지)
            var shown = (v, _ws.ValueTruncatedTotal);
            if (v is RedisValue.Str && _shown is { } prev && prev.Equals(shown) && _content.Content != null) return;
            _shown = shown;
            // 같은 키 · 같은 타입을 다시 읽은 것(자동 새로고침, 쓰기 후)이면 표를 그대로 두고 행만 바꾼다
            // — Mac 은 Table 이 그대로라 선택(행 번호) · 스크롤 · 입력 칸이 유지된다
            if (v is not RedisValue.Str && _updateBody != null && _bodyKind == v.GetType() && _content.Content != null)
            {
                _updateBody(v);
                return;
            }
            _updateBody = null;
            _bodyKind = v.GetType();
            _content.Content = Body(key, v);
        }
        else
        {
            _shown = null;
            _updateBody = null;
            _content.Content = new TextBlock { Text = "…", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
    }

    private void UpdateHeader()
    {
        _keyText.Text = _ws.SelectedKey;
        _loading.IsVisible = _ws.LoadingValue;
        var w = !_ws.ReadOnly;
        _ttlButton.IsEnabled = w;
        _renameButton.IsEnabled = w;
        _deleteButton.IsEnabled = w;
        if (_ws.KeyInfo is { } info)
        {
            _typeBadge.IsVisible = true;
            _typeText.Text = info.Type;
            _typeText.Foreground = RedisWorkspace.TypeBrush(info.Type);
            _typeBadge.Background = RedisWorkspace.TypeBackground(info.Type);
            var parts = new List<string> { "TTL " + RedisFormat.TtlText(info.TtlMillis) };
            if (info.Length is { } n)
                parts.Add(info.Type == "string" ? Tr("%@바이트", n.ToString(CultureInfo.InvariantCulture)) : Tr("%@개", n.ToString(CultureInfo.InvariantCulture)));
            if (info.Memory is { } m) parts.Add(RedisWorkspace.ByteText(m));
            if (info.Encoding is { } enc) parts.Add(enc);
            _infoText.Text = string.Join("   ", parts);
        }
        else
        {
            _typeBadge.IsVisible = false;
            _infoText.Text = "";
        }
    }

    private Flyout TtlFlyout()
    {
        var box = RedisUi.Box(Tr("초"), 160);
        var f = new Flyout();
        Button apply = null!;
        apply = Ui.Button(Tr("적용"), () =>
        {
            if (_ws.SelectedKey is not { } key || !long.TryParse(box.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var s)) return;
            f.Hide();
            _ws.Fire(async () => Error = await _ws.SetTtlAsync(key, s));
        }, isDefault: true);
        var clear = Ui.Button(Tr("만료 없앰"), () =>
        {
            if (_ws.SelectedKey is not { } key) return;
            f.Hide();
            _ws.Fire(async () => Error = await _ws.SetTtlAsync(key, null));
        });
        box.TextChanged += (_, _) => apply.IsEnabled = long.TryParse(box.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        f.Opening += (_, _) => { box.Text = ""; apply.IsEnabled = false; };
        f.Content = Ui.Stack(8, Ui.Headline(Tr("만료(TTL) 설정")), box, Ui.HStack(8, clear, apply));
        return f;
    }

    private Flyout RenameFlyout()
    {
        var box = RedisUi.Box(Tr("새 이름"), 280);
        var f = new Flyout();
        Button ok = null!;
        ok = Ui.Button(Tr("변경"), () =>
        {
            if (_ws.SelectedKey is not { } key || box.Text is not { Length: > 0 } name || name == key) return;
            f.Hide();
            _ws.Fire(async () => Error = await _ws.RenameKeyAsync(key, name));
        }, isDefault: true);
        box.TextChanged += (_, _) => ok.IsEnabled = box.Text is { Length: > 0 } t && t != _ws.SelectedKey;
        f.Opening += (_, _) => { box.Text = _ws.SelectedKey; ok.IsEnabled = false; };
        f.Content = Ui.Stack(8, Ui.Headline(Tr("이름 변경")), box, ok);
        return f;
    }

    private async Task ConfirmDeleteAsync()
    {
        if (_ws.SelectedKey is not { } key || _ws.App is not { } app) return;
        if (await app.Dialogs.ConfirmAsync(Tr("키 %@ 를 삭제할까요?", key), Tr("되돌릴 수 없어요."), Tr("삭제"), Tr("취소"), destructive: true))
            await _ws.DeleteKeyAsync(key);
    }

    private void Run(IReadOnlyList<string> args, string label, RedisTable? selection = null) =>
        _ws.Fire(async () =>
        {
            Error = await _ws.ApplyAsync(args, label);
            if (Error == null) selection?.ClearSelection();   // Mac: if error == nil { selection = [] }
        });

    public sealed record KV(int Index, string A, string B);

    /// 두 칸 표 — Mac table(_:a:b:aWidth:): 앞 칸 min/ideal/max = aWidth, aWidth, aWidth*2 (없으면 120/220/600), 값 칸 3줄
    private RedisTable KvTable(string a, string b, double? aWidth = null)
    {
        var table = new RedisTable([
            new RedisColumn(a, o => ((KV)o).A) { Width = aWidth is { } w ? new RedisWidth(w, w, w * 2) : new RedisWidth(120, 220, 600), Selectable = true },
            new RedisColumn(b, o => ((KV)o).B) { MaxLines = 3, Selectable = true },
        ], RedisSelection.Multiple, key: o => ((KV)o).Index);
        table.Menu = ids =>
        {
            var rows = ids.Cast<KV>().ToList();
            return
            [
                RedisUi.Item(Tr("값 복사"), () => RedisUi.Copy(_ws, string.Join("\n", rows.Select(r => r.B)))),
                RedisUi.Item(Tr("두 칸 복사 (탭 구분)"), () => RedisUi.Copy(_ws, string.Join("\n", rows.Select(r => r.A + "\t" + r.B)))),
            ];
        };
        return table;
    }

    private static List<int> Selected(RedisTable table) => table.SelectedAs<KV>().Select(r => r.Index).Order().ToList();

    /// 표 아래 '더 불러오기' 단추
    private static Button MoreButton(Func<Task> load, RedisWorkspace ws)
    {
        var b = new Button { Margin = new Thickness(4), Classes = { "hover" } };
        b.Click += (_, _) => ws.Fire(load);
        return b;
    }

    private static void UpdateMore(Button more, int count, long total)
    {
        more.IsVisible = count < total;
        more.Content = Tr("더 불러오기 (%@ / %@)", count.ToString(CultureInfo.InvariantCulture), total.ToString(CultureInfo.InvariantCulture));
    }

    /// 테스트용: 지금 보이는 두 칸 표
    public RedisTable? KvTableView => (_content.Content as DockPanel)?.Children.OfType<RedisTable>().FirstOrDefault();

    private Control EditBar(params Control[] controls)
    {
        var p = new Grid { ColumnSpacing = 8, IsEnabled = !_ws.ReadOnly };
        for (var i = 0; i < controls.Length; i++)
        {
            p.ColumnDefinitions.Add(new ColumnDefinition(controls[i] is TextBox { Width: double.NaN } ? new GridLength(1, GridUnitType.Star) : GridLength.Auto));
            Grid.SetColumn(controls[i], i);
            p.Children.Add(controls[i]);
        }
        return RedisUi.Bar(p);
    }

    private TextBox Input(string placeholder, bool field, double? width = null)
    {
        var b = RedisUi.Box(placeholder, width);
        b.Text = field ? _fieldInput : _valueInput;
        b.TextChanged += (_, _) => { if (field) _fieldInput = b.Text ?? ""; else _valueInput = b.Text ?? ""; };
        return b;
    }

    private static Control Stacked(Control main, params Control?[] bottom)
    {
        var d = new DockPanel();
        foreach (var c in bottom.Reverse())
        {
            if (c == null) continue;
            DockPanel.SetDock(c, Dock.Bottom);
            d.Children.Add(c);
        }
        d.Children.Add(main);
        return d;
    }

    private Control Body(string key, RedisValue v)
    {
        switch (v)
        {
            case RedisValue.Str s:
                return StringEditor(key, s.Value);
            case RedisValue.Hash h:
            {
                var items = h.Items;
                var table = KvTable(Tr("필드"), Tr("값"));
                var field = Input(Tr("필드"), true, 180);
                var value = Input(Tr("값"), false);
                var save = Ui.Button(Tr("저장 (HSET)"), () => Run(["HSET", key, _fieldInput, _valueInput], Tr("HSET %@ %@", key, _fieldInput), table));
                var del = Ui.Button(Tr("선택 삭제 (HDEL)"), () =>
                {
                    var fields = Selected(table).Where(i => i < items.Count).Select(i => items[i].Key).ToList();
                    Run(["HDEL", key, .. fields], Tr("HDEL %@ %@개", key, fields.Count.ToString(CultureInfo.InvariantCulture)), table);
                });
                void Update() { save.IsEnabled = _fieldInput.Length > 0; del.IsEnabled = Selected(table).Count > 0; }
                field.TextChanged += (_, _) => Update();
                table.SelectionChanged += (_, _) =>
                {
                    var sel = Selected(table);
                    if (!table.Restoring && sel.Count == 1 && sel[0] < items.Count) { field.Text = items[sel[0]].Key; value.Text = items[sel[0]].Value; }
                    Update();
                };
                void Fill() => table.SetItems(items.Select((p, i) => new KV(i, p.Key, p.Value)));
                _updateBody = nv => { items = ((RedisValue.Hash)nv).Items; Fill(); Update(); };
                Fill();
                Update();
                return Stacked(table, EditBar(field, value, save, del));
            }
            case RedisValue.List l:
            {
                var items = l.Items;
                var table = KvTable("#", Tr("값"), 70);
                var value = Input(Tr("값"), false);
                var lset = Ui.Button(Tr("선택 행 바꾸기 (LSET)"), () =>
                {
                    if (Selected(table) is [var i]) Run(["LSET", key, i.ToString(CultureInfo.InvariantCulture), _valueInput], $"LSET {key} {i}", table);
                });
                var lrem = Ui.Button(Tr("선택 값 삭제 (LREM)"), () =>
                {
                    if (Selected(table) is [var i] && i < items.Count) Run(["LREM", key, "1", items[i]], $"LREM {key}", table);
                });
                void Update() { lset.IsEnabled = lrem.IsEnabled = Selected(table).Count == 1; }
                table.SelectionChanged += (_, _) =>
                {
                    if (!table.Restoring && Selected(table) is [var i] && i < items.Count) value.Text = items[i];
                    Update();
                };
                var more = MoreButton(_ws.LoadMoreListAsync, _ws);
                void Fill(RedisValue.List lv)
                {
                    table.SetItems(items.Select((x, i) => new KV(i, i.ToString(CultureInfo.InvariantCulture), x)));
                    UpdateMore(more, items.Count, lv.Total);
                }
                _updateBody = nv => { var lv = (RedisValue.List)nv; items = lv.Items; Fill(lv); Update(); };
                Fill(l);
                Update();
                return Stacked(table, more, EditBar(value,
                    Ui.Button("LPUSH", () => Run(["LPUSH", key, _valueInput], $"LPUSH {key}", table)),
                    Ui.Button("RPUSH", () => Run(["RPUSH", key, _valueInput], $"RPUSH {key}", table)),
                    lset, lrem));
            }
            case RedisValue.Set st:
            {
                var items = st.Items;
                var table = KvTable("#", Tr("멤버"), 70);
                var value = Input(Tr("멤버"), false);
                var add = Ui.Button(Tr("추가 (SADD)"), () => Run(["SADD", key, _valueInput], $"SADD {key}", table));
                var rem = Ui.Button(Tr("선택 삭제 (SREM)"), () =>
                {
                    var ms = Selected(table).Where(i => i < items.Count).Select(i => items[i]).ToList();
                    Run(["SREM", key, .. ms], Tr("SREM %@ %@개", key, ms.Count.ToString(CultureInfo.InvariantCulture)), table);
                });
                void Update() { add.IsEnabled = _valueInput.Length > 0; rem.IsEnabled = Selected(table).Count > 0; }
                value.TextChanged += (_, _) => Update();
                table.SelectionChanged += (_, _) => Update();
                void Fill() => table.SetItems(items.Select((x, i) => new KV(i, (i + 1).ToString(CultureInfo.InvariantCulture), x)));
                _updateBody = nv => { items = ((RedisValue.Set)nv).Items; Fill(); Update(); };
                Fill();
                Update();
                return Stacked(table, EditBar(value, add, rem));
            }
            case RedisValue.Zset z:
            {
                var items = z.Items;
                var table = KvTable(Tr("멤버"), Tr("점수 (높은 순)"));
                var member = Input(Tr("멤버"), true);
                var score = Input(Tr("점수"), false, 120);
                var add = Ui.Button(Tr("저장 (ZADD)"), () => Run(["ZADD", key, _valueInput, _fieldInput], $"ZADD {key}", table));
                var rem = Ui.Button(Tr("선택 삭제 (ZREM)"), () =>
                {
                    var ms = Selected(table).Where(i => i < items.Count).Select(i => items[i].Key).ToList();
                    Run(["ZREM", key, .. ms], Tr("ZREM %@ %@개", key, ms.Count.ToString(CultureInfo.InvariantCulture)), table);
                });
                void Update()
                {
                    add.IsEnabled = _fieldInput.Length > 0 && double.TryParse(_valueInput, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                    rem.IsEnabled = Selected(table).Count > 0;
                }
                member.TextChanged += (_, _) => Update();
                score.TextChanged += (_, _) => Update();
                table.SelectionChanged += (_, _) =>
                {
                    if (!table.Restoring && Selected(table) is [var i] && i < items.Count) { member.Text = items[i].Key; score.Text = items[i].Value; }
                    Update();
                };
                var more = MoreButton(_ws.LoadMoreZsetAsync, _ws);
                void Fill(RedisValue.Zset zv)
                {
                    table.SetItems(items.Select((p, i) => new KV(i, p.Key, p.Value)));
                    UpdateMore(more, items.Count, zv.Total);
                }
                _updateBody = nv => { var zv = (RedisValue.Zset)nv; items = zv.Items; Fill(zv); Update(); };
                Fill(z);
                Update();
                return Stacked(table, more, EditBar(member, score, add, rem));
            }
            case RedisValue.Stream sv:
            {
                var table = new StreamEntriesTable(_ws, null);
                table.SetEntries(sv.Entries);
                var count = RedisUi.Label(Tr("최근 %@개", sv.Entries.Count.ToString(CultureInfo.InvariantCulture)), "caption");
                var bar = RedisUi.Row(8, [count],
                    [RedisUi.Btn(Tr("Streams 탭에서 열기 (소비자 그룹, 실시간 보기)"), () => _ws.Fire(() => _ws.OpenStreamAsync(key)))]);
                _updateBody = nv =>
                {
                    var entries = ((RedisValue.Stream)nv).Entries;
                    table.SetEntries(entries);
                    count.Text = Tr("최근 %@개", entries.Count.ToString(CultureInfo.InvariantCulture));
                };
                return Stacked(table, RedisUi.Bar(bar));
            }
            case RedisValue.Other o:
                return new TextBlock { Text = Tr("%@ 타입은 콘솔에서 다뤄 주세요.", o.Type), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "secondary" } };
        }
        return new Panel();
    }

    private TextBox? _draftBox;
    private Button? _saveButton;
    private string _savedValue = "";

    private Control StringEditor(string key, string s)
    {
        _savedValue = s;
        var json = RedisWorkspace.PrettyJson(s);
        var root = new DockPanel();
        if (_ws.ValueTruncatedTotal is { } total)
        {
            var warn = new TextBlock
            {
                Text = "⚠ " + Tr("값이 커서 앞 1MB 만 보여요 (전체 %@). 이 화면에서는 편집할 수 없어요.", RedisWorkspace.ByteText(total)),
                Foreground = RedisUi.Orange, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap,
            };
            DockPanel.SetDock(warn, Dock.Top);
            root.Children.Add(warn);
        }
        var changed = RedisUi.Label(Tr("바뀜"));
        changed.Foreground = RedisUi.Orange;
        changed.FontSize = 11;
        var revert = Ui.Button(Tr("되돌리기"), () => { if (_draftBox != null) _draftBox.Text = s; });
        var save = Ui.Button(Tr("저장 (SET, TTL 유지)"), SaveString);
        ToolTip.SetTip(save, "Ctrl+S");
        _saveButton = save;
        var left = new List<Control>();
        if (json != null)
        {
            var cb = new CheckBox { Content = Tr("JSON 보기"), IsChecked = _prettyJson };
            cb.IsCheckedChanged += (_, _) => { _prettyJson = cb.IsChecked == true; _content.Content = StringEditor(key, s); };
            left.Add(cb);
        }
        left.Add(new CopyButton { Title = Tr("복사"), Text = s });
        var bar = RedisUi.Bar(RedisUi.Row(8, left, [changed, revert, save]));
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        void Update()
        {
            var dirty = _draft != s;
            changed.IsVisible = revert.IsVisible = dirty;
            save.IsEnabled = dirty && !_ws.ReadOnly && !(_prettyJson && json != null) && _ws.ValueTruncatedTotal == null;
        }
        if (_prettyJson && json != null)
        {
            _draftBox = null;
            var t = new SelectableTextBlock { Text = json, FontFamily = RedisUi.Mono, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
            root.Children.Add(new ScrollViewer { Content = t });
        }
        else
        {
            var box = RedisUi.Editor();
            box.Text = _draft;
            box.BorderThickness = new Thickness(0);
            box.TextChanged += (_, _) => { _draft = box.Text ?? ""; Update(); };
            _draftBox = box;
            root.Children.Add(box);
        }
        Update();
        return root;
    }

    /// 문자열 저장 (SET … KEEPTTL) — 저장 버튼 · Ctrl+S
    private void SaveString()
    {
        if (_ws.SelectedKey is not { } key || _ws.Value is not RedisValue.Str || _saveButton is not { IsEnabled: true } || _draftBox == null) return;
        Run(["SET", key, _draft, "KEEPTTL"], $"SET {key}");
    }

    /// 테스트용: 문자열 편집 칸
    public TextBox? DraftBox => _draftBox;
    public Button? SaveButton => _saveButton;
}

/// 스트림 항목 표 (ID · 시각 · 필드) — Mac StreamEntriesTable
public sealed class StreamEntriesTable : UserControl
{
    private readonly RedisWorkspace _ws;

    public StreamEntriesTable(RedisWorkspace ws, Action<IReadOnlyList<string>>? onDelete)
    {
        _ws = ws;
        // Mac: ID 150/190/260 (고정폭 callout, 글 선택), 시각 120/150/200 (흐림), 필드 2줄 (글 선택). 선택은 ID 묶음.
        Table = new RedisTable([
            new RedisColumn("ID", o => ((RedisStreamEntry)o).Id) { Width = new RedisWidth(150, 190, 260), Mono = true, Selectable = true },
            new RedisColumn(Tr("시각"), o => RedisWorkspace.StreamTime(((RedisStreamEntry)o).Id)) { Width = new RedisWidth(120, 150, 200), Secondary = _ => true },
            new RedisColumn(Tr("필드"), o => string.Join("  ", ((RedisStreamEntry)o).Fields.Select(f => $"{f.Key}={f.Value}"))) { MaxLines = 2, Selectable = true },
        ], RedisSelection.Multiple, key: o => ((RedisStreamEntry)o).Id);
        Table.Menu = ids =>
        {
            var sel = ids.Cast<RedisStreamEntry>().ToList();
            var items = new List<Control>
            {
                RedisUi.Item(Tr("ID 복사"), () => RedisUi.Copy(_ws, string.Join("\n", sel.Select(x => x.Id).Order(StringComparer.Ordinal)))),
                RedisUi.Item(Tr("JSON 으로 복사"), () => RedisUi.Copy(_ws, Json(sel))),
            };
            if (onDelete != null)
            {
                items.Add(new Separator());
                // Mac 과 같이 묻지 않고 지운다
                items.Add(RedisUi.Item(Tr("삭제 (XDEL)"), () => onDelete(sel.Select(x => x.Id).Order(StringComparer.Ordinal).ToList())));
            }
            return items;
        };
        Content = Table;
    }

    public RedisTable Table { get; }
    public List<RedisStreamEntry> Selected => Table.SelectedAs<RedisStreamEntry>();

    public void SetEntries(IEnumerable<RedisStreamEntry> entries) => Table.SetItems(entries);

    /// 선택한 항목을 JSON 으로 (키 정렬)
    public static string Json(IEnumerable<RedisStreamEntry> entries)
    {
        var rows = entries.Select(e => new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["fields"] = new SortedDictionary<string, string>(e.Fields.GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.First().Value), StringComparer.Ordinal),
            ["id"] = e.Id,
        }).ToList();
        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}

// MARK: - Workbench

/// Workbench: 위에 결과(redis-cli 모양), 아래 여러 줄 편집기 — Mac RedisConsoleView
public sealed class RedisConsoleView : UserControl
{
    private readonly RedisWorkspace _ws;
    public TextBox Editor { get; }
    public Button RunButton { get; }
    private readonly TextBlock _running = RedisUi.Label("…", "secondary");
    private readonly TextBlock _db = RedisUi.Label("", "secondary");
    private readonly TextBlock _hint = RedisUi.Label("", "secondary");
    private readonly ItemsControl _output = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _emptyText;
    private bool _syncing;

    public RedisConsoleView(RedisWorkspace ws)
    {
        _ws = ws;
        _hint.FontFamily = RedisUi.Mono;
        _hint.FontSize = 11;
        _hint.TextTrimming = TextTrimming.CharacterEllipsis;
        _output.ItemsSource = ws.Console;
        _output.ItemTemplate = new FuncDataTemplate<RedisConsoleEntry>((e, _) => Entry(e));
        _emptyText = new TextBlock
        {
            Text = Tr("아래 편집기에 명령을 한 줄에 하나씩 쓰고 Ctrl+Enter 또는 실행을 누르세요. 선택 영역이 있으면 그 줄만 실행해요. 결과는 여기에 쌓여요. 구독은 Pub/Sub 탭에서 해요."),
            TextWrapping = TextWrapping.Wrap, Classes = { "secondary" },
        };
        _scroll = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(10), Spacing = 8, Children = { _emptyText, _output } } };
        var outBorder = new Border { Child = _scroll };
        outBorder.Bind(Border.BackgroundProperty, outBorder.GetResourceObservable("CodeBackground"));

        Editor = RedisUi.Editor();
        Editor.TextWrapping = TextWrapping.NoWrap;
        Editor.Text = ws.Workbench;
        Editor.BorderThickness = new Thickness(0);
        Editor.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            ws.Workbench = Editor.Text ?? "";
            _syncing = false;
            UpdateState();
        };
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty || e.Property == TextBox.CaretIndexProperty)
                UpdateState();
        };
        // Ctrl+Enter 실행 (편집기가 줄바꿈으로 먹기 전에)
        Editor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            {
                e.Handled = true;
                Run();
            }
        }, RoutingStrategies.Tunnel);

        RunButton = new Button { Classes = { "accent" } };
        ToolTip.SetTip(RunButton, Tr("Ctrl+Enter · 선택 영역이 있으면 그 줄만, 없으면 전체를 위에서부터 차례로 실행"));
        RunButton.Click += (_, _) => Run();
        var history = RedisUi.Btn("🕘", () => { }, tip: Tr("이전 명령을 편집기에 넣기"));
        history.Click += (_, _) =>
        {
            var items = new List<Control>();
            if (ws.History.Count == 0) items.Add(new MenuItem { Header = Tr("아직 실행한 명령이 없어요."), IsEnabled = false });
            foreach (var h in Enumerable.Reverse(ws.History.TakeLast(30))) items.Add(RedisUi.Item(h, () => ws.InsertIntoWorkbench(h)));
            new ContextMenu { ItemsSource = items }.Open(history);
        };
        var bar = RedisUi.Row(8, [RunButton, _running, _db, _hint], [history, RedisUi.Btn(Tr("결과 지우기"), ws.Console.Clear)]);
        bar.Margin = new Thickness(10, 6);
        // 결과의 마지막 칸 _hint 가 남는 자리를 쓰도록 (Row 는 마지막 왼쪽 다음 칸이 *)
        var editorPanel = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        editorPanel.Children.Add(bar);
        var editorBorder = new Border { Child = Editor };
        editorBorder.Bind(Border.BackgroundProperty, editorBorder.GetResourceObservable("CodeBackground"));
        editorPanel.Children.Add(editorBorder);

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,5,160") };
        grid.RowDefinitions[2].MinHeight = 80;
        grid.RowDefinitions[2].MaxHeight = 500;
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        splitter.Bind(BackgroundProperty, splitter.GetResourceObservable("DividerBrush"));
        Grid.SetRow(splitter, 1);
        Grid.SetRow(editorPanel, 2);
        grid.Children.Add(outBorder);
        grid.Children.Add(splitter);
        grid.Children.Add(editorPanel);
        Content = grid;

        ws.Console.CollectionChanged += OnConsoleChanged;
        ws.PropertyChanged += OnWsChanged;
        UpdateState();
    }

    private Control Entry(RedisConsoleEntry e)
    {
        var cmd = new TextBlock { FontFamily = RedisUi.Mono, TextWrapping = TextWrapping.Wrap };
        cmd.Inlines!.Add(new Avalonia.Controls.Documents.Run(e.Prompt) { Foreground = Brushes.Gray });
        cmd.Inlines.Add(new Avalonia.Controls.Documents.Run(e.Command) { FontWeight = FontWeight.Bold });
        var ms = RedisUi.Label(e.MillisText, "caption");
        var again = RedisUi.Btn("⤓", () => _ws.InsertIntoWorkbench(e.Command), "borderless", Tr("편집기에 다시 넣기"));
        var head = RedisUi.Row(6, [cmd], [ms, again]);
        Grid.SetColumnSpan(cmd, 2);
        var output = new SelectableTextBlock { Text = e.Output, FontFamily = RedisUi.Mono, TextWrapping = TextWrapping.Wrap };
        if (e.IsError) output.Foreground = RedisUi.Red;
        return new StackPanel { Spacing = 2, Children = { head, output } };
    }

    private void OnConsoleChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateState();
        if (e.Action == NotifyCollectionChangedAction.Add) Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void OnWsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RedisWorkspace.Workbench) when !_syncing && Editor.Text != _ws.Workbench:
                _syncing = true;
                Editor.Text = _ws.Workbench;
                Editor.CaretIndex = Editor.Text.Length;
                _syncing = false;
                UpdateState();
                break;
            case nameof(RedisWorkspace.ConsoleRunning) or nameof(RedisWorkspace.Db):
                UpdateState();
                break;
        }
    }

    private bool HasSelection => Editor.SelectionStart != Editor.SelectionEnd;

    /// 선택 영역이 있으면 그 글, 없으면 전체
    public string TextToRun => RedisWorkspace.TextToRun(Editor.Text ?? "", Editor.SelectionStart, Editor.SelectionEnd);

    private void UpdateState()
    {
        RunButton.Content = Glyphs.Run + " " + (HasSelection ? Tr("선택 실행") : Tr("실행"));
        RunButton.IsEnabled = !_ws.ConsoleRunning && RedisText.ScriptLines(TextToRun).Count > 0;
        _running.IsVisible = _ws.ConsoleRunning;
        _db.Text = $"db{_ws.Db}";
        var h = RedisWorkspace.HintFor(Editor.Text ?? "", Editor.CaretIndex);
        _hint.Text = h ?? "";
        _hint.IsVisible = h != null;
        _emptyText.IsVisible = _ws.Console.Count == 0;
    }

    public void Run()
    {
        if (!RunButton.IsEnabled) return;
        var t = TextToRun;
        _ws.Fire(() => _ws.RunScriptAsync(t));
    }
}

// MARK: - Pub/Sub

/// 채널/패턴 구독, 실시간 메시지 표, PUBLISH, 활성 채널 — Mac RedisPubSubView
public sealed class RedisPubSubView : UserControl
{
    private readonly RedisWorkspace _ws;
    public TextBox SubscribeBox { get; } = RedisUi.Box(Tr("채널 또는 패턴 (쉼표로 여러 개, 예: news, evt:*)"));
    public TextBox FilterBox { get; } = new() { PlaceholderText = Tr("메시지 거르기"), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    public TextBox PubChannel { get; } = RedisUi.Box(Tr("채널"));
    public TextBox PubMessage { get; } = RedisUi.Editor();
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _count = RedisUi.Label("", "secondary");
    private readonly SelectableTextBlock _error = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Classes = { "error" } };
    private readonly RedisTable _messages;
    private readonly ListBox _active;
    private readonly TextBlock _activeEmpty;
    private readonly DispatcherTimer _throttle = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Button _send;

    public RedisPubSubView(RedisWorkspace ws)
    {
        _ws = ws;
        var subscribe = Ui.Button(Tr("구독"), Subscribe);
        SubscribeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Subscribe(); } };
        SubscribeBox.TextChanged += (_, _) => subscribe.IsEnabled = SubscribeBox.Text is { Length: > 0 };
        subscribe.IsEnabled = false;
        var top = RedisUi.Row(6, [], [subscribe]);
        top.Children.Insert(0, SubscribeBox);
        Grid.SetColumn(SubscribeBox, 0);
        top.Margin = new Thickness(8);

        var paused = new CheckBox { Content = Tr("일시 정지"), IsChecked = ws.PubsubPaused };
        paused.IsCheckedChanged += (_, _) => ws.PubsubPaused = paused.IsChecked == true;
        FilterBox.TextChanged += (_, _) => Schedule();
        var filterRow = RedisUi.Row(8, [RedisUi.Label("⏷", "secondary")], [paused, _count, RedisUi.Btn(Tr("지우기"), ws.Messages.Clear)]);
        filterRow.Children.Insert(1, FilterBox);
        Grid.SetColumn(FilterBox, 1);
        filterRow.Margin = new Thickness(8, 4);

        // Mac Table(shown): 시각 90/100/130 (흐림), 채널 100/160/300 (패턴은 아래 작은 줄), 메시지 3줄 · 글 선택.
        // selection 을 묶지 않은 표라 행이 골라지지 않고 행 메뉴도 없다.
        static bool IsMessage(object o) => ((RedisPubSubMessage)o).Kind.EndsWith("message", StringComparison.Ordinal);
        _messages = new RedisTable([
            new RedisColumn(Tr("시각"), o => ((RedisPubSubMessage)o).Date.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)) { Width = new RedisWidth(90, 100, 130), Secondary = _ => true },
            new RedisColumn(Tr("채널"), o => ((RedisPubSubMessage)o).Channel) { Width = new RedisWidth(100, 160, 300), Sub = o => ((RedisPubSubMessage)o).Pattern },
            new RedisColumn(Tr("메시지"), o => IsMessage(o) ? ((RedisPubSubMessage)o).Payload : $"({((RedisPubSubMessage)o).Kind} · {((RedisPubSubMessage)o).Payload})")
                { MaxLines = 3, Selectable = true, Secondary = o => !IsMessage(o) },
        ], RedisSelection.None);
        var left = new DockPanel { MinWidth = 420 };
        foreach (var c in new Control[] { top, _chips, new Border { Height = 1, Classes = { "divider" } }, filterRow })
        {
            DockPanel.SetDock(c, Dock.Top);
            left.Children.Add(c);
        }
        _chips.Margin = new Thickness(8, 0, 8, 6);
        left.Children.Add(_messages);

        // 오른쪽: PUBLISH · 활성 채널
        PubMessage.Height = 100;
        _send = Ui.Button(Tr("보내기"), () =>
        {
            var ch = PubChannel.Text ?? "";
            var msg = PubMessage.Text ?? "";
            _ws.Fire(async () => SetError(await _ws.PublishAsync(ch, msg)));
        });
        PubChannel.TextChanged += (_, _) => UpdateSend();
        var readOnlyNote = new TextBlock { Text = Tr("읽기전용 접속이라 보낼 수 없어요."), FontSize = 11, Foreground = RedisUi.Orange, IsVisible = ws.ReadOnly, TextWrapping = TextWrapping.Wrap };
        // Mac List: 채널 이름 … 구독자 수(작게, 흐림). 누르면 보낼 채널에 넣고, 메뉴로 구독.
        var (aView, aList, aEmpty) = RedisUi.PlainList(o =>
        {
            var c = (RedisChannelCount)o;
            var subs = RedisUi.Label(Tr("구독자 %@", c.Subscribers.ToString(CultureInfo.InvariantCulture)), "secondary");
            subs.FontSize = 11;
            var name = new TextBlock { Text = c.Channel, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel { Background = Brushes.Transparent };
            DockPanel.SetDock(subs, Dock.Right);
            row.Children.Add(subs);
            row.Children.Add(name);
            return row;
        }, SelectionMode.Single, Tr("없음"));
        _active = aList;
        _activeEmpty = aEmpty;
        aList.SelectionChanged += (_, _) =>
        {
            // Mac 은 onTapGesture 라 행이 골라진 채로 남지 않는다
            if (aList.SelectedItem is not RedisChannelCount c) return;
            PubChannel.Text = c.Channel;
            aList.SelectedItem = null;
        };
        aList.ContextRequested += (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is not RedisChannelCount c) return;
            e.Handled = true;
            new ContextMenu { ItemsSource = new[] { RedisUi.Item(Tr("구독"), () => _ws.Fire(() => _ws.SubscribeAsync(c.Channel))) } }.Open(aList);
        };
        var right = new DockPanel { Margin = new Thickness(10) };
        var rightTop = Ui.Stack(10,
            Ui.Headline(Tr("메시지 보내기 (PUBLISH)")), PubChannel, PubMessage,
            new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { SetCol(_send, 1) } },
            readOnlyNote, _error, new Border { Height = 1, Classes = { "divider" } },
            RedisUi.Row(6, [Ui.Headline(Tr("활성 채널"))], [RedisUi.Btn(Glyphs.Refresh, () => _ws.Fire(_ws.LoadActiveChannelsAsync))]));
        DockPanel.SetDock(rightTop, Dock.Top);
        right.Children.Add(rightTop);
        right.Children.Add(aView);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,300") };
        grid.ColumnDefinitions[2].MinWidth = 260;
        grid.ColumnDefinitions[2].MaxWidth = 420;
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        splitter.Bind(BackgroundProperty, splitter.GetResourceObservable("DividerBrush"));
        grid.Children.Add(left);
        grid.Children.Add(SetCol(splitter, 1));
        grid.Children.Add(SetCol(right, 2));
        Content = grid;

        _throttle.Tick += (_, _) => { _throttle.Stop(); RefreshMessages(); };
        ws.Messages.CollectionChanged += (_, _) => Schedule();
        ws.Channels.CollectionChanged += (_, _) => RefreshChips();
        ws.Patterns.CollectionChanged += (_, _) => RefreshChips();
        ws.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RedisWorkspace.ActiveChannels)) RedisUi.SetItems(_active, _activeEmpty, _ws.ActiveChannels);
            if (e.PropertyName == nameof(RedisWorkspace.Subscriber)) RefreshMessages();
            if (e.PropertyName == nameof(RedisWorkspace.PubsubPaused)) paused.IsChecked = _ws.PubsubPaused;
        };
        SetError(null);
        UpdateSend();
        RefreshChips();
        RefreshMessages();
        RedisUi.SetItems(_active, _activeEmpty, ws.ActiveChannels);
    }

    private static T SetCol<T>(T c, int col) where T : Control { Grid.SetColumn(c, col); return c; }

    private void UpdateSend() => _send.IsEnabled = PubChannel.Text is { Length: > 0 } && !_ws.ReadOnly;

    private void SetError(string? e)
    {
        _error.Text = e;
        _error.IsVisible = e != null;
    }

    private void Subscribe()
    {
        var t = SubscribeBox.Text ?? "";
        if (t.Length == 0) return;
        SubscribeBox.Text = "";
        _ws.Fire(() => _ws.SubscribeAsync(t));
    }

    private void Schedule()
    {
        if (!_throttle.IsEnabled) _throttle.Start();
    }

    private void RefreshChips()
    {
        _chips.Children.Clear();
        var all = _ws.Channels.Concat(_ws.Patterns).ToList();
        _chips.IsVisible = all.Count > 0;
        foreach (var c in all)
        {
            var x = RedisUi.Btn("✕", () => _ws.Unsubscribe(c), "borderless");
            x.FontSize = 10;
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#0A84FF"), 0.12), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1, 2, 1),
                Margin = new Thickness(0, 0, 6, 4),
                Child = Ui.HStack(2, RedisUi.Label(c, "caption"), x),
            };
            _chips.Children.Add(chip);
        }
        if (all.Count > 0) _chips.Children.Add(RedisUi.Btn(Tr("모두 해제"), () => _ws.Fire(_ws.UnsubscribeAllAsync)));
    }

    /// 걸러서 최신이 위로
    public List<RedisPubSubMessage> Shown
    {
        get
        {
            var filter = FilterBox.Text ?? "";
            IEnumerable<RedisPubSubMessage> all = _ws.Messages.Reverse();
            if (filter.Length > 0)
                all = all.Where(m => m.Channel.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || m.Payload.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
            return all.ToList();
        }
    }

    public void RefreshMessages()
    {
        _messages.ShowEmpty = _ws.Messages.Count == 0;
        _messages.EmptyText = _ws.Subscriber == null ? Tr("채널을 구독하면 들어오는 메시지가 여기에 실시간으로 보여요.") : Tr("메시지를 기다리는 중…");
        _messages.SetItems(Shown);
        _count.Text = Tr("%@건", _ws.Messages.Count(m => m.Kind.EndsWith("message", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture));
    }

    public RedisTable MessageTable => _messages;
}

// MARK: - Streams

/// 스트림 목록 → 항목 (최신순, XADD/XDEL, 실시간 보기) + 소비자 그룹 · PEL — Mac RedisStreamsView
public sealed class RedisStreamsView : UserControl
{
    private readonly RedisWorkspace _ws;
    private readonly ListBox _keys;
    private readonly TextBlock _keysEmpty;
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _infoText = RedisUi.Label("", "caption");
    private readonly ToggleButton _tail = new();
    public StreamEntriesTable Entries { get; }
    private readonly Button _older;
    private readonly TextBlock _shown = RedisUi.Label("", "caption");
    public TextBox AddFields { get; } = RedisUi.Box(Tr("field=value field2=value2"), 320);
    private readonly Button _add;
    private readonly SelectableTextBlock _error = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 4), Classes = { "error" } };
    private readonly RedisTable _groups, _consumers, _pending;
    private readonly TextBlock _groupTitle = new() { FontSize = 14, FontWeight = FontWeight.SemiBold };
    public TextBox NewGroup { get; } = RedisUi.Box(Tr("새 그룹"));
    private readonly ComboBox _newGroupStart = new() { ItemsSource = new[] { Tr("새 항목부터 ($)"), Tr("처음부터 (0)") }, SelectedIndex = 0 };
    private readonly Button _create, _ack;
    private readonly Control _detail;
    private readonly TextBlock _pick = new() { Text = Tr("왼쪽에서 스트림을 고르세요."), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "secondary" } };
    private string? _localError;
    private bool _syncing;

    public RedisStreamsView(RedisWorkspace ws)
    {
        _ws = ws;
        AddFields.Text = "field=value";
        // 스트림 목록
        // Mac List(streamKeys, selection:) — 머리글 없는 목록, 긴 이름은 가운데를 줄임
        var (kView, kList, kEmpty) = RedisUi.PlainList(o => new TextBlock { Text = (string)o, TextTrimming = TextTrimming.PrefixCharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                                                        SelectionMode.Single, Tr("스트림이 없어요."));
        _keys = kList;
        _keysEmpty = kEmpty;
        kList.SelectionChanged += (_, _) =>
        {
            if (_syncing || kList.SelectedItem is not string k || k == _ws.Stream) return;
            _ws.Fire(() => _ws.OpenStreamAsync(k));
        };
        var keysPanel = new DockPanel();
        var keysTop = RedisUi.Row(6, [Ui.Headline(Tr("스트림"))], [RedisUi.Btn(Glyphs.Refresh, () => _ws.Fire(_ws.LoadStreamKeysAsync), "borderless")]);
        keysTop.Margin = new Thickness(8);
        DockPanel.SetDock(keysTop, Dock.Top);
        keysPanel.Children.Add(keysTop);
        keysPanel.Children.Add(kView);

        // 항목
        _tail.Content = Tr("실시간 보기");
        ToolTip.SetTip(_tail, Tr("XREAD BLOCK 으로 새로 들어오는 항목을 위에 계속 붙여요 (별도 연결)"));
        _tail.Click += (_, _) => { _ws.ToggleTail(); _tail.IsChecked = _ws.Tailing; };
        var entriesTop = RedisUi.Row(8, [_title, _infoText], [_tail, RedisUi.Btn(Glyphs.Refresh, () => _ws.Fire(_ws.ReloadStreamAsync))]);
        entriesTop.Margin = new Thickness(8);
        Entries = new StreamEntriesTable(ws, ws.ReadOnly ? null : ids =>
        {
            if (_ws.Stream is not { } key) return;
            _ws.Fire(async () => SetError(await _ws.StreamWriteAsync(["XDEL", key, .. ids], Tr("XDEL %@ %@개", key, ids.Count.ToString(CultureInfo.InvariantCulture)))));
        });
        _older = RedisUi.Btn(Tr("이전 항목 더 보기"), () => _ws.Fire(_ws.LoadOlderStreamAsync));
        _add = Ui.Button(Tr("추가 (XADD)"), () =>
        {
            if (_ws.Stream is not { } key) return;
            if (RedisWorkspace.ParsePairs(AddFields.Text ?? "") is not { } pairs) { SetError(Tr("field=value 형식으로 넣어 주세요.")); return; }
            _ws.Fire(async () => SetError(await _ws.StreamWriteAsync(["XADD", key, "*", .. pairs.SelectMany(p => new[] { p.Key, p.Value })], $"XADD {key}")));
        });
        _add.IsEnabled = !ws.ReadOnly;
        var entriesBar = RedisUi.Bar(RedisUi.Row(8, [_older, _shown], [AddFields, _add]));
        var entriesPane = new DockPanel();
        DockPanel.SetDock(entriesTop, Dock.Top);
        DockPanel.SetDock(_error, Dock.Bottom);
        DockPanel.SetDock(entriesBar, Dock.Bottom);
        entriesPane.Children.Add(entriesTop);
        entriesPane.Children.Add(_error);
        entriesPane.Children.Add(entriesBar);
        entriesPane.Children.Add(Entries);

        // 소비자 그룹
        // Mac: 그룹 · 소비자(50) · 대기(50, 있으면 주황 · 없으면 흐림) · Lag(50) · 마지막 전달 ID(caption 고정폭). 하나만 고름 (ws.group).
        _groups = new RedisTable([
            new RedisColumn(Tr("그룹"), o => ((RedisStreamGroup)o).Name),
            new RedisColumn(Tr("소비자"), o => ((RedisStreamGroup)o).Consumers.ToString(CultureInfo.InvariantCulture)) { Width = RedisWidth.Fixed(50) },
            new RedisColumn(Tr("대기"), o => ((RedisStreamGroup)o).Pending.ToString(CultureInfo.InvariantCulture))
                { Width = RedisWidth.Fixed(50), Brush = o => ((RedisStreamGroup)o).Pending > 0 ? RedisUi.Orange : null, Secondary = o => ((RedisStreamGroup)o).Pending <= 0 },
            new RedisColumn("Lag", o => ((RedisStreamGroup)o).Lag?.ToString(CultureInfo.InvariantCulture) ?? "-") { Width = RedisWidth.Fixed(50) },
            new RedisColumn(Tr("마지막 전달 ID"), o => ((RedisStreamGroup)o).LastDeliveredId) { Mono = true, Caption = true },
        ], RedisSelection.Single, key: o => ((RedisStreamGroup)o).Name);
        _groups.SelectionChanged += (_, _) =>
        {
            if (_syncing || _groups.Restoring || _groups.Grid.SelectedItem is not RedisStreamGroup g || g.Name == _ws.Group) return;
            _ws.Group = g.Name;
            _ws.Fire(_ws.ReloadGroupAsync);
        };
        _groups.Menu = ids =>
        {
            if (ids.FirstOrDefault() is not RedisStreamGroup g || _ws.ReadOnly || _ws.Stream is not { } key) return null;
            // Mac 과 같이 묻지 않는다
            return
            [
                RedisUi.Item(Tr("처음부터 다시 읽기 (SETID 0)"), () => _ws.Fire(async () =>
                    SetError(await _ws.StreamWriteAsync(["XGROUP", "SETID", key, g.Name, "0"], $"XGROUP SETID {key} {g.Name} 0")))),
                RedisUi.Item(Tr("그룹 삭제 (DESTROY)"), () => _ws.Fire(async () =>
                    SetError(await _ws.StreamWriteAsync(["XGROUP", "DESTROY", key, g.Name], $"XGROUP DESTROY {key} {g.Name}")))),
            ];
        };
        _create = Ui.Button(Tr("만들기"), () =>
        {
            if (_ws.Stream is not { } key || NewGroup.Text is not { Length: > 0 } name) return;
            var start = _newGroupStart.SelectedIndex == 1 ? "0" : "$";
            _ws.Fire(async () =>
            {
                var err = await _ws.StreamWriteAsync(["XGROUP", "CREATE", key, name, start], $"XGROUP CREATE {key} {name}");
                SetError(err);
                if (err == null) NewGroup.Text = "";
            });
        });
        NewGroup.TextChanged += (_, _) => _create.IsEnabled = NewGroup.Text is { Length: > 0 } && !_ws.ReadOnly;
        _create.IsEnabled = false;
        var groupsLeft = new DockPanel { Margin = new Thickness(8), MinWidth = 320 };
        var gTitle = Ui.Headline(Tr("소비자 그룹"));
        DockPanel.SetDock(gTitle, Dock.Top);
        var createRow = RedisUi.Row(6, [], [_newGroupStart, _create]);
        createRow.Children.Insert(0, NewGroup);
        Grid.SetColumn(NewGroup, 0);
        createRow.Margin = new Thickness(0, 6, 0, 0);
        DockPanel.SetDock(createRow, Dock.Bottom);
        groupsLeft.Children.Add(gTitle);
        groupsLeft.Children.Add(createRow);
        groupsLeft.Children.Add(_groups);

        // Mac Table(ws.consumers) — 선택 없음: 소비자 · 대기(50) · 유휴(80)
        _consumers = new RedisTable([
            new RedisColumn(Tr("소비자"), o => ((RedisStreamConsumer)o).Name),
            new RedisColumn(Tr("대기"), o => ((RedisStreamConsumer)o).Pending.ToString(CultureInfo.InvariantCulture)) { Width = RedisWidth.Fixed(50) },
            new RedisColumn(Tr("유휴"), o => RedisFormat.TtlText(((RedisStreamConsumer)o).IdleMillis)) { Width = RedisWidth.Fixed(80) },
        ], RedisSelection.None);
        // Mac Table(ws.pending, selection: Set<String>) — ID(caption 고정폭) · 소비자 · 유휴(80) · 전달 횟수(60, 3 넘으면 빨강)
        _pending = new RedisTable([
            new RedisColumn("ID", o => ((RedisPendingEntry)o).Id) { Mono = true, Caption = true },
            new RedisColumn(Tr("소비자"), o => ((RedisPendingEntry)o).Consumer),
            new RedisColumn(Tr("유휴"), o => RedisFormat.TtlText(((RedisPendingEntry)o).IdleMillis)) { Width = RedisWidth.Fixed(80) },
            new RedisColumn(Tr("전달 횟수"), o => ((RedisPendingEntry)o).Deliveries.ToString(CultureInfo.InvariantCulture))
                { Width = RedisWidth.Fixed(60), Brush = o => ((RedisPendingEntry)o).Deliveries > 3 ? RedisUi.Red : null },
        ], RedisSelection.Multiple, key: o => ((RedisPendingEntry)o).Id);
        _ack = Ui.Button(Tr("선택 항목 처리 완료 (XACK)"), () =>
        {
            if (_ws.Stream is not { } key || _ws.Group is not { } g) return;
            var ids = _pending.SelectedAs<RedisPendingEntry>().Select(p => p.Id).Order(StringComparer.Ordinal).ToList();
            if (ids.Count == 0) return;
            _ws.Fire(async () =>
            {
                SetError(await _ws.StreamWriteAsync(["XACK", key, g, .. ids], Tr("XACK %@ %@ %@개", key, g, ids.Count.ToString(CultureInfo.InvariantCulture))));
                _pending.ClearSelection();   // Mac: pendingSelection = []
            });
        });
        _pending.SelectionChanged += (_, _) => _ack.IsEnabled = _pending.Selected.Count > 0 && !_ws.ReadOnly;
        _ack.IsEnabled = false;
        var groupsRight = new DockPanel { Margin = new Thickness(8), MinWidth = 320 };
        DockPanel.SetDock(_groupTitle, Dock.Top);
        // Mac .frame(minHeight: 70, maxHeight: 120)
        _consumers.MinHeight = 70;
        _consumers.Height = 120;
        DockPanel.SetDock(_consumers, Dock.Top);
        var ackRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { SetCol(_ack, 1) }, Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(ackRow, Dock.Bottom);
        groupsRight.Children.Add(_groupTitle);
        groupsRight.Children.Add(_consumers);
        groupsRight.Children.Add(ackRow);
        groupsRight.Children.Add(_pending);

        var groupsPane = Split(true, groupsLeft, groupsRight, "*", "*");
        var detail = new Grid { RowDefinitions = new RowDefinitions("*,5,240") };
        detail.RowDefinitions[0].MinHeight = 200;
        detail.RowDefinitions[2].MinHeight = 160;
        var hs = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        hs.Bind(BackgroundProperty, hs.GetResourceObservable("DividerBrush"));
        Grid.SetRow(hs, 1);
        Grid.SetRow(groupsPane, 2);
        detail.Children.Add(entriesPane);
        detail.Children.Add(hs);
        detail.Children.Add(groupsPane);
        _detail = detail;

        var right = new Panel { Children = { _pick, detail } };
        var root = Split(true, keysPanel, right, "200", "*");
        ((Grid)root).ColumnDefinitions[0].MinWidth = 160;
        ((Grid)root).ColumnDefinitions[0].MaxWidth = 320;
        Content = root;

        ws.PropertyChanged += OnWsChanged;
        ws.StreamEntries.CollectionChanged += (_, _) => RefreshEntries();
        RefreshAll();
    }

    private static T SetCol<T>(T c, int col) where T : Control { Grid.SetColumn(c, col); return c; }

    private static Control Split(bool columns, Control a, Control b, string aSize, string bSize)
    {
        var g = new Grid();
        if (columns) g.ColumnDefinitions = new ColumnDefinitions($"{aSize},5,{bSize}");
        else g.RowDefinitions = new RowDefinitions($"{aSize},5,{bSize}");
        var s = new GridSplitter { ResizeDirection = columns ? GridResizeDirection.Columns : GridResizeDirection.Rows };
        s.Bind(BackgroundProperty, s.GetResourceObservable("DividerBrush"));
        if (columns) { Grid.SetColumn(s, 1); Grid.SetColumn(b, 2); }
        else { Grid.SetRow(s, 1); Grid.SetRow(b, 2); }
        g.Children.Add(a);
        g.Children.Add(s);
        g.Children.Add(b);
        return g;
    }

    private void SetError(string? e)
    {
        _localError = e;
        UpdateError();
    }

    private void UpdateError()
    {
        var e = _localError ?? _ws.StreamError;
        _error.Text = e;
        _error.IsVisible = e != null;
    }

    private void OnWsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RedisWorkspace.StreamKeys) or nameof(RedisWorkspace.Stream) or nameof(RedisWorkspace.Groups) or nameof(RedisWorkspace.Group)
                or nameof(RedisWorkspace.Consumers) or nameof(RedisWorkspace.Pending) or nameof(RedisWorkspace.StreamInfo)
                or nameof(RedisWorkspace.Tailing) or nameof(RedisWorkspace.StreamHasMore):
                RefreshAll();
                break;
            case nameof(RedisWorkspace.StreamError):
                UpdateError();
                break;
        }
    }

    private void RefreshEntries()
    {
        Entries.SetEntries(_ws.StreamEntries.ToList());
        _shown.Text = Tr("%@개 표시 (최신순)", _ws.StreamEntries.Count.ToString(CultureInfo.InvariantCulture));
    }

    private void RefreshAll()
    {
        _syncing = true;
        RedisUi.SetItems(_keys, _keysEmpty, _ws.StreamKeys);
        _keys.SelectedItem = _ws.Stream;
        _pick.IsVisible = _ws.Stream == null;
        _detail.IsVisible = _ws.Stream != null;
        _title.Text = _ws.Stream;
        _infoText.Text = string.Join("   ", _ws.StreamInfo.Where(p => p.Key is "length" or "groups" or "last-generated-id").Select(p => $"{p.Key} {p.Value}"));
        _tail.IsChecked = _ws.Tailing;
        _tail.Content = (_ws.Tailing ? "📡 " : "⏸ ") + Tr("실시간 보기");
        _older.IsVisible = _ws.StreamHasMore;
        _groups.SetItems(_ws.Groups);
        _groups.Select(_ws.Groups.Where(g => g.Name == _ws.Group).Cast<object>());
        _groupTitle.Text = _ws.Group is { } gr ? Tr("%@ · 소비자 / 대기 중 (PEL)", gr) : Tr("그룹을 고르세요");
        _consumers.SetItems(_ws.Consumers);
        _pending.SetItems(_ws.Pending);
        _syncing = false;
        RefreshEntries();
        UpdateError();
    }

    public RedisTable PendingTable => _pending;
    public RedisTable GroupTable => _groups;
    public RedisTable ConsumerTable => _consumers;
}

// MARK: - 서버 정보

/// INFO 섹션별 표 + 주요 값, 찾기, Cluster 노드 고르기 — Mac RedisInfoView
public sealed class RedisInfoView : UserControl
{
    private static readonly string[] Highlights =
    [
        "redis_version", "redis_mode", "os", "uptime_in_days", "connected_clients", "used_memory_human",
        "maxmemory_human", "maxmemory_policy", "role", "connected_slaves", "total_commands_processed",
        "instantaneous_ops_per_sec", "keyspace_hits", "keyspace_misses", "expired_keys", "evicted_keys",
    ];

    private readonly RedisWorkspace _ws;
    private readonly StackPanel _highlights = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly ComboBox _node = new() { MinWidth = 160 };
    public TextBox FilterBox { get; } = RedisUi.Box(Tr("찾기"), 160);
    private readonly StackPanel _sections = new() { Margin = new Thickness(10), Spacing = 2 };
    private bool _syncing;

    public RedisInfoView(RedisWorkspace ws)
    {
        _ws = ws;
        _node.SelectionChanged += (_, _) =>
        {
            if (_syncing || _node.SelectedItem is not string n) return;
            _ws.InfoNode = n;
            _ws.Fire(_ws.LoadInfoAsync);
        };
        FilterBox.TextChanged += (_, _) => RefreshSections();
        var nodeLabel = RedisUi.Label(Tr("노드"), "secondary");
        var nodeRow = Ui.HStack(6, nodeLabel, _node);
        nodeRow.IsVisible = ws.MultipleNodes;
        var top = RedisUi.Row(10, [new ScrollViewer { Content = _highlights, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }],
            [nodeRow, FilterBox, RedisUi.Btn(Glyphs.Refresh, () => _ws.Fire(_ws.LoadInfoAsync))]);
        Grid.SetColumnSpan(top.Children[0], 2);
        var topBorder = new Border { Child = top, Padding = new Thickness(10), BorderThickness = new Thickness(0, 0, 0, 1) };
        topBorder.Bind(Border.BorderBrushProperty, topBorder.GetResourceObservable("DividerBrush"));
        var d = new DockPanel();
        DockPanel.SetDock(topBorder, Dock.Top);
        d.Children.Add(topBorder);
        d.Children.Add(new ScrollViewer { Content = _sections });
        Content = d;
        ws.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RedisWorkspace.Info)) Refresh();
            if (e.PropertyName is nameof(RedisWorkspace.Nodes) or nameof(RedisWorkspace.InfoNode)) { nodeRow.IsVisible = _ws.MultipleNodes; RefreshNodes(); }
        };
        RefreshNodes();
        Refresh();
    }

    private void RefreshNodes()
    {
        _syncing = true;
        _node.ItemsSource = _ws.Nodes;
        _node.SelectedItem = _ws.InfoNode ?? _ws.Nodes.FirstOrDefault();
        _syncing = false;
    }

    private void Refresh()
    {
        _highlights.Children.Clear();
        foreach (var k in Highlights.Take(_ws.MultipleNodes ? 6 : 8))
        {
            if (_ws.InfoValue(k) is not { } v) continue;
            var value = new TextBlock { Text = v, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = k == "os" ? 200 : double.PositiveInfinity };
            _highlights.Children.Add(new StackPanel { Spacing = 1, Children = { new TextBlock { Text = k, FontSize = 10, Classes = { "secondary" } }, value } });
        }
        RefreshSections();
    }

    private void RefreshSections()
    {
        _sections.Children.Clear();
        var filter = FilterBox.Text ?? "";
        foreach (var section in _ws.Info)
        {
            var items = section.Items.Where(p => filter.Length == 0
                || p.Key.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || p.Value.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (items.Count == 0) continue;
            var head = Ui.Headline(section.Name);
            head.Margin = new Thickness(0, 10, 0, 4);
            _sections.Children.Add(head);
            foreach (var kv in items)
            {
                var k = new TextBlock { Text = kv.Key, Width = 260, FontFamily = RedisUi.Mono };
                if (Highlights.Contains(kv.Key)) k.Foreground = RedisUi.Accent;
                var v = new SelectableTextBlock { Text = kv.Value, FontFamily = RedisUi.Mono, TextWrapping = TextWrapping.Wrap };
                _sections.Children.Add(new DockPanel { Children = { k, v } });
                DockPanel.SetDock(k, Dock.Left);
            }
        }
    }

    public int VisibleRows => _sections.Children.OfType<DockPanel>().Count();
}

// MARK: - 새 키

/// 새 키 만들기 (타입별, 만료 지정) — Mac RedisNewKeySheet
public sealed class RedisNewKeyWindow : Window
{
    private readonly RedisWorkspace _ws;
    public string Type { get; private set; } = "string";
    public TextBox KeyBox { get; } = new() { PlaceholderText = "user:1001" };
    public TextBox FieldBox { get; } = new();
    public TextBox ValueBox { get; } = RedisUi.Editor();
    public TextBox TtlBox { get; } = new() { PlaceholderText = Tr("비우면 없음"), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _fieldLabel = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _valueLabel = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly SelectableTextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Classes = { "error" } };
    public Button CreateButton { get; }

    public RedisNewKeyWindow(RedisWorkspace ws)
    {
        _ws = ws;
        Title = Tr("새 키");
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ValueBox.Height = 90;

        var types = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        foreach (var t in RedisWorkspace.KeyTypes)
        {
            var b = new RadioButton { Content = t, GroupName = "redisNewKeyType" + GetHashCode(), IsChecked = t == Type, Margin = new Thickness(0, 0, 10, 0) };
            b.IsCheckedChanged += (_, _) => { if (b.IsChecked == true) { Type = t; UpdateType(); } };
            types.Children.Add(b);
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 12, RowSpacing = 10 };
        void Add(Control label, Control field, int row)
        {
            Grid.SetRow(label, row);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            grid.Children.Add(label);
            grid.Children.Add(field);
        }
        TextBlock L(string s) => new() { Text = s, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Add(L(Tr("타입")), types, 0);
        Add(L(Tr("키")), KeyBox, 1);
        Add(_fieldLabel, FieldBox, 2);
        Add(_valueLabel, ValueBox, 3);
        Add(L(Tr("만료 (초)")), TtlBox, 4);

        CreateButton = Ui.Button(Tr("만들기"), Create, isDefault: true);
        KeyBox.TextChanged += (_, _) => UpdateEnabled();
        FieldBox.TextChanged += (_, _) => UpdateEnabled();
        var stack = Ui.Stack(14, Ui.Headline("🔑 " + Tr("새 키")), grid, _error,
            Ui.ButtonRow([Ui.Button(Tr("취소"), Close, isCancel: true), CreateButton]));
        Content = new Border { Padding = new Thickness(20), Child = stack };
        _error.IsVisible = false;
        UpdateType();
        Opened += (_, _) => KeyBox.Focus();
    }

    private void UpdateType()
    {
        var label = Type switch
        {
            "hash" => Tr("필드"),
            "zset" => Tr("점수"),
            "stream" => Tr("필드"),
            _ => null,
        };
        _fieldLabel.Text = label;
        _fieldLabel.IsVisible = FieldBox.IsVisible = label != null;
        FieldBox.PlaceholderText = Type == "zset" ? "0" : "name";
        _valueLabel.Text = Type == "zset" ? Tr("멤버") : Tr("값");
        UpdateEnabled();
    }

    private void UpdateEnabled() =>
        CreateButton.IsEnabled = KeyBox.Text is { Length: > 0 } && !(Type == "hash" && FieldBox.Text is not { Length: > 0 });

    public void SelectType(string type)
    {
        Type = type;
        if (Content is Border { Child: StackPanel s } && s.Children[1] is Grid g && g.Children[1] is StackPanel types)
            foreach (var r in types.Children.OfType<RadioButton>()) r.IsChecked = (string?)r.Content == type;
        UpdateType();
    }

    private void Create()
    {
        if (!CreateButton.IsEnabled) return;
        long? ttl = long.TryParse(TtlBox.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var t) ? t : null;
        string type = Type, key = KeyBox.Text ?? "", field = FieldBox.Text ?? "", value = ValueBox.Text ?? "";
        _ws.Fire(async () =>
        {
            var e = await _ws.CreateAsync(type, key, field, value, ttl);
            _error.Text = e;
            _error.IsVisible = e != null;
            if (e == null) Close();
        });
    }
}
