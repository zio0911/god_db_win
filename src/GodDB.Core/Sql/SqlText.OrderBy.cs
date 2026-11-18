using System.Text.RegularExpressions;

namespace GodDB.Core.Sql;

public static partial class SqlText
{
    [GeneratedRegex(@"\GORDER\s+BY\b")] private static partial Regex OrderByAt();

    /// 괄호 밖(맨 바깥) 마지막 ORDER BY 의 시작 위치. 없으면 null. (ORDER BY 부터 끝까지가 그 범위)
    public static int? TopLevelOrderByStart(string sql)
    {
        // ToUpperInvariant 는 글자 수를 바꾸지 않으므로 위치를 그대로 원문에 쓸 수 있다
        var upper = sql.ToUpperInvariant();
        int depth = 0;
        int? found = null;
        char? quote = null;
        for (int i = 0; i < upper.Length; i++)
        {
            char c = upper[i];
            if (quote is char q)
            {
                if (c == q) quote = null;
            }
            else if (c is '\'' or '"' or '`' or '[')
            {
                quote = c == '[' ? ']' : c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && c == 'O' && OrderByAt().Match(upper, i).Success)
            {
                char before = i == 0 ? ' ' : upper[i - 1];
                if (!(char.IsLetter(before) || char.IsNumber(before) || before == '_')) found = i;
            }
        }
        return found;
    }

    public static bool HasTopLevelOrderBy(string sql) => TopLevelOrderByStart(sql) != null;

    public static string RemovingTopLevelOrderBy(string sql)
    {
        if (TopLevelOrderByStart(sql) is not int start) return sql;
        return sql[..start].Trim();
    }

    /// Swift `.trimmingCharacters(in: .whitespaces)` — 줄바꿈은 남기고 공백·탭만 자른다
    internal static string TrimSpaces(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsSpace(s[a])) a++;
        while (b > a && IsSpace(s[b - 1])) b--;
        return s[a..b];
        static bool IsSpace(char c) => c == '\t' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.SpaceSeparator;
    }

    /// 끝의 세미콜론을 (공백과 함께) 떼고 앞뒤 공백을 자른다 (Dialect 의 페이징·정렬·건수 SQL 공통)
    internal static string TrimmedBody(string sql)
    {
        var body = sql.Trim();
        while (body.EndsWith(';')) body = body[..^1].Trim();
        return body;
    }
}
