using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace GodDB.Core.Drivers;

/// SQL Server (Microsoft.Data.SqlClient). SQL 인증 또는 Windows 통합 인증.
/// 결과 컬럼이 있는 첫 결과 집합을 돌려준다.
public sealed class MssqlDriver : IDbDriver
{
    private readonly SqlConnection _conn;
    private MssqlDriver(SqlConnection conn) => _conn = conn;

    /// `host\인스턴스` 이면 SQL Browser 로 포트를 찾는다 (포트는 쓰지 않음)
    internal static string DataSource(string host, int port) =>
        host.Contains('\\') ? host : $"{host},{port}";

    public static async Task<MssqlDriver> ConnectAsync(DriverEndpoint e, CancellationToken ct = default)
    {
        async Task<MssqlDriver> Open(string server)
        {
            var b = new SqlConnectionStringBuilder
            {
                DataSource = DataSource(server, e.Port),
                InitialCatalog = e.Database,
                // 인증서는 검증하지 않는다 (터널 경유 시 호스트명이 달라짐 — Mac 과 같음)
                Encrypt = e.UseTLS ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = true,
                Pooling = false,
                ConnectTimeout = e.ConnectTimeoutSeconds,
                ApplicationName = "GodDB",
            };
            if (e.WindowsAuth) b.IntegratedSecurity = true;
            else { b.UserID = e.Username; b.Password = e.Password ?? ""; }
            var conn = new SqlConnection(b.ConnectionString);
            try { await conn.OpenAsync(ct); }
            catch { await conn.DisposeAsync(); throw; }
            return new MssqlDriver(conn);
        }
        // Windows 인증(Kerberos)은 서버 이름으로 SPN 을 찾으므로, 인스턴스 이름이 있을 때와 함께 호스트 이름 그대로 연결한다
        if (e.WindowsAuth || e.Host.Contains('\\')) return await Open(e.Host);
        return await DriverUtil.TryEachAsync(e.Host, Open, ct);
    }

    public bool IsOpen => _conn.State == System.Data.ConnectionState.Open;

    public async Task<RawResult> QueryAsync(string sql, int timeoutSeconds = 0, CancellationToken ct = default)
    {
        if (!IsOpen) throw new GodDbException(DbErrorKind.NotConnected);
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
