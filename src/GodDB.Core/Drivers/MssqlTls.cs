using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using static GodDB.Core.L10n;

namespace GodDB.Core.Drivers;

// F9-3. SQL Server 서버 인증서 확인 (Mac 1.4.1 MSSQLTLS.swift)
//
// Mac 은 FreeTDS(DB-Library) 에 확인 콜백이 없어서 "미리 받기 → FreeTDS 에 ca file 로 다시 확인" 두 단계로 한다.
// Windows 판의 Microsoft.Data.SqlClient 도 확인 콜백이 없다. 그래서 인증서 미리 받기는 Mac 과 똑같이 하고,
// 두 번째 단계는 SqlClient 가 허용하는 방식 중 Mac 보다 틈이 없는 것을 고른다.
//
// 1) 미리 받기 (Mac 과 같음): 연결할 IP · 포트로 PRELOGIN(ENCRYPTION=ON) 을 보내고, TDS 패킷(0x12)에 감싼 TLS 핸드셰이크로
//    서버 인증서 체인만 받는다 (로그인하지 않음). 받은 체인은 다른 DB 와 같은 판정(TlsVerifier — 시스템 신뢰 저장소 →
//    고정 지문 → 확인 창)을 거친다.
// 2) SqlClient 연결 (판정에 따라):
//    - 시스템 신뢰 저장소로 통과(고정 없음): Encrypt=Mandatory · TrustServerCertificate=false ·
//      HostNameInCertificate=접속 정보의 호스트 이름 → SqlClient 가 같은 저장소로 체인 + 이름을 다시 확인한다
//      (IP 로 붙어도 이름은 접속 정보의 이름으로 확인. Mac 의 "ca file = 시스템 루트 + 체인, certificate hostname" 과 같은 일).
//    - 고정 인증서(또는 통과 + 고정 지문 있음): 미리 받은 서버 인증서를 본인만 읽는 임시 파일에 써서 ServerCertificate 로 넘긴다.
//      SqlClient 는 로그인하는 그 TLS 연결의 인증서가 이 파일과 **바이트까지 같아야** 통과시킨다 = 지문 고정을 SqlClient 가 직접 한다.
//      Mac(FreeTDS)은 지문을 비교하지 못해 두 연결 사이에 인증서가 바뀌면 모를 수 있는 틈이 있었지만, 여기서는 그 틈이 없다.
//    - 확인 끔: 미리 받지 않고 Encrypt=Mandatory · TrustServerCertificate=true (암호화만).
//    - SSH 터널도 1.4.3 부터 위와 같이 미리 받아 원래 호스트 이름(HostNameInCertificate)으로 확인한다 (#197).
//    왜 이 방식인가: SqlClient 7 은 RemoteCertificateValidationCallback 을 열어 두지 않는다. 연결 문자열로 할 수 있는 것은
//    Encrypt · TrustServerCertificate · HostNameInCertificate · ServerCertificate 뿐이라, 확인 창에 보여 줄 인증서 정보와
//    지문은 미리 받기로 얻고, 실제 연결에서의 확인은 위 옵션으로 SqlClient 에 맡긴다.
// - `host\인스턴스` 는 SQL Browser(UDP 1434)에 TCP 포트를 물어서 그 포트로 미리 받는다 (SqlClient 와 같은 방법).
public static class MssqlTls
{
    // MARK: - TDS 패킷

    /// PRELOGIN 패킷 종류 (TDS 7.x 에서는 TLS 핸드셰이크도 이 종류의 패킷에 감싼다)
    public const byte PreloginType = 0x12;
    /// 패킷 머리 8바이트
    public const int HeaderSize = 8;
    /// 핸드셰이크에 쓰는 패킷 크기 (로그인 전 기본값)
    public const int PacketSize = 4096;

    /// PRELOGIN ENCRYPTION 값
    public enum Encryption : byte { Off = 0x00, On = 0x01, NotSupported = 0x02, Required = 0x03 }

    /// 페이로드 → TDS 패킷들 (마지막 패킷에 EOM)
    public static byte[] Packets(ReadOnlySpan<byte> payload, byte type = PreloginType, int maxSize = PacketSize)
    {
        var chunk = maxSize - HeaderSize;
        var output = new List<byte>(payload.Length + HeaderSize * (payload.Length / chunk + 1));
        var offset = 0;
        byte id = 1;
        do
        {
            var end = Math.Min(offset + chunk, payload.Length);
            var length = end - offset + HeaderSize;
            output.AddRange([type, (byte)(end == payload.Length ? 0x01 : 0x00), (byte)(length >> 8), (byte)(length & 0xFF), 0, 0, id, 0]);
            output.AddRange(payload[offset..end].ToArray());
            offset = end;
            id = unchecked((byte)(id + 1));
        } while (offset < payload.Length);
        return [.. output];
    }

    /// PRELOGIN 메시지: VERSION · ENCRYPTION=ON · INSTOPT · THREADID · MARS=off
    public static byte[] PreloginPayload()
    {
        (byte Token, byte[] Data)[] options =
        [
            (0x00, [9, 0, 0, 0, 0, 0]),
            (0x01, [(byte)Encryption.On]),
            (0x02, [0]),
            (0x03, [0, 0, 0, 0]),
            (0x04, [0]),
        ];
        var offset = options.Length * 5 + 1;
        var head = new List<byte>();
        var body = new List<byte>();
        foreach (var (token, data) in options)
        {
            head.AddRange([token, (byte)(offset >> 8), (byte)(offset & 0xFF), (byte)(data.Length >> 8), (byte)(data.Length & 0xFF)]);
            body.AddRange(data);
            offset += data.Length;
        }
        return [.. head, 0xFF, .. body];
    }

    /// PRELOGIN 응답에서 ENCRYPTION 값 (없거나 깨졌으면 null)
    public static Encryption? EncryptionIn(ReadOnlySpan<byte> payload)
    {
        var i = 0;
        while (i < payload.Length && payload[i] != 0xFF)
        {
            if (i + 5 > payload.Length) return null;
            var token = payload[i];
            var offset = payload[i + 1] << 8 | payload[i + 2];
            var length = payload[i + 3] << 8 | payload[i + 4];
            if (token == 0x01)
            {
                if (length < 1 || offset >= payload.Length) return null;
                var v = payload[offset];
                return Enum.IsDefined(typeof(Encryption), v) ? (Encryption)v : null;
            }
            i += 5;
        }
        return null;
    }

    // MARK: - 인증서 미리 받기

    /// 받은 인증서 체인(서버 인증서 먼저)과 이 주소에서의 판정
    public sealed record Probe(IReadOnlyList<byte[]> Chain, TlsVerdict Verdict);

    /// 서버 인증서를 받아 판정한다. 거부하면 TlsCertificateException(인증서 정보 포함)을 던진다.
    public static async Task<Probe> ProbeAsync(string ip, int port, string? serverName, TlsVerifier verifier,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient(IPAddress.TryParse(ip, out var addr) ? addr.AddressFamily : AddressFamily.InterNetwork);
        try
        {
            await tcp.ConnectAsync(ip, port, cts.Token);
            var net = tcp.GetStream();
            // PRELOGIN
            await net.WriteAsync(Packets(PreloginPayload()), cts.Token);
            var response = await ReadMessageAsync(net, cts.Token);
            var enc = EncryptionIn(response);
            if (enc == null) throw new MssqlTlsException(Tr("서버의 PRELOGIN 응답을 읽을 수 없어요."));
            if (enc == Encryption.NotSupported) throw TlsCertificateException.ServerNoTls();
            // TLS 핸드셰이크 (TDS 패킷에 감싸서)
            var tds = new TdsTlsStream(net);
            await using var ssl = new SslStream(tds, leaveInnerStreamOpen: true);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = serverName ?? "",
                    RemoteCertificateValidationCallback = verifier.Callback,
                }, cts.Token);
            }
            catch (Exception) when (verifier.Failure is { } failure) { throw failure; }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MssqlTlsException(Tr("서버 인증서를 받는 중에 시간이 초과됐어요."));
        }
        catch (IOException) when (verifier.Failure is null && verifier.Verdict is null)
        {
            throw new MssqlTlsException(Tr("서버 인증서를 받기 전에 연결이 끊어졌어요."));
        }
        if (verifier.Verdict is not { } verdict || verifier.Chain is not { } chain)
            throw new MssqlTlsException(Tr("서버 인증서를 받지 못했어요."));
        return new Probe(chain, verdict);
    }

    /// EOM 까지 패킷을 읽어 페이로드를 잇는다
    private static async Task<byte[]> ReadMessageAsync(Stream s, CancellationToken ct)
    {
        var message = new List<byte>();
        while (true)
        {
            var (payload, eom) = await ReadPacketAsync(s, ct);
            message.AddRange(payload);
            if (eom) return [.. message];
        }
    }

    internal static async Task<(byte[] Payload, bool Eom)> ReadPacketAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await s.ReadExactlyAsync(header, ct);
        var length = header[2] << 8 | header[3];
        if (length < HeaderSize) throw new MssqlTlsException(Tr("서버가 보낸 TDS 패킷이 올바르지 않아요."));
        var payload = new byte[length - HeaderSize];
        await s.ReadExactlyAsync(payload, ct);
        return (payload, (header[1] & 0x01) != 0);
    }

    /// TLS 바이트를 PRELOGIN 패킷에 감싸고, 받은 패킷에서 페이로드를 꺼내는 스트림 (TLS 레코드가 패킷 경계를 넘을 수 있음)
    private sealed class TdsTlsStream(Stream inner) : Stream
    {
        private byte[] _buffer = [];
        private int _pos;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_pos >= _buffer.Length)
            {
                (_buffer, _) = await ReadPacketAsync(inner, ct);
                _pos = 0;
            }
            var n = Math.Min(buffer.Length, _buffer.Length - _pos);
            _buffer.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(Packets(buffer.AsSpan(offset, count)));

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            await inner.WriteAsync(Packets(buffer.Span), ct);
    }

    // MARK: - SQL Browser (host\인스턴스 → TCP 포트)

    /// SSRP CLNT_UCAST_INST 응답에서 tcp 포트 ("ServerName;X;InstanceName;Y;…;tcp;1433;;")
    public static int? ParseBrowserResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length < 3 || response[0] != 0x05) return null;
        var len = BinaryPrimitives.ReadUInt16LittleEndian(response[1..3]);
        var text = Encoding.ASCII.GetString(response[3..Math.Min(response.Length, 3 + len)]);
        var parts = text.Split(';');
        for (var i = 0; i + 1 < parts.Length; i++)
            if (parts[i].Equals("tcp", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[i + 1], out var p)) return p;
        return null;
    }

    /// SQL Browser 에 인스턴스의 TCP 포트를 묻는다 (못 받으면 null)
    public static async Task<int?> InstancePortAsync(string host, string instance, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var request = new byte[] { 0x04 }.Concat(Encoding.ASCII.GetBytes(instance)).Append((byte)0).ToArray();
            await udp.SendAsync(request, host, 1434, cts.Token);
            var r = await udp.ReceiveAsync(cts.Token);
            return ParseBrowserResponse(r.Buffer);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    // MARK: - SqlClient 에 넘길 확인 설정

    /// SqlClient 가 실제 연결에서 다시 확인할 방법
    /// - ServerCertificateDer: 있으면 이 인증서와 정확히 같아야 통과 (ServerCertificate)
    /// - HostName: 체인 · 이름 확인에 쓸 이름 (HostNameInCertificate). ServerCertificateDer 가 있으면 쓰지 않는다
    /// - TrustServerCertificate: true 면 암호화만
    public sealed record SqlClientCheck(bool TrustServerCertificate, string? HostName, byte[]? ServerCertificateDer);

    /// 판정 → SqlClient 확인 설정
    /// - trusted(고정 없음): SqlClient 가 체인 + 이름 다시 확인
    /// - pinned · trusted+고정: 미리 받은 서버 인증서와 정확히 같아야 함
    /// - 그 밖(확인 안 함): 암호화만
    public static SqlClientCheck CheckFor(TlsVerdict verdict, IReadOnlyList<byte[]> chain, TlsPolicy policy)
    {
        var hasPin = !string.IsNullOrEmpty(policy.Pin);
        return verdict.Kind switch
        {
            TlsVerdictKind.Trusted when !hasPin && policy.Hostname != null => new(false, policy.Hostname, null),
            TlsVerdictKind.Trusted or TlsVerdictKind.Pinned when chain.Count > 0 => new(false, null, chain[0]),
            _ => new(true, null, null),
        };
    }

    /// 서버 인증서를 본인만 읽는 임시 파일에 쓴다 (연결 시도가 끝나면 지운다)
    internal static string WriteServerCertificate(byte[] der)
    {
        var dir = Path.Combine(Path.GetTempPath(), "GodDB-mssql");
        AppPaths.Ensure(dir);
        var path = Path.Combine(dir, "goddb-" + Guid.NewGuid().ToString("N") + ".cer");
        using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            f.Write(der);
        Platform.UserOnlyFile.Restrict(path);
        return path;
    }
}

/// 미리 받기 중 TDS 수준 오류
public sealed class MssqlTlsException(string message) : Exception(message);
