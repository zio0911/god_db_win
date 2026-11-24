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
    /// timer (자동 새로고침 꺼짐)
    Timer,
    /// timer.circle.fill (자동 새로고침 켜짐 — 초록으로 칠해 쓴다)
    TimerFill,
    /// list.bullet.indent (트리)
    ListIndent,
    /// list.bullet (목록)
    List,
    /// bookmark (저장 쿼리)
    Bookmark,
    /// square.and.arrow.up (내보내기 · 올리기)
    Upload,
    /// trash (지우기)
    Trash,
    /// doc.on.doc (복사)
    Copy,
    /// checkmark (복사했어요)
    Checkmark,
    /// plus.bubble (새 대화)
    PlusBubble,
    /// arrow.up.circle.fill (보내기)
    SendCircle,
    /// stop.circle.fill (멈춤)
    StopCircle,
    /// wand.and.stars (물리명 적용)
    Wand,
    /// hand.thumbsup (맞아요)
    ThumbsUp,
    /// hand.thumbsdown (아니에요)
    ThumbsDown,
    /// wifi.exclamationmark (목록을 못 가져옴)
    WifiExclamation,
    /// link (단축키 연결)
    Link,
    /// magnifyingglass (찾기)
    Magnifier,
    /// folder (그룹 고르기)
    Folder,
    /// folder.fill (그룹 머리)
    FolderFill,
    /// pin.fill (고정)
    Pin,
    /// eye (뷰)
    Eye,
    /// cylinder.split.1x2 (데이터베이스)
    Cylinder,
    /// bolt (트리거 · 자주 쓰는 질문)
    Bolt,
    /// gearshape (프로시저)
    Gear,
    /// function (함수)
    Function,
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
            case Symbol.Timer:
            case Symbol.TimerFill:
            {
                bool fill = Symbol == Symbol.TimerFill;
                var face = new Point(ox + 8 * k, oy + 8.8 * k);
                if (fill) ctx.DrawEllipse(brush, null, face, 6.6 * k, 6.6 * k);
                else ctx.DrawEllipse(null, thin, face, 6.2 * k, 6.2 * k);
                var hand = fill ? new Pen(Brushes.White, Math.Max(1, 1.6 * k), lineCap: PenLineCap.Round) : thin;
                ctx.DrawLine(hand, face, P(10.6, 6.4));
                if (!fill) ctx.DrawLine(thin, P(6.4, 0.9), P(9.6, 0.9));   // 스톱워치 꼭지
                break;
            }
            case Symbol.ListIndent:
            case Symbol.List:
            {
                bool indent = Symbol == Symbol.ListIndent;
                double[] ys = [3.5, 8, 12.5];
                double[] xs = indent ? [1.8, 5.3, 5.3] : [1.8, 1.8, 1.8];
                for (int i = 0; i < 3; i++)
                {
                    ctx.DrawEllipse(brush, null, P(xs[i], ys[i]), 1.25 * k, 1.25 * k);
                    ctx.DrawLine(thin, P(xs[i] + 2.8, ys[i]), P(14.5, ys[i]));
                }
                break;
            }
            case Symbol.Bookmark:
                // 위가 네모, 아래가 V 로 파인 책갈피 (선)
                ctx.DrawGeometry(null, thin, Poly((3.5, 1.8), (12.5, 1.8), (12.5, 14.5), (8, 10.8), (3.5, 14.5)));
                break;
            case Symbol.Upload:
            {
                // 쟁반 + 위 화살표
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(P(5, 6.5), false); c.LineTo(P(2.5, 6.5)); c.LineTo(P(2.5, 14.5)); c.LineTo(P(13.5, 14.5)); c.LineTo(P(13.5, 6.5)); c.LineTo(P(11, 6.5));
                    c.EndFigure(false);
                }
                ctx.DrawGeometry(null, thin, g);
                ctx.DrawLine(thin, P(8, 1.4), P(8, 10.5));
                ctx.DrawLine(thin, P(5.2, 4.2), P(8, 1.4)); ctx.DrawLine(thin, P(10.8, 4.2), P(8, 1.4));
                break;
            }
            case Symbol.Trash:
            {
                ctx.DrawLine(thin, P(2, 3.8), P(14, 3.8));
                ctx.DrawGeometry(null, thin, Poly((6, 3.8), (6.5, 1.8), (9.5, 1.8), (10, 3.8)));
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(P(3.6, 3.8), false); c.LineTo(P(4.5, 14.5)); c.LineTo(P(11.5, 14.5)); c.LineTo(P(12.4, 3.8));
                    c.EndFigure(false);
                }
                ctx.DrawGeometry(null, thin, g);
                ctx.DrawLine(thin, P(6.5, 6.5), P(6.7, 12)); ctx.DrawLine(thin, P(9.5, 6.5), P(9.3, 12));
                break;
            }
            case Symbol.Copy:
                // 뒤 문서 (모서리만) + 앞 문서
                ctx.DrawRectangle(null, thin, new Rect(P(5.5, 5), P(14, 14.8)), 1.5 * k, 1.5 * k);
                ctx.DrawLine(thin, P(2.2, 11), P(2.2, 2.6)); ctx.DrawLine(thin, P(2.2, 2.6), P(3.2, 1.6)); ctx.DrawLine(thin, P(3.2, 1.6), P(10, 1.6));
                ctx.DrawLine(thin, P(7.8, 8.5), P(11.7, 8.5)); ctx.DrawLine(thin, P(7.8, 11.2), P(11.7, 11.2));
                break;
            case Symbol.Checkmark:
            {
                var bold = new Pen(brush, Math.Max(1, 2 * k), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                var g = new StreamGeometry();
                using (var c = g.Open()) { c.BeginFigure(P(2.5, 8.5), false); c.LineTo(P(6.3, 12.5)); c.LineTo(P(13.5, 3.5)); c.EndFigure(false); }
                ctx.DrawGeometry(null, bold, g);
                break;
            }
            case Symbol.PlusBubble:
            {
                // 말풍선 + 가운데 더하기
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(P(4, 12.2), false);
                    c.ArcTo(P(8, 1.6), new Size(6.5 * k, 5.3 * k), 0, false, SweepDirection.Clockwise);
                    c.ArcTo(P(8, 12.2), new Size(6.5 * k, 5.3 * k), 0, false, SweepDirection.Clockwise);
                    c.LineTo(P(5.5, 12));
                    c.LineTo(P(2.2, 14.6));
                    c.LineTo(P(4, 12.2));
                    c.EndFigure(false);
                }
                ctx.DrawGeometry(null, thin, g);
                ctx.DrawLine(thin, P(8, 4.4), P(8, 9.4)); ctx.DrawLine(thin, P(5.5, 6.9), P(10.5, 6.9));
                break;
            }
            case Symbol.SendCircle:
            case Symbol.StopCircle:
            {
                // 칠한 원 + 흰 글자 (위 화살표 / 네모)
                ctx.DrawEllipse(brush, null, P(8, 8), 7 * k, 7 * k);
                var white = new Pen(Brushes.White, Math.Max(1, 1.8 * k), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                if (Symbol == Symbol.StopCircle)
                    ctx.DrawRectangle(Brushes.White, null, new Rect(P(5.5, 5.5), P(10.5, 10.5)), 1 * k, 1 * k);
                else
                {
                    ctx.DrawLine(white, P(8, 4.3), P(8, 11.8));
                    ctx.DrawLine(white, P(4.9, 7.3), P(8, 4.3)); ctx.DrawLine(white, P(11.1, 7.3), P(8, 4.3));
                }
                break;
            }
            case Symbol.Wand:
            {
                var bold = new Pen(brush, Math.Max(1, 2 * k), lineCap: PenLineCap.Round);
                ctx.DrawLine(bold, P(2, 14.2), P(10, 6.2));
                Star(12.3, 3.7, 3);
                Star(5.3, 3.2, 1.8);
                Star(13, 10.6, 1.8);
                break;
            }
            case Symbol.ThumbsUp:
            case Symbol.ThumbsDown:
            {
                // 엄지 손 (선) — 아래 방향은 위아래를 뒤집는다
                bool down = Symbol == Symbol.ThumbsDown;
                Point Q(double x, double y) => down ? P(x, 16 - y) : P(x, y);
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(Q(1.8, 7.2), true); c.LineTo(Q(4.4, 7.2)); c.LineTo(Q(4.4, 14.3)); c.LineTo(Q(1.8, 14.3)); c.EndFigure(true);
                    c.BeginFigure(Q(4.4, 7.4), false);
                    c.LineTo(Q(7.2, 3.6)); c.LineTo(Q(7.6, 1.7));
                    c.QuadraticBezierTo(Q(9.8, 1.6), Q(9.3, 4.4));
                    c.LineTo(Q(8.8, 6.4)); c.LineTo(Q(12.9, 6.4));
                    c.QuadraticBezierTo(Q(14.6, 6.6), Q(14.1, 8.4));
                    c.LineTo(Q(12.9, 13));
                    c.QuadraticBezierTo(Q(12.5, 14.3), Q(11.2, 14.3));
                    c.LineTo(Q(4.4, 14.3));
                    c.EndFigure(false);
                }
                ctx.DrawGeometry(null, thin, g);
                break;
            }
            case Symbol.WifiExclamation:
            {
                // 와이파이 부채꼴 (왼쪽) + 느낌표 (오른쪽)
                Point C = P(6.5, 13.5);
                void Arc(double r)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        double a0 = -135 * Math.PI / 180, a1 = -45 * Math.PI / 180;
                        c.BeginFigure(P(6.5 + r * Math.Cos(a0), 13.5 + r * Math.Sin(a0)), false);
                        c.ArcTo(P(6.5 + r * Math.Cos(a1), 13.5 + r * Math.Sin(a1)), new Size(r * k, r * k), 0, false, SweepDirection.Clockwise);
                        c.EndFigure(false);
                    }
                    ctx.DrawGeometry(null, thin, g);
                }
                Arc(3.6); Arc(6.6); Arc(9.6);
                ctx.DrawEllipse(brush, null, C, 1.2 * k, 1.2 * k);
                ctx.DrawLine(thin, P(14.2, 5.5), P(14.2, 11));
                ctx.DrawEllipse(brush, null, P(14.2, 13.6), 1 * k, 1 * k);
                break;
            }
            case Symbol.Link:
            {
                // 비스듬한 고리 두 개
                void Ring(double cx, double cy)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        // 45° 기운 둥근 막대 (가로 7, 세로 4)
                        double cos = Math.Cos(-Math.PI / 4), sin = Math.Sin(-Math.PI / 4);
                        Point R(double x, double y) => P(cx + x * cos - y * sin, cy + x * sin + y * cos);
                        c.BeginFigure(R(-2, -2.1), false);
                        c.LineTo(R(2, -2.1));
                        c.ArcTo(R(2, 2.1), new Size(2.1 * k, 2.1 * k), 0, false, SweepDirection.Clockwise);
                        c.LineTo(R(-2, 2.1));
                        c.ArcTo(R(-2, -2.1), new Size(2.1 * k, 2.1 * k), 0, false, SweepDirection.Clockwise);
                        c.EndFigure(true);
                    }
                    ctx.DrawGeometry(null, thin, g);
                }
                Ring(5.6, 10.4); Ring(10.4, 5.6);
                break;
            }
            case Symbol.Magnifier:
                ctx.DrawEllipse(null, thin, P(6.8, 6.8), 4.8 * k, 4.8 * k);
                ctx.DrawLine(new Pen(brush, Math.Max(1, 2 * k), lineCap: PenLineCap.Round), P(10.4, 10.4), P(14.3, 14.3));
                break;
            case Symbol.Folder:
            case Symbol.FolderFill:
            {
                var g = Poly((1.5, 3), (6, 3), (7.5, 4.8), (14.5, 4.8), (14.5, 13.5), (1.5, 13.5));
                if (Symbol == Symbol.FolderFill) ctx.DrawGeometry(brush, null, g);
                else { ctx.DrawGeometry(null, thin, g); ctx.DrawLine(thin, P(1.5, 6.8), P(14.5, 6.8)); }
                break;
            }
            case Symbol.Pin:
                // 압정: 머리 (칠함) + 바늘
                ctx.DrawGeometry(brush, null, Poly((5, 1.5), (11, 1.5), (10, 3), (10, 6.5), (12.5, 9.5), (3.5, 9.5), (6, 6.5), (6, 3)));
                ctx.DrawLine(thin, P(8, 9.5), P(8, 15));
                break;
            case Symbol.Eye:
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(P(1, 8), true);
                    c.QuadraticBezierTo(P(8, -0.5), P(15, 8));
                    c.QuadraticBezierTo(P(8, 16.5), P(1, 8));
                    c.EndFigure(true);
                }
                ctx.DrawGeometry(null, thin, g);
                ctx.DrawEllipse(brush, null, P(8, 8), 2.4 * k, 2.4 * k);
                break;
            }
            case Symbol.Cylinder:
            {
                // 위 타원 + 옆선 + 가운데 · 아래 반 타원
                ctx.DrawEllipse(null, thin, P(8, 3.3), 5.5 * k, 1.9 * k);
                ctx.DrawLine(thin, P(2.5, 3.3), P(2.5, 12.7)); ctx.DrawLine(thin, P(13.5, 3.3), P(13.5, 12.7));
                foreach (var y in new[] { 8.0, 12.7 })
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(P(2.5, y), false);
                        c.ArcTo(P(13.5, y), new Size(5.5 * k, 1.9 * k), 0, false, SweepDirection.CounterClockwise);
                        c.EndFigure(false);
                    }
                    ctx.DrawGeometry(null, thin, g);
                }
                break;
            }
            case Symbol.Bolt:
                ctx.DrawGeometry(null, thin, Poly((9.5, 1.2), (3, 9.2), (7.6, 9.2), (6.5, 14.8), (13, 6.8), (8.4, 6.8)));
                break;
            case Symbol.Gear:
            {
                // 톱니 8개 + 가운데 구멍
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    const int teeth = 8;
                    for (int i = 0; i < teeth * 4; i++)
                    {
                        double a = (i / (teeth * 4.0)) * 2 * Math.PI - Math.PI / 2;
                        double r = (i % 4) is 0 or 1 ? 7 : 5.3;
                        var pt = P(8 + r * Math.Cos(a), 8 + r * Math.Sin(a));
                        if (i == 0) c.BeginFigure(pt, false); else c.LineTo(pt);
                    }
                    c.EndFigure(true);
                }
                ctx.DrawGeometry(null, thin, g);
                ctx.DrawEllipse(null, thin, P(8, 8), 2.2 * k, 2.2 * k);
                break;
            }
            case Symbol.Function:
            {
                // 이탤릭 f + x
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(P(9.5, 2.2), false);
                    c.QuadraticBezierTo(P(7.2, 1.2), P(6.8, 4.5));
                    c.LineTo(P(5.6, 12.5));
                    c.QuadraticBezierTo(P(5.1, 15.2), P(2.6, 14.2));
                    c.EndFigure(false);
                }
                ctx.DrawGeometry(null, thin, g);
                ctx.DrawLine(thin, P(4, 6.5), P(9, 6.5));
                ctx.DrawLine(thin, P(10, 8), P(14.5, 13)); ctx.DrawLine(thin, P(14.5, 8), P(10, 13));
                break;
            }
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
