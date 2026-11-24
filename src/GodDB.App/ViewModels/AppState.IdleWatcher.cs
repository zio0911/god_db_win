namespace GodDB.App.ViewModels;

/// 운영 접속 자동 끊기 (설정 > 일반 > 안전, Mac ProdIdleWatcher.swift). 운영(production) 접속을 정해진 시간 동안 쓰지 않으면 끊는다.
/// - 사용 = 쿼리 · 페이지 · 내보내기 · 스키마 읽기 · Redis 명령 (자동 새로고침 · 화면 보기는 사용이 아님)
/// - 커밋하지 않은 변경이 있으면 끊지 않고 한 번만 알린다. 실행 · 내보내기 중이면 끝날 때까지 미룬다.
/// - Mac 은 알릴 때 Dock 아이콘을 튀어 오르게 한다(requestUserAttention). Windows 는 같은 일을 하는 공용 API 가
///   Avalonia 에 없어 상태창 경고(노란 줄)로만 알린다.
public sealed partial class AppState
{
    public const string ProdIdleMinutesKey = "GodDB.safety.prodIdleMinutes";
    /// 화면 테스트용 (초). GODDB_UI_TEST 일 때만 쓴다
    public const string ProdIdleSecondsForTestKey = "GodDB.safety.prodIdleSecondsForTest";
    public static readonly int[] ProdIdleChoices = [0, 10, 30, 60];

    private CancellationTokenSource? _idleWatcher;
    /// 커밋 안 한 변경 때문에 이미 알린 접속
    private readonly HashSet<Guid> _idleWarned = [];

    /// 끊기까지의 시간. 0 이면 끔
    public static TimeSpan ProdIdleLimit
    {
        get
        {
            if (AppPaths.UiTest && Prefs.Get(ProdIdleSecondsForTestKey, 0) is > 0 and var s) return TimeSpan.FromSeconds(s);
            var m = Prefs.Get(ProdIdleMinutesKey, 30);
            return TimeSpan.FromMinutes(Math.Max(0, m));
        }
    }

    public void StartIdleWatcher()
    {
        if (_idleWatcher != null) return;
        var cts = new CancellationTokenSource();
        _idleWatcher = cts;
        Fire(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                // 설정을 바꾸면 바로 반영되게 짧게 자주 본다 (화면 테스트는 1초, 평소 15초 — 확인은 가벼움)
                try { await Task.Delay(AppPaths.UiTest ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(15), cts.Token); }
                catch (OperationCanceledException) { return; }
                await CheckIdleProductionAsync(ProdIdleLimit);
            }
        });
    }

    public void StopIdleWatcher()
    {
        _idleWatcher?.Cancel();
        _idleWatcher = null;
    }

    public async Task CheckIdleProductionAsync(TimeSpan limit, DateTime? now = null)
    {
        if (limit <= TimeSpan.Zero) return;
        var at = now ?? DateTime.UtcNow;
        foreach (var profile in Profiles.Where(p => p.Environment == ConnectionEnvironment.Production && ConnectionState(p.Id).IsConnected).ToList())
        {
            var id = profile.Id;
            DateTime last;
            if (RedisWorkspaces.TryGetValue(id, out var ws)) last = ws.Session.LastActivity;
            else if (Connections.TryGetValue(id, out var info) && info.Session is { } session)
            {
                if (session.IsRunning) continue;
                last = session.LastActivity;
            }
            else continue;
            if (Tabs.Any(t => t.ProfileId == id && (t.Running || t.ExportProgress != null))) continue;
            if (at - last < limit)
            {
                _idleWarned.Remove(id);
                continue;
            }
            var minutes = (int)limit.TotalMinutes;
            var span = minutes > 0 ? Tr("%@분", minutes) : Tr("%@초", (int)limit.TotalSeconds);
            if (Connections.TryGetValue(id, out var c) && c.Session is { InTransaction: true })
            {
                if (!_idleWarned.Add(id)) continue;
                Log(LogLevel.Warning, LogSource.Connection, Tr("운영 접속 %@ 에 커밋하지 않은 변경이 있어 자동으로 끊지 않았어요", profile.Name));
                continue;
            }
            _idleWarned.Remove(id);
            await DisconnectAsync(id, force: true,
                reason: Tr("운영 접속 %@ 을 %@ 동안 쓰지 않아 끊었어요", profile.Name, span),
                auditResult: $"자동 ({(minutes > 0 ? $"{minutes}분" : $"{(int)limit.TotalSeconds}초")} 미사용)");
        }
    }
}
