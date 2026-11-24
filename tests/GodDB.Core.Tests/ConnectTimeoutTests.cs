using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GodDB.Core.Audit;
using GodDB.Core.Drivers;

namespace GodDB.Core.Tests;

/// 스키마 비교 · 일반 접속에서 서버가 응답하지 않으면 끝없이 기다리던 문제 (Mac ConnectTimeoutTests)
[Collection("DB connect timeout")]
public class ConnectTimeoutTests
{
    /// 연결만 받고 응답하지 않는 포트를 연다 (SSH 터널 너머가 안 붙는 경우와 같다).
    /// Mac 은 /usr/bin/nc -l 을 쓴다. Windows 에는 nc 가 없어 같은 일을 하는 TcpListener 로 대신한다.
    private sealed class SilentServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _accepted = [];
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }

        public SilentServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var c = await _listener.AcceptTcpClientAsync(_cts.Token);
                        lock (_accepted) _accepted.Add(c);   // 닫지 않고 붙잡아 둔다
                    }
                }
                catch { }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            lock (_accepted) foreach (var c in _accepted) c.Dispose();
        }
    }

    [Theory]
    [InlineData(DatabaseKind.Mysql)]
    [InlineData(DatabaseKind.Postgres)]
    public async Task SilentServerFailsWithinTimeout(DatabaseKind kind)
    {
        using var server = new SilentServer();
        var profile = new ConnectionProfile
        {
            Kind = kind, Name = "silent", Host = "127.0.0.1", Port = server.Port, Username = "u", Database = "", UseTLS = false,
        };
        var started = Stopwatch.StartNew();
        await Assert.ThrowsAsync<DbConnectTimeoutException>(() => DbSession.ConnectAsync(profile, "p", timeout: 2));
        Assert.True(started.Elapsed.TotalSeconds < 5, $"{started.Elapsed.TotalSeconds}s");
    }

    /// 닿지 않는 주소 (패킷이 버려짐)
    [Fact]
    public async Task UnroutableAddressFailsWithinTimeout()
    {
        var profile = new ConnectionProfile
        {
            Kind = DatabaseKind.Mysql, Name = "unroutable", Host = "10.255.255.1", Port = 3306, Username = "u", Database = "", UseTLS = false,
        };
        var started = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => DbSession.ConnectAsync(profile, "p", timeout: 3));
        Assert.True(started.Elapsed.TotalSeconds < 6, $"{started.Elapsed.TotalSeconds}s");
    }

    [Fact]
    public async Task DeadlineReturnsFastResult()
    {
        var v = await ConnectDeadline.RunAsync(5, _ => Task.FromResult(42));
        Assert.Equal(42, v);
    }

    private sealed class Boom : Exception;

    [Fact]
    public async Task DeadlinePassesErrorThrough()
    {
        await Assert.ThrowsAsync<Boom>(() => ConnectDeadline.RunAsync<int>(5, _ => throw new Boom()));
    }

    /// 시간이 지난 뒤 끝난 결과는 late 로 넘겨 정리한다
    [Fact]
    public async Task LateResultIsHandedOver()
    {
        var got = 0;
        await Assert.ThrowsAsync<DbConnectTimeoutException>(() => ConnectDeadline.RunAsync(0.2, _ =>
        {
            // 취소를 모르는 작업처럼 끝까지 돈다
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 0.6) Thread.Sleep(20);
            return Task.FromResult(7);
        }, late: v => { Interlocked.Exchange(ref got, v); return Task.CompletedTask; }));
        await Task.Delay(1000);
        Assert.Equal(7, Volatile.Read(ref got));
    }

    /// 호출한 쪽이 취소하면 (비교 화면의 취소 버튼) 기다리지 않고 끝난다
    [Fact]
    public async Task CancellingCallerStopsWaiting()
    {
        var started = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        var task = ConnectDeadline.RunAsync(30, _ =>
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 3) Thread.Sleep(20);
            return Task.FromResult(1);
        }, ct: cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(started.Elapsed.TotalSeconds < 2, $"{started.Elapsed.TotalSeconds}s");
    }

    [Fact]
    public void TimeoutErrorIsDescribed()
    {
        var d = ErrorText.Describe(new DbConnectTimeoutException(15));
        Assert.Contains(L10n.IsEnglish ? "15 seconds" : "15초", d.Message);
        Assert.NotNull(d.Hint);
    }
}
