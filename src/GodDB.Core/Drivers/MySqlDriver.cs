using System.Data.Common;
using MySqlConnector;

namespace GodDB.Core.Drivers;

/// MySQL (MySqlConnector). 프로시저(CALL)가 결과 집합을 여러 개 돌려주면 첫 결과 집합만.
/// 행이 없는 결과는 컬럼도 없다 (Mac MySQLDriver 는 첫 행에서 컬럼 이름을 읽어서, 빈 결과면 컬럼이 비고 화면에 "결과 없음").
public sealed class MySqlDriver : IDbDriver
{
    private readonly MySqlConnection _conn;
    public TlsStatus TlsStatus { get; }

    private MySqlDriver(MySqlConnection conn, TlsStatus tlsStatus)
    {
        _conn = conn;
        TlsStatus = tlsStatus;
    }

    public static async Task<MySqlDriver> ConnectAsync(DriverEndpoint e, CancellationToken ct = default)
    {
        var verifier = e.UseTLS && e.Tls.Verify ? new TlsVerifier(e.Tls) : null;
        try
        {
            var conn = await OpenAsync(e, verifier, ct);
            return new MySqlDriver(conn, e.UseTLS ? verifier?.Status ?? TlsStatus.Unchecked : TlsStatus.Off);
        }
        catch (Exception ex) when (verifier?.Failure is { } failure && ex is not OperationCanceledException)
        {
            // 인증서 확인에서 거부했으면 그 오류(인증서 정보 포함)로 — 화면이 확인 창을 띄운다
            throw failure;
        }
        catch (MySqlException me) when (e.UseTLS && me.Message.Contains("does not support SSL", StringComparison.OrdinalIgnoreCase))
        {
            throw TlsCertificateException.ServerNoTls();
        }
    }

    private static Task<MySqlConnection> OpenAsync(DriverEndpoint e, TlsVerifier? verifier, CancellationToken ct) =>
        DriverUtil.TryEachAsync(e.Host, async ip =>
        {
            var b = new MySqlConnectionStringBuilder
            {
                Server = ip,
                Port = (uint)e.Port,
                UserID = e.Username,
                Password = e.Password ?? "",
                Database = e.Database,
                // Required: TLS 를 켜면 서버가 TLS 를 안 해도 평문으로 내려가지 않는다 (Mac requireTLS 와 같음).
                // 인증서는 아래 콜백(TlsVerifier)이 판정한다. 콜백이 없으면(확인 끔) 암호화만 한다.
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
            if (verifier != null) conn.RemoteCertificateValidationCallback = verifier.Callback;
            try { await conn.OpenAsync(ct); }
            catch (MySqlException me) when (me.ErrorCode == MySqlErrorCode.UnableToConnectToHost)
            {
                await conn.DisposeAsync();
                // MySqlConnector 는 소켓 오류(거부 · 시간 초과 · 경로 없음)를 1042 하나로 감싼다.
                // Mac 처럼 원인별 안내를 보여주려고 같은 주소로 직접 연결해 실제 소켓 오류를 알아낸다.
                await DriverUtil.ProbeAsync(ip, e.Port, e.ConnectTimeoutSeconds, ct);
                throw;
            }
            catch { await conn.DisposeAsync(); throw; }
            return conn;
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
            if (r.FieldCount > 0 && first == null)
            {
                var set = await DriverUtil.ReadAsync(r, Cell, ct);
                if (set.Rows.Count > 0) first = set;
            }
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
