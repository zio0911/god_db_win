using GodDB.Core.Audit;
using System.Text;
using System.Text.Json;
using GodDB.Core.Drivers;
using GodDB.Core.Profiles;
using GodDB.Core.Redis;

namespace GodDB.Core.Tests;

/// Mac TLSVerifyTests.swift — TLS 서버 인증서 확인 (F9-3)
public class TLSVerifyTests
{
    /// 테스트용 자체 서명 인증서 (CN=goddb-test.local, SAN DNS:goddb-test.local, 2026-11-20 ~ 2036-11-17). 개인키는 버렸다.
    public const string SelfSignedPEM = """
        -----BEGIN CERTIFICATE-----
        MIIBsTCCAVegAwIBAgIUQ/qf3P/PaO/yeeJWQWwvR4lvi1UwCgYIKoZIzj0EAwIw
        MDEZMBcGA1UEAwwQZ29kZGItdGVzdC5sb2NhbDETMBEGA1UECgwKR29kREIgVGVz
        dDAeFw0yNjExMjAwMTQ2MDdaFw0zNjExMTcwMTQ2MDdaMDAxGTAXBgNVBAMMEGdv
        ZGRiLXRlc3QubG9jYWwxEzARBgNVBAoMCkdvZERCIFRlc3QwWTATBgcqhkjOPQIB
        BggqhkjOPQMBBwNCAATMmpK9cQap0r6EBs6Q7U9tFMGuWfaR4wRRvdW5oT29QEFu
        64uXgYAOpCL1RjrcEvRpyYe2IaHPvej4PbZ3cnxYo08wTTAdBgNVHQ4EFgQUQe8D
        Urbsb9ksnfenSznfHcXyhwEwDwYDVR0TAQH/BAUwAwEB/zAbBgNVHREEFDASghBn
        b2RkYi10ZXN0LmxvY2FsMAoGCCqGSM49BAMCA0gAMEUCIQDGF5rpDY3xa+39XozO
        0wMCCnIjFfvyxP6oikE5rSG/zgIgSYPvVzIGSmyDNaFCnp4bM+SazrVwwCAgn+gY
        snrgrBs=
        -----END CERTIFICATE-----
        """;
    public const string SelfSignedSHA = "2E:28:02:F7:6D:D2:5C:CF:F1:42:AB:3B:1F:2E:B7:5A:30:7F:B6:6B:32:94:BF:03:66:78:07:15:0A:7C:09:F6";

    public static byte[] SelfSignedDER() => TlsVerify.DerFromPem(SelfSignedPEM) ?? throw new InvalidOperationException("PEM");

    private static readonly TlsFailureReason[] NoReasons = [];

    // MARK: 지문

    [Fact]
    public void FingerprintFormat()
    {
        Assert.Equal("3F:A2:00:9C", TlsVerify.Fingerprint([0x3F, 0xA2, 0x00, 0x9C]));
        Assert.Equal("", TlsVerify.Fingerprint([]));
        // "abc" 의 SHA-256
        Assert.Equal("BA:78:16:BF:8F:01:CF:EA:41:41:40:DE:5D:AE:22:23:B0:03:61:A3:96:17:7A:9C:B4:10:FF:61:F2:00:15:AD",
            TlsVerify.Sha256Fingerprint(Encoding.UTF8.GetBytes("abc")));
    }

    [Fact]
    public void PinComparisonIgnoresFormatting()
    {
        const string fp = "3F:A2:00:9C";
        Assert.True(TlsVerify.PinMatches("3fa2009c", fp));
        Assert.True(TlsVerify.PinMatches("SHA256:3F:A2:00:9C", fp));
        Assert.True(TlsVerify.PinMatches(" 3f a2 00 9c ", fp));
        Assert.False(TlsVerify.PinMatches("3F:A2:00:9D", fp));
        Assert.False(TlsVerify.PinMatches("", fp));
    }

    // MARK: 판정 표

    [Fact]
    public void DecisionTableDirect()
    {
        const string fp = "AA:BB";
        // 체인 통과 · 고정 없음 → 확인됨
        Assert.Equal(TlsVerdict.Trusted, TlsVerify.Decide(true, true, NoReasons, fp, null));
        // 체인 실패 · 고정 없음 → 묻기 (이유 그대로)
        Assert.Equal(TlsVerdict.Untrusted(TlsFailureReason.NameMismatch, TlsFailureReason.Expired),
            TlsVerify.Decide(true, false, [TlsFailureReason.NameMismatch, TlsFailureReason.Expired], fp, null));
        // 이유를 못 찾았으면 신뢰할 수 없는 발급자
        Assert.Equal(TlsVerdict.Untrusted(TlsFailureReason.UntrustedIssuer), TlsVerify.Decide(true, false, NoReasons, fp, null));
        // 체인 실패 · 고정 지문 같음 → 고정 인증서로 연결
        Assert.Equal(TlsVerdict.Pinned, TlsVerify.Decide(true, false, [TlsFailureReason.UntrustedIssuer], fp, "aabb"));
        // 체인 통과 · 고정 지문 같음 → 확인됨
        Assert.Equal(TlsVerdict.Trusted, TlsVerify.Decide(true, true, NoReasons, fp, fp));
        // 고정 지문 다름 → 체인이 통과해도 거부
        Assert.Equal(TlsVerdict.PinMismatch, TlsVerify.Decide(true, true, NoReasons, fp, "CC:DD"));
        Assert.Equal(TlsVerdict.PinMismatch, TlsVerify.Decide(true, false, NoReasons, fp, "CC:DD"));
        // 빈 고정 값은 없는 것으로
        Assert.Equal(TlsVerdict.Trusted, TlsVerify.Decide(true, true, NoReasons, fp, ""));
    }

    [Fact]
    public void DecisionTableTunnel()
    {
        const string fp = "AA:BB";
        Assert.Equal(TlsVerdict.Unchecked, TlsVerify.Decide(false, false, NoReasons, fp, null));
        Assert.Equal(TlsVerdict.Pinned, TlsVerify.Decide(false, false, NoReasons, fp, fp));
        Assert.Equal(TlsVerdict.PinMismatch, TlsVerify.Decide(false, false, NoReasons, fp, "CC"));
    }

    [Fact]
    public void VerdictAllowsConnection()
    {
        Assert.True(TlsVerdict.Trusted.AllowsConnection);
        Assert.True(TlsVerdict.Pinned.AllowsConnection);
        Assert.True(TlsVerdict.Unchecked.AllowsConnection);
        Assert.False(TlsVerdict.Untrusted(TlsFailureReason.Expired).AllowsConnection);
        Assert.False(TlsVerdict.PinMismatch.AllowsConnection);
    }

    [Fact]
    public void StatusText()
    {
        Assert.Equal("TLS 사용 · 인증서 확인됨", TlsStatus.Verified.Text());
        Assert.Equal("TLS 사용 · 고정 인증서", TlsStatus.Pinned.Text());
        Assert.Equal("TLS 사용 · 확인 안 함(SSH 터널)", TlsStatus.TunnelUnchecked.Text());
        Assert.Equal("TLS 안 씀", TlsStatus.Off.Text());
        Assert.Equal(TlsStatus.Verified, TlsVerify.Status(TlsVerdict.Trusted));
        Assert.Equal(TlsStatus.Pinned, TlsVerify.Status(TlsVerdict.Pinned));
        Assert.Equal(TlsStatus.TunnelUnchecked, TlsVerify.Status(TlsVerdict.Unchecked));
    }

    // MARK: 정책

    [Fact]
    public void PolicyFromProfile()
    {
        var p = new ConnectionProfile { Host = "db.example.com" };
        Assert.Equal(new TlsPolicy(true, "db.example.com", null), TlsPolicy.Of(p));
        p.TlsPinSHA256 = "";
        Assert.Null(TlsPolicy.Of(p).Pin);
        p.TlsPinSHA256 = "AA:BB";
        p.Ssh = new SshConfig { Host = "bastion" };
        // 터널이면 이름 확인 없음, 고정 지문만
        Assert.Equal(new TlsPolicy(true, null, "AA:BB"), TlsPolicy.Of(p));
        p.TlsVerify = false;
        Assert.False(TlsPolicy.Of(p).Verify);
    }

    // MARK: 프로필 저장 형식

    [Fact]
    public void OldProfileDecodesWithVerifyOn()
    {
        const string json = """
            [{"id":"7D3C2D8E-1A3B-4C55-9F00-2B9C0E7A1111","kind":"mysql","name":"old","host":"db","port":3306,
              "username":"u","useTLS":true}]
            """;
        var p = JsonSerializer.Deserialize<List<ConnectionProfile>>(json, ProfileStore.Json)![0];
        Assert.True(p.TlsVerify);
        Assert.Null(p.TlsPinSHA256);
    }

    [Fact]
    public void ProfileRoundTripsTLSFields()
    {
        var p = new ConnectionProfile { Name = "x", Host = "h", TlsVerify = false, TlsPinSHA256 = "AA:BB" };
        var text = JsonSerializer.Serialize(p, ProfileStore.Json);
        Assert.Contains("\"tlsVerify\": false", text);
        Assert.Contains("\"tlsPinSHA256\": \"AA:BB\"", text);
        var back = JsonSerializer.Deserialize<ConnectionProfile>(text, ProfileStore.Json)!;
        Assert.False(back.TlsVerify);
        Assert.Equal("AA:BB", back.TlsPinSHA256);
        // 접속 정보 내보내기 파일(Mac 과 같은 형식)에도 들어간다
        var bundle = ConnectionTransfer.Parse(ConnectionTransfer.Export([p]));
        Assert.Equal("AA:BB", bundle.Profiles[0].TlsPinSHA256);
        Assert.False(bundle.Profiles[0].TlsVerify);
    }

    // MARK: 실제 인증서 (자체 서명)

    [Fact]
    public void CertInfoFromSelfSigned()
    {
        var info = TlsVerify.CertInfo(SelfSignedDER());
        Assert.Equal(SelfSignedSHA, info.Sha256);
        Assert.Equal("CN=goddb-test.local, O=GodDB Test", info.Subject);
        Assert.Equal("CN=goddb-test.local, O=GodDB Test", info.Issuer);
        Assert.Equal("2026-11-20 ~ 2036-11-17", info.ValidityText);
    }

    [Fact]
    public void SelfSignedIsUntrusted()
    {
        var der = SelfSignedDER();
        var r = TlsVerify.Evaluate([der], "goddb-test.local");
        Assert.False(r.Ok);
        Assert.Equal([TlsFailureReason.UntrustedIssuer], r.Reasons);
        // 다른 이름으로 접속 → 이름도 다름
        var other = TlsVerify.Evaluate([der], "127.0.0.1");
        Assert.Contains(TlsFailureReason.UntrustedIssuer, other.Reasons);
        Assert.Contains(TlsFailureReason.NameMismatch, other.Reasons);
        // 유효 기간이 지난 시각 → 만료됨
        var later = TlsVerify.Evaluate([der], "goddb-test.local", DateTimeOffset.FromUnixTimeSeconds(2_200_000_000).LocalDateTime);
        Assert.Contains(TlsFailureReason.Expired, later.Reasons);
    }

    [Fact]
    public void ReasonsFromTrustDetails()
    {
        Assert.Equal([TlsFailureReason.UntrustedIssuer], TlsVerify.Reasons(new HashSet<string> { "AnchorTrusted", "ServerAuthEKU" }));
        Assert.Equal([TlsFailureReason.NameMismatch], TlsVerify.Reasons(new HashSet<string> { "SSLHostname" }));
        Assert.Equal([TlsFailureReason.Expired], TlsVerify.Reasons(new HashSet<string> { "TemporalValidity" }));
        Assert.Equal([TlsFailureReason.UntrustedIssuer, TlsFailureReason.NameMismatch, TlsFailureReason.Expired],
            TlsVerify.Reasons(new HashSet<string> { "AnchorTrusted", "SSLHostname", "TemporalValidity" }));
        Assert.Equal([TlsFailureReason.UntrustedIssuer], TlsVerify.Reasons(new HashSet<string>()));
    }

    [Fact]
    public void VerifierFlow()
    {
        var der = SelfSignedDER();
        // 고정 없음 → 묻기 (오류에 인증서 정보)
        var first = new TlsVerifier(new TlsPolicy(true, "goddb-test.local", null));
        Assert.Equal(TlsVerdict.Untrusted(TlsFailureReason.UntrustedIssuer), first.Judge([der]));
        var failure = Assert.IsType<TlsCertificateException>(first.Failure);
        Assert.Equal(TlsCertificateErrorKind.Untrusted, failure.Kind);
        var info = failure.Info!;
        Assert.Equal(SelfSignedSHA, info.Sha256);
        // 신뢰(고정) 후 → 연결
        var pinned = new TlsVerifier(new TlsPolicy(true, "goddb-test.local", info.Sha256));
        Assert.Equal(TlsVerdict.Pinned, pinned.Judge([der]));
        Assert.Null(pinned.Failure);
        Assert.Equal(TlsStatus.Pinned, pinned.Status);
        // 인증서가 바뀜 → 거부
        var changed = new TlsVerifier(new TlsPolicy(true, "goddb-test.local", "00:11"));
        Assert.Equal(TlsVerdict.PinMismatch, changed.Judge([der]));
        Assert.True(changed.Failure!.SameAs(TlsCertificateException.PinMismatch(info, "00:11")));
        // 터널 · 고정 없음 → 확인 안 함
        var tunnel = new TlsVerifier(new TlsPolicy(true, null, null));
        Assert.Equal(TlsVerdict.Unchecked, tunnel.Judge([der]));
        Assert.Equal(TlsStatus.TunnelUnchecked, tunnel.Status);
        // 오류 바꾸기: 거부했으면 인증서 오류, 아니면 원래 오류
        Assert.IsType<TlsCertificateException>(changed.Map(new GodDbException(DbErrorKind.NotConnected)));
        Assert.IsType<GodDbException>(pinned.Map(new GodDbException(DbErrorKind.NotConnected)));
    }

    [Fact]
    public void ErrorMessages()
    {
        Assert.Equal("서버가 TLS 를 지원하지 않아요. TLS 를 끄거나 SSH 터널을 쓰세요.", TlsCertificateException.ServerNoTls().Message);
        var info = new TlsCertInfo("CN=a", "CN=a", null, null, "AA");
        Assert.StartsWith("서버 인증서가 바뀌었어요.", TlsCertificateException.PinMismatch(info, "BB").Message);
        Assert.Equal("서버가 TLS 를 지원하지 않아요. TLS 를 끄거나 SSH 터널을 쓰세요.", ErrorText.Describe(TlsCertificateException.ServerNoTls()).Message);
    }

    [Fact]
    public void RedisCertificateHost()
    {
        Assert.Equal("node1.cache.example.com", RedisSession.CertificateHost("node1.cache.example.com", "cfg.example.com"));
        Assert.Equal("cfg.example.com", RedisSession.CertificateHost("10.0.0.5", "cfg.example.com"));
        Assert.Equal("s1.example.com", RedisSession.CertificateHost("10.0.0.5", "s1.example.com:26379, s2:26379"));
        Assert.Equal("10.0.0.5", RedisSession.CertificateHost("10.0.0.5", "10.0.0.1"));
    }

    /// Windows: SslStream 콜백이 넘기는 인증서 · 체인에서 서버 인증서를 맨 앞에 두고 모은다
    [Fact]
    public void CollectPutsLeafFirst()
    {
        using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(SelfSignedDER());
        var list = TlsVerifier.Collect(cert, null);
        Assert.Single(list);
        Assert.Equal(SelfSignedDER(), list[0]);
        // 콜백 자체: 고정 지문이 맞으면 허용, 없으면 거부
        Assert.True(new TlsVerifier(new TlsPolicy(true, "goddb-test.local", SelfSignedSHA)).Callback(this, cert, null, default));
        Assert.False(new TlsVerifier(new TlsPolicy(true, "goddb-test.local", null)).Callback(this, cert, null, default));
        // PEM ↔ DER
        Assert.Equal(SelfSignedDER(), TlsVerify.DerFromPem(TlsVerify.Pem(SelfSignedDER())));
    }
}
