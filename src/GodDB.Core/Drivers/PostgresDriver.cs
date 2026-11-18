using Npgsql;

namespace GodDB.Core.Drivers;

/// PostgreSQL (Npgsql). 결과는 모두 **텍스트 형식**으로 받는다 (Mac 판 postgres-nio 텍스트 패치와 같은 모양).
/// 여러 문장이면 행이 있는 마지막 문장의 결과를 돌려준다.
/// (Mac PostgresDriver 는 첫 행에서 컬럼 이름을 읽어서, 행이 없는 문장은 건너뛰고 빈 결과면 컬럼도 없다 → 화면에 "결과 없음")
public sealed class PostgresDriver : IDbDriver
{
    private readonly NpgsqlConnection _conn;
    private PostgresDriver(NpgsqlConnection conn) => _conn = conn;

    public static Task<PostgresDriver> ConnectAsync(DriverEndpoint e, CancellationToken ct = default) =>
        DriverUtil.TryEachAsync(e.Host, async ip =>
        {
            var b = new NpgsqlConnectionStringBuilder
            {
                Host = ip,
                Port = e.Port,
                Username = e.Username,
                Password = e.Password ?? "",
                Database = string.IsNullOrEmpty(e.Database) ? "postgres" : e.Database,
                // Prefer: 서버가 TLS 를 지원하면 암호화, 인증서는 검증하지 않는다 (Mac 과 같음)
                SslMode = e.UseTLS ? SslMode.Prefer : SslMode.Disable,
                Pooling = false,
                Timeout = e.ConnectTimeoutSeconds,
                ApplicationName = "GodDB",
            };
            var conn = new NpgsqlConnection(b.ConnectionString);
            try { await conn.OpenAsync(ct); }
            catch { await conn.DisposeAsync(); throw; }
            return new PostgresDriver(conn);
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
