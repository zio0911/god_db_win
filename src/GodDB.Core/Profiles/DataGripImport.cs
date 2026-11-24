using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using static GodDB.Core.L10n;

namespace GodDB.Core.Profiles;

public enum DataGripParseErrorKind { NotDataGrip, NothingSupported }

public sealed class DataGripImportException(DataGripParseErrorKind kind, IReadOnlyList<string>? names = null)
    : Exception(Describe(kind, names ?? []))
{
    public DataGripParseErrorKind Kind { get; } = kind;
    /// NothingSupported 일 때 지원하지 않는 데이터 소스 이름들
    public IReadOnlyList<string> Names { get; } = names ?? [];

    private static string Describe(DataGripParseErrorKind kind, IReadOnlyList<string> names) => kind switch
    {
        DataGripParseErrorKind.NotDataGrip => Tr("DataGrip 에서 복사한 접속 정보가 아니에요. (DataGrip: 데이터 소스 우클릭 > SQL 스크립트 > 복사가 아니라, 데이터 소스를 고르고 Ctrl+C)"),
        _ => Tr("지원하지 않는 DB 예요: %@", string.Join(", ", names)),
    };
}

/// DataGrip 에서 "복사"한 접속 정보(#DataSourceSettings# … &lt;data-source …&gt;)를 읽는다.
/// 비밀번호는 DataGrip 이 따로 보관하므로 들어 있지 않다. SSH 는 설정 id 만 있어 직접 입력해야 한다.
public static partial class DataGripImport
{
    /// <param name="Notes">사용자에게 알려줄 점 (SSH 직접 입력 필요, 지원하지 않는 DB 등)</param>
    public sealed record Source(ConnectionProfile Profile, IReadOnlyList<string> Notes);

    public static bool LooksLikeDataGrip(string text) =>
        text.Contains("<data-source") && (text.Contains("#DataSourceSettings#") || text.Contains("jdbc-url"));

    [GeneratedRegex(@"<data-source[\s\S]*?</data-source>")] private static partial Regex DataSourceBlock();

    public static List<Source> Parse(string text)
    {
        if (!LooksLikeDataGrip(text)) throw new DataGripImportException(DataGripParseErrorKind.NotDataGrip);
        // #BEGIN# 과 #END# 사이 (여러 개일 수 있다), 없으면 <data-source> 부분만
        var xml = string.Concat(DataSourceBlock().Matches(text).Select(m => m.Value + "\n"));
        XDocument doc;
        if (xml.Length == 0) throw new DataGripImportException(DataGripParseErrorKind.NotDataGrip);
        try { doc = XDocument.Parse("<root>" + xml + "</root>"); }
        catch (XmlException) { throw new DataGripImportException(DataGripParseErrorKind.NotDataGrip); }

        var output = new List<Source>();
        var unsupported = new List<string>();
        foreach (var el in doc.Descendants("data-source"))
        {
            var name = el.Attribute("name")?.Value ?? "DataGrip";
            var url = Child(el, "jdbc-url") ?? "";
            var user = Child(el, "user-name") ?? "";
            if (ProfileFromJdbc(url) is not { } p)
            {
                unsupported.Add(name);
                continue;
            }
            p.Name = name;
            p.Username = user;
            if (el.Attribute("uuid")?.Value is { } uuid && Guid.TryParseExact(uuid, "D", out var id)) p.Id = id;
            var notes = new List<string>();
            if (Child(el, "ssh-properties", "enabled") == "true")
            {
                notes.Add(Tr("SSH 터널 사용 — DataGrip 복사본에는 SSH 정보가 없어서 접속 편집에서 바스티온을 직접 넣어 주세요."));
                // SSH 를 켜 두고 호스트만 비워 둔다 (편집 화면에서 채우도록)
                p.Ssh = new SshConfig { Host = "", Port = 22, Username = "ec2-user", KeyPath = "" };
            }
            notes.Add(Tr("비밀번호는 처음 연결할 때 입력해요."));
            output.Add(new Source(p, notes));
        }
        if (output.Count == 0)
        {
            if (unsupported.Count == 0) throw new DataGripImportException(DataGripParseErrorKind.NotDataGrip);
            throw new DataGripImportException(DataGripParseErrorKind.NothingSupported, unsupported);
        }
        return output;
    }

    private static string? Child(XElement el, params string[] path)
    {
        XElement? cur = el;
        foreach (var name in path) cur = cur?.Element(name);
        return cur?.Value.Trim();
    }

    private static readonly string[] Wrappers = ["aws-wrapper:", "p6spy:", "log4jdbc:", "otel:"];
    private static readonly string[] SubProtocolBases = ["mysql", "mariadb", "postgresql"];

    /// 드라이버 래퍼가 붙은 JDBC URL 을 기본 형태로
    ///   jdbc:aws-wrapper:mysql://…      (AWS Advanced JDBC Wrapper) → jdbc:mysql://…
    ///   jdbc:p6spy:mysql://… / jdbc:log4jdbc:mysql://…               → jdbc:mysql://…
    ///   jdbc:mysql:replication://… / loadbalance:// / aws://        → jdbc:mysql://… (첫 호스트)
    internal static string Normalize(string url)
    {
        var s = url.Trim();
        if (!s.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase)) return s;
        s = s[5..];
        foreach (var wrapper in Wrappers)
            if (s.StartsWith(wrapper, StringComparison.OrdinalIgnoreCase)) s = s[wrapper.Length..];
        // mysql:replication:// 같은 하위 프로토콜은 떼어낸다
        var r = s.IndexOf("://", StringComparison.Ordinal);
        if (r >= 0)
        {
            var parts = s[..r].Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && SubProtocolBases.Contains(parts[0].ToLowerInvariant()))
                s = parts[0] + s[r..];
            // 여러 호스트(host1,host2:3306)면 첫 호스트만
            var r2 = s.IndexOf("://", StringComparison.Ordinal);
            if (r2 >= 0)
            {
                int start = r2 + 3;
                int end = s.IndexOfAny(['/', '?', ';'], start);
                if (end < 0) end = s.Length;
                var comma = s.IndexOf(',', start, end - start);
                if (comma >= 0) s = s[..comma] + s[end..];
            }
        }
        return "jdbc:" + s;
    }

    private static bool TryInt(string s, out int value) =>
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    /// JDBC URL → 접속 정보 (호스트, 포트, DB, TLS)
    public static ConnectionProfile? ProfileFromJdbc(string rawUrl)
    {
        var url = Normalize(rawUrl);
        var lower = url.ToLowerInvariant();
        var p = new ConnectionProfile();
        bool Has(string prefix) => lower.StartsWith(prefix, StringComparison.Ordinal);
        if (Has("jdbc:mysql://") || Has("jdbc:mariadb://") || Has("jdbc:mysql:aws://")) p.Kind = DatabaseKind.Mysql;
        else if (Has("jdbc:postgresql://")) p.Kind = DatabaseKind.Postgres;
        else if (Has("jdbc:redshift://"))
        {
            // Amazon Redshift: PostgreSQL 프로토콜, 기본 포트 5439, 드라이버 기본이 SSL
            p.Kind = DatabaseKind.Postgres;
            p.Redshift = true;
            p.UseTLS = true;
        }
        else if (Has("jdbc:sqlserver://") || Has("jdbc:jtds:sqlserver://")) p.Kind = DatabaseKind.Mssql;
        else if (Has("jdbc:redis://") || Has("jdbc:rediss://") || Has("redis://") || Has("rediss://"))
        {
            p.Kind = DatabaseKind.Redis;
            p.UseTLS = lower.Contains("rediss://");
        }
        else return null;
        var rest = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
        p.Port = Has("jdbc:redshift://") ? 5439 : p.Kind.DefaultPort();

        // 쿼리 / 속성
        var parameters = new Dictionary<string, string>();
        void AddParams(string s, char sep)
        {
            foreach (var kv in s.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = kv.Split('=', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) parameters[parts[0].ToLowerInvariant()] = parts[1];
            }
        }
        string? Param(string key) => parameters.TryGetValue(key, out var v) ? v : null;

        if (p.Kind == DatabaseKind.Mssql)
        {
            // host[\instance][:port][;prop=value…]
            var pieces = rest.Split(';', 2, StringSplitOptions.RemoveEmptyEntries);
            rest = pieces.Length > 0 ? pieces[0] : "";
            if (pieces.Length > 1) AddParams(pieces[1], ';');
            p.Database = Param("database") ?? Param("databasename") ?? "";
        }
        else
        {
            var q = rest.IndexOf('?');
            if (q >= 0)
            {
                AddParams(rest[(q + 1)..], '&');
                rest = rest[..q];
            }
            var slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                p.Database = Unescape(rest[(slash + 1)..]);
                rest = rest[..slash];
            }
        }
        // user:pass@host (redis) — 비밀번호는 쓰지 않는다
        var at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            var u = rest[..at].Split(':', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (u.Length > 0) p.Username = u;
            rest = rest[(at + 1)..];
        }
        // 호스트[:포트] (IPv6 [::1]:port, SQL Server host\instance)
        var hostPort = rest;
        var close = hostPort.IndexOf(']');
        if (hostPort.StartsWith('[') && close >= 0)
        {
            p.Host = hostPort[1..close];
            hostPort = hostPort[(close + 1)..];
            if (hostPort.StartsWith(':') && TryInt(hostPort[1..], out var port)) p.Port = port;
        }
        else
        {
            var parts = hostPort.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);
            p.Host = parts.Length > 0 ? parts[0] : "";
            if (parts.Length > 1 && TryInt(parts[1], out var port)) p.Port = port;
        }
        if (p.Kind == DatabaseKind.Mssql && p.Host.IndexOf('\\') is var bs and >= 0) p.Host = p.Host[..bs];
        if (p.Kind == DatabaseKind.Mssql && Param("portnumber") is { } pn && TryInt(pn, out var portNumber)) p.Port = portNumber;
        if (p.Kind == DatabaseKind.Redis && p.Database.Length == 0) p.Database = Param("database") ?? "0";
        if (p.Host.Length == 0) return null;

        // TLS
        switch (p.Kind)
        {
            case DatabaseKind.Mysql:
                var v = Param("usessl") ?? (Param("sslmode") is { } mode ? (mode.ToLowerInvariant() == "disabled" ? "false" : "true") : null);
                if (v != null) p.UseTLS = v.ToLowerInvariant() != "false";
                break;
            case DatabaseKind.Postgres:
                if (Param("sslmode")?.ToLowerInvariant() is { } sslmode) p.UseTLS = sslmode != "disable";
                if (Param("ssl")?.ToLowerInvariant() is { } ssl) p.UseTLS = ssl == "true";
                break;
            case DatabaseKind.Mssql:
                if (Param("encrypt")?.ToLowerInvariant() is { } encrypt) p.UseTLS = encrypt is "true" or "strict" or "mandatory";
                break;
            case DatabaseKind.Redis:
                if (Param("ssl")?.ToLowerInvariant() is { } rssl) p.UseTLS = rssl == "true";
                break;
        }
        return p;
    }

    /// 퍼센트 인코딩 풀기. 잘못된 인코딩이면 빈 문자열 (Swift removingPercentEncoding 이 nil 일 때와 같음)
    private static string Unescape(string s)
    {
        if (!s.Contains('%')) return s;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '%') continue;
            if (i + 2 >= s.Length || !char.IsAsciiHexDigit(s[i + 1]) || !char.IsAsciiHexDigit(s[i + 2])) return "";
        }
        try { return Uri.UnescapeDataString(s); } catch { return ""; }
    }
}
