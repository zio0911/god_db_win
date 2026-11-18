using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GodDB.App.Controls;

/// 테이블 아이콘 — Mac SF Symbol "tablecells" 와 같은 모양: 둥근 테두리 안에 2열 × 3행 (칸 6개).
/// 기본 색은 강조 파랑(Mac 사이드바 Label 아이콘 색). Filled 면 "tablecells.fill" (칸을 채우고 선은 흰색).
public sealed class TableCellsIcon : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<TableCellsIcon, IBrush?>(nameof(Foreground), new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<TableCellsIcon, bool>(nameof(Filled));

    static TableCellsIcon() => AffectsRender<TableCellsIcon>(ForegroundProperty, FilledProperty);

    public TableCellsIcon()
    {
        Width = 16;
        Height = 16;
    }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        if (Foreground is { } brush) Draw(ctx, new Rect(Bounds.Size), brush, Filled);
    }

    /// 캔버스(모델링 · 다이어그램)에서 같은 모양을 직접 그릴 때
    public static void Draw(DrawingContext ctx, Rect bounds, IBrush brush, bool filled = false)
    {
        var s = Math.Min(bounds.Width, bounds.Height);
        if (s <= 0) return;
        var Bounds = bounds;
        var Filled = filled;
        // SF Symbol 비율: 가로가 조금 더 긴 사각형, 선 굵기 약 1/9
        double w = s * 0.92, h = s * 0.74, t = Math.Max(1, s / 9.0);
        double x = Bounds.X + (Bounds.Width - w) / 2, y = Bounds.Y + (Bounds.Height - h) / 2;
        var outer = new Rect(x + t / 2, y + t / 2, w - t, h - t);
        double r = s * 0.16;
        var linePen = new Pen(Filled ? Brushes.White : brush, Filled ? Math.Max(1, t * 0.8) : t);
        if (Filled) ctx.DrawRectangle(brush, null, outer.Inflate(t / 2), r, r);
        else ctx.DrawRectangle(null, new Pen(brush, t), outer, r, r);
        // 세로 1줄 (2열) · 가로 2줄 (3행)
        var cx = outer.X + outer.Width / 2;
        ctx.DrawLine(linePen, new Point(cx, outer.Y), new Point(cx, outer.Bottom));
        for (int i = 1; i <= 2; i++)
        {
            var yy = outer.Y + outer.Height * i / 3;
            ctx.DrawLine(linePen, new Point(outer.X, yy), new Point(outer.Right, yy));
        }
    }
}
