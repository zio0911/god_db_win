using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Infrastructure;

namespace GodDB.App.Views;

/// 행 상세가 다시 그려질 때마다 같은 값을 다시 파싱하지 않도록 최근 결과를 기억한다 (Mac: JSONViewUI.swift 의 JSONCache)
public static class JsonCache
{
    private static readonly Dictionary<(string Text, bool JsonType), JSONDetection> Detections = [];
    private static readonly Dictionary<string, string> PrettyTexts = [];

    public static JSONDetection Detect(string? value, string? columnType)
    {
        // 대부분의 칸은 첫 글자만 보고 바로 거른다 (캐시에 넣지 않음)
        if (value == null || !JSONView.MightBeJSON(value, columnType)) return JSONDetection.NotJSON;
        var key = (value, JSONView.IsJSONType(columnType));
        if (Detections.TryGetValue(key, out var hit)) return hit;
        if (Detections.Count > 64) Detections.Clear();
        var d = JSONView.Detect(value, columnType);
        Detections[key] = d;
        return d;
    }

    public static string Pretty(string text, JSONNode node)
    {
        if (PrettyTexts.TryGetValue(text, out var hit)) return hit;
        if (PrettyTexts.Count > 32) PrettyTexts.Clear();
        var p = JSONView.Pretty(node);
        PrettyTexts[text] = p;
        return p;
    }
}

/// 행 상세의 JSON 칸: 원문 · 정렬 · 트리 전환 (마지막 선택 기억, 기본 트리). (Mac: JSONFieldView)
/// 편집 중이면 원문 = 편집 칸, 정렬 · 트리는 지금 값의 읽기 전용 보기.
public sealed class JsonFieldView : StackPanel
{
    /// 보기 방식이 바뀌면 열린 JSON 칸이 모두 따라 바뀐다 (Mac @AppStorage)
    private static event Action? ModeChanged;

    private readonly string? _columnType;
    private readonly Control? _rawEditor;
    private readonly ContentControl _content = new();
    private readonly StackPanel _top = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private string _text;
    private Control? _rawBox;

    public JSONDetection Detection { get; private set; }
    /// 지금 보기 방식 (JSON 일 때)
    public JSONViewMode Mode => JSONViewModes.Current;
    /// 원문 · 정렬 · 트리 단추 (테스트용)
    public IReadOnlyList<ToggleButton> ModeButtons { get; private set; } = [];
    /// 트리 보기 (트리일 때만)
    public JsonTreeView? Tree => (_content.Content as Border)?.Child as JsonTreeView;
    /// 정렬 보기 글자 (정렬일 때만)
    public string? PrettyText => Detection.Node is { } n && Mode == JSONViewMode.Pretty && Detection.Kind == JSONDetection.Kinds.JSON
        ? JsonCache.Pretty(_text, n) : null;
    /// 값 · 경로 복사에 쓰는 클립보드 (없으면 이 창의 클립보드)
    public Func<string, Task>? Copier
    {
        get => _copier;
        set
        {
            _copier = value;
            if (Tree is { } t) t.Copier = value;
        }
    }
    private Func<string, Task>? _copier;

    public JsonFieldView(string text, string? columnType, Control? rawEditor = null)
    {
        Spacing = 4;
        _text = text;
        _columnType = columnType;
        _rawEditor = rawEditor;
        Detection = JsonCache.Detect(text, columnType);
        Children.Add(_top);
        Children.Add(_content);
        Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ModeChanged += Rebuild;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ModeChanged -= Rebuild;
    }

    /// 값이 바뀌면 (편집 중) 다시 판단한다. 원문 편집 칸은 그대로 둔다 (입력 중 포커스를 잃지 않게).
    public void SetText(string text)
    {
        if (text == _text) return;
        _text = text;
        Detection = JsonCache.Detect(text, _columnType);
        Rebuild();
    }

    public void SetMode(JSONViewMode mode)
    {
        if (JSONViewModes.Current == mode) { Rebuild(); return; }
        JSONViewModes.Current = mode;
        ModeChanged?.Invoke();
    }

    private void Rebuild()
    {
        _top.Children.Clear();
        ModeButtons = [];
        switch (Detection.Kind)
        {
            case JSONDetection.Kinds.TooLarge:
                _top.Children.Add(new TextBlock { Text = "ⓘ " + Tr("너무 커서 원문만 보여요"), FontSize = 11, Classes = { "secondary" } });
                Show(Raw());
                break;
            case JSONDetection.Kinds.JSON:
                var mode = Mode;
                var buttons = new List<ToggleButton>();
                foreach (var (m, title) in new[] { (JSONViewMode.Raw, Tr("원문")), (JSONViewMode.Pretty, Tr("정렬")), (JSONViewMode.Tree, Tr("트리")) })
                {
                    var b = new ToggleButton { Content = title, IsChecked = m == mode, FontSize = 11, Padding = new Thickness(8, 1), MinHeight = 0 };
                    var target = m;
                    b.Click += (_, _) => SetMode(target);
                    buttons.Add(b);
                    _top.Children.Add(b);
                }
                ModeButtons = buttons;
                var node = Detection.Node!;
                Show(mode switch
                {
                    JSONViewMode.Raw => Raw(),
                    JSONViewMode.Pretty => CodeBox(JsonCache.Pretty(_text, node)),
                    _ => Box(new JsonTreeView(node) { Copier = Copier }),
                });
                break;
            default:
                Show(Raw());
                break;
        }
        _top.IsVisible = _top.Children.Count > 0;
    }

    private void Show(Control c)
    {
        if (!ReferenceEquals(_content.Content, c)) _content.Content = c;
    }

    private Control Raw() => _rawEditor ?? (_rawBox is { } rb && rb.Tag as string == _text ? rb : _rawBox = CodeBox(_text, _text));

    private Border CodeBox(string text, string? tag = null) => Box(new SelectableTextBlock
    {
        Text = text,
        FontFamily = Q.Mono,
        TextWrapping = TextWrapping.Wrap,
    }, tag);

    private Border Box(Control child, string? tag = null) => new()
    {
        Tag = tag,
        Background = this.TryFindResource("CodeBackground", ActualThemeVariant, out var cb) ? cb as IBrush : null,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6),
        Child = child,
    };
}

/// JSON 트리: 펼치기/접기 (처음엔 2단계까지), 값 종류별 색, 우클릭 > 값 복사 · 경로 복사 (Mac: JSONTreeView)
public sealed class JsonTreeView : StackPanel
{
    private readonly JSONNode _root;
    /// 기본 펼침 상태에서 뒤집은 항목 (경로 글자)
    private readonly HashSet<string> _toggled = [];

    public sealed record Line(IReadOnlyList<JSONPathComponent> Path, JSONNode Node, Control View)
    {
        public string PathText => JSONView.Path(Path);
    }

    /// 지금 보이는 줄 (테스트용)
    public IReadOnlyList<Line> Lines { get; private set; } = [];
    public Func<string, Task>? Copier { get; set; }

    private static readonly IBrush StringBrush = new SolidColorBrush(Color.Parse("#34C759"));
    private static readonly IBrush NumberBrush = new SolidColorBrush(Color.Parse("#0A84FF"));
    private static readonly IBrush BoolBrush = new SolidColorBrush(Color.Parse("#AF52DE"));

    public JsonTreeView(JSONNode root)
    {
        _root = root;
        Spacing = 2;
        Build();
    }

    private bool Expanded(IReadOnlyList<JSONPathComponent> path) => (path.Count < 2) != _toggled.Contains(JSONView.Path(path));

    /// 줄을 누른 것처럼 펼치기/접기 (객체 · 배열만)
    public void Toggle(Line line)
    {
        if (!line.Node.IsContainer) return;
        var key = line.PathText;
        if (!_toggled.Remove(key)) _toggled.Add(key);
        Build();
    }

    private void Build()
    {
        Children.Clear();
        var lines = new List<Line>();
        void Visit(JSONNode node, List<JSONPathComponent> path)
        {
            var line = new Line(path, node, new Panel());
            line = line with { View = Row(line) };
            lines.Add(line);
            if (!Expanded(path)) return;
            if (node.Kind == JSONKind.Object)
                foreach (var m in node.Members) Visit(m.Value, [.. path, JSONPathComponent.Key(m.Key)]);
            else if (node.Kind == JSONKind.Array)
                for (var i = 0; i < node.Items.Count; i++) Visit(node.Items[i], [.. path, JSONPathComponent.Index(i)]);
        }
        // 맨 위 객체 · 배열은 줄로 그리지 않고 그 항목부터
        if (_root.Kind == JSONKind.Object)
        {
            if (_root.Members.Count == 0) Children.Add(Secondary("{}"));
            foreach (var m in _root.Members) Visit(m.Value, [JSONPathComponent.Key(m.Key)]);
        }
        else if (_root.Kind == JSONKind.Array)
        {
            if (_root.Items.Count == 0) Children.Add(Secondary("[]"));
            for (var i = 0; i < _root.Items.Count; i++) Visit(_root.Items[i], [JSONPathComponent.Index(i)]);
        }
        else Visit(_root, []);
        Lines = lines;
        foreach (var l in lines) Children.Add(l.View);
    }

    private TextBlock Secondary(string text) => new()
    {
        Text = text, FontFamily = Q.Mono,
        Foreground = this.TryFindResource("SecondaryText", ActualThemeVariant, out var s) ? s as IBrush : Brushes.Gray,
    };

    private TextBlock Tertiary(string text, double size = 12) => new()
    {
        Text = text, FontFamily = Q.Mono, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
        Foreground = this.TryFindResource("TertiaryText", ActualThemeVariant, out var s) ? s as IBrush : Brushes.Gray,
    };

    private Control Row(Line line)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Background = Brushes.Transparent,
            Margin = new Thickness(Math.Max(0, line.Path.Count - 1) * 14, 0, 0, 0),
        };
        var expanded = Expanded(line.Path);
        row.Children.Add(new TextBlock
        {
            Text = line.Node.IsContainer ? (expanded ? "▾" : "▸") : "",
            Width = 12, FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
            Foreground = this.TryFindResource("SecondaryText", ActualThemeVariant, out var s) ? s as IBrush : Brushes.Gray,
        });
        if (line.Path.Count > 0)
        {
            var last = line.Path[^1];
            if (last.IsKey) row.Children.Add(new TextBlock { Text = last.KeyName, FontFamily = Q.Mono });
            else row.Children.Add(Secondary($"[{last.IndexValue}]"));
            row.Children.Add(Tertiary(":"));
        }
        foreach (var v in Value(line.Node, expanded)) row.Children.Add(v);
        row.Tapped += (_, _) => Toggle(line);
        var copyValue = new MenuItem { Header = Tr("값 복사") };
        copyValue.Click += (_, _) => _ = Copy(JSONView.CopyText(line.Node));
        var copyPath = new MenuItem { Header = Tr("경로 복사") };
        copyPath.Click += (_, _) => _ = Copy(line.PathText);
        row.ContextMenu = new ContextMenu { Items = { copyValue, copyPath } };
        AutomationProperties.SetName(row, line.PathText);
        return row;
    }

    private IEnumerable<Control> Value(JSONNode node, bool expanded)
    {
        TextBlock T(string text, IBrush? brush = null) => new()
        {
            Text = text, FontFamily = Q.Mono, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Foreground = brush,
        };
        switch (node.Kind)
        {
            case JSONKind.Object:
                yield return Secondary(expanded ? "{" : "{…}");
                yield return Tertiary($"{node.Members.Count}", 10);
                break;
            case JSONKind.Array:
                yield return Secondary(expanded ? "[" : "[…]");
                yield return Tertiary($"{node.Items.Count}", 10);
                break;
            case JSONKind.String:
                yield return T(JSONView.Quote(node.Text), StringBrush);
                break;
            case JSONKind.Number:
                yield return T(node.Text, NumberBrush);
                break;
            case JSONKind.Bool:
                yield return T(node.BoolValue ? "true" : "false", BoolBrush);
                break;
            default:
                var n = Secondary("null");
                n.FontStyle = FontStyle.Italic;
                yield return n;
                break;
        }
    }

    /// 값 · 경로를 클립보드에
    public async Task Copy(string text)
    {
        if (Copier != null) await Copier(text);
        else if (TopLevel.GetTopLevel(this)?.Clipboard is { } c) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(c, text);
    }
}
