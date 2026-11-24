using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace GodDB.App.Controls;

/// Amazon Redshift 아이콘: 보라 원통 + 흰 띠 두 줄 (직접 그린 그림, AWS 로고 아님) — Mac RedshiftIcon.
/// 데이터 웨어하우스(열 저장)를 나타낸다. 색은 AWS 분석 서비스 보라 계열.
public static class RedshiftIcon
{
    public static readonly Color Body = Color.FromRgb(0x8C, 0x4F, 0xFF);     // 몸통
    public static readonly Color Bottom = Color.FromRgb(0x5A, 0x30, 0xB5);   // 아랫면
    public static readonly Color Top = Color.FromRgb(191, 166, 255);          // 윗면 (0.75, 0.65, 1.0)

    public static Control Create(double size)
    {
        var canvas = new Canvas { Width = size, Height = size, Name = "RedshiftIcon" };
        double w = size * 0.78, h = size * 0.9, cap = h * 0.28;
        double x = (size - w) / 2, y = (size - h) / 2;
        void Add(Control c, double left, double top) { Canvas.SetLeft(c, left); Canvas.SetTop(c, top); canvas.Children.Add(c); }

        Add(new Ellipse { Width = w, Height = cap, Fill = new SolidColorBrush(Bottom), Name = "CylinderBottom" }, x, y + h - cap);
        Add(new Rectangle { Width = w, Height = h - cap, Fill = new SolidColorBrush(Body), Name = "CylinderBody" }, x, y + cap / 2);
        // 몸통을 두르는 흰 띠 (앞쪽 반 타원 = 아래쪽 호)
        var stroke = Math.Max(1, size * 0.07);
        foreach (var t in new[] { 0.42, 0.68 })
        {
            var cy = y + h * t;
            string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            var arc = Geometry.Parse($"M {F(x)},{F(cy)} A {F(w / 2)},{F(cap / 2)} 0 0 0 {F(x + w)},{F(cy)}");
            canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = arc, Stroke = Brushes.White, StrokeThickness = stroke, Name = "Band" });
        }
        Add(new Ellipse { Width = w, Height = cap, Fill = new SolidColorBrush(Top), Name = "CylinderTop" }, x, y);
        return canvas;
    }
}
