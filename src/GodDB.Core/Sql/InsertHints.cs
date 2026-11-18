using System.Text.RegularExpressions;
using GodDB.Core.Schema;

namespace GodDB.Core.Sql;

/// INSERT … VALUES (…) 의 각 값이 어느 컬럼에 들어가는지 찾는다. (편집기에 회색 글씨로 보여주기만 하고 SQL 은 바꾸지 않는다)
public static partial class InsertHints
{
    /// <param name="Location">값이 시작하는 위치 (C# 문자열 인덱스 = Mac 의 UTF-16 오프셋)</param>
    public readonly record struct Hint(int Location, string Column);

    [GeneratedRegex(@"\bINSERT\s+(?:IGNORE\s+)?INTO\s+((?:`[^`]+`|""[^""]+""|\[[^\]]+\]|[A-Za-z0-9_$#@]+)(?:\s*\.\s*(?:`[^`]+`|""[^""]+""|\[[^\]]+\]|[A-Za-z0-9_$#@]+))*)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex InsertRegex();

    /// <param name="tables">컬럼 목록을 쓰지 않은 INSERT 에서 테이블 컬럼 순서를 알기 위해</param>
    public static List<Hint> Hints(string text, IReadOnlyList<TableMeta> tables, int limit = 2000)
    {
        var s = text;
        var output = new List<Hint>();
        if (s.Length >= 200_000) return output;
        var masked = MaskCommentsAndStrings(s);
        foreach (Match m in InsertRegex().Matches(masked))
        {
            var g = m.Groups[1];
            var tableName = Unquote(s.Substring(g.Index, g.Length));
            var i = m.Index + m.Length;
            List<string>? columns = null;
            // (컬럼 목록)
            if (i < s.Length && s[i] == '(')
            {
                var (items, end) = Tuple(s, i);
                columns = items.Select(r => Unquote(s.Substring(r.Start, r.Length).Trim())).ToList();
                i = end;
            }
            i = SkipSpace(s, i);
            if (!(i + 6 <= s.Length && s.Substring(i, 6).ToUpperInvariant() == "VALUES")) continue;
            i = SkipSpace(s, i + 6);
            var names = columns ?? Table(tableName, tables)?.Columns.OrderBy(c => c.Position).Select(c => c.Name).ToList();
            if (names == null || names.Count == 0) continue;
            // (값, …), (값, …)
            while (i < s.Length && s[i] == '(')
            {
                var (items, end) = Tuple(s, i);
                for (var k = 0; k < items.Count && k < names.Count; k++)
                {
                    var r = items[k];
                    var start = SkipSpace(s, r.Start);
                    if (start < r.Start + r.Length) output.Add(new Hint(start, names[k]));
                }
                if (output.Count >= limit) return output;
                i = SkipSpace(s, end);
                if (!(i < s.Length && s[i] == ',')) break;
                i = SkipSpace(s, i + 1);
            }
        }
        return output;
    }

    /// 편집기에 그릴 수 있는 힌트만 (Mac: HintTextView.applyHints).
    /// 바로 앞 글자가 줄바꿈이면 그 줄 첫 글자라 간격을 벌릴 수 없어 표시하지 않는다.
    public static List<Hint> Displayable(string text, IReadOnlyList<TableMeta> tables) =>
        Hints(text, tables).Where(h => h.Location > 0 && text[h.Location - 1] != '\n').ToList();

    internal static TableMeta? Table(string name, IReadOnlyList<TableMeta> tables)
    {
        var lower = name.ToLowerInvariant();
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var last = parts.Length > 0 ? parts[^1].ToLowerInvariant() : lower;
        return tables.FirstOrDefault(t => t.Name.ToLowerInvariant() == lower)
            ?? tables.FirstOrDefault(t => t.Name.ToLowerInvariant() == last)
            ?? tables.FirstOrDefault(t => t.Name.ToLowerInvariant().EndsWith("." + last, StringComparison.Ordinal));
    }

    private static readonly char[] QuoteChars = ['`', '"', '[', ']'];

    internal static string Unquote(string s) =>
        string.Join(".", s.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim(' ', '\t').Trim(QuoteChars)));

    internal static int SkipSpace(string s, int i)
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (c is ' ' or '\t' or '\n' or '\r') { i++; continue; }
            // -- 주석
            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var r = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = r < 0 ? s.Length : r + 2;
                continue;
            }
            break;
        }
        return i;
    }

    /// i 가 "(" 일 때 최상위 쉼표로 나눈 항목 범위들과 ")" 다음 위치. 닫히지 않았으면 끝까지 (입력 중).
    internal static (List<(int Start, int Length)> Items, int End) Tuple(string s, int start)
    {
        var depth = 0;
        var i = start + 1;
        var itemStart = i;
        var items = new List<(int, int)>();
        while (i < s.Length)
        {
            var c = s[i];
            switch (c)
            {
                case '\'' or '"' or '`':
                    i = SkipQuoted(s, i, c);
                    continue;
                case '(': depth++; break;
                case ')':
                    if (depth == 0)
                    {
                        items.Add((itemStart, i - itemStart));
                        return (items, i + 1);
                    }
                    depth--;
                    break;
                case ',' when depth == 0:
                    items.Add((itemStart, i - itemStart));
                    itemStart = i + 1;
                    break;
                case ';' when depth == 0:   // ; 이면 구문이 끝남 (괄호가 안 닫힘)
                    items.Add((itemStart, i - itemStart));
                    return (items, i);
            }
            i++;
        }
        items.Add((itemStart, i - itemStart));
        return (items, i);
    }

    internal static int SkipQuoted(string s, int start, char quote)
    {
        var i = start + 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && quote != '`') { i += 2; continue; }   // \ 이스케이프
            if (c == quote)
            {
                if (i + 1 < s.Length && s[i + 1] == quote) { i += 2; continue; }   // '' 이스케이프
                return i + 1;
            }
            i++;
        }
        return i;
    }

    /// 주석과 문자열 안의 INSERT 는 찾지 않도록 같은 길이의 공백으로 가린다.
    internal static string MaskCommentsAndStrings(string s)
    {
        var chars = s.ToCharArray();
        var i = 0;
        void Blank(int a, int b)
        {
            for (var k = a; k < Math.Min(b, chars.Length); k++) if (chars[k] != '\n') chars[k] = ' ';
        }
        while (i < chars.Length)
        {
            var c = chars[i];
            if (c is '\'' or '"')
            {
                var end = SkipQuoted(s, i, c);
                Blank(i, end); i = end; continue;
            }
            if (c == '-' && i + 1 < chars.Length && chars[i + 1] == '-')
            {
                var e = i;
                while (e < chars.Length && chars[e] != '\n') e++;
                Blank(i, e); i = e; continue;
            }
            if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                var r = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var e = r < 0 ? chars.Length : r + 2;
                Blank(i, e); i = e; continue;
            }
            i++;
        }
        return new string(chars);
    }
}
