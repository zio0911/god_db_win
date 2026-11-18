namespace GodDB.App.Controls;

public enum SqlTokenKind { Keyword, Number, Identifier, String, Comment }

public readonly record struct SqlToken(int Start, int Length, SqlTokenKind Kind)
{
    public int End => Start + Length;
}

/// SQL 편집기 색칠 (Mac SQLHighlighter). Mac 은 정규식 다섯 개를 차례로 칠해 뒤의 것이 덮었다
/// (키워드 → 숫자 → 백틱 → 문자열 → 주석). 여기서는 한 번 훑는 낱말 분석으로 같은 결과를 낸다
/// — 주석·문자열 안의 키워드는 칠하지 않고, 겹치는 범위가 없다.
/// DB 별 차이: MySQL 은 `#` 주석 · "…" 문자열 · `…` 식별자, PostgreSQL 은 "…" 식별자 · $$…$$ 문자열,
/// SQL Server 는 "…" · […] 식별자 (Mac 은 백틱만 칠했다 — Windows 판은 DB 별 식별자 따옴표도 칠한다).
public static class SqlHighlighter
{
    /// 아주 큰 텍스트는 하이라이트 생략 (Mac 과 같음)
    public const int MaxLength = 300_000;

    private static readonly string[] Common =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "AS", "ON", "JOIN", "LEFT", "RIGHT",
        "INNER", "OUTER", "CROSS", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET", "UNION", "ALL", "DISTINCT",
        "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "CREATE", "ALTER", "DROP", "TABLE", "INDEX",
        "VIEW", "DATABASE", "SCHEMA", "PRIMARY", "KEY", "UNIQUE", "FOREIGN", "REFERENCES", "DEFAULT", "COLUMN",
        "ADD", "MODIFY", "CHANGE", "RENAME", "TRUNCATE", "SHOW", "DESC", "DESCRIBE", "EXPLAIN", "USE", "WITH",
        "CASE", "WHEN", "THEN", "ELSE", "END", "LIKE", "BETWEEN", "EXISTS", "ASC", "COUNT", "SUM", "AVG", "MIN",
        "MAX", "IFNULL", "COALESCE", "NOW", "TRUE", "FALSE", "AFTER", "FIRST", "COMMENT", "AUTO_INCREMENT",
        "BEGIN", "COMMIT", "ROLLBACK", "TRANSACTION", "CALL", "PROCEDURE", "FUNCTION", "TRIGGER", "RETURNS", "RETURN",
        "IF", "FOR", "CAST", "CONSTRAINT", "CHECK", "GRANT", "REVOKE", "USING", "NATURAL", "FULL", "OVER", "PARTITION",
    ];

    private static readonly string[] MySql = ["REPLACE", "IGNORE", "STRAIGHT_JOIN", "ENGINE", "CHARSET", "UNSIGNED", "ZEROFILL", "DUAL", "REGEXP"];
    private static readonly string[] Postgres = ["ILIKE", "RETURNING", "SERIAL", "BIGSERIAL", "CONFLICT", "NOTHING", "INTERVAL", "LATERAL", "FETCH", "ONLY", "ROWS", "ROW", "SEARCH_PATH", "LANGUAGE", "DO"];
    private static readonly string[] Mssql = ["TOP", "EXEC", "EXECUTE", "GO", "IDENTITY", "NOLOCK", "FETCH", "NEXT", "ROWS", "ONLY", "OUTPUT", "MERGE", "DECLARE", "ISNULL", "GETDATE", "TRAN", "PRINT", "NVARCHAR"];

    private static readonly Dictionary<DatabaseKind, HashSet<string>> KeywordSets = new();

    public static HashSet<string> Keywords(DatabaseKind kind)
    {
        lock (KeywordSets)
        {
            if (KeywordSets.TryGetValue(kind, out var set)) return set;
            var extra = kind switch
            {
                DatabaseKind.Postgres => Postgres,
                DatabaseKind.Mssql => Mssql,
                _ => MySql,
            };
            set = new HashSet<string>(Common.Concat(extra), StringComparer.OrdinalIgnoreCase);
            KeywordSets[kind] = set;
            return set;
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    /// 색칠할 범위 (시작 순서, 겹치지 않음)
    public static List<SqlToken> Tokens(string text, DatabaseKind kind = DatabaseKind.Mysql)
    {
        var tokens = new List<SqlToken>();
        if (text.Length >= MaxLength) return tokens;
        var keywords = Keywords(kind);
        var n = text.Length;
        var i = 0;
        while (i < n)
        {
            var c = text[i];
            var next = i + 1 < n ? text[i + 1] : '\0';
            // 줄 주석: -- (모든 DB), # (MySQL)
            if ((c == '-' && next == '-') || (c == '#' && kind == DatabaseKind.Mysql))
            {
                var end = text.IndexOf('\n', i);
                if (end < 0) end = n;
                tokens.Add(new SqlToken(i, end - i, SqlTokenKind.Comment));
                i = end;
                continue;
            }
            if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                tokens.Add(new SqlToken(i, end - i, SqlTokenKind.Comment));
                i = end;
                continue;
            }
            if (c == '\'' || (c == '"' && kind == DatabaseKind.Mysql))
            {
                var end = QuotedEnd(text, i, c, backslash: true);
                tokens.Add(new SqlToken(i, end - i, SqlTokenKind.String));
                i = end;
                continue;
            }
            if (c == '"' || c == '`')
            {
                var end = QuotedEnd(text, i, c, backslash: false);
                tokens.Add(new SqlToken(i, end - i, SqlTokenKind.Identifier));
                i = end;
                continue;
            }
            if (c == '[' && kind == DatabaseKind.Mssql)
            {
                var end = text.IndexOf(']', i + 1);
                var nl = text.IndexOf('\n', i + 1);
                if (end < 0 || (nl >= 0 && nl < end)) { i++; continue; }   // 닫히지 않은 [ 는 그냥 둔다
                tokens.Add(new SqlToken(i, end + 1 - i, SqlTokenKind.Identifier));
                i = end + 1;
                continue;
            }
            // PostgreSQL $$ … $$ / $tag$ … $tag$
            if (c == '$' && kind == DatabaseKind.Postgres && (i == 0 || !IsWordChar(text[i - 1])))
            {
                var close = text.IndexOf('$', i + 1);
                if (close > i)
                {
                    var tag = text.Substring(i, close - i + 1);
                    if (tag.Skip(1).Take(tag.Length - 2).All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                    {
                        var end = text.IndexOf(tag, close + 1, StringComparison.Ordinal);
                        end = end < 0 ? n : end + tag.Length;
                        tokens.Add(new SqlToken(i, end - i, SqlTokenKind.String));
                        i = end;
                        continue;
                    }
                }
            }
            if (char.IsAsciiDigit(c) && (i == 0 || !IsWordChar(text[i - 1])))
            {
                var j = i;
                while (j < n && char.IsAsciiDigit(text[j])) j++;
                if (j + 1 < n && text[j] == '.' && char.IsAsciiDigit(text[j + 1]))
                {
                    j++;
                    while (j < n && char.IsAsciiDigit(text[j])) j++;
                }
                // 123abc 처럼 낱말 중간이면 숫자가 아니다 (Mac \b…\b)
                if (j >= n || !IsWordChar(text[j])) tokens.Add(new SqlToken(i, j - i, SqlTokenKind.Number));
                else while (j < n && IsWordChar(text[j])) j++;
                i = j;
                continue;
            }
            if (IsWordChar(c))
            {
                var j = i;
                while (j < n && IsWordChar(text[j])) j++;
                if (keywords.Contains(text.Substring(i, j - i))) tokens.Add(new SqlToken(i, j - i, SqlTokenKind.Keyword));
                i = j;
                continue;
            }
            i++;
        }
        return tokens;
    }

    /// 따옴표로 둘러싼 범위의 끝 (닫는 따옴표 다음). 두 번 쓴 따옴표('')는 글자, backslash 면 \ 이스케이프도.
    private static int QuotedEnd(string text, int start, char quote, bool backslash)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (backslash && c == '\\') { i += 2; continue; }
            if (c == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return text.Length;
    }
}
