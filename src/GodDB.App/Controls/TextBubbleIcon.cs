using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace GodDB.App.Controls;

/// 말풍선 아이콘 (Mac SF Symbol text.bubble / text.bubble.fill) — 사이드바 머리 줄의 테이블 설명 버튼 (Mac 1.4.3 #185).
/// Filled 면 채운 말풍선 (글줄은 바탕색으로 비운다), 아니면 테두리만.
public sealed class TextBubbleIcon : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<TextBubbleIcon>();
    public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<TextBubbleIcon, bool>(nameof(Filled));

    static TextBubbleIcon() => AffectsRender<TextBubbleIcon>(ForegroundProperty, FilledProperty);

    public TextBubbleIcon()
    {
        Width = 14;
        Height = 14;
    }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var brush = Foreground ?? Brushes.Gray;
        var s = Math.Min(Bounds.Width, Bounds.Height);
        if (s <= 0) return;
        double k = s / 16.0, ox = (Bounds.Width - s) / 2, oy = (Bounds.Height - s) / 2;
        Point P(double x, double y) => new(ox + x * k, oy + y * k);
        var pen = new Pen(brush, Math.Max(1, 1.3 * k), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        // 둥근 사각 말풍선 + 왼쪽 아래 꼬리
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(P(4, 2), true);
            c.LineTo(P(12, 2));
            c.QuadraticBezierTo(P(15, 2), P(15, 5));
            c.LineTo(P(15, 9.5));
            c.QuadraticBezierTo(P(15, 12.5), P(12, 12.5));
            c.LineTo(P(7, 12.5));
            c.LineTo(P(3.5, 15));
            c.LineTo(P(4, 12.4));
            c.QuadraticBezierTo(P(1, 12), P(1, 9.5));
            c.LineTo(P(1, 5));
            c.QuadraticBezierTo(P(1, 2), P(4, 2));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(Filled ? brush : null, pen, g);
        // 글줄 두 개
        var line = new Pen(Filled ? Brushes.White : brush, Math.Max(1, 1.2 * k), lineCap: PenLineCap.Round);
        ctx.DrawLine(line, P(4.5, 5.8), P(11.5, 5.8));
        ctx.DrawLine(line, P(4.5, 8.8), P(9.5, 8.8));
    }
}
