#pragma warning disable xUnit1051 // 통합 테스트는 자체 시간 제한을 쓴다
using GodDB.Core;
using GodDB.Core.Drivers;
using GodDB.Core.Redis;
using Microsoft.Data.SqlClient;

namespace GodDB.Integration.Tests;

/// Mac TLSDatabaseIntegrationTests.swift — 실제 MySQL · PostgreSQL · SQL Server 로 TLS 인증서 확인 · 고정 · TLS 필수를 본다.
/// 테스트 MySQL · SQL Server(Docker)는 자동 생성 자체 서명 인증서, 테스트 PostgreSQL 은 TLS 꺼짐.
public class TLSDatabaseIntegrationTests
{
    private static ConnectionProfile MySql(string? pin = null, bool verify = true) =>
        It.MySql() with { Name = "mysql-tls", TlsVerify = verify, TlsPinSHA256 = pin };

    [Fact]
    public async Task MysqlSelfSignedAsksThenPinConnects()
    {
        TestDb.Port(DatabaseKind.Mysql);
        // 1) 처음: 자체 서명 → 인증서 정보를 담은 오류 (연결 안 함)
        var e = await Assert.ThrowsAsync<TlsCertificateException>(() => DbSession.ConnectAsync(MySql(), It.MySqlPassword));
        Assert.Equal(TlsCertificateErrorKind.Untrusted, e.Kind);
        Assert.Contains(TlsFailureReason.UntrustedIssuer, e.Reasons);
        var pin = e.Info!.Sha256;

        // 2) 고정하면 묻지 않고 연결, 상태 "고정 인증서", 실제 쿼리 동작
        await using (var s = await DbSession.ConnectAsync(MySql(pin), It.MySqlPassword))
        {
            Assert.Equal(TlsStatus.Pinned, s.TlsStatus);
            await s.ExecuteAsync("SELECT 1", limit: 1);
        }

        // 3) 지문이 다르면 거부
        var changed = await Assert.ThrowsAsync<TlsCertificateException>(() => DbSession.ConnectAsync(MySql("00:11:22"), It.MySqlPassword));
        Assert.Equal(TlsCertificateErrorKind.PinMismatch, changed.Kind);

        // 4) 확인 끔 → 암호화만 하고 연결
        await using var u = await DbSession.ConnectAsync(MySql(verify: false), It.MySqlPassword);
        Assert.Equal(TlsStatus.Unchecked, u.TlsStatus);
    }

    [Fact]
    public async Task PostgresTLSIsRequired()
    {
        // 테스트 PostgreSQL 은 TLS 를 안 한다 → TLS 를 켜면 평문으로 붙지 않고 "TLS 를 지원하지 않아요"
        TestDb.Port(DatabaseKind.Postgres);
        var (p, pw) = It.Multi(DatabaseKind.Postgres, "shop");
        var e = await Assert.ThrowsAsync<TlsCertificateException>(() => DbSession.ConnectAsync(p with { UseTLS = true }, pw));
        Assert.Equal(TlsCertificateErrorKind.ServerNoTls, e.Kind);
        // TLS 끄면 정상
        await using var s = await DbSession.ConnectAsync(p with { UseTLS = false }, pw);
        Assert.Equal(TlsStatus.Off, s.TlsStatus);
    }

    // MARK: - SQL Server (F9-3: 인증서를 미리 받아 판정 → SqlClient 도 같은 인증서인지 다시 확인)

    private static (ConnectionProfile Profile, string Password) Mssql(string? pin = null, bool verify = true)
    {
        var (p, pw) = It.Multi(DatabaseKind.Mssql, "master");
        return (p with { UseTLS = true, TlsVerify = verify, TlsPinSHA256 = pin }, pw);
    }

    [Fact]
    public async Task MssqlSelfSignedAsksThenPinConnects()
    {
        TestDb.Port(DatabaseKind.Mssql);
        var (p, pw) = Mssql();
        // 1) 처음: 자체 서명 → 인증서 정보를 담은 오류 (로그인하지 않음)
        var e = await Assert.ThrowsAsync<TlsCertificateException>(() => DbSession.ConnectAsync(p, pw));
        Assert.Equal(TlsCertificateErrorKind.Untrusted, e.Kind);
        Assert.Contains(TlsFailureReason.UntrustedIssuer, e.Reasons);
        var pin = e.Info!.Sha256;
        Assert.Equal(95, pin.Length);

        // 2) 고정하면 묻지 않고 연결, 상태 "고정 인증서", 실제로 암호화된 연결
        await using (var s = await DbSession.ConnectAsync(Mssql(pin).Profile, pw))
        {
            Assert.Equal(TlsStatus.Pinned, s.TlsStatus);
            var r = await s.ExecuteAsync("SELECT encrypt_option FROM sys.dm_exec_connections WHERE session_id = @@SPID", limit: 1);
            Assert.Equal("TRUE", r.Rows.FirstOrDefault()?.Values[0]);
        }

        // 3) 지문이 다르면 거부
        var changed = await Assert.ThrowsAsync<TlsCertificateException>(() => DbSession.ConnectAsync(Mssql("00:11:22").Profile, pw));
        Assert.Equal(TlsCertificateErrorKind.PinMismatch, changed.Kind);

        // 4) 확인 끔 → 암호화만 하고 연결
        await using var u = await DbSession.ConnectAsync(Mssql(verify: false).Profile, pw);
        Assert.Equal(TlsStatus.Unchecked, u.TlsStatus);
    }

    /// Mac mssqlFreeTDSChecksCAFileAndName → SqlClient 쪽 확인이 실제로 동작하는지:
    /// 미리 받은 인증서와 같으면 연결, 다른 인증서 · 다른 이름이면 SqlClient 가 거부
    [Fact]
    public async Task MssqlSqlClientChecksServerCertificateAndName()
    {
        var port = TestDb.Port(DatabaseKind.Mssql);
        var (p, pw) = Mssql();
        var verifier = new TlsVerifier(new TlsPolicy(true, null, null));
        var probe = await MssqlTls.ProbeAsync("127.0.0.1", port, null, verifier);
        Assert.Equal(TlsVerdict.Unchecked, probe.Verdict);
        var leaf = probe.Chain[0];

        async Task Open(Action<SqlConnectionStringBuilder> configure)
        {
            var b = new SqlConnectionStringBuilder
            {
                DataSource = $"127.0.0.1,{port}", InitialCatalog = "master", UserID = p.Username, Password = pw,
                Encrypt = SqlConnectionEncryptOption.Mandatory, TrustServerCertificate = false, Pooling = false, ConnectTimeout = 10,
            };
            configure(b);
            await using var c = new SqlConnection(b.ConnectionString);
            await c.OpenAsync();
        }

        // 미리 받은 서버 인증서와 정확히 같으면 → 연결 (고정 인증서 경로)
        var file = MssqlTls.WriteServerCertificate(leaf);
        try { await Open(b => b.ServerCertificate = file); }
        finally { File.Delete(file); }
        // 다른 인증서 → SqlClient 가 거부 (인증서가 바뀌면 로그인하는 연결에서 바로 막힘)
        var other = MssqlTls.WriteServerCertificate(TLSDatabaseIntegrationTestsData.OtherDER());
        try { await Assert.ThrowsAnyAsync<Exception>(() => Open(b => b.ServerCertificate = other)); }
        finally { File.Delete(other); }
        // 시스템 신뢰 저장소만 → 자체 서명이라 거부
        await Assert.ThrowsAnyAsync<Exception>(() => Open(b => b.HostNameInCertificate = "localhost"));
        // 같은 인증서라도 TrustServerCertificate=false + 이름이 다르면(시스템 확인 경로) 거부
        await Assert.ThrowsAnyAsync<Exception>(() => Open(b => b.HostNameInCertificate = "wrong.example.com"));
    }

    [Fact]
    public async Task MssqlTunnelWithoutPinDoesNotProbe()
    {
        // SSH 터널 · 고정 없음: 미리 받지 않고 암호화만 (상태 "확인 안 함(SSH 터널)")
        var port = TestDb.Port(DatabaseKind.Mssql);
        var (p, pw) = Mssql();
        var e = new DriverEndpoint
        {
            Host = "127.0.0.1", Port = port, Username = p.Username, Password = pw, Database = "master", UseTLS = true,
            Tls = new TlsPolicy(true, null, null),
        };
        await using var d = await MssqlDriver.ConnectAsync(e);
        Assert.Equal(TlsStatus.TunnelUnchecked, d.TlsStatus);
    }
}

/// Mac TLSIntegrationTests (TLSVerifyTests.swift) — 실제 TLS Redis (자체 서명 인증서). 로컬 redis-server 를 TLS 로 띄우고 포트를 넘긴다.
///   redis-server --port 0 --tls-port 46379 --tls-cert-file a.crt --tls-key-file a.key --tls-auth-clients no --requirepass goddbtest
///   GODDB_REDIS_TLS_PORT=46379 ./scripts/test.sh
public class TLSIntegrationTests
{
    private static ConnectionProfile Profile(string? pin = null, bool verify = true)
    {
        var v = Environment.GetEnvironmentVariable("GODDB_REDIS_TLS_PORT");
        if (string.IsNullOrEmpty(v)) Assert.Skip("GODDB_REDIS_TLS_PORT 없음 (TLS redis-server 를 직접 띄울 때만)");
        return new ConnectionProfile
        {
            Kind = DatabaseKind.Redis, Name = "redis-tls", Host = "127.0.0.1", Port = int.Parse(v!), Username = "", Database = "0",
            UseTLS = true, TlsVerify = verify, TlsPinSHA256 = pin,
        };
    }

    [Fact]
    public async Task SelfSignedAsksThenPinConnects()
    {
        // 1) 처음: 신뢰할 수 없는 발급자 → 인증서 정보를 담은 오류
        var e = await Assert.ThrowsAsync<TlsCertificateException>(() => RedisSession.ConnectAsync(Profile(), "goddbtest"));
        Assert.Contains(TlsFailureReason.UntrustedIssuer, e.Reasons);
        var pin = e.Info!.Sha256;
        Assert.Equal(95, pin.Length);
        // 2) 고정 후: 묻지 않고 연결, 상태 "고정 인증서"
        await using (var s = await RedisSession.ConnectAsync(Profile(pin), "goddbtest"))
            Assert.Equal(TlsStatus.Pinned, s.TlsStatus);
        // 3) 다른 지문 → 거부
        await Assert.ThrowsAsync<TlsCertificateException>(() => RedisSession.ConnectAsync(Profile("00:11:22"), "goddbtest"));
        // 4) 확인 끔 → 암호화만
        await using var off = await RedisSession.ConnectAsync(Profile(verify: false), "goddbtest");
        Assert.Equal(TlsStatus.Unchecked, off.TlsStatus);
    }
}

internal static class TLSDatabaseIntegrationTestsData
{
    /// 서버 인증서와 다른 자체 서명 인증서 (GodDB.Core.Tests TLSVerifyTests 의 테스트 인증서와 같음)
    public static byte[] OtherDER() => TlsVerify.DerFromPem("""
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
        """)!;
}
