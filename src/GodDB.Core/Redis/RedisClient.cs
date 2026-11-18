using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace GodDB.Core.Redis;

/// Redis 연결 하나. 명령은 보낸 순서대로 응답이 온다 (파이프라인).
/// 구독(SUBSCRIBE) 연결로 쓰면 모든 응답을 push 콜백으로 넘긴다.
/// Mac 판은 SwiftNIO 를 쓰고, 여기서는 TcpClient (+ TLS 면 SslStream) 와 읽기 루프 하나로 같은 동작을 한다.
public sealed class RedisConnection : IAsyncDisposable
{
    internal sealed record Options
    {
        public required string Host { get; init; }
        public required int Port { get; init; }
        /// TLS SNI 에 쓸 이름 (IP 이거나 터널이면 null → SNI 안 보냄)
        public string? ServerName { get; init; }
        public bool UseTls { get; init; }
        public string Username { get; init; } = "";
        public string? Password { get; init; }
        public int Database { get; init; }
        public string ClientName { get; init; } = "GodDB";
        /// 연결 과정 명령(AUTH · SELECT 등)의 응답 제한
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    }

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _sync = new();
    private readonly Queue<TaskCompletionSource<RespValue>> _waiting = new();
    private readonly CancellationTokenSource _closing = new();
    private Action<RespValue>? _push;
    private Action? _onClosed;
    private volatile bool _open = true;

    private RedisConnection(TcpClient tcp, Stream stream)
    {
        _tcp = tcp;
        _stream = stream;
        _ = Task.Run(ReadLoop);
    }

    public bool IsOpen => _open;

    internal static async Task<RedisConnection> ConnectAsync(Options o, CancellationToken ct = default)
    {
        var conn = await HostResolver.TryEach(o.Host, o.Port, async ip =>
        {
            var tcp = new TcpClient(ip.AddressFamily);
            try
            {
                tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                tcp.NoDelay = true;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    try { await tcp.ConnectAsync(ip, o.Port, timeout.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new SocketException((int)SocketError.TimedOut);
                    }
                }
                Stream stream = tcp.GetStream();
                if (o.UseTls)
                {
                    // 터널 경유 시 호스트명이 127.0.0.1 이 되어 인증서 검증이 불가하므로 암호화만 사용한다 (Mac 판과 같음).
                    var ssl = new SslStream(stream, false, (_, _, _, _) => true);
                    // TLS 인사도 연결 과정이라 같은 응답 제한을 둔다 (Mac 판은 NIO 가 첫 명령과 함께 처리해서 AUTH 제한에 포함됨)
                    using var tlsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    tlsTimeout.CancelAfter(o.Timeout);
                    try
                    {
                        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                        {
                            TargetHost = o.ServerName ?? "",
                            RemoteCertificateValidationCallback = (_, _, _, _) => true,
                        }, tlsTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        ssl.Dispose();
                        throw new RedisTimeoutException(Seconds(o.Timeout));
                    }
                    stream = ssl;
                }
                return new RedisConnection(tcp, stream);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        });
        try
        {
            if (!string.IsNullOrEmpty(o.Password))
            {
                if (string.IsNullOrEmpty(o.Username)) await conn.SendAsync(["AUTH", o.Password], o.Timeout, ct);
                else await conn.SendAsync(["AUTH", o.Username, o.Password], o.Timeout, ct);
            }
            if (o.Database != 0) await conn.SendAsync(["SELECT", o.Database.ToString(CultureInfo.InvariantCulture)], o.Timeout, ct);
            // 이름 붙이기를 막은 서버(권한 등)는 무시. 시간 초과 · 연결 끊김은 실패로 본다
            try { await conn.SendAsync(["CLIENT", "SETNAME", o.ClientName], o.Timeout, ct); } catch (RedisServerException) { }
        }
        catch
        {
            await conn.CloseAsync();
            throw;
        }
        return conn;
    }

    /// 명령 하나를 보내고 응답을 받는다. 서버 오류(-ERR …)는 RedisServerException 으로 던진다.
    public Task<RespValue> SendAsync(IReadOnlyList<string> args, CancellationToken ct = default) =>
        SendBytesAsync(args.Select(a => Encoding.UTF8.GetBytes(a)).ToList(), null, ct);

    /// timeout: 이 시간 안에 응답이 없으면 연결을 닫고 RedisTimeoutException (null 이면 제한 없음 — BLPOP 같은 콘솔 명령)
    public Task<RespValue> SendAsync(IReadOnlyList<string> args, TimeSpan? timeout, CancellationToken ct = default) =>
        SendBytesAsync(args.Select(a => Encoding.UTF8.GetBytes(a)).ToList(), timeout, ct);

    public async Task<RespValue> SendBytesAsync(IReadOnlyList<byte[]> args, CancellationToken ct = default) =>
        await SendBytesAsync(args, null, ct);

    public async Task<RespValue> SendBytesAsync(IReadOnlyList<byte[]> args, TimeSpan? timeout, CancellationToken ct = default)
    {
        var v = await RawAsync(args, timeout, ct);
        if (v.Kind == RespKind.Error) throw new RedisServerException(v.Text!);
        return v;
    }

    /// 오류 응답도 값으로 돌려준다 (콘솔용)
    public Task<RespValue> RawAsync(IReadOnlyList<byte[]> args, CancellationToken ct = default) => RawAsync(args, null, ct);

    public async Task<RespValue> RawAsync(IReadOnlyList<byte[]> args, TimeSpan? timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        await WriteAsync(args, tcs, ct);
        if (timeout is not { } limit) return await tcs.Task.WaitAsync(ct);
        try { return await tcs.Task.WaitAsync(limit, ct); }
        catch (TimeoutException)
        {
            // 응답이 없으면 대기 중인 요청을 모두 실패시키고 연결을 닫는다
            var error = new RedisTimeoutException(Seconds(limit));
            Fail(error);
            throw error;
        }
    }

    private static int Seconds(TimeSpan t) => (int)t.TotalSeconds;

    internal Task<RespValue> RawAsync(IReadOnlyList<string> args, CancellationToken ct = default) =>
        RawAsync(args.Select(a => Encoding.UTF8.GetBytes(a)).ToList(), ct);

    private async Task WriteAsync(IReadOnlyList<byte[]> args, TaskCompletionSource<RespValue>? tcs, CancellationToken ct)
    {
        if (!_open) throw new RedisNotConnectedException();
        var bytes = RespCoder.Encode(args);
        await _writeLock.WaitAsync(ct);
        try
        {
            if (!_open) throw new RedisNotConnectedException();
            // 응답 순서 = 보낸 순서. 기다리는 줄에 넣는 것과 쓰기를 같은 잠금 안에서 한다.
            if (tcs != null) lock (_sync) _waiting.Enqueue(tcs);
            try
            {
                await _stream.WriteAsync(bytes, _closing.Token);
                await _stream.FlushAsync(_closing.Token);
            }
            catch (Exception e)
            {
                Fail(new RedisNotConnectedException(e));
                throw new RedisNotConnectedException(e);
            }
        }
        finally { _writeLock.Release(); }
    }

    /// 구독 모드로 바꾼다. 이후 기다리는 요청이 없을 때 오는 값은 모두 onPush 로 간다. 끊기면 onClosed.
    internal void SetPush(Action<RespValue> onPush, Action onClosed)
    {
        bool closedAlready;
        lock (_sync)
        {
            _push = onPush;
            _onClosed = onClosed;
            closedAlready = !_open;
        }
        if (closedAlready) onClosed();
    }

    /// 응답을 기다리지 않고 보낸다 (구독 모드)
    internal void Fire(IReadOnlyList<string> args)
    {
        // 바로 시작해서 부른 순서대로 쓰이게 한다 (끝을 기다리지는 않음)
        _ = FireCore(args.Select(a => Encoding.UTF8.GetBytes(a)).ToList());
    }

    private async Task FireCore(IReadOnlyList<byte[]> args)
    {
        try { await WriteAsync(args, null, CancellationToken.None); }
        catch { }
    }

    private async Task ReadLoop()
    {
        var buffer = new byte[64 * 1024];
        int length = 0;
        Exception? failure = null;
        try
        {
            while (!_closing.IsCancellationRequested)
            {
                if (length == buffer.Length) System.Array.Resize(ref buffer, buffer.Length * 2);
                int n = await _stream.ReadAsync(buffer.AsMemory(length), _closing.Token);
                if (n <= 0) break;
                length += n;
                int offset = 0;
                while (offset < length)
                {
                    var v = RespCoder.Decode(buffer.AsSpan(offset, length - offset), out int used);
                    if (v == null) break;
                    offset += used;
                    Deliver(v);
                }
                if (offset > 0)
                {
                    Buffer.BlockCopy(buffer, offset, buffer, 0, length - offset);
                    length -= offset;
                }
            }
        }
        catch (Exception e) { failure = e; }
        Fail(failure is RedisProtocolException ? failure : new RedisNotConnectedException());
    }

    private void Deliver(RespValue v)
    {
        TaskCompletionSource<RespValue>? tcs = null;
        Action<RespValue>? push;
        lock (_sync)
        {
            push = _push;
            if (_waiting.Count > 0) tcs = _waiting.Dequeue();
        }
        if (tcs != null) tcs.TrySetResult(v);
        else push?.Invoke(v);
    }

    /// 연결을 닫고 기다리는 요청을 모두 실패시킨다
    private void Fail(Exception error)
    {
        List<TaskCompletionSource<RespValue>> pending;
        Action? closed;
        lock (_sync)
        {
            if (!_open && _waiting.Count == 0 && _onClosed == null) return;
            _open = false;
            pending = [.. _waiting];
            _waiting.Clear();
            closed = _onClosed;
            _onClosed = null;
        }
        foreach (var p in pending) p.TrySetException(error);
        try { _closing.Cancel(); } catch (ObjectDisposedException) { }
        try { _stream.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
        closed?.Invoke();
    }

    public Task CloseAsync()
    {
        Fail(new RedisNotConnectedException());
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}

/// 호스트 이름 → IP 들 (IPv4 먼저). Mac 판 HostResolver 와 같은 순서·규칙.
internal static class HostResolver
{
    internal static async Task<IReadOnlyList<IPAddress>> Addresses(string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var direct) && IsIPAddress(host.Trim('[', ']'))) return [direct];
        try
        {
            var all = await Dns.GetHostAddressesAsync(host);
            var ordered = all.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Concat(all.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6))
                .Distinct().ToList();
            if (ordered.Count > 0) return ordered;
        }
        catch (SocketException) { }
        throw new SocketException((int)SocketError.HostNotFound);
    }

    /// 숫자 IP 주소인지 (inet_pton 처럼 엄격: "1" 같은 짧은 형식은 IP 로 보지 않는다)
    internal static bool IsIPAddress(string host)
    {
        if (host.Contains(':')) return IPAddress.TryParse(host, out var a) && a.AddressFamily == AddressFamily.InterNetworkV6;
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit) && int.Parse(p, CultureInfo.InvariantCulture) <= 255);
    }

    /// 주소를 차례로 시도한다. 소켓 연결 단계 오류면 다음 주소로.
    internal static async Task<T> TryEach<T>(string host, int port, Func<IPAddress, Task<T>> body)
    {
        Exception? last = null;
        foreach (var ip in await Addresses(host))
        {
            try { return await body(ip); }
            catch (Exception e) when (e is SocketException or IOException)
            {
                last = e;
            }
        }
        throw last ?? new RedisNotConnectedException();
    }
}
