using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GodDB.App.ViewModels;

namespace GodDB.App.Controls;

/// 모델 다이어그램 (Mac ModelCanvas + ModelTableCard). 카드·관계 선·관계 표시를 직접 그리고, 누르기·끌기를 뷰 모델에 넘긴다.
/// 관계 선은 IE 표기: 자식 쪽 까마귀발 = N, 막대 = 1, 원 = 선택(0 허용), 부모 쪽 막대 둘. 식별 = 실선, 비식별 = 점선.
/// 크기 = 모델 캔버스 크기 × 확대 비율 (ScrollViewer 안에 둔다).
public sealed class ModelCanvas : Control
{
    public static readonly Color Accent = Color.Parse("#0A84FF");

    /// Mac ModelTableCard.colors (blue = 앱 강조색)
    public static Color CardColor(string? name) => name switch
    {
        "purple" => Color.Parse("#AF52DE"),
        "green" => Color.Parse("#34C759"),
        "orange" => Color.Parse("#FF9500"),
        "red" => Color.Parse("#FF3B30"),
        "teal" => Color.Parse("#30B0C7"),
        "gray" => Color.Parse("#8E8E93"),
        _ => Accent,
    };

    /// 화면용이면 뷰 모델, PNG 저장용이면 null (그대로 그리기만)
    public ModelingViewModel? Vm { get; }
    private readonly DataModel? _fixedModel;
    private readonly ModelNameMode _fixedMode;

    private Guid? _hovered;
    private Guid? _pressTable;
    private Guid? _pressRelation;
    private Point _pressPoint;
    private bool _dragging;
    private bool _pressed;

    public ModelCanvas(ModelingViewModel vm)
    {
        Vm = vm;
        Focusable = true;
        ClipToBounds = true;
        vm.Redraw += () => { InvalidateMeasure(); InvalidateVisual(); };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModelingViewModel.Zoom)) { InvalidateMeasure(); InvalidateVisual(); }
        };
    }

    /// PNG 저장용 (확대 1, 고른 것 없음)
    public ModelCanvas(DataModel model, ModelNameMode mode)
    {
        _fixedModel = model;
        _fixedMode = mode;
    }

    private DataModel? Model => Vm?.Model ?? _fixedModel;
    private ModelNameMode NameMode => Vm?.NameMode ?? _fixedMode;
    private double Zoom => Vm?.Zoom ?? 1;
    private ModelSelection? Selection => Vm?.Selection;
    private Guid? Pending => Vm?.PendingParent;
    /// PNG 저장 때 칠할 배경 (Mac: windowBackgroundColor)
    public IBrush? ExportBackground { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Model is not { } m) return default;
        var (w, h) = ModelDiagram.Size(m);
        return new Size(w * Zoom, h * Zoom);
    }

    // MARK: - 그리기

    private IBrush Res(string key, string fallback)
    {
        var theme = ActualThemeVariant;
        if (this.TryFindResource(key, theme, out var v) && v is IBrush b) return b;
        if (Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var v2) && v2 is IBrush b2) return b2;
        return new SolidColorBrush(Color.Parse(fallback));
    }

    private bool IsDark => (Parent == null ? Application.Current?.ActualThemeVariant : ActualThemeVariant) == Avalonia.Styling.ThemeVariant.Dark;

    private static readonly Typeface UiFace = new(FontFamily.Default);
    private static readonly FontFamily MonoFamily = new("Cascadia Mono, Consolas, Menlo, monospace");

    private static FormattedText Text(string s, double size, IBrush brush, bool mono = false, bool bold = false, bool italic = false,
                                      double maxWidth = 0)
    {
        var face = new Typeface(mono ? MonoFamily : FontFamily.Default, italic ? FontStyle.Italic : FontStyle.Normal,
                                bold ? FontWeight.SemiBold : FontWeight.Normal);
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush);
        if (maxWidth > 0)
        {
            ft.MaxTextWidth = maxWidth;
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        return ft;
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(ExportBackground ?? Brushes.Transparent, bounds);
        if (Model is not { } m) return;
        using var _ = ctx.PushTransform(Matrix.CreateScale(Zoom, Zoom));
        foreach (var r in m.Relations) DrawRelation(ctx, m, r);
        foreach (var r in m.Relations) DrawBadge(ctx, m, r);
        foreach (var t in m.Tables) DrawCard(ctx, m, t);
    }

    private static Point P((double X, double Y) p) => new(p.X, p.Y);

    private void DrawRelation(DrawingContext ctx, DataModel m, ModelRelation r)
    {
        if (ModelDiagram.Geometry(m, r) is not { } g) return;
        bool active = ModelDiagram.IsRelationActive(r, _hovered, Selection);
        var color = active ? Accent : Color.FromArgb((byte)(255 * 0.35), 142, 142, 147);
        var brush = new SolidColorBrush(color);
        bool selected = Selection == new ModelSelection.Relation(r.Id);
        double width = selected ? 2.6 : 1.6;
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(P(g.Start), false);
            c.CubicBezierTo(P(g.C1), P(g.C2), P(g.End));
            c.EndFigure(false);
        }
        // 식별 관계 = 실선, 비식별 = 점선 (Avalonia 점선 길이는 선 굵기 배수)
        var pen = new Pen(brush, width) { DashStyle = r.Identifying ? null : new DashStyle([6 / width, 4 / width], 0) };
        ctx.DrawGeometry(null, pen, geo);
        var markPen = new Pen(brush, 1.6);
        var s = g.Start;
        double o = g.Out;
        if (r.Cardinality == ModelCardinality.OneToMany)
        {
            // 자식 쪽 까마귀발 (여러 개)
            var tip = new Point(s.X + o * 12, s.Y);
            ctx.DrawLine(markPen, tip, new Point(s.X, s.Y - 7));
            ctx.DrawLine(markPen, tip, new Point(s.X, s.Y + 7));
            ctx.DrawLine(markPen, tip, P(s));
        }
        else
        {
            ctx.DrawLine(markPen, new Point(s.X + o * 8, s.Y - 7), new Point(s.X + o * 8, s.Y + 7));
        }
        if (r.Optional)
        {
            // 선택(0 허용): 작은 원
            ctx.DrawEllipse(null, new Pen(brush, 1.4), new Point(s.X + o * 19, s.Y), 4, 4);
        }
        // 부모 쪽: 막대 둘 (반드시 하나)
        var e = g.End;
        double n = -g.Inward;
        foreach (var d in new[] { 7.0, 12.0 })
            ctx.DrawLine(markPen, new Point(e.X + n * d, e.Y - 7), new Point(e.X + n * d, e.Y + 7));
    }

    private Rect? BadgeRect(DataModel m, ModelRelation r, out FormattedText? text, IBrush? fg = null)
    {
        text = null;
        if (ModelDiagram.Midpoint(m, r) is not { } mid) return null;
        text = Text(ModelDiagram.RelationBadge(r), 10, fg ?? Brushes.White, bold: true);
        double w = text.Width + 10, h = Math.Max(14, text.Height + 2);
        return new Rect(mid.X - w / 2, mid.Y - h / 2, w, h);
    }

    private void DrawBadge(DrawingContext ctx, DataModel m, ModelRelation r)
    {
        bool selected = Selection == new ModelSelection.Relation(r.Id);
        var fg = selected ? Brushes.White : (IBrush)new SolidColorBrush(Accent);
        if (BadgeRect(m, r, out var text, fg) is not { } rect || text == null) return;
        var bg = selected ? new SolidColorBrush(Accent) : Res("CodeBackground", "#FFFFFF");
        ctx.DrawRectangle(bg, new Pen(new SolidColorBrush(Accent, 0.6), 1), rect, rect.Height / 2, rect.Height / 2);
        ctx.DrawText(text, new Point(rect.X + 5, rect.Y + (rect.Height - text.Height) / 2));
    }

    private void DrawCard(DrawingContext ctx, DataModel m, ModelTable t)
    {
        var f = ModelDiagram.Frame(t);
        var rect = new Rect(f.X, f.Y, f.Width, f.Height);
        var header = new SolidColorBrush(CardColor(t.Color));
        var body = Res("CodeBackground", IsDark ? "#1C1C1E" : "#FFFFFF");
        var primary = IsDark ? Colors.White : Colors.Black;
        var secondary = Res("SecondaryText", "#8A8A8E");
        bool selected = Selection == new ModelSelection.Table(t.Id) || Pending == t.Id;
        bool highlighted = ModelDiagram.IsHighlighted(m, t.Id, _hovered);
        var shadow = new BoxShadows(new BoxShadow { OffsetY = 3, Blur = 6, Color = Color.FromArgb(31, 0, 0, 0) });
        ctx.DrawRectangle(body, null, rect, 8, 8, shadow);
        using (ctx.PushClip(new RoundedRect(rect, 8)))
        {
            ctx.FillRectangle(header, new Rect(f.X, f.Y, f.Width, ModelDiagram.HeaderHeight));
            // 머리: 아이콘 + 이름 (+ 둘째 줄)
            var icon = Text("▦", 11, Brushes.White);
            double hy = f.Y + ModelDiagram.HeaderHeight / 2;
            ctx.DrawText(icon, new Point(f.X + 10, hy - icon.Height / 2));
            var n = ModelDiagram.Label(t.LogicalName, t.PhysicalName, NameMode);
            double x = f.X + 10 + icon.Width + 6, maxRight = f.X + f.Width - 10;
            var main = Text(n.Main.Length == 0 ? Tr("(이름 없음)") : n.Main, 13, Brushes.White, mono: NameMode == ModelNameMode.Physical,
                            bold: true, italic: n.Missing, maxWidth: Math.Max(10, maxRight - x));
            ctx.DrawText(main, new Point(x, hy - main.Height / 2));
            if (n.Sub is { } sub)
            {
                double sx = x + main.Width + 6;
                if (maxRight - sx > 12)
                {
                    var st = Text(sub, 10, new SolidColorBrush(Colors.White, 0.85), mono: true, maxWidth: maxRight - sx);
                    ctx.DrawText(st, new Point(sx, hy - st.Height / 2));
                }
            }
            // 컬럼 줄
            var fk = ModelDiagram.ForeignKeyColumns(m, t.Id);
            double rowY = f.Y + ModelDiagram.HeaderHeight + 5;
            if (t.Columns.Count == 0)
            {
                var none = Text(Tr("컬럼 없음"), 11, secondary);
                ctx.DrawText(none, new Point(f.X + 8, rowY + (ModelDiagram.RowHeight - none.Height) / 2));
            }
            foreach (var c in t.Columns)
            {
                double cy = rowY + ModelDiagram.RowHeight / 2;
                double ix = f.X + 8;
                if (c.PrimaryKey) DrawKey(ctx, ix + 1, cy, fk.Contains(c.Id) ? Color.Parse("#FF9500") : Color.Parse("#FFCC00"));
                else if (fk.Contains(c.Id)) DrawLink(ctx, ix + 1, cy, Accent);
                else DrawDiamond(ctx, ix + 4, cy, c.NotNull, secondary);
                double tx = ix + 14 + 5;
                double right = f.X + f.Width - 8;
                if (NameMode != ModelNameMode.Logical)
                {
                    var type = Text(c.DataType, 10, secondary, mono: true, maxWidth: 90);
                    right -= type.Width;
                    ctx.DrawText(type, new Point(right, cy - type.Height / 2));
                    right -= 4;
                }
                var cn = ModelDiagram.Label(c.LogicalName, c.PhysicalName, NameMode);
                var nameText = Text(cn.Main.Length == 0 ? Tr("(이름 없음)") : cn.Main, 12,
                                    cn.Missing ? secondary : new SolidColorBrush(primary),
                                    mono: NameMode == ModelNameMode.Physical, italic: cn.Missing, maxWidth: Math.Max(10, right - tx));
                ctx.DrawText(nameText, new Point(tx, cy - nameText.Height / 2));
                if (cn.Sub is { } csub)
                {
                    double sx = tx + nameText.Width + 5;
                    if (right - sx > 12)
                    {
                        var subBrush = cn.Missing ? new SolidColorBrush(Color.Parse("#FF9500")) : new SolidColorBrush(Color.Parse("#8E8E93"), 0.7);
                        var st = Text(csub, 10, subBrush, mono: true, maxWidth: right - sx);
                        ctx.DrawText(st, new Point(sx, cy - st.Height / 2));
                    }
                }
                rowY += ModelDiagram.RowHeight;
            }
        }
        var border = selected ? new Pen(new SolidColorBrush(Color.Parse("#FF9500")), 2.5)
            : highlighted ? new Pen(new SolidColorBrush(Accent), 2)
            : new Pen(new SolidColorBrush(primary, 0.15), 1);
        var inset = border.Thickness / 2;
        ctx.DrawRectangle(null, border, rect.Deflate(inset), 8 - inset, 8 - inset);
    }

    /// 열쇠 (Mac key.fill): 고리 + 막대 + 이
    private static void DrawKey(DrawingContext ctx, double x, double y, Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1.6);
        ctx.DrawEllipse(null, pen, new Point(x + 3, y), 2.6, 2.6);
        ctx.DrawLine(pen, new Point(x + 5.6, y), new Point(x + 12, y));
        ctx.DrawLine(pen, new Point(x + 10, y), new Point(x + 10, y + 3));
        ctx.DrawLine(pen, new Point(x + 12, y), new Point(x + 12, y + 2.5));
    }

    /// 사슬 (Mac link): 겹친 고리 둘
    private static void DrawLink(DrawingContext ctx, double x, double y, Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1.4);
        ctx.DrawRectangle(null, pen, new Rect(x, y - 2.5, 7, 5), 2.5, 2.5);
        ctx.DrawRectangle(null, pen, new Rect(x + 5, y - 2.5, 7, 5), 2.5, 2.5);
    }

    /// 마름모 (NOT NULL 이면 채움)
    private static void DrawDiamond(DrawingContext ctx, double cx, double cy, bool filled, IBrush brush)
    {
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(new Point(cx, cy - 3), true);
            c.LineTo(new Point(cx + 3, cy));
            c.LineTo(new Point(cx, cy + 3));
            c.LineTo(new Point(cx - 3, cy));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(filled ? brush : null, filled ? null : new Pen(brush, 1), geo);
    }

    // MARK: - 누르기 · 끌기

    /// 캔버스 좌표(확대 전)에서 맨 위 카드
    public Guid? TableAt(Point p)
    {
        if (Model is not { } m) return null;
        for (int i = m.Tables.Count - 1; i >= 0; i--)
        {
            var f = ModelDiagram.Frame(m.Tables[i]);
            if (p.X >= f.X && p.X <= f.X + f.Width && p.Y >= f.Y && p.Y <= f.Y + f.Height) return m.Tables[i].Id;
        }
        return null;
    }

    public Guid? RelationBadgeAt(Point p)
    {
        if (Model is not { } m) return null;
        for (int i = m.Relations.Count - 1; i >= 0; i--)
            if (BadgeRect(m, m.Relations[i], out _) is { } r && r.Inflate(2).Contains(p)) return m.Relations[i].Id;
        return null;
    }

    private Point ModelPoint(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        return new Point(p.X / Zoom, p.Y / Zoom);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Vm == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var p = ModelPoint(e);
        _pressPoint = p;
        _pressed = true;
        _dragging = false;
        _pressTable = TableAt(p);
        _pressRelation = _pressTable == null ? RelationBadgeAt(p) : null;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Vm == null) return;
        var p = ModelPoint(e);
        if (_pressed && _pressTable is { } id)
        {
            double dx = p.X - _pressPoint.X, dy = p.Y - _pressPoint.Y;
            if (!_dragging && Math.Sqrt(dx * dx + dy * dy) * Zoom >= 3 && Vm.Tool is ModelTool.Select) _dragging = true;
            if (_dragging) Vm.DragTable(id, dx, dy);
            return;
        }
        var hover = TableAt(p);
        if (hover != _hovered)
        {
            _hovered = hover;
            InvalidateVisual();
        }
        // 관계 표시에 마우스를 올리면 설명
        var rel = hover == null ? RelationBadgeAt(p) : null;
        var tip = rel is { } rid && Model is { } m && m.Relations.FirstOrDefault(x => x.Id == rid) is { } r ? ModelDiagram.RelationHelp(m, r) : null;
        if (!Equals(ToolTip.GetTip(this), tip)) ToolTip.SetTip(this, tip);
        Cursor = Vm.Tool is ModelTool.Select ? (hover != null || rel != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default)
            : new Cursor(StandardCursorType.Cross);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Vm == null || !_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        if (_pressTable is { } id)
        {
            if (_dragging) Vm.EndDrag(id);
            else Vm.TapTable(id);
        }
        else if (_pressRelation is { } rid) Vm.SelectRelation(rid);
        else Vm.TapBackground(_pressPoint.X, _pressPoint.Y);
        _dragging = false;
        _pressTable = null;
        _pressRelation = null;
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hovered != null)
        {
            _hovered = null;
            InvalidateVisual();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Vm == null) return;
        switch (e.Key)
        {
            case Key.Escape:
                Vm.Escape();
                e.Handled = true;
                break;
            case Key.Delete or Key.Back when Vm.Selection != null:
                _ = Vm.DeleteSelectionAsync();
                e.Handled = true;
                break;
        }
    }

    // MARK: - PNG

    /// 다이어그램 전체를 2배 해상도 PNG 로 (Mac exportPNG: ImageRenderer scale 2)
    public static byte[] RenderPng(DataModel model, ModelNameMode mode, IBrush background, double scale = 2)
    {
        var canvas = new ModelCanvas(model, mode) { ExportBackground = background };
        var (w, h) = ModelDiagram.Size(model);
        var size = new Size(w, h);
        canvas.Measure(size);
        canvas.Arrange(new Rect(size));
        using var bmp = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale)),
                                               new Vector(96 * scale, 96 * scale));
        bmp.Render(canvas);
        using var ms = new MemoryStream();
        bmp.Save(ms, PngBitmapEncoderOptions.Default);
        return ms.ToArray();
    }
}
