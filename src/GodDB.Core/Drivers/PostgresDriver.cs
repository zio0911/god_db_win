using Npgsql;

namespace GodDB.Core.Drivers;

/// PostgreSQL (Npgsql). 결과는 모두 **텍스트 형식**으로 받는다 (Mac 판 postgres-nio 텍스트 패치와 같은 모양).
/// 여러 문장이면 행이 있는 마지막 문장의 결과를 돌려준다.
/// (Mac PostgresDriver 는 첫 행에서 컬럼 이름을 읽어서, 행이 없는 문장은 건너뛰고 빈 결과면 컬럼도 없다 → 화면에 "결과 없음")
public sealed class PostgresDriver : IDbDriver
{
    private readonly NpgsqlConnection _conn;
    public TlsStatus TlsStatus { get; }

    private PostgresDriver(NpgsqlConnection conn, TlsStatus tlsStatus)
    {
        _conn = conn;
        TlsStatus = tlsStatus;
    }

    public static async Task<PostgresDriver> ConnectAsync(DriverEndpoint e, CancellationToken ct = default)
    {
        var verifier = e.UseTLS && e.Tls.Verify ? new TlsVerifier(e.Tls) : null;
        try
        {
            var conn = await OpenAsync(e, verifier, ct);
            return new PostgresDriver(conn, e.UseTLS ? verifier?.Status ?? TlsStatus.Unchecked : TlsStatus.Off);
        }
        catch (Exception ex) when (verifier?.Failure is { } failure && ex is not OperationCanceledException)
        {
            throw failure;
        }
        catch (Exception ex) when (e.UseTLS && IsSslUnsupported(ex))
        {
            throw TlsCertificateException.ServerNoTls();
        }
    }

    /// TLS 필수인데 서버가 TLS 를 하지 않을 때 Npgsql 이 내는 오류
    internal static bool IsSslUnsupported(Exception ex)
    {
        for (var x = ex; x != null; x = x.InnerException)
            if (x.Message.Contains("SSL connection requested", StringComparison.OrdinalIgnoreCase)
                || x.Message.Contains("does not support SSL", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static Task<NpgsqlConnection> OpenAsync(DriverEndpoint e, TlsVerifier? verifier, CancellationToken ct) =>
        DriverUtil.TryEachAsync(e.Host, async ip =>
        {
            var b = new NpgsqlConnectionStringBuilder
            {
                Host = ip,
                Port = e.Port,
                Username = e.Username,
                Password = e.Password ?? "",
                Database = string.IsNullOrEmpty(e.Database) ? "postgres" : e.Database,
                // Require: TLS 를 켜면 반드시 TLS (서버가 안 하면 오류 — "가능하면 TLS" 로 평문에 내려가지 않는다, Mac 과 같음).
                // 인증서는 아래 콜백(TlsVerifier)이 판정한다. 콜백이 없으면(확인 끔) 암호화만 한다.
                SslMode = e.UseTLS ? SslMode.Require : SslMode.Disable,
                Pooling = false,
                Timeout = e.ConnectTimeoutSeconds,
                ApplicationName = "GodDB",
            };
            var conn = new NpgsqlConnection(b.ConnectionString);
            if (verifier != null)
                conn.SslClientAuthenticationOptionsCallback = o => o.RemoteCertificateValidationCallback = verifier.Callback;
            // SNI 에는 도메인만 쓸 수 있다 (IP 로 붙어도 원래 호스트 이름으로)
            if (e.UseTLS && e.ServerName is { } sni)
                conn.SslClientAuthenticationOptionsCallback += o => o.TargetHost = sni;
            try { await conn.OpenAsync(ct); }
            catch { await conn.DisposeAsync(); throw; }
            return conn;
        }, ct);

    public bool IsOpen => _conn.State == System.Data.ConnectionState.Open;

    public async Task<RawResult> QueryAsync(string sql, int timeoutSeconds = 0, CancellationToken ct = default)
    {
        if (!IsOpen) throw new GodDbException(DbErrorKind.NotConnected);
        // 빈 문장: Mac(postgres-nio simpleQuery)은 오류 없이 빈 결과. Npgsql 은 CommandText 가 비면 예외라 여기서 돌려준다.
        // (스키마 비교에서 identity 만 다른 컬럼은 보정 SQL 이 빈 문자열이다)
        if (string.IsNullOrWhiteSpace(sql)) return RawResult.Empty;
        await using var cmd = new NpgsqlCommand(sql, _conn) { CommandTimeout = timeoutSeconds, AllResultTypesAreUnknown = true };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        RawResult? last = null;
        do
        {
            if (r.FieldCount > 0)
            {
                var set = await DriverUtil.ReadAsync(r, Cell, ct);
                if (set.Rows.Count > 0) last = set;
            }
        } while (await r.NextResultAsync(ct));
        return last ?? RawResult.Empty with { RecordsAffected = r.RecordsAffected };
    }

    private static string? Cell(System.Data.Common.DbDataReader r, int i)
    {
        var s = r.GetString(i);
        // bytea 는 텍스트 형식에서 \x… 로 온다 → 다른 DB 와 같게 0x…
        if (s.StartsWith("\\x", StringComparison.Ordinal) && r.GetDataTypeName(i) == "bytea")
            return "0x" + s[2..].ToUpperInvariant();
        return s;
    }

    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();
}
