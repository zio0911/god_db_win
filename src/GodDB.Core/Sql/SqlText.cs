using System.Text;
using System.Text.RegularExpressions;

namespace GodDB.Core.Sql;

/// 원문 안의 구문 하나 (Start..End 는 원문 문자 위치, End 는 포함하지 않음)
public readonly record struct SqlStatement(string Sql, int Start, int End)
{
    public bool Contains(int cursor) => Start <= cursor && cursor <= End;
}

/// 되돌리기 어려운 문장 종류 (실행 전에 한 번 더 확인한다)
public enum SqlDanger { DeleteAll, UpdateAll, Truncate, Drop }

/// SQL 문자열 처리 유틸 (구문 분리, 읽기전용 검사, LIMIT 자동 부여). 읽기전용 검사는 SqlText.ReadOnlyGuard.cs
public static partial class SqlText
{
    [GeneratedRegex(@"^[ \t]*GO([ \t]+\d+)?[ \t]*;?[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GoLineAnywhere();

    [GeneratedRegex(@"^GO([ \t]+\d+)?[ \t]*;?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    /// 세미콜론 기준으로 구문을 나눈다. 따옴표·백틱·주석 안의 세미콜론은 무시한다.
    /// - MySQL `DELIMITER $$` 줄을 만나면 그 뒤로는 그 구분자로 나눈다 (DELIMITER 줄은 실행하지 않음)
    /// - SQL Server `GO` 줄이 하나라도 있으면 GO 로만 나눈다 (배치 안의 세미콜론은 나누지 않음 — 프로시저 본문 보호)
    public static List<SqlStatement> Statements(string text)
    {
        var result = new List<SqlStatement>();
        int n = text.Length, start = 0, i = 0;
        char? quote = null;
        string delimiter = ";";
        bool goMode = GoLineAnywhere().IsMatch(text);

        (string line, int next) LineAt(int at)
        {
            int j = at;
            while (j < n && text[j] != '\n') j++;
            return (text[at..j], Math.Min(n, j + 1));
        }

        void Flush(int end)
        {
            var s = text[start..end];
            if (!string.IsNullOrWhiteSpace(StripComments(s)))
                result.Add(new SqlStatement(s.Trim(), start, end));
        }

        while (i < n)
        {
            char c = text[i];
            // 줄 맨 앞: DELIMITER / GO 줄인지
            if (quote == null && (i == 0 || text[i - 1] == '\n'))
            {
                var (l, next) = LineAt(i);
                var trimmed = l.Trim(' ', '\t', '\r');
                var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (!goMode && parts.Length == 2 && parts[0].Equals("DELIMITER", StringComparison.OrdinalIgnoreCase))
                {
                    Flush(i);
                    delimiter = parts[1];
                    start = next; i = next;
                    continue;
                }
                if (goMode && GoLine().IsMatch(trimmed))
                {
                    Flush(i);
                    start = next; i = next;
                    continue;
                }
            }
            if (quote is char q)
            {
                if (c == '\\' && q != '`') { i += 2; continue; }
                if (c == q) quote = null;
                i++;
                continue;
            }
            if (c is '\'' or '"' or '`') { quote = c; i++; continue; }
            // 바꾼 구분자 ($$, // 등) — 달러 인용보다 먼저 본다
            if (!goMode && delimiter != ";" && string.CompareOrdinal(text, i, delimiter, 0, delimiter.Length) == 0 && i + delimiter.Length <= n)
            {
                Flush(i);
                i += delimiter.Length;
                start = i;
                continue;
            }
            // PostgreSQL 달러 인용 ($$ … $$, $tag$ … $tag$) 안의 세미콜론은 무시
            if (c == '$' && DollarTagEnd(text, i) is int tagEnd)
            {
                var tag = text[i..(tagEnd + 1)];
                int close = text.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
                i = close >= 0 ? close + tag.Length : n;
                continue;
            }
            if (c == '#' || (c == '-' && i + 1 < n && text[i + 1] == '-'))
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i += 2;
                continue;
            }
            if (c == ';' && delimiter == ";" && !goMode)
            {
                Flush(i);
                start = i + 1;
            }
            i++;
        }
        if (start < n) Flush(n);
        return result;
    }

    /// `$tag$` 의 끝 `$` 위치. 달러 인용 시작이 아니면 null. (앞 글자가 식별자 문자면 $1 같은 파라미터로 본다)
    internal static int? DollarTagEnd(string s, int i)
    {
        if (i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_')) return null;
        for (int j = i + 1; j < s.Length; j++)
        {
            char c = s[j];
            if (c == '$') return j;
            if (!(char.IsLetter(c) || c == '_' || (j > i + 1 && char.IsDigit(c)))) return null;
        }
        return null;
    }

    /// 커서 위치가 속한 구문. 구문 사이 공백에 있으면 바로 앞 구문을 고른다.
    public static SqlStatement? StatementAt(string text, int cursor)
    {
        var all = Statements(text);
        foreach (var s in all) if (s.Contains(cursor)) return s;
        SqlStatement? before = null;
        foreach (var s in all) if (s.End <= cursor) before = s;
        return before ?? (all.Count > 0 ? all[0] : null);
    }

    public static string? Statement(string text, int cursor) => StatementAt(text, cursor)?.Sql;

    [GeneratedRegex(@"/\*[\s\S]*?\*/")] private static partial Regex BlockComment();
    [GeneratedRegex(@"(--|#)[^\n]*")] private static partial Regex LineComment();

    /// 주석 제거 (따옴표 안은 단순화를 위해 고려하지 않음 — 키워드 판별용)
    public static string StripComments(string sql) => LineComment().Replace(BlockComment().Replace(sql, " "), " ");

    public static string FirstKeyword(string sql)
    {
        var cleaned = StripComments(sql).Trim();
        int k = 0;
        while (k < cleaned.Length && char.IsLetter(cleaned[k])) k++;
        return cleaned[..k].ToUpperInvariant();
    }

    /// WHERE 없는 DELETE / UPDATE, TRUNCATE, DROP 이면 그 종류를 돌려준다.
    /// 주석 · 문자열 · 괄호(서브쿼리) 안은 보지 않는다.
    public static SqlDanger? Danger(string sql) => FirstKeyword(sql) switch
    {
        "TRUNCATE" => SqlDanger.Truncate,
        "DROP" => SqlDanger.Drop,
        "DELETE" => HasTopLevelWhere(sql) ? null : SqlDanger.DeleteAll,
        "UPDATE" => HasTopLevelWhere(sql) ? null : SqlDanger.UpdateAll,
        _ => null,
    };

    private static readonly Regex[] LiteralPatterns =
    [
        new(@"'(?:[^'\\]|\\.|'')*'"), new("\"(?:[^\"]|\"\")*\""), new("`[^`]*`"), new(@"\[[^\]]*\]"),
    ];
    [GeneratedRegex(@"\([^()]*\)")] private static partial Regex InnerParens();
    [GeneratedRegex(@"\bWHERE\b", RegexOptions.IgnoreCase)] private static partial Regex WhereWord();

    /// 문자열 · 따옴표 식별자를 지우고 괄호 안을 지운 SQL (맨 바깥 수준만 남김)
    internal static string TopLevel(string sql)
    {
        var s = StripComments(sql);
        foreach (var p in LiteralPatterns) s = p.Replace(s, " ");
        while (true)
        {
            var next = InnerParens().Replace(s, " ");
            if (next == s) break;
            s = next;
        }
        return s;
    }

    internal static bool HasTopLevelWhere(string sql) => WhereWord().IsMatch(TopLevel(sql));

    [GeneratedRegex(@"\bLIMIT\s+\d")] private static partial Regex LimitNumber();
    [GeneratedRegex(@"\bFOR\s+UPDATE\b|\bINTO\b")] private static partial Regex ForUpdateOrInto();

    /// LIMIT 이 없는 SELECT 에 LIMIT 을 붙인다.
    public static string ApplyingLimit(string sql, int limit)
    {
        if (limit <= 0 || FirstKeyword(sql) != "SELECT") return sql;
        var upper = StripComments(sql).ToUpperInvariant();
        if (LimitNumber().IsMatch(upper) || ForUpdateOrInto().IsMatch(upper)) return sql;
        return TrimTrailingSemicolons(sql) + "\nLIMIT " + limit;
    }

    public static string TrimTrailingSemicolons(string sql)
    {
        var t = sql.Trim();
        while (t.EndsWith(';')) t = t[..^1].TrimEnd();
        return t;
    }

    public static string QuoteIdent(string name) => "`" + name.Replace("`", "``") + "`";

    public static string QuoteString(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";
}
