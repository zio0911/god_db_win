using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace GodDB.App.Controls;

/// 도구 막대 아이콘 — Mac SF Symbols 모양을 벡터로 직접 그린다 (글자 기호는 OS 마다 이모지로 그려져 모양이 들쭉날쭉).
public enum Symbol
{
    /// play.fill
    Play,
    /// forward.fill
    Forward,
    /// stop.fill
    Stop,
    /// point.3.connected.trianglepath.dotted (실행 계획)
    Plan,
    /// sparkles (AI)
    Sparkles,
    /// clock.arrow.circlepath (히스토리)
    History,
    /// arrow.clockwise (새로고침)
    Refresh,
    /// doc.text (CSV)
    Doc,
    /// curlybraces (JSON)
    Braces,
    /// chevron.left.forwardslash.chevron.right (SQL)
    Code,
    /// text.alignleft (Markdown)
    AlignLeft,
    /// square.stack.3d.down.right (쿼리 전체 데이터)
    Stack,
    /// rectangle.on.rectangle (보이는 행만)
    Rectangles,
    /// square.and.arrow.down (내보내기)
    Download,
}

public sealed class SymbolIcon : Control
{
    /// 아이콘 + 글자 (단추 Content 용). 글자는 "label" 클래스라 좁은 도구 막대에서는 숨는다
    public static StackPanel WithText(Symbol symbol, string text) => new()
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Spacing = 5,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        Children =
        {
            new SymbolIcon(symbol) { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            new TextBlock { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Classes = { "label" } },
        },
    };

    /// 글자 색을 이어받는다 (단추 안에서는 단추 글자 색 — 강조 단추면 흰색)
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<SymbolIcon>();
    public static readonly StyledProperty<Symbol> SymbolProperty = AvaloniaProperty.Register<SymbolIcon, Symbol>(nameof(Symbol));

    static SymbolIcon() => AffectsRender<SymbolIcon>(ForegroundProperty, SymbolProperty);

    public SymbolIcon()
    {
        Width = 14;
        Height = 14;
    }

    public SymbolIcon(Symbol s) : this() => Symbol = s;

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Symbol Symbol { get => GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var brush = Foreground ?? Brushes.Black;
        var s = Math.Min(Bounds.Width, Bounds.Height);
        if (s <= 0) return;
        // 16 칸 좌표계로 그린다
        double k = s / 16.0, ox = (Bounds.Width - s) / 2, oy = (Bounds.Height - s) / 2;
        Point P(double x, double y) => new(ox + x * k, oy + y * k);
        var thin = new Pen(brush, Math.Max(1, 1.5 * k), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        StreamGeometry Poly(params (double x, double y)[] pts)
        {
            var g = new StreamGeometry();
            using var c = g.Open();
            c.BeginFigure(P(pts[0].x, pts[0].y), true);
            foreach (var p in pts[1..]) c.LineTo(P(p.x, p.y));
            c.EndFigure(true);
            return g;
        }

        // 네 갈래 별 (sparkles): 가운데 (cx, cy), 반지름 r, 오목한 곡선
        void Star(double cx, double cy, double r)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(P(cx, cy - r), true);
                c.QuadraticBezierTo(P(cx, cy), P(cx + r, cy));
                c.QuadraticBezierTo(P(cx, cy), P(cx, cy + r));
                c.QuadraticBezierTo(P(cx, cy), P(cx - r, cy));
                c.QuadraticBezierTo(P(cx, cy), P(cx, cy - r));
                c.EndFigure(true);
            }
            ctx.DrawGeometry(brush, null, g);
        }

        // 원호 + 화살촉 (clock.arrow.circlepath / arrow.clockwise)
        void ArcArrow(bool clockwise)
        {
            var g = new StreamGeometry();
            double cx = 8, cy = 8.5, r = 6;
            using (var c = g.Open())
            {
                // 왼쪽 위에서 시작해 한 바퀴 가까이 돈다
                var a0 = clockwise ? -100.0 : -150.0;
                var a1 = a0 + (clockwise ? 300 : -300);
                Point At(double deg) => P(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));
                c.BeginFigure(At(a0), false);
                c.ArcTo(At(a1), new Size(r * k, r * k), 0, true, clockwise ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
                c.EndFigure(false);
            }
            ctx.DrawGeometry(null, thin, g);
            // 화살촉: 시작점 쪽
            var head = clockwise ? Poly((5.2, 0.8), (9.6, 2.6), (5.8, 5.4)) : Poly((1.0, 2.2), (5.6, 1.6), (3.4, 5.8));
            ctx.DrawGeometry(brush, null, head);
        }

        switch (Symbol)
        {
            case Symbol.Play:
                ctx.DrawGeometry(brush, null, Poly((3.5, 2), (14, 8), (3.5, 14)));
                break;
            case Symbol.Forward:
                ctx.DrawGeometry(brush, null, Poly((1, 3), (8, 8), (1, 13)));
                ctx.DrawGeometry(brush, null, Poly((8, 3), (15, 8), (8, 13)));
                break;
            case Symbol.Stop:
                ctx.DrawRectangle(brush, null, new Rect(P(3, 3), P(13, 13)), 2 * k, 2 * k);
                break;
            case Symbol.Plan:
            {
                var dotted = new Pen(brush, Math.Max(1, 1.2 * k), new DashStyle([1.2, 1.6], 0), PenLineCap.Round);
                Point a = P(8, 2.8), b = P(2.8, 12.8), c = P(13.2, 12.8);
                ctx.DrawLine(dotted, a, b);
                ctx.DrawLine(dotted, b, c);
                ctx.DrawLine(dotted, c, a);
                foreach (var p in new[] { a, b, c }) ctx.DrawEllipse(brush, null, p, 2.3 * k, 2.3 * k);
                break;
            }
            case Symbol.Sparkles:
                Star(6.5, 9.5, 5.8);
                Star(12.6, 3.6, 2.8);
                Star(13.2, 12.4, 1.9);
                break;
            case Symbol.History:
                ArcArrow(clockwise: false);
                // 시계 바늘
                ctx.DrawLine(thin, P(8, 8.5), P(8, 5.2));
                ctx.DrawLine(thin, P(8, 8.5), P(10.6, 10));
                break;
            case Symbol.Refresh:
                ArcArrow(clockwise: true);
                break;
            case Symbol.Doc:
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(P(3.5, 1.5), false);
                    c.LineTo(P(9.5, 1.5)); c.LineTo(P(12.5, 4.5)); c.LineTo(P(12.5, 14.5)); c.LineTo(P(3.5, 14.5)); c.LineTo(P(3.5, 1.5));
                    c.EndFigure(true);
                    c.BeginFigure(P(9.5, 1.5), false); c.LineTo(P(9.5, 4.5)); c.LineTo(P(12.5, 4.5)); c.EndFigure(false);
                }
                ctx.DrawGeometry(null, thin, g);
                foreach (var y in new[] { 7.5, 9.8, 12.1 }) ctx.DrawLine(thin, P(5.8, y), P(10.2, y));
                break;
            }
            case Symbol.Braces:
            {
                void Brace(bool left)
                {
                    double x0 = left ? 6 : 10, x1 = left ? 4.2 : 11.8, xm = left ? 2.2 : 13.8;
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(P(x0, 1.8), false);
                        c.QuadraticBezierTo(P(x1, 1.8), P(x1, 4));
                        c.LineTo(P(x1, 6.3));
                        c.QuadraticBezierTo(P(x1, 8), P(xm, 8));
                        c.QuadraticBezierTo(P(x1, 8), P(x1, 9.7));
                        c.LineTo(P(x1, 12));
                        c.QuadraticBezierTo(P(x1, 14.2), P(x0, 14.2));
                        c.EndFigure(false);
                    }
                    ctx.DrawGeometry(null, thin, g);
                }
                Brace(true); Brace(false);
                break;
            }
            case Symbol.Code:
                ctx.DrawLine(thin, P(5, 4), P(1.5, 8)); ctx.DrawLine(thin, P(1.5, 8), P(5, 12));
                ctx.DrawLine(thin, P(11, 4), P(14.5, 8)); ctx.DrawLine(thin, P(14.5, 8), P(11, 12));
                ctx.DrawLine(thin, P(9.3, 2.5), P(6.7, 13.5));
                break;
            case Symbol.AlignLeft:
                ctx.DrawLine(thin, P(2, 3.5), P(14, 3.5));
                ctx.DrawLine(thin, P(2, 6.5), P(10, 6.5));
                ctx.DrawLine(thin, P(2, 9.5), P(14, 9.5));
                ctx.DrawLine(thin, P(2, 12.5), P(9, 12.5));
                break;
            case Symbol.Stack:
                // 겹친 사각형 3장 (오른쪽 아래로)
                ctx.DrawRectangle(null, thin, new Rect(P(5.5, 5.5), P(14, 14)), 1.5 * k, 1.5 * k);
                ctx.DrawLine(thin, P(3.7, 12), P(3.7, 4.8)); ctx.DrawLine(thin, P(3.7, 4.8), P(4.8, 3.7)); ctx.DrawLine(thin, P(4.8, 3.7), P(12, 3.7));
                ctx.DrawLine(thin, P(1.9, 10), P(1.9, 3)); ctx.DrawLine(thin, P(1.9, 3), P(3, 1.9)); ctx.DrawLine(thin, P(3, 1.9), P(10, 1.9));
                break;
            case Symbol.Rectangles:
                ctx.DrawRectangle(null, thin, new Rect(P(5, 6), P(14.5, 13.5)), 1.5 * k, 1.5 * k);
                ctx.DrawLine(thin, P(2, 10.5), P(2, 4)); ctx.DrawLine(thin, P(2, 4), P(3, 3)); ctx.DrawLine(thin, P(3, 3), P(11, 3)); ctx.DrawLine(thin, P(11, 3), P(11.5, 3.5));
                break;
            case Symbol.Download:
            {
                // 쟁반 + 아래 화살표
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(P(5, 5.5), false); c.LineTo(P(2.5, 5.5)); c.LineTo(P(2.5, 14.5)); c.LineTo(P(13.5, 14.5)); c.LineTo(P(13.5, 5.5)); c.LineTo(P(11, 5.5));
                    c.EndFigure(false);
                }
                ctx.DrawGeometry(null, thin, g);
                ctx.DrawLine(thin, P(8, 1.5), P(8, 10.5));
                ctx.DrawLine(thin, P(5.2, 7.8), P(8, 10.6)); ctx.DrawLine(thin, P(10.8, 7.8), P(8, 10.6));
                break;
            }
        }
    }
}
