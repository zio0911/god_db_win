using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GodDB.Core.Sql;

namespace GodDB.Core.Audit;

/// 감사 로그 구분 (파일에 쓰는 한국어 값은 Mac 판과 같다 — 번역하지 않는다)
public enum AuditAction
{
    AppStart, Connect, Disconnect, Read, Insert, Update, Delete, Ddl, Grant, Execute, Export, Compare, Ai, Settings,
}

public static class AuditActionExtensions
{
    /// 파일의 "구분" 칸 값
    public static string RawValue(this AuditAction a) => a switch
    {
        AuditAction.AppStart => "앱시작",
        AuditAction.Connect => "접속",
        AuditAction.Disconnect => "접속해제",
        AuditAction.Read => "조회",
        AuditAction.Insert => "추가",
        AuditAction.Update => "수정",
        AuditAction.Delete => "삭제",
        AuditAction.Ddl => "구조변경",
        AuditAction.Grant => "권한",
        AuditAction.Execute => "실행",
        AuditAction.Export => "내보내기",
        AuditAction.Compare => "비교",
        AuditAction.Ai => "AI",
        _ => "설정",
    };
}

/// 감사 로그 한 줄. 로그 파일에는 이것이 있는 항목만 남는다 (상태창에는 모든 항목이 보인다).
/// 파일 형식(탭 구분): 시각 · 사용자@PC · 구분 · 대상 · 결과 · 내용
public sealed partial record AuditRecord(AuditAction Action, string Target = "", string Result = "", string Statement = "")
{
    /// 접속 이름과 DB (예: "dev/shop", Redis 는 "redis_dev db0")
    public string Target { get; set; } = Target;
    /// 결과 요약 (예: "1행 · 3ms", "3건", "실패: …")
    public string Result { get; set; } = Result;
    /// 실행한 SQL·명령 (한 줄로 펴서 기록)
    public string Statement { get; set; } = Statement;

    /// 파일 열 머리글
    public static readonly IReadOnlyList<string> Header = ["시각", "사용자@PC", "구분", "대상", "결과", "내용"];

    [GeneratedRegex(@"\)\s*DELETE\b")] private static partial Regex WithDelete();
    [GeneratedRegex(@"\)\s*UPDATE\b")] private static partial Regex WithUpdate();
    [GeneratedRegex(@"\)\s*INSERT\b")] private static partial Regex WithInsert();

    /// SQL 의 구분. 여러 문장이면 첫 문장 기준 (스크립트는 문장마다 따로 기록한다).
    public static AuditAction Classify(string sql)
    {
        var body = SqlText.StripComments(sql);
        switch (SqlText.FirstKeyword(body))
        {
            case "SELECT" or "SHOW" or "DESC" or "DESCRIBE" or "EXPLAIN" or "VALUES" or "TABLE":
                return AuditAction.Read;
            case "WITH":
                // WITH … INSERT/UPDATE/DELETE (PostgreSQL 등)
                var upper = body.ToUpperInvariant();
                if (WithDelete().IsMatch(upper)) return AuditAction.Delete;
                if (WithUpdate().IsMatch(upper)) return AuditAction.Update;
                if (WithInsert().IsMatch(upper)) return AuditAction.Insert;
                return AuditAction.Read;
            case "INSERT": return AuditAction.Insert;
            case "UPDATE" or "REPLACE" or "MERGE" or "UPSERT": return AuditAction.Update;
            case "DELETE" or "TRUNCATE": return AuditAction.Delete;
            case "CREATE" or "ALTER" or "DROP" or "RENAME" or "COMMENT": return AuditAction.Ddl;
            case "GRANT" or "REVOKE": return AuditAction.Grant;
            default: return AuditAction.Execute;
        }
    }

    private static readonly HashSet<string> RedisRead =
    [
        "GET", "MGET", "GETRANGE", "STRLEN", "EXISTS", "TYPE", "TTL", "PTTL", "SCAN", "KEYS", "HGET", "HMGET", "HGETALL",
        "HKEYS", "HVALS", "HLEN", "HSCAN", "HEXISTS", "LRANGE", "LLEN", "LINDEX", "SMEMBERS", "SCARD", "SISMEMBER", "SSCAN",
        "ZRANGE", "ZREVRANGE", "ZRANGEBYSCORE", "ZCARD", "ZSCORE", "ZRANK", "ZSCAN", "XRANGE", "XREVRANGE", "XLEN", "XINFO",
        "XPENDING", "XREAD", "INFO", "DBSIZE", "MEMORY", "OBJECT", "DUMP", "RANDOMKEY", "PING", "SELECT", "CLIENT", "CONFIG GET",
        "SLOWLOG", "LASTSAVE", "ROLE", "CLUSTER", "COMMAND", "PUBSUB", "SUBSCRIBE", "PSUBSCRIBE",
    ];

    private static readonly HashSet<string> RedisDelete =
    [
        "DEL", "UNLINK", "HDEL", "LREM", "LPOP", "RPOP", "SREM", "SPOP", "ZREM", "ZREMRANGEBYSCORE", "ZREMRANGEBYRANK",
        "XDEL", "XTRIM", "FLUSHDB", "FLUSHALL", "GETDEL", "PERSIST",
    ];

    private static readonly HashSet<string> RedisExecute = ["PUBLISH", "EVAL", "EVALSHA", "FUNCTION", "SCRIPT", "SAVE", "BGSAVE", "SHUTDOWN"];

    /// Redis 명령의 구분
    public static AuditAction Classify(IReadOnlyList<string> redisArgs)
    {
        if (redisArgs.Count == 0) return AuditAction.Execute;
        var first = redisArgs[0].ToUpperInvariant();
        if (first == "CONFIG" && redisArgs.Count > 1)
            return redisArgs[1].ToUpperInvariant() == "GET" ? AuditAction.Read : AuditAction.Settings;
        if (RedisRead.Contains(first)) return AuditAction.Read;
        if (RedisDelete.Contains(first)) return AuditAction.Delete;
        if (RedisExecute.Contains(first)) return AuditAction.Execute;
        return AuditAction.Update;   // SET, HSET, EXPIRE, RENAME, LPUSH, ZADD, XADD …
    }

    /// 한 칸에 들어가도록: 탭·줄바꿈을 공백으로, 연속 공백 하나로, 너무 길면 자른다.
    /// 글자 수는 Mac 판(Swift Character)처럼 사람이 보는 글자(텍스트 요소) 기준.
    public static string Cell(string s, int limit = 20_000)
    {
        var sb = new StringBuilder(Math.Min(s.Length, limit));
        int count = 0;
        bool lastSpace = false;
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            var ch = e.GetTextElement();
            if (char.IsWhiteSpace(ch[0]))
            {
                if (!lastSpace && sb.Length > 0) { sb.Append(' '); count++; }
                lastSpace = true;
            }
            else
            {
                sb.Append(ch);
                count++;
                lastSpace = false;
            }
            if (count >= limit) return sb + $" …({new StringInfo(s).LengthInTextElements}자 중 일부)";
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }
}
