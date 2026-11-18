using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace GodDB.App.Controls;

/// 끝을 모르는 진행 표시 (Mac ProgressView() 의 동그란 살 12개 스피너). 막대(ProgressBar) 대신 Mac 처럼 작은 원을 돌린다.
/// 크기: Mini 10 · Small 16 · Regular 32 (Mac controlSize .mini / .small / 기본).
public sealed class ActivitySpinner : Control
{
    /// 살 색 (비우면 Mac 스피너처럼 회색)
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<ActivitySpinner, IBrush?>(nameof(Foreground));

    static ActivitySpinner() => AffectsRender<ActivitySpinner>(ForegroundProperty);

    private const int Spokes = 12;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.0 / Spokes) };
    private int _step;
    private bool _attached;

    public ActivitySpinner() : this(16) { }

    public ActivitySpinner(double size)
    {
        Width = Height = size;
        IsHitTestVisible = false;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _timer.Tick += (_, _) =>
        {
            _step = (_step + 1) % Spokes;
            InvalidateVisual();
        };
    }

    public static ActivitySpinner Mini() => new(10);
    public static ActivitySpinner Small() => new(16);
    public static ActivitySpinner Regular() => new(32);

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// 돌고 있는지 (보이고 화면에 붙어 있을 때만 돈다 — 테스트용)
    public bool IsSpinning => _timer.IsEnabled;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Sync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) Sync();
    }

    private void Sync()
    {
        if (_attached && IsVisible) _timer.Start();
        else _timer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var color = Foreground is ISolidColorBrush b ? b.Color : Color.FromRgb(0x8A, 0x8A, 0x8E);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var outer = size / 2;
        var inner = outer * 0.5;
        var thickness = Math.Max(1, size * 0.09);
        for (var i = 0; i < Spokes; i++)
        {
            // 지금 단계의 살이 가장 진하고, 뒤로 갈수록 옅어진다
            var age = (_step - i + Spokes) % Spokes;
            var alpha = (byte)(255 * (1 - age / (double)Spokes * 0.8));
            var angle = i * 2 * Math.PI / Spokes - Math.PI / 2;
            var (sin, cos) = Math.SinCos(angle);
            var pen = new Pen(new SolidColorBrush(color, alpha / 255.0), thickness, lineCap: PenLineCap.Round);
            context.DrawLine(pen,
                new Point(c.X + cos * (inner + thickness / 2), c.Y + sin * (inner + thickness / 2)),
                new Point(c.X + cos * (outer - thickness / 2), c.Y + sin * (outer - thickness / 2)));
        }
    }
}
