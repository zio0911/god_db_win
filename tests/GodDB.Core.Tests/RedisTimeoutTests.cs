using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GodDB.Core.Redis;

namespace GodDB.Core.Tests;

/// 연결은 받지만 아무 응답도 안 하는 서버 (네트워크가 잠깐 끊김, SSH 터널 너머가 안 붙음, TLS 설정 다름 등).
/// Mac 판 RedisTimeoutTests 는 nc 로 띄우고, 여기서는 TcpListener 로 같은 서버를 만든다 (Windows 에 nc 가 없음).
public class RedisTimeoutTests
{
    /// 가짜 서버. answers 만큼은 "+OK" 로 답하고 그 뒤로는 침묵. closeOnAccept 면 받자마자 닫는다.
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _held = [];
        public int Port { get; }

        public FakeServer(int answers = 0, bool closeOnAccept = false)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var c = await _listener.AcceptTcpClientAsync();
                        if (closeOnAccept) { c.Dispose(); continue; }
                        lock (_held) _held.Add(c);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var s = c.GetStream();
                                var buf = new byte[4096];
                                for (int i = 0; i < answers; i++)
                                {
                                    if (await s.ReadAsync(buf) <= 0) return;
                                    await s.WriteAsync(Encoding.ASCII.GetBytes("+OK\r\n"));
                                }
                                while (await s.ReadAsync(buf) > 0) { }  // 이후로는 읽기만 하고 답하지 않는다
                            }
                            catch { }
                        });
                    }
                }
                catch { }
            });
        }

        public void Dispose()
        {
            _listener.Stop();
            lock (_held) foreach (var c in _held) c.Dispose();
        }
    }

    private static ConnectionProfile Profile(string name, int port) =>
        new() { Kind = DatabaseKind.Redis, Name = name, Host = "127.0.0.1", Port = port, Username = "", Database = "", UseTLS = false };

    [Theory]
    [InlineData("secret")]
    [InlineData("")]
    public async Task SilentServerFailsWithinTimeout(string password)
    {
        using var server = new FakeServer();
        var started = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<RedisTimeoutException>(() =>
            RedisSession.ConnectAsync(Profile("silent", server.Port), password, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(6));
        Assert.Equal(2, e.Seconds);
    }

    /// 연결 과정 중에 서버가 연결을 끊으면 "연결됨"으로 넘어가지 않고 실패한다
    [Fact]
    public async Task ServerClosingDuringHandshakeFails()
    {
        using var server = new FakeServer(closeOnAccept: true);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            RedisSession.ConnectAsync(Profile("closing", server.Port), "", TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }

    /// 안내 문장은 Mac 판과 같다 (기본 제한 10초)
    [Fact]
    public void MessageMatchesMac()
    {
        Assert.Equal("Redis 서버가 10초 안에 응답하지 않았어요. 주소 · 포트 · TLS 설정과 네트워크(SSH 터널)를 확인하세요.",
                     new RedisTimeoutException(10).Message);
    }

    /// 연결이 된 뒤의 콘솔 · 차단 명령(BLPOP 등)은 제한 없이 기다린다
    [Fact]
    public async Task ConsoleCommandsAreNotLimited()
    {
        // CLIENT SETNAME · INFO cluster 에만 답하고 그 뒤로는 침묵하는 서버
        using var server = new FakeServer(answers: 2);
        var ct = TestContext.Current.CancellationToken;
        await using var session = await RedisSession.ConnectAsync(Profile("console", server.Port), "", TimeSpan.FromSeconds(1), ct);
        var blpop = session.ConsoleAsync(["BLPOP", "queue", "0"], ct);
        var finished = await Task.WhenAny(blpop, Task.Delay(TimeSpan.FromSeconds(2.5), ct));
        Assert.NotSame(blpop, finished);  // 연결 과정 제한(1초)이 지나도 계속 기다린다
        Assert.True(session.IsConnected);
    }
}
