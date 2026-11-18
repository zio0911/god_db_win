using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// ER 다이어그램 그림: 관계선(Render) + 테이블 카드(자식). 좌표는 배율 1 기준 — 확대/축소는 바깥 LayoutTransformControl 이 한다.
/// (Mac DiagramView.canvas · drawRelation)
public sealed class DiagramCanvas : Panel
{
    public static readonly Color Accent = Color.Parse("#0A84FF");
    public static readonly Color Purple = Color.Parse("#AF52DE");
    private static readonly Color Dim = Color.FromArgb(0x59, 0x8A, 0x8A, 0x8E);

    public DiagramViewModel Vm { get; }
    private readonly Dictionary<string, DiagramCard> _cards = [];
    private string? _dragTable;
    private Point _dragStartPointer;
    private Point _dragStartOrigin;
    private bool _dragging;
    private readonly RelationLayer _lines;
    // 자동 정렬 애니메이션 (Mac withAnimation(.easeInOut(duration: 0.25)) { relayout() }):
    // 카드는 출발 자리 → 새 자리로 움직이고, 관계선(Mac Canvas)은 애니메이션 없이 새 자리로 바로 그린다
    private readonly Tween _tween = new();
    private Dictionary<string, Rect>? _animFrom;
    private double _animT = 1;

    public DiagramCanvas(DiagramViewModel vm)
    {
        Vm = vm;
        ClipToBounds = false;
        this.Bind(BackgroundProperty, this.GetResourceObservable("PanelBackground"));
        // Panel.Render 는 봉인돼 있어서 관계선은 맨 아래 층 컨트롤이 그린다
        _lines = new RelationLayer(this);
        Children.Add(_lines);
        foreach (var t in vm.Doc.Tables)
        {
            var card = new DiagramCard(t);
            Hook(card);
            _cards[t.Name] = card;
            Children.Add(card);
        }
        RefreshCards();
        vm.Relayouting += CaptureForAnimation;
        vm.LayoutChanged += OnLayoutChanged;
        vm.PropertyChanged += OnVmChanged;
    }

    public IReadOnlyDictionary<string, DiagramCard> Cards => _cards;

    /// 자동 정렬 애니메이션 (테스트에서 Finish 로 바로 끝낸다)
    public Tween LayoutTween => _tween;

    private bool _relayouting;

    private void CaptureForAnimation()
    {
        // 지금 보이는 자리(움직이는 중이면 그 중간)에서 출발한다
        var from = new Dictionary<string, Rect>();
        foreach (var name in _cards.Keys)
            if (Shown(name) is { } r) from[name] = r;
        _animFrom = from;
        _relayouting = true;
    }

    /// 카드가 지금 그려지는 자리 (여백 포함, 높이 제외)
    public Rect? Shown(string name)
    {
        if (Vm.Frame(name) is not { } f) return null;
        if (_animFrom == null || !_animFrom.TryGetValue(name, out var a) || _animT >= 1) return f;
        return new Rect(Tween.Lerp(a.X, f.X, _animT), Tween.Lerp(a.Y, f.Y, _animT), Tween.Lerp(a.Width, f.Width, _animT), f.Height);
    }

    private void OnLayoutChanged()
    {
        if (_relayouting)
        {
            _relayouting = false;
            _tween.Start(0.25, t => { _animT = t; InvalidateMeasure(); InvalidateArrange(); }, () => _animFrom = null);
        }
        InvalidateMeasure();
        InvalidateArrange();
        _lines.InvalidateVisual();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DiagramViewModel.Hovered):
                foreach (var (name, card) in _cards) card.SetHighlighted(Vm.IsHighlighted(name));
                _lines.InvalidateVisual();
                break;
            case nameof(DiagramViewModel.NameMode):
            case nameof(DiagramViewModel.ShowInferred):
                RefreshCards();
                OnLayoutChanged();
                break;
        }
    }

    private void RefreshCards()
    {
        foreach (var (name, card) in _cards)
            card.Refresh(Vm.NameMode, Vm.Doc.Highlighted.Contains(name), Vm.IsHighlighted(name), Vm.InferredColumns(name));
    }

    // MARK: - 끌기 · 마우스 올리기

    private void Hook(DiagramCard card)
    {
        var name = card.Table.Name;
        card.PointerEntered += (_, _) => Vm.Hovered = name;
        card.PointerExited += (_, _) => { if (Vm.Hovered == name && !_dragging) Vm.Hovered = null; };
        card.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !Vm.Frames.TryGetValue(name, out var f)) return;
            _tween.Finish();   // 정렬 애니메이션 중에 잡으면 끝 자리에서 끈다
            _dragTable = name;
            _dragStartPointer = e.GetPosition(this);
            _dragStartOrigin = new Point(f.X, f.Y);
            _dragging = false;
            e.Pointer.Capture(card);
            e.Handled = true;
        };
        card.PointerMoved += (_, e) =>
        {
            if (_dragTable != name) return;
            var d = e.GetPosition(this) - _dragStartPointer;
            // Mac DragGesture(minimumDistance: 2)
            if (!_dragging && Math.Sqrt(d.X * d.X + d.Y * d.Y) < 2) return;
            _dragging = true;
            Vm.Move(name, _dragStartOrigin.X + d.X, _dragStartOrigin.Y + d.Y);
        };
        card.PointerReleased += (_, e) =>
        {
            if (_dragTable != name) return;
            _dragTable = null;
            _dragging = false;
            e.Pointer.Capture(null);
            // 끌다가 카드 밖에서 놓으면 강조를 푼다
            if (!card.IsPointerOver && Vm.Hovered == name) Vm.Hovered = null;
        };
    }

    // MARK: - 배치

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var (name, card) in _cards)
        {
            var f = Shown(name);
            card.Measure(f is { } r ? new Size(r.Width, DiagramLayout.CardHeight(card.Table)) : default);
        }
        var size = Vm.ContentSize;
        _lines.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _lines.Arrange(new Rect(finalSize));
        foreach (var (name, card) in _cards)
        {
            if (Shown(name) is { } f) card.Arrange(new Rect(f.X, f.Y, f.Width, DiagramLayout.CardHeight(card.Table)));
            else card.Arrange(default);
        }
        return finalSize;
    }

    // MARK: - 관계선

    private sealed class RelationLayer(DiagramCanvas owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            foreach (var fk in owner.Vm.Relations) owner.DrawRelation(context, fk);
        }
    }

    private void DrawRelation(DrawingContext context, ForeignKeyMeta fk)
    {
        if (Vm.Frame(fk.Table) is not { } child || Vm.Frame(fk.ReferencedTable) is not { } parent
            || Vm.RowY(fk.Table, fk.Columns.FirstOrDefault() ?? "") is not { } cy
            || Vm.RowY(fk.ReferencedTable, fk.ReferencedColumns.FirstOrDefault() ?? "") is not { } py) return;
        var hovered = Vm.Hovered;
        var active = hovered == null || hovered == fk.Table || hovered == fk.ReferencedTable;
        var color = active ? Accent : Dim;
        Point start, end;
        double outDir;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            if (fk.Table == fk.ReferencedTable)
            {
                // 자기 참조: 오른쪽으로 고리
                start = new Point(child.Right, cy);
                end = new Point(child.Right, py);
                g.BeginFigure(start, false);
                g.CubicBezierTo(new Point(start.X + 70, start.Y), new Point(end.X + 70, end.Y), end);
                outDir = 1;
            }
            else
            {
                var childLeft = child.Center.X > parent.Center.X;
                start = new Point(childLeft ? child.Left : child.Right, cy);
                end = new Point(childLeft ? parent.Right : parent.Left, py);
                var bend = Math.Max(50, Math.Abs(end.X - start.X) * 0.45);
                g.BeginFigure(start, false);
                g.CubicBezierTo(new Point(start.X + (childLeft ? -bend : bend), start.Y),
                                new Point(end.X + (childLeft ? bend : -bend), end.Y), end);
                outDir = childLeft ? -1 : 1;
            }
            g.EndFigure(false);
        }
        var inferred = fk.Name.StartsWith('~');
        var width = active && hovered != null ? 2.5 : 1.6;
        var lineColor = inferred ? Color.FromArgb((byte)(color.A * 0.85), color.R, color.G, color.B) : color;
        // Avalonia 의 점선 길이는 선 굵기 단위
        var pen = new Pen(new SolidColorBrush(lineColor), width,
                          inferred ? new DashStyle([7 / width, 5 / width], 0) : null);
        context.DrawGeometry(null, pen, geo);

        // 자식 쪽: 까마귀발(여러 개), 부모 쪽: 막대(하나)
        var thin = new Pen(new SolidColorBrush(color), 1.6);
        var tip = new Point(start.X + outDir * 12, start.Y);
        context.DrawLine(thin, tip, new Point(start.X, start.Y - 7));
        context.DrawLine(thin, tip, new Point(start.X, start.Y + 7));
        context.DrawLine(thin, tip, start);
        var inward = fk.Table == fk.ReferencedTable ? 1 : (child.Center.X > parent.Center.X ? 1 : -1);
        var bar = new Pen(new SolidColorBrush(color), 2);
        context.DrawLine(bar, new Point(end.X + inward * 8, end.Y - 7), new Point(end.X + inward * 8, end.Y + 7));
    }

    // MARK: - PNG

    /// 그림 전체를 PNG 로 (Mac ImageRenderer scale 2). 화면 배율과 상관없이 배율 1 좌표를 scale 배로 그린다.
    public byte[] RenderPng(double scale = 2)
    {
        using var bitmap = RenderBitmap(scale);
        using var ms = new MemoryStream();
        bitmap.Save(ms, PngBitmapEncoderOptions.Default);
        return ms.ToArray();
    }

    /// 그림 전체를 비트맵으로 (배율 1 좌표 × scale 픽셀)
    public RenderTargetBitmap RenderBitmap(double scale = 2)
    {
        var size = Vm.ContentSize;
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            Measure(size);
            Arrange(new Rect(size));
        }
        var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
        var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        return bitmap;
    }
}

/// 테이블 카드: 이름 + 컬럼 (🔑 기본 키, 🔗 외래 키, 타입) — Mac TableCard
public sealed class DiagramCard : Border
{
    public TableMeta Table { get; }
    private readonly Border _header = new() { Height = DiagramLayout.HeaderHeight, Padding = new Thickness(10, 0) };
    private readonly StackPanel _rows = new() { Margin = new Thickness(8, 5) };

    public DiagramCard(TableMeta table)
    {
        Table = table;
        CornerRadius = new CornerRadius(8);
        ClipToBounds = true;
        BoxShadow = BoxShadows.Parse("0 3 6 0 #1F000000");
        this.Bind(BackgroundProperty, this.GetResourceObservable("CodeBackground"));
        ToolTip.SetTip(this, table.Comment.Length == 0 ? table.Name : table.Comment);
        var dock = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        dock.Children.Add(_header);
        dock.Children.Add(_rows);
        Child = dock;
    }

    public bool IsFocusCard { get; private set; }
    public bool IsHighlightedCard { get; private set; }

    public void SetHighlighted(bool on)
    {
        IsHighlightedCard = on;
        BorderThickness = new Thickness(on ? 2 : 1);
        BorderBrush = on ? new SolidColorBrush(DiagramCanvas.Accent) : new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80));
    }

    private static FontFamily Mono => Application.Current?.TryGetResource("MonoFont", null, out var f) == true && f is FontFamily ff
        ? ff : new FontFamily("Cascadia Mono, Consolas, Menlo, monospace");

    public void Refresh(DiagramNameMode mode, bool focus, bool highlighted, HashSet<string> inferredColumns)
    {
        IsFocusCard = focus;
        SetHighlighted(highlighted);
        var physical = mode == DiagramNameMode.Physical;
        _header.Background = new SolidColorBrush(focus ? DiagramCanvas.Purple : DiagramCanvas.Accent);
        var t = DiagramNames.Label(mode, Table.Name, Table.Comment);
        var title = new TextBlock
        {
            FontWeight = FontWeight.SemiBold, FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontStyle = t.Missing && !physical ? FontStyle.Italic : FontStyle.Normal,
        };
        if (physical) title.FontFamily = Mono;
        title.Inlines = [new Run(Glyphs.Table + "  ") { FontSize = 11 }, new Run(t.Main)];
        if (t.Sub is { } sub) title.Inlines.Add(new Run("  " + sub) { FontSize = 10, FontFamily = Mono, FontWeight = FontWeight.Normal, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)) });
        _header.Child = title;

        _rows.Children.Clear();
        var fkColumns = Table.ForeignKeys.SelectMany(f => f.Columns).Select(c => c.ToLowerInvariant()).ToHashSet();
        foreach (var c in Table.Columns.Take(DiagramLayout.MaxRows))
        {
            var row = new Grid { Height = DiagramLayout.RowHeight, ColumnDefinitions = new ColumnDefinitions("14,*,Auto") };
            var lower = c.Name.ToLowerInvariant();
            TextBlock icon;
            if (c.IsPrimaryKey) icon = new TextBlock { Text = Glyphs.Key, FontSize = 9 };
            else if (fkColumns.Contains(lower)) icon = new TextBlock { Text = Glyphs.Link, FontSize = 9 };
            else if (inferredColumns.Contains(lower)) icon = new TextBlock { Text = Glyphs.Link, FontSize = 9, Opacity = 0.5 };
            else icon = new TextBlock { Text = "•", FontSize = 8, Classes = { "tertiary" } };
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            row.Children.Add(icon);

            var n = DiagramNames.Label(mode, c.Name, c.Comment);
            var name = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(5, 0, 4, 0) };
            if (physical) name.FontFamily = Mono;
            // 코멘트가 없으면 흐리게 (Mac .secondary)
            var main = new Run(n.Main);
            if (n.Missing && !physical) main.Foreground = new SolidColorBrush(Color.Parse("#8A8A8E"));
            name.Inlines = [main];
            if (n.Sub is { } sub2) name.Inlines.Add(new Run("  " + sub2) { FontSize = 10, FontFamily = Mono, Foreground = new SolidColorBrush(Color.Parse("#B0B0B5")) });
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var type = new TextBlock { Text = c.ColumnType, FontSize = 10, FontFamily = Mono, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#8A8A8E")) };
            Grid.SetColumn(type, 2);
            row.Children.Add(type);
            _rows.Children.Add(row);
        }
        if (Table.Columns.Count > DiagramLayout.MaxRows)
            _rows.Children.Add(new TextBlock
            {
                Text = Tr("… 컬럼 %@개 더", Table.Columns.Count - DiagramLayout.MaxRows), FontSize = 10, Height = DiagramLayout.RowHeight,
                Foreground = new SolidColorBrush(Color.Parse("#8A8A8E")),
            });
    }
}
