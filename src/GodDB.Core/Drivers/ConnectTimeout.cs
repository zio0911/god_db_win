using static GodDB.Core.L10n;

namespace GodDB.Core.Drivers;

/// DB 서버가 정해진 시간 안에 연결을 끝내지 않음
/// (연결은 받지만 응답이 없는 서버, SSH 터널 너머가 안 붙는 경우 등). Mac DBConnectTimeoutError
public sealed class DbConnectTimeoutException(int seconds)
    : Exception(Tr("DB 서버가 %@초 안에 연결되지 않았어요. 주소 · 포트와 네트워크(VPN · SSH 터널)를 확인하세요.", seconds))
{
    public int Seconds { get; } = seconds;
}

/// 연결 작업에 시간 제한을 건다 (Mac ConnectDeadline).
/// 시간이 지나거나 호출한 쪽이 취소되면 작업을 기다리지 않고 바로 끝낸다. 작업에는 취소 토큰을 넘기지만,
/// 토큰을 보지 않는 단계(드라이버 내부 로그인 등)는 뒤에서 계속 돌 수 있으므로, 늦게 끝나면 `late` 로 결과를 넘겨
/// 정리(연결 닫기)하게 한다.
public static class ConnectDeadline
{
    public static async Task<T> RunAsync<T>(double seconds, Func<CancellationToken, Task<T>> work,
        Func<T, Task>? late = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // 결과 · 시간 초과 · 취소 중 먼저 온 것 하나만 이긴다
        var race = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 작업이 뒤에서 계속 돌며 토큰을 볼 수 있으므로 버리지(Dispose) 않는다 (타이머가 없는 CTS 라 GC 로 충분)
        var workCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                var value = await work(workCts.Token).ConfigureAwait(false);
                if (!race.TrySetResult(value) && late != null)
                {
                    try { await late(value).ConfigureAwait(false); } catch { }
                }
            }
            catch (Exception e)
            {
                race.TrySetException(e);
            }
        });
        using var timer = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(Math.Max(0, seconds)), timer.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (race.TrySetException(new DbConnectTimeoutException((int)Math.Round(seconds, MidpointRounding.AwayFromZero))))
                CancelQuietly(workCts);
        }, TaskScheduler.Default);
        await using var reg = ct.Register(() =>
        {
            if (race.TrySetCanceled(ct)) CancelQuietly(workCts);
        });
        try { return await race.Task.ConfigureAwait(false); }
        finally { timer.Cancel(); }
    }

    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (Exception) { }
    }
}
