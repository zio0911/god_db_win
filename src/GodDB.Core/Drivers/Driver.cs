using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GodDB.Core.Drivers;

/// 드라이버가 돌려주는 결과 하나. 모든 값은 문자열(NULL 은 null).
public sealed record RawResult(IReadOnlyList<string> Columns, List<string?[]> Rows)
{
    public static readonly RawResult Empty = new([], []);
    /// 영향을 받은 행 수 (INSERT/UPDATE/DELETE). 모르면 -1
    public int RecordsAffected { get; init; } = -1;
}

/// 드라이버가 연결할 때 필요한 값 (SSH 터널을 거쳤다면 host/port 는 로컬 주소)
public sealed record DriverEndpoint
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    /// TLS SNI 에 쓸 원래 호스트명 (IP 이거나 터널이면 null)
    public string? ServerName { get; init; }
    public string Username { get; init; } = "";
    public string? Password { get; init; }
    public string Database { get; init; } = "";
    public bool UseTLS { get; init; }
    /// SQL Server Windows 통합 인증
    public bool WindowsAuth { get; init; }
    public int ConnectTimeoutSeconds { get; init; } = 15;
}

/// DB 와 실제로 통신하는 부분.
public interface IDbDriver : IAsyncDisposable
{
    /// SQL 을 실행하고 결과 집합 하나를 돌려준다 (DB 별 규칙은 각 드라이버 참고). 결과가 없으면 빈 결과.
    Task<RawResult> QueryAsync(string sql, int timeoutSeconds = 0, CancellationToken ct = default);
    bool IsOpen { get; }
}

public static class BinaryText
{
    /// 바이트를 `0xCD01FF…` 로 (DataGrip 과 같은 모양)
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        const string digits = "0123456789ABCDEF";
        var sb = new StringBuilder(2 + bytes.Length * 2);
        sb.Append("0x");
        foreach (var b in bytes) { sb.Append(digits[b >> 4]); sb.Append(digits[b & 0x0F]); }
        return sb.ToString();
    }
}

/// ADO.NET 공통: 결과 집합 읽기, 큰 값 자르기, 주소 차례로 시도
internal static class DriverUtil
{
    /// 하나의 결과 집합을 문자열 행으로 읽는다.
    public static async Task<RawResult> ReadAsync(DbDataReader r, Func<DbDataReader, int, string?> cell, CancellationToken ct)
    {
        var cols = new string[r.FieldCount];
        for (int i = 0; i < cols.Length; i++) cols[i] = r.GetName(i);
        var rows = new List<string?[]>();
        while (await r.ReadAsync(ct))
        {
            var row = new string?[cols.Length];
            for (int i = 0; i < cols.Length; i++)
            {
                if (await r.IsDBNullAsync(i, ct)) continue;
                row[i] = cell(r, i);
            }
            rows.Add(row);
        }
        return new RawResult(cols, rows) { RecordsAffected = r.RecordsAffected };
    }

    public static string Invariant(object v) => v switch
    {
        string s => s,
        byte[] b => BinaryText.Hex(b),
        bool b => b ? "1" : "0",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// 소수 초를 뒤의 0 없이 붙인다 (Mac MSSQLDriver.format 과 같은 모양)
    public static string Time(TimeSpan t)
    {
        var sign = t < TimeSpan.Zero ? "-" : "";
        t = t.Duration();
        var s = $"{sign}{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        var ticks = t.Ticks % TimeSpan.TicksPerSecond;
        return ticks > 0 ? s + "." + ticks.ToString("0000000").TrimEnd('0') : s;
    }

    public static string DateTimeText(DateTime d, bool dateOnly = false)
    {
        var date = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return dateOnly ? date : date + " " + Time(d.TimeOfDay);
    }

    /// 모든 숫자 주소. IPv4 먼저 (로컬 Docker 등은 IPv4 만 여는 경우가 많음)
    public static async Task<List<string>> AddressesAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out _)) return [host];
        try
        {
            var all = await Dns.GetHostAddressesAsync(host, ct);
            var list = all.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Concat(all.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6))
                .Select(a => a.ToString()).Distinct().ToList();
            return list.Count == 0 ? [host] : list;
        }
        catch (SocketException) { return [host]; }
    }

    /// TCP 로 직접 연결해 보고, 실패하면 그 SocketException 을 던진다 (성공하면 아무것도 하지 않음)
    public static async Task ProbeAsync(string ip, int port, int timeoutSeconds, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        try { await client.ConnectAsync(ip, port, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SocketException((int)SocketError.TimedOut); }
    }

    public static bool IsIPAddress(string host) => IPAddress.TryParse(host, out _);

    /// 소켓 연결 단계 오류인지 (다음 주소로 넘어가도 되는지)
    public static bool IsSocketError(Exception e)
    {
        for (var x = e; x != null; x = x.InnerException)
            if (x is SocketException || x is TimeoutException) return true;
        return false;
    }

    /// 주소를 차례로 시도한다 (localhost → 127.0.0.1 → ::1)
    public static async Task<T> TryEachAsync<T>(string host, Func<string, Task<T>> body, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var address in await AddressesAsync(host, ct))
        {
            try { return await body(address); }
            catch (Exception e) when (IsSocketError(e) && !ct.IsCancellationRequested) { last = e; }
        }
        throw last ?? new GodDbException(DbErrorKind.NotConnected);
    }
}
