using System.Diagnostics;
using Avalonia.Animation.Easings;
using Avalonia.Threading;

namespace GodDB.App.Infrastructure;

/// 짧은 화면 전환 (Mac `withAnimation(.easeInOut(duration:))`). 진행도(0→1, ease-in-out 적용)를 차례로 알려 준다.
/// 끝나면 마지막에 꼭 1 을 한 번 준다. Cancel/새 전환을 시작하면 지금 것은 그 자리에서 멈춘다(1 을 주지 않음).
public sealed class Tween
{
    /// SwiftUI .easeInOut = cubic-bezier(0.42, 0, 0.58, 1)
    private static readonly SplineEasing EaseInOut = new(0.42, 0, 0.58, 1);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly Stopwatch _clock = new();
    private TimeSpan _duration;
    private Action<double>? _step;
    private Action? _done;

    public Tween() => _timer.Tick += (_, _) => Tick();

    public bool IsRunning => _timer.IsEnabled;

    public void Start(double seconds, Action<double> step, Action? done = null)
    {
        Cancel();
        _duration = TimeSpan.FromSeconds(seconds);
        _step = step;
        _done = done;
        if (_duration <= TimeSpan.Zero) { Finish(); return; }
        _clock.Restart();
        step(0);
        _timer.Start();
    }

    public void Cancel()
    {
        _timer.Stop();
        _step = null;
        _done = null;
    }

    /// 지금 바로 끝 상태로
    public void Finish()
    {
        _timer.Stop();
        var step = _step;
        var done = _done;
        _step = null;
        _done = null;
        step?.Invoke(1);
        done?.Invoke();
    }

    private void Tick()
    {
        var t = _clock.Elapsed.TotalMilliseconds / _duration.TotalMilliseconds;
        if (t >= 1) { Finish(); return; }
        _step?.Invoke(EaseInOut.Ease(t));
    }

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
