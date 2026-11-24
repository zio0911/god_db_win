using System.Text;
using System.Text.RegularExpressions;
using GodDB.Core.Profiles;

namespace GodDB.Core.Sql;

/// 쿼리를 **실행하지 않고** DB 에 문법 · 이름 검사만 맡긴다 (Mac SQLServerCheck).
///   - MySQL: `PREPARE` 후 바로 `DEALLOCATE` (준비만 하고 실행 안 함)
///   - PostgreSQL: `PREPARE … AS` 후 `DEALLOCATE`
///   - SQL Server: `sp_describe_first_result_set` (컴파일만, 실행 안 함)
public static class SqlServerCheck
{
    /// 검사 결과 오류. Start/Length 는 구문 안 위치 (모르면 null)
    public sealed record Failure(string Message, int? Start = null, int? Length = null);

    internal const string Name = "goddb_syntax_check";

    /// 이 구문을 검사할 수 있는 종류 (PREPARE 가 받는 문장만)
    public static bool Checkable(string sql, DatabaseKind kind)
    {
        var t = SqlText.StripComments(sql).Trim();
        var first = new string(t.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        return kind switch
        {
            DatabaseKind.Mysql => first is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "REPLACE" or "WITH",
            DatabaseKind.Postgres => first is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "VALUES" or "WITH",
            DatabaseKind.Mssql => first is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "WITH" or "MERGE",
            _ => false,
        };
    }

    /// 검사용 SQL 들 (차례로 실행), 마지막은 정리용 (실패해도 무시)
    public static (List<string> Check, List<string> Cleanup) Commands(string sql, DatabaseKind kind)
    {
        var body = Trimmed(sql);
        switch (kind)
        {
            case DatabaseKind.Mysql:
                // 따옴표 · 역슬래시 처리 문제를 피하려고 16진수로 넘긴다
                var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(body));
                return ([$"SET @{Name} = CONVERT(X'{hex}' USING utf8mb4)", $"PREPARE {Name} FROM @{Name}"],
                        [$"DEALLOCATE PREPARE {Name}", $"SET @{Name} = NULL"]);
            case DatabaseKind.Postgres:
                return ([$"PREPARE {Name} AS " + body], [$"DEALLOCATE {Name}"]);
            case DatabaseKind.Mssql:
                return (["EXEC sp_describe_first_result_set @tsql = N'" + body.Replace("'", "''") + "'"], []);
            default:
                return ([], []);
        }
    }

    internal static string Trimmed(string sql)
    {
        var s = sql.Trim();
        while (s.EndsWith(';')) s = s[..^1].Trim();
        return s;
    }

    /// 무시할 오류 (검사 방식 자체의 한계)
    internal static bool Ignorable(string message, DatabaseKind kind)
    {
        var m = message.ToLowerInvariant();
        return kind switch
        {
            // sp_describe_first_result_set 이 임시 테이블 · 동적 SQL 등을 분석하지 못한 경우
            DatabaseKind.Mssql => m.Contains("could not be determined") || m.Contains("sp_describe_first_result_set")
                                  || m.Contains('#') || m.Contains("temp"),
            // PREPARE 가 받지 않는 문장
            DatabaseKind.Mysql => m.Contains("not supported in the prepared statement protocol"),
            DatabaseKind.Postgres => m.Contains("already exists"),
            _ => true,
        };
    }

    /// 오류를 구문 안 위치로 바꾼다. 서버 오류 메시지와 (PostgreSQL 은) 서버가 알려 준 위치(1부터, "PREPARE … AS " 포함).
    /// Windows: Mac 은 드라이버 오류 타입(MySQLNIO · PostgresNIO · FreeTDS)에서 읽고, 여기서는 MySqlConnector · Npgsql ·
    /// Microsoft.Data.SqlClient 예외에서 읽는다 (DbSession.ServerCheck.cs).
    public static Failure? FailureFrom(string message, int? pgPosition, string sql, DatabaseKind kind)
    {
        var body = Trimmed(sql);
        (int Start, int Length)? range = null;
        if (kind == DatabaseKind.Postgres && pgPosition is int p)
        {
            var prefix = $"PREPARE {Name} AS ".Length;
            var charIndex = p - 1 - prefix;
            if (charIndex >= 0 && charIndex < body.Length) range = WordRange(body, charIndex);
        }
        if (Ignorable(message, kind)) return null;
        range ??= GuessRange(message, body);
        return new Failure(message, range?.Start, range?.Length);
    }

    /// 메시지 안의 'near …' · 'column …' 이름을 구문에서 찾아 위치를 정한다.
    public static (int Start, int Length)? GuessRange(string message, string sql)
    {
        // MySQL: near 'xxx' at line N
        var near = message.IndexOf("near '", StringComparison.Ordinal);
        if (near >= 0)
        {
            var from = near + "near '".Length;
            var end = message.IndexOf("' at line", from, StringComparison.Ordinal);
            if (end >= 0)
            {
                var snippet = message[from..end];
                var probe = snippet.Length > 40 ? snippet[..40] : snippet;
                if (probe.Length > 0)
                {
                    var found = sql.IndexOf(probe, StringComparison.Ordinal);
                    if (found >= 0) return WordRange(sql, found);
                }
                else
                {
                    return (Math.Max(0, sql.Length - 1), Math.Min(1, sql.Length));  // 끝에서 끊김
                }
            }
        }
        // 따옴표 안 이름: Unknown column 'a.username', Invalid column name 'username', column "username" does not exist
        foreach (var pattern in new[] { "'([^']+)'", "\"([^\"]+)\"" })
        {
            var m = Regex.Match(message, pattern);
            if (!m.Success) continue;
            var name = m.Groups[1].Value;
            // 'a.username' → 그대로 먼저, 없으면 마지막 부분
            foreach (var candidate in new[] { name, name.Split('.').Last() })
            {
                if (candidate.Length == 0) continue;
                var found = sql.IndexOf(candidate, StringComparison.OrdinalIgnoreCase);
                if (found >= 0) return (found, candidate.Length);
            }
        }
        return null;
    }

    internal static (int Start, int Length) WordRange(string sql, int location)
    {
        if (location >= sql.Length) return (Math.Max(0, sql.Length - 1), Math.Min(1, sql.Length));
        var end = location;
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || "_$.`\"[]".Contains(sql[end]))) end++;
        return (location, Math.Max(1, end - location));
    }
}
