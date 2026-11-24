using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace GodDB.Core.Drivers;

/// SQL Server (Microsoft.Data.SqlClient). SQL 인증 또는 Windows 통합 인증.
/// 결과 컬럼이 있는 첫 결과 집합을 돌려준다.
public sealed class MssqlDriver : IDbDriver
{
    private readonly SqlConnection _conn;
    public TlsStatus TlsStatus { get; }

    private MssqlDriver(SqlConnection conn, TlsStatus tlsStatus)
    {
        _conn = conn;
        TlsStatus = tlsStatus;
    }

    /// `host\인스턴스` 이면 SQL Browser 로 포트를 찾는다 (포트는 쓰지 않음)
    internal static string DataSource(string host, int port) =>
        host.Contains('\\') ? host : $"{host},{port}";

    public static async Task<MssqlDriver> ConnectAsync(DriverEndpoint e, CancellationToken ct = default)
    {
        // 서버 인증서 확인 (F9-3): 확인을 켰고 직접 접속이거나 고정 지문이 있으면 인증서를 미리 받아 판정한다 (MssqlTls.cs)
        var probe = e.UseTLS && e.Tls.Verify && (e.Tls.Hostname != null || !string.IsNullOrEmpty(e.Tls.Pin));
        async Task<MssqlDriver> Open(string server, string? probeIp, int probePort)
        {
            MssqlTls.SqlClientCheck check;
            var status = !e.UseTLS ? TlsStatus.Off : !e.Tls.Verify ? TlsStatus.Unchecked : TlsStatus.TunnelUnchecked;
            if (probe)
            {
                var verifier = new TlsVerifier(e.Tls);
                var p = await MssqlTls.ProbeAsync(probeIp ?? server, probePort, e.ServerName, verifier, ct: ct);
                check = MssqlTls.CheckFor(p.Verdict, p.Chain, e.Tls);
                status = verifier.Status;
            }
            else check = new(TrustServerCertificate: true, HostName: null, ServerCertificateDer: null);
            var b = new SqlConnectionStringBuilder
            {
                DataSource = DataSource(server, e.Port),
                InitialCatalog = e.Database,
                // TLS 를 켜면 반드시 암호화 (Mandatory). 인증서 확인은 위 판정에 따라 SqlClient 에 맡긴다
                Encrypt = e.UseTLS ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = !e.UseTLS || check.TrustServerCertificate,
                Pooling = false,
                ConnectTimeout = e.ConnectTimeoutSeconds,
                ApplicationName = "GodDB",
            };
            if (e.UseTLS && check.HostName is { } name) b.HostNameInCertificate = name;
            string? certFile = null;
            if (e.UseTLS && check.ServerCertificateDer is { } der) b.ServerCertificate = certFile = MssqlTls.WriteServerCertificate(der);
            if (e.WindowsAuth) b.IntegratedSecurity = true;
            else { b.UserID = e.Username; b.Password = e.Password ?? ""; }
            var conn = new SqlConnection(b.ConnectionString);
            try { await conn.OpenAsync(ct); }
            catch { await conn.DisposeAsync(); throw; }
            finally
            {
                if (certFile != null) try { File.Delete(certFile); } catch { }
            }
            return new MssqlDriver(conn, status);
        }
        // `host\인스턴스`: 미리 받기는 SQL Browser 가 알려 준 포트로
        if (e.Host.Contains('\\'))
        {
            var parts = e.Host.Split('\\', 2);
            var port = probe ? await MssqlTls.InstancePortAsync(parts[0], parts[1], ct) ?? e.Port : e.Port;
            return await Open(e.Host, parts[0], port);
        }
        // Windows 인증(Kerberos)은 서버 이름으로 SPN 을 찾으므로 호스트 이름 그대로 연결한다
        if (e.WindowsAuth) return await Open(e.Host, null, e.Port);
        return await DriverUtil.TryEachAsync(e.Host, ip => Open(ip, ip, e.Port), ct);
    }

    public bool IsOpen => _conn.State == System.Data.ConnectionState.Open;

    public async Task<RawResult> QueryAsync(string sql, int timeoutSeconds = 0, CancellationToken ct = default)
    {
        if (!IsOpen) throw new GodDbException(DbErrorKind.NotConnected);
        // 빈 문장: Mac(FreeTDS)은 오류 없이 빈 결과. SqlClient 는 CommandText 가 비면 예외라 여기서 돌려준다.
        // (스키마 비교에서 identity 만 다른 컬럼은 보정 SQL 이 빈 문자열이다)
        if (string.IsNullOrWhiteSpace(sql)) return RawResult.Empty;
        await using var cmd = new SqlCommand(sql, _conn) { CommandTimeout = timeoutSeconds };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        RawResult? first = null;
        do
        {
            if (r.FieldCount > 0 && first == null) first = await DriverUtil.ReadAsync(r, Cell, ct);
        } while (await r.NextResultAsync(ct));
        return first ?? RawResult.Empty with { RecordsAffected = r.RecordsAffected };
    }

    /// Mac 판(FreeTDS) 과 같은 모양: 날짜 yyyy-MM-dd, 시각 HH:mm:ss(.소수 초, 뒤의 0 없음), 바이너리 0x…
    private static string? Cell(DbDataReader r, int i)
    {
        var type = r.GetDataTypeName(i).ToLowerInvariant();
        var v = r.GetValue(i);
        return v switch
        {
            DateTime d => DriverUtil.DateTimeText(d, type == "date"),
            DateTimeOffset o => DriverUtil.DateTimeText(o.DateTime) + " " + o.ToString("zzz"),
            TimeSpan t => DriverUtil.Time(t),
            Guid g => g.ToString().ToUpperInvariant(),
            _ => DriverUtil.Invariant(v),
        };
    }

    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();
}
