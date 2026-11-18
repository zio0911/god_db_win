using System.Data.Common;
using MySqlConnector;

namespace GodDB.Core.Drivers;

/// MySQL (MySqlConnector). 프로시저(CALL)가 결과 집합을 여러 개 돌려주면 첫 결과 집합만.
public sealed class MySqlDriver : IDbDriver
{
    private readonly MySqlConnection _conn;
    private MySqlDriver(MySqlConnection conn) => _conn = conn;

    public static Task<MySqlDriver> ConnectAsync(DriverEndpoint e, CancellationToken ct = default) =>
        DriverUtil.TryEachAsync(e.Host, async ip =>
        {
            var b = new MySqlConnectionStringBuilder
            {
                Server = ip,
                Port = (uint)e.Port,
                UserID = e.Username,
                Password = e.Password ?? "",
                Database = e.Database,
                // 터널 경유 시 호스트명이 127.0.0.1 이 되어 인증서 검증이 불가하므로 암호화만 사용한다 (Required = 검증 안 함)
                SslMode = e.UseTLS ? MySqlSslMode.Required : MySqlSslMode.None,
                Pooling = false,
                ConnectionTimeout = (uint)e.ConnectTimeoutSeconds,
                AllowUserVariables = true,
                AllowZeroDateTime = true,
                ConvertZeroDateTime = false,
                TreatTinyAsBoolean = false,
                GuidFormat = MySqlGuidFormat.None,
                AllowPublicKeyRetrieval = true,
                CharacterSet = "utf8mb4",
                ApplicationName = "GodDB",
            };
            var conn = new MySqlConnection(b.ConnectionString);
            try { await conn.OpenAsync(ct); }
            catch { await conn.DisposeAsync(); throw; }
            return new MySqlDriver(conn);
        }, ct);

    public bool IsOpen => _conn.State == System.Data.ConnectionState.Open;

    public async Task<RawResult> QueryAsync(string sql, int timeoutSeconds = 0, CancellationToken ct = default)
    {
        if (!IsOpen) throw new GodDbException(DbErrorKind.NotConnected);
        await using var cmd = new MySqlCommand(sql, _conn) { CommandTimeout = timeoutSeconds };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        RawResult? first = null;
        do
        {
            if (r.FieldCount > 0 && first == null) first = await DriverUtil.ReadAsync(r, Cell, ct);
        } while (await r.NextResultAsync(ct));
        return first ?? RawResult.Empty with { RecordsAffected = r.RecordsAffected };
    }

    /// 서버가 텍스트로 보여주는 모양과 같게 (DATE → 날짜만, DATETIME/TIMESTAMP → 소수 초는 있을 때만)
    private static string? Cell(DbDataReader r, int i)
    {
        var type = r.GetDataTypeName(i).ToUpperInvariant();
        var v = r.GetValue(i);
        return v switch
        {
            MySqlDateTime md => md.IsValidDateTime
                ? DriverUtil.DateTimeText(md.GetDateTime(), type == "DATE")
                : type == "DATE" ? "0000-00-00" : "0000-00-00 00:00:00",
            DateTime d => DriverUtil.DateTimeText(d, type == "DATE"),
            TimeSpan t => DriverUtil.Time(t),
            byte[] b => BinaryText.Hex(b),
            ulong bit when type == "BIT" => bit.ToString(),
            _ => DriverUtil.Invariant(v),
        };
    }

    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();
}
