using GodDB.Core.Dialects;
using static GodDB.Core.L10n;

namespace GodDB.Core.Drivers;

/// 사용자 세션과 따로 잠깐 여는 연결 (Mac 1.4.0 SideConnection.swift — AI 데이터 찾기 샘플 조회). DbSession.SideConnection() 으로 만든다.
/// - 처음 조회할 때 연결하고, 같은 찾기 안에서는 다시 쓴다. 다 쓰면 CloseAsync().
/// - 조회가 제한 시간을 넘으면 그 연결의 서버 세션을 취소(KILL 등)하고 연결을 버린다. 다음 조회는 새로 연결한다.
///   사용자 세션은 건드리지 않으므로 사용자의 트랜잭션 · SQL Server 세션이 그대로 남는다.
public sealed class SideConnection : IAsyncDisposable
{
    public sealed class TimedOutException(int seconds) : Exception(Tr("시간 초과 (%@초)", seconds))
    {
        public int Seconds { get; } = seconds;
    }

    private readonly Func<CancellationToken, Task<IDbDriver>> _makeDriver;
    private readonly ISqlDialect _dialect;
    private readonly bool _readOnly;
    /// 사용자 세션이 보던 DB(MySQL · SQL Server) 또는 스키마(PostgreSQL)
    private readonly string _container;
    private readonly double _connectTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDbDriver? _driver;
    private int? _sessionId;

    internal SideConnection(Func<CancellationToken, Task<IDbDriver>> makeDriver, ISqlDialect dialect, bool readOnly, string container, double connectTimeout)
    {
        _makeDriver = makeDriver;
        _dialect = dialect;
        _readOnly = readOnly;
        _container = container;
        _connectTimeout = connectTimeout;
    }

    /// 미리 연결해 본다 (실패하면 앱이 사용자 세션으로 조회하도록)
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await OpenAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<IDbDriver> OpenAsync(CancellationToken ct)
    {
        if (_driver is { IsOpen: true } d) return d;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_connectTimeout));
        IDbDriver? opened = null;
        try
        {
            opened = await _makeDriver(cts.Token);
            if (_dialect.SessionSetupSql is { } setup) { try { await opened.QueryAsync(setup, 0, cts.Token); } catch { } }
            // 서버 측에서도 변경을 거부하도록 세션을 읽기전용으로 (사용자 세션과 같은 설정)
            if (_readOnly && _dialect.ReadOnlySessionSql is { } ro) await opened.QueryAsync(ro, 0, cts.Token);
            if (_container.Length > 0) await opened.QueryAsync(_dialect.UseSql(_container), 0, cts.Token);
            try
            {
                var v = (await opened.QueryAsync(_dialect.SessionIdSql, 0, cts.Token)).Rows.FirstOrDefault()?.FirstOrDefault();
                _sessionId = int.TryParse(v, out var id) ? id : null;
            }
            catch { _sessionId = null; }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (opened != null) { try { await opened.DisposeAsync(); } catch { } }
            throw new TimeoutException(Tr("DB 서버가 %@초 안에 연결되지 않았어요. 주소 · 포트와 네트워크(VPN · SSH 터널)를 확인하세요.", (int)_connectTimeout));
        }
        catch
        {
            if (opened != null) { try { await opened.DisposeAsync(); } catch { } }
            throw;
        }
        _driver = opened;
        return opened;
    }

    /// 조회 하나. 제한 시간을 넘으면 TimedOutException 을 던지고 그 연결을 정리한다 (기다리지 않는다).
    public async Task<RawResult> QueryAsync(string sql, double timeoutSeconds, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var d = await OpenAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var work = d.QueryAsync(sql, 0, cts.Token);
            var timer = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), ct);
            if (await Task.WhenAny(work, timer) != work)
            {
                ct.ThrowIfCancellationRequested();
                Abandon(cts);
                _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                throw new TimedOutException((int)Math.Round(timeoutSeconds));
            }
            try { return await work; }
            catch
            {
                if (!d.IsOpen) Abandon(null);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// 지금 연결을 버린다: 서버에 취소를 보내고(다른 연결로) 닫는다. 뒤에서 하므로 기다리지 않는다.
    private void Abandon(CancellationTokenSource? running)
    {
        if (_driver is not { } d) return;
        var id = _sessionId;
        _driver = null;
        _sessionId = null;
        var make = _makeDriver;
        var dialect = _dialect;
        _ = Task.Run(async () =>
        {
            if (id is int sid)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await using var other = await make(cts.Token);
                    await other.QueryAsync(dialect.CancelSql(sid), 0, cts.Token);
                }
                catch { }
            }
            try { running?.Cancel(); } catch { }
            try { await d.DisposeAsync(); } catch { }
        });
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_driver != null) { try { await _driver.DisposeAsync(); } catch { } }
            _driver = null;
            _sessionId = null;
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}

public sealed partial class DbSession
{
    /// 사용자 세션과 따로 잠깐 여는 연결 (같은 주소 · 계정 · 읽기전용 설정 · 지금 DB). 연결돼 있지 않으면 null.
    /// (Mac DBSession.sideConnection — 연결 제한 시간은 최대 10초)
    public SideConnection? SideConnection()
    {
        if (_endpoint is not { } e || _driver == null) return null;
        var kind = Profile.Kind;
        // 비밀번호는 연결을 열 때마다 읽어 온다 (세션이 들고 있지 않을 수 있음 — Mac 1.4.1 passwordProvider)
        return new SideConnection(ct => MakeDriverAsync(kind, LiveEndpoint() ?? e, ct), Dialect, Profile.ReadOnly, Current,
                                  Math.Min(e.ConnectTimeoutSeconds, 10));
    }
}
