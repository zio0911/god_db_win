using System.Globalization;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using static GodDB.Core.L10n;

namespace GodDB.Core.Audit;

public enum LogLevel { Info, Success, Warning, Error }

/// 상태창 항목의 종류 (원래 값은 Mac 판과 같은 한국어)
public enum LogSource { Connection, Query, Schema, Compare, App }

public static class LogSourceExtensions
{
    public static string RawValue(this LogSource s) => s switch
    {
        LogSource.Connection => "접속",
        LogSource.Query => "쿼리",
        LogSource.Schema => "스키마",
        LogSource.Compare => "비교",
        _ => "앱",
    };

    /// 화면 표시용 이름 (현재 언어)
    public static string Title(this LogSource s) => Tr(s.RawValue());
}

/// 상태창(로그 패널)에 쌓이는 항목.
/// <param name="Detail">원본 오류, 실행한 SQL 등 자세한 내용</param>
/// <param name="Hint">해결 방법 안내</param>
/// <param name="Audit">감사 로그 파일에 남길 내용 (없으면 화면 상태창에만)</param>
public sealed record LogEntry(LogLevel Level, LogSource Source, string Message,
                              string? Detail = null, string? Hint = null, AuditRecord? Audit = null)
{
    public Guid Id { get; } = Guid.NewGuid();
    /// 로컬 시각 (Mac: init(date:))
    public DateTime Date { get; init; } = DateTime.Now;

    /// 복사용 전체 텍스트
    public string FullText
    {
        get
        {
            var parts = new List<string> { $"[{Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] [{Source.Title()}] {Message}" };
            if (Hint != null) parts.Add(Tr("해결 방법: %@", Hint));
            if (Detail != null) parts.Add(Detail);
            return string.Join('\n', parts);
        }
    }
}

/// 상태창 로그 (메모리, 최대 1,000건 — 앱 종료 시 사라짐). 감사 대상 항목은 로그 파일에도 남긴다.
/// Mac 판 AppState.log / logError 의 화면과 무관한 부분.
public sealed class AppLog(LogFile? file = null)
{
    public const int MaxEntries = 1000;

    private readonly Lock _lock = new();
    private readonly List<LogEntry> _entries = [];
    private readonly LogFile? _file = file;

    /// 항목이 추가되면 (오류면 화면이 상태창을 열고 그 항목을 고른다)
    public event Action<LogEntry>? Added;

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public LogEntry? Last
    {
        get { lock (_lock) return _entries.Count > 0 ? _entries[^1] : null; }
    }

    public int ErrorCount
    {
        get { lock (_lock) return _entries.Count(e => e.Level == LogLevel.Error); }
    }

    public LogEntry Log(LogLevel level, LogSource source, string message, string? detail = null, string? hint = null, AuditRecord? audit = null)
    {
        var entry = new LogEntry(level, source, message, detail, hint, audit);
        Add(entry);
        return entry;
    }

    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        _file?.Append(entry);
        Added?.Invoke(entry);
    }

    /// 오류를 상태창에 남기고, 화면에 보여줄 메시지를 돌려준다.
    /// 감사 로그는 같은 구분으로, 결과 칸에 실패 사유.
    public string LogError(Exception error, LogSource source, string context, string? sql = null, AuditRecord? audit = null)
    {
        var d = ErrorText.Describe(error);
        var detail = Tr("원본 오류: %@", d.Raw);
        if (sql != null) detail = Tr("실행한 SQL:\n%@\n\n", sql) + detail;
        var record = audit == null ? null : audit with { Result = "실패: " + d.Message };
        Log(LogLevel.Error, source, $"{context}: {d.Message}", detail, d.Hint, record);
        return d.Hint != null ? Tr("%@\n\n해결 방법: %@", d.Message, d.Hint) : d.Message;
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    /// 문제 보고에 넣을 최근 오류 줄 ("[종류] 메시지 — 해결 방법")
    public List<string> RecentErrorLines(int count = 20)
    {
        lock (_lock)
        {
            return _entries.Where(e => e.Level == LogLevel.Error).TakeLast(count)
                .Select(e => $"[{e.Source.RawValue()}] {e.Message}" + (e.Hint != null ? $" — {e.Hint}" : ""))
                .ToList();
        }
    }
}

/// 오류를 사람이 읽을 수 있는 한국어 메시지 + 해결 힌트로 바꾼다.
/// Mac 판은 MySQLNIO/PostgresNIO/자체 드라이버 오류를 보고, Windows 판은 MySqlConnector/Npgsql/SqlClient/소켓 오류를 본다.
/// Redis·SSH 등 다른 오류는 그 드라이버를 옮길 때 <see cref="Describers"/> 에 붙인다.
public static class ErrorText
{
    public sealed record Described(string Message, string? Hint, string Raw);

    /// 드라이버별 추가 변환 (먼저 맞는 것을 쓴다)
    public static readonly List<Func<Exception, string, Described?>> Describers = [];

    /// 인증 실패로 볼 추가 판정 (Redis WRONGPASS/NOAUTH 등)
    public static readonly List<Func<Exception, bool>> AuthFailureChecks = [];

    public static Described Describe(Exception error)
    {
        var raw = RawText(error);

        foreach (var f in Describers)
            if (f(error, raw) is { } custom) return custom;

        // F9-3 · F9-4: 인증서 · SSH 호스트 키 (화면이 확인 창을 띄운다). 문구에 TLS 가 들어 있어 아래 TLS 추측보다 먼저 본다
        if (error is Drivers.TlsCertificateException or Platform.SshHostKeyException) return new(error.Message, null, raw);
        if (error is Drivers.GodDbException) return new(error.Message, null, raw);

        switch (error)
        {
            case MySqlException my when my.Number > 0 && my.Number < 2000:
                // 서버가 돌려준 오류 (Mac: MySQLError.server)
                return new($"[{my.Number}] {my.Message}", ServerHint(my.Number, my.Message), raw);
            case PostgresException pg:
            {
                var text = $"[{pg.SqlState}] {pg.MessageText}";
                if (pg.Detail != null) text += "\n" + pg.Detail;
                return new(text, PostgresHint(pg.SqlState) ?? pg.Hint, raw);
            }
            case NpgsqlException npg when npg.InnerException != null:
                return Describe(npg.InnerException);
            case SqlException ms when ms.Number != 0:
                return new($"[{ms.Number}] {ms.Message}", MssqlHint(ms.Number), raw);
        }

        if (FindSocketError(error) is { } se) return DescribeSocket(se, raw);
        if (error is TimeoutException) return DescribeSocket(new SocketException((int)SocketError.TimedOut), raw);

        var lower = raw.ToLowerInvariant();
        if (lower.Contains("connection refused") || lower.Contains("econnrefused"))
            return new(Tr("서버가 연결을 거부했어요."), Tr("호스트와 포트가 맞는지, DB 서버가 켜져 있는지 확인해 주세요. 사내망/VPN이 필요한 서버일 수도 있어요."), raw);
        if (lower.Contains("timed out") || lower.Contains("timeout"))
            return new(Tr("연결 시간이 초과됐어요."), Tr("네트워크(VPN) 연결이나 보안 그룹에서 이 IP가 허용돼 있는지 확인해 주세요."), raw);
        // "no such host is known" 은 Windows 의 이름 조회 실패 문구
        if (lower.Contains("nodename nor servname") || lower.Contains("unknown host") || lower.Contains("resolve") || lower.Contains("no such host"))
            return new(Tr("호스트 이름을 찾을 수 없어요."), Tr("호스트 주소 오타를 확인해 주세요. 사내 도메인이면 VPN이 필요할 수 있어요."), raw);
        if (lower.Contains("nioss") || lower.Contains("ssl") || lower.Contains("tls") || lower.Contains("handshake"))
            return new(Tr("TLS 연결에 실패했어요."), Tr("서버가 TLS를 지원하지 않으면 접속 편집에서 'TLS 사용'을 꺼 보세요."), raw);
        return new(error.Message, null, raw);
    }

    /// 사용자/비밀번호 인증 실패 여부
    public static bool IsAuthFailure(Exception error)
    {
        if (error is MySqlException { Number: 1045 }) return true;
        if (error is PostgresException { SqlState: "28P01" }) return true;
        if (error is SqlException { Number: 18456 }) return true;
        return AuthFailureChecks.Any(f => f(error));
    }

    public static string? PostgresHint(string state) => state switch
    {
        "28P01" or "28000" => Tr("사용자 이름 또는 비밀번호가 틀렸어요. 접속 편집에서 비밀번호를 다시 입력해 주세요."),
        "3D000" => Tr("접속 설정의 DB 이름이 서버에 없어요. (PostgreSQL 은 DB 마다 따로 접속해요)"),
        "3F000" => Tr("스키마가 없어요. 사이드바에서 스키마를 다시 골라 주세요."),
        "42P01" => Tr("테이블이 없어요. 사이드바에서 선택된 스키마가 맞는지 확인해 주세요."),
        "42703" => Tr("컬럼 이름이 틀렸어요. PostgreSQL 은 따옴표 없는 이름을 소문자로 바꿔요."),
        "42601" => Tr("SQL 문법 오류예요. PostgreSQL 은 식별자에 백틱(`) 대신 큰따옴표(\")를 써요."),
        "42501" => Tr("권한이 없어요. DBA에게 권한을 요청해 주세요."),
        "25006" => Tr("읽기전용 접속이라 서버가 변경을 거부했어요."),
        "57014" => Tr("쿼리가 취소됐어요 (시간 초과 등)."),
        _ => null,
    };

    public static string? MssqlHint(int number) => number switch
    {
        18456 => Tr("사용자 이름 또는 비밀번호가 틀렸어요. SQL Server 인증(sa 등) 계정인지도 확인해 주세요."),
        4060 => Tr("접속 설정의 기본 DB 를 열 수 없어요. 이름과 권한을 확인해 주세요."),
        208 => Tr("개체(테이블/뷰)가 없어요. 사이드바에서 DB 가 맞는지, 스키마(dbo 등)를 붙였는지 확인해 주세요."),
        207 => Tr("컬럼 이름이 틀렸어요."),
        102 or 156 => Tr("SQL 문법 오류예요. SQL Server 는 LIMIT 대신 TOP, 식별자는 [대괄호]를 써요."),
        229 or 230 or 262 => Tr("권한이 없어요. DBA에게 권한을 요청해 주세요."),
        20009 or 20002 => Tr("서버에 연결할 수 없어요. 호스트/포트, VPN, 방화벽(1433)을 확인해 주세요."),
        20017 or 20047 => Tr("연결이 끊겼어요. TLS 설정이 서버와 맞지 않으면 접속 편집에서 'TLS 사용'을 바꿔 보세요."),
        _ => null,
    };

    public static string? ServerHint(int code, string message) => code switch
    {
        1045 => Tr("사용자 이름 또는 비밀번호가 틀렸어요. 접속 편집에서 비밀번호를 다시 입력해 주세요."),
        1044 or 1142 or 1143 => Tr("이 계정에 해당 DB/테이블 권한이 없어요. DBA에게 권한을 요청해 주세요."),
        1049 => Tr("DB 이름이 없어요. 접속 편집의 '기본 DB'를 확인해 주세요."),
        1146 => Tr("테이블이 없어요. 사이드바에서 현재 선택된 DB가 맞는지 확인해 주세요."),
        1054 => Tr("컬럼 이름이 틀렸어요. 사이드바에서 테이블을 펼쳐 컬럼명을 확인해 주세요."),
        1064 => Tr("SQL 문법 오류예요. 오류 위치('near ...') 앞부분을 확인해 주세요."),
        1792 => Tr("읽기전용 접속이라 서버가 변경을 거부했어요."),
        2013 or 2006 => Tr("쿼리 도중 연결이 끊겼어요. 다시 연결해 주세요."),
        3024 => Tr("쿼리 실행 시간 제한을 넘었어요. 조건을 좁히거나 LIMIT을 줄여 주세요."),
        _ => null,
    };

    /// Redis 서버 오류 코드의 해결 방법 (Redis 드라이버가 쓴다)
    public static string? RedisHint(string code) => code switch
    {
        "WRONGPASS" => Tr("비밀번호(또는 ACL 사용자)가 맞는지 확인해 주세요."),
        "NOAUTH" => Tr("이 서버는 비밀번호가 필요해요. 접속 편집에서 비밀번호를 넣어 주세요."),
        "NOPERM" => Tr("이 ACL 사용자에게 권한이 없는 명령이나 키예요."),
        "WRONGTYPE" => Tr("키의 타입과 맞지 않는 명령이에요. TYPE 으로 타입을 확인해 보세요."),
        "MOVED" or "ASK" => Tr("Redis Cluster 의 다른 노드에 있는 키예요. 지금은 Cluster 를 지원하지 않아 해당 노드에 직접 접속해야 해요."),
        "READONLY" => Tr("복제본(읽기 전용) 노드라 쓰기를 할 수 없어요. 주 노드에 접속해 주세요."),
        _ => null,
    };

    /// SSH 터널 시간 초과의 해결 방법
    public static string SshTimeoutHint => Tr("바스티온까지 네트워크가 닿는지(VPN), SSH 포트가 맞는지 확인해 주세요.");

    /// SSH 오류 문구의 해결 방법. chmod·~/.ssh 안내는 Windows 표현으로 바꿨다.
    public static string? SshHint(string message)
    {
        var m = message.ToLowerInvariant();
        if (m.Contains("permission denied"))
            return Tr("SSH 키 파일 또는 SSH 사용자 이름이 맞는지 확인해 주세요. 키 파일은 본인 계정만 읽을 수 있어야 해요 (파일 속성 > 보안).");
        if (m.Contains("host key verification failed") || m.Contains("remote host identification has changed"))
            return Tr("바스티온의 호스트 키가 바뀌었어요. %USERPROFILE%\\.ssh\\known_hosts 에서 해당 호스트 줄을 지운 뒤 다시 시도해 주세요.");
        if (m.Contains("could not resolve hostname"))
            return Tr("바스티온 호스트 주소를 확인해 주세요.");
        if (m.Contains("unprotected private key") || m.Contains("bad permissions"))
            return Tr("키 파일 권한이 너무 열려 있어요. 파일 속성 > 보안에서 본인 계정만 읽을 수 있게 바꿔 주세요.");
        return null;
    }

    private static SocketException? FindSocketError(Exception? e)
    {
        for (int depth = 0; e != null && depth < 8; depth++, e = e.InnerException)
            if (e is SocketException se) return se;
        return null;
    }

    /// 소켓 오류 (Mac 판 describeErrno 와 같은 분류). 로컬 네트워크 권한 안내는 Windows 방화벽 안내로 바꿨다.
    internal static Described DescribeSocket(SocketException e, string raw) => e.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => new(Tr("서버가 연결을 거부했어요. (포트가 닫혀 있음)"),
            Tr("호스트와 포트가 맞는지, DB 서버(또는 Docker 컨테이너)가 켜져 있는지 확인해 주세요."), raw),
        SocketError.TimedOut => new(Tr("연결 시간이 초과됐어요."),
            Tr("VPN 연결 여부와, 보안 그룹/방화벽에서 이 PC가 허용돼 있는지 확인해 주세요."), raw),
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => new(Tr("서버까지 네트워크 경로가 없어요."),
            Tr("VPN 연결을 확인해 주세요. 사내망 IP라면 Windows 방화벽이나 회사 보안 프로그램이 GodDB를 막고 있지 않은지 확인해 주세요."), raw),
        SocketError.AccessDenied => new(Tr("운영체제가 네트워크 연결을 막았어요."),
            Tr("Windows 방화벽 또는 회사 보안 프로그램(백신·방화벽)에서 GodDB를 허용해 주세요."), raw),
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => new(Tr("호스트 이름을 찾을 수 없어요."),
            Tr("호스트 주소 오타를 확인해 주세요. 사내 도메인이면 VPN이 필요할 수 있어요."), raw),
        _ => new(Tr("네트워크 오류 (errno %@: %@)", e.ErrorCode, e.Message), null, raw),
    };

    internal static string RawText(Exception error)
    {
        var described = $"{error.GetType().Name}: {error.Message}";
        var inner = error.InnerException;
        for (int depth = 0; inner != null && depth < 4; depth++, inner = inner.InnerException)
            described += $" ← {inner.GetType().Name}: {inner.Message}";
        return $"{error.Message}\n({described})";
    }
}
