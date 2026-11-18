using System.Globalization;
using System.Text;

namespace GodDB.Core.Sql;

/// 편집기에 적용할 바꾸기: [Start, Start+Length) 를 Replacement 로 바꾸고 [SelectionStart, +SelectionLength) 를 선택한다.
/// 위치는 C# 문자열 인덱스 (Mac 의 NSRange UTF-16 오프셋과 같음).
public readonly record struct TextEdit(int Start, int Length, string Replacement, int SelectionStart, int SelectionLength);

/// 편집기 도구: 줄 주석 토글, SQL 정렬
public static class SqlEditing
{
    // MARK: 주석 토글 (Ctrl+/)

    private static bool IsLineBreak(char c) => c == '\n' || c == '\r';

    /// NSString.lineRange(for:) — 범위가 걸친 줄 전체 (끝 줄바꿈 포함)
    internal static (int Start, int Length) LineRange(string s, int location, int length)
    {
        location = Math.Clamp(location, 0, s.Length);
        var start = location;
        while (start > 0 && !IsLineBreak(s[start - 1])) start--;
        var last = length > 0 ? Math.Min(s.Length, location + length) - 1 : location;
        var end = Math.Max(last, location);
        if (length > 0 && last >= location && last < s.Length && IsLineBreak(s[last]))
        {
            // 마지막 글자가 줄바꿈이면 그 줄에서 끝 (\r\n 은 한 번에)
            end = last + 1;
            if (s[last] == '\r' && end < s.Length && s[end] == '\n') end++;
            return (start, end - start);
        }
        while (end < s.Length && !IsLineBreak(s[end])) end++;
        if (end < s.Length)
        {
            if (s[end] == '\r' && end + 1 < s.Length && s[end + 1] == '\n') end += 2;
            else end++;
        }
        return (start, end - start);
    }

    /// Mac 의 .whitespaces (줄바꿈 제외 공백)
    private static bool IsBlank(string line) => line.All(c => char.IsWhiteSpace(c) && c != '\n' && c != '\r');

    private static string TrimLeadingWhitespaces(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i]) && line[i] != '\n' && line[i] != '\r') i++;
        return line[i..];
    }

    /// 선택이 걸친 줄 전체를 `-- ` 주석으로 감싸거나 푼다. 모든 내용 줄이 주석이면 풀고, 아니면 단다.
    /// 돌려주는 값: 바꿀 범위와 새 글자, 바꾼 뒤 선택할 범위
    public static TextEdit ToggleLineComment(string text, int selectionStart, int selectionLength)
    {
        var lineRange = LineRange(text, Math.Min(selectionStart, text.Length), 0);
        if (selectionLength > 0)
        {
            // 선택 끝이 줄 맨 앞이면 그 줄은 넣지 않는다
            var end = selectionStart + selectionLength;
            if (end > selectionStart && end <= text.Length && end > 0 && text[end - 1] == '\n') end--;
            lineRange = LineRange(text, selectionStart, Math.Max(0, end - selectionStart));
        }
        var block = text.Substring(lineRange.Start, lineRange.Length);
        var lines = block.Split('\n').ToList();
        var trailingNewline = block.EndsWith('\n');
        if (trailingNewline) lines.RemoveAt(lines.Count - 1);
        var content = lines.Where(l => !IsBlank(l)).ToList();
        var allCommented = content.Count > 0 && content.All(l => TrimLeadingWhitespaces(l).StartsWith("--", StringComparison.Ordinal));
        var indent = content.Count == 0 ? 0 : content.Min(l => l.TakeWhile(c => c == ' ' || c == '\t').Count());
        var changed = lines.Select(line =>
        {
            if (IsBlank(line)) return line;
            if (allCommented)
            {
                var r = line.IndexOf("--", StringComparison.Ordinal);
                if (r < 0) return line;
                var after = line[(r + 2)..];
                if (after.StartsWith(' ')) after = after[1..];
                return line[..r] + after;
            }
            return line[..indent] + "-- " + line[indent..];
        });
        var replacement = string.Join("\n", changed) + (trailingNewline ? "\n" : "");
        return new TextEdit(lineRange.Start, lineRange.Length, replacement,
                            lineRange.Start, replacement.Length - (trailingNewline ? 1 : 0));
    }

    // MARK: SQL 정렬 (Ctrl+Alt+L)

    internal abstract record Token
    {
        public sealed record Word(string S) : Token;
        public sealed record Quoted(string S) : Token;
        public sealed record Comment(string S) : Token;
        public sealed record Symbol(string S) : Token;
        public sealed record Space : Token;
        public sealed record Newline : Token;
    }

    /// Swift Character 처럼 글자(텍스트 요소) 단위로 나눈다 (서로게이트 쌍 · 결합 문자를 쪼개지 않도록)
    private static List<string> Characters(string s)
    {
        var list = new List<string>(s.Length);
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }

    private static Rune FirstRune(string ch) => Rune.GetRuneAt(ch, 0);
    private static bool IsWhitespace(string ch) => ch.All(char.IsWhiteSpace);

    /// 문자열 · 따옴표 식별자 · 주석은 그대로 두고 나눈다
    internal static List<Token> Tokenize(string sql)
    {
        var c = Characters(sql);
        var output = new List<Token>();
        var i = 0;
        static bool IsWordChar(string ch)
        {
            var r = FirstRune(ch);
            return Rune.IsLetter(r) || Rune.IsNumber(r) || ch is "_" or "$" or "@" or ".";
        }
        string Join(int a, int b) => string.Concat(c.Skip(a).Take(b - a));
        while (i < c.Count)
        {
            var ch = c[i];
            if (ch == "\n") { output.Add(new Token.Newline()); i++; continue; }
            if (IsWhitespace(ch))
            {
                while (i < c.Count && IsWhitespace(c[i]) && c[i] != "\n") i++;
                output.Add(new Token.Space());
                continue;
            }
            if ((ch == "-" && i + 1 < c.Count && c[i + 1] == "-") || ch == "#")
            {
                var j = i;
                while (j < c.Count && c[j] != "\n") j++;
                output.Add(new Token.Comment(Join(i, j))); i = j; continue;
            }
            if (ch == "/" && i + 1 < c.Count && c[i + 1] == "*")
            {
                var j = i + 2;
                while (j + 1 < c.Count && !(c[j] == "*" && c[j + 1] == "/")) j++;
                j = Math.Min(c.Count, j + 2);
                output.Add(new Token.Comment(Join(i, j))); i = j; continue;
            }
            if (ch is "'" or "\"" or "`" or "[")
            {
                var close = ch == "[" ? "]" : ch;
                var j = i + 1;
                while (j < c.Count)
                {
                    if (c[j] == "\\" && ch == "'") { j += 2; continue; }
                    if (c[j] == close)
                    {
                        if (j + 1 < c.Count && c[j + 1] == close && ch != "[") { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                j = Math.Min(c.Count, j + 1);
                output.Add(new Token.Quoted(Join(i, j))); i = j; continue;
            }
            if (IsWordChar(ch))
            {
                var j = i;
                while (j < c.Count && IsWordChar(c[j])) j++;
                output.Add(new Token.Word(Join(i, j))); i = j; continue;
            }
            output.Add(new Token.Symbol(ch)); i++;
        }
        return output;
    }

    /// 줄을 바꿔 시작하는 절
    internal static readonly string[][] ClauseStarts =
    [
        ["SELECT"], ["FROM"], ["WHERE"], ["GROUP", "BY"], ["ORDER", "BY"], ["HAVING"], ["LIMIT"], ["OFFSET"],
        ["UNION", "ALL"], ["UNION"], ["EXCEPT"], ["INTERSECT"],
        ["LEFT", "OUTER", "JOIN"], ["RIGHT", "OUTER", "JOIN"], ["FULL", "OUTER", "JOIN"],
        ["LEFT", "JOIN"], ["RIGHT", "JOIN"], ["INNER", "JOIN"], ["CROSS", "JOIN"], ["FULL", "JOIN"], ["JOIN"],
        ["INSERT", "INTO"], ["VALUES"], ["UPDATE"], ["SET"], ["DELETE", "FROM"], ["ON", "DUPLICATE", "KEY", "UPDATE"],
        ["RETURNING"], ["WITH"],
    ];

    internal static readonly HashSet<string> FormatKeywords =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "AS", "ON", "JOIN", "LEFT", "RIGHT", "FULL",
        "INNER", "OUTER", "CROSS", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET", "UNION", "ALL", "DISTINCT",
        "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "CASE", "WHEN", "THEN", "ELSE", "END", "LIKE",
        "BETWEEN", "EXISTS", "ASC", "DESC", "WITH", "RETURNING", "DUPLICATE", "KEY", "EXCEPT", "INTERSECT", "TOP",
        "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "IFNULL", "TRUE", "FALSE",
    ];

    private static bool EndsWith(StringBuilder sb, char c) => sb.Length > 0 && sb[^1] == c;

    private static void TrimTrailingSpaces(StringBuilder sb)
    {
        while (EndsWith(sb, ' ')) sb.Length--;
    }

    /// 절마다 줄을 바꾸고 AND / OR 는 들여 쓰며 키워드는 대문자로. 괄호 안(서브쿼리)은 한 단계 더 들여 쓴다.
    /// 문자열 · 주석 · 따옴표 식별자 글자는 바꾸지 않는다. 결과를 다시 정렬해도 같다.
    public static string Format(string sql, string indent = "    ")
    {
        var tokens = Tokenize(sql);
        var output = new StringBuilder();
        var depth = 0;
        var i = 0;
        var pendingSpace = false;
        var lineStart = true;
        void NewLine(int extra = 0)
        {
            TrimTrailingSpaces(output);
            if (output.Length > 0 && !EndsWith(output, '\n')) output.Append('\n');
            for (var k = 0; k < Math.Max(0, depth + extra); k++) output.Append(indent);
            lineStart = true;
            pendingSpace = false;
        }
        void Emit(string s)
        {
            if (pendingSpace && !lineStart) output.Append(' ');
            output.Append(s);
            pendingSpace = false;
            lineStart = false;
        }
        List<string> WordsAhead(int n)
        {
            var w = new List<string>();
            var j = i;
            while (j < tokens.Count && w.Count < n)
            {
                switch (tokens[j])
                {
                    case Token.Word word: w.Add(word.S.ToUpperInvariant()); break;
                    case Token.Space or Token.Newline: break;
                    default: return w;
                }
                j++;
            }
            return w;
        }
        while (i < tokens.Count)
        {
            switch (tokens[i])
            {
                case Token.Space or Token.Newline:
                    pendingSpace = true;
                    i++;
                    break;
                case Token.Comment cm:
                    Emit(cm.S);
                    if (cm.S.StartsWith("--", StringComparison.Ordinal) || cm.S.StartsWith('#')) NewLine();
                    i++;
                    break;
                case Token.Quoted q:
                    Emit(q.S); i++;
                    break;
                case Token.Symbol sym:
                {
                    var s = sym.S;
                    if (s == "(")
                    {
                        // 함수 호출 count(*) 처럼 원문에서 단어에 붙어 있던 괄호는 붙여 쓴다
                        if (i > 0 && tokens[i - 1] is Token.Word) pendingSpace = false;
                        Emit(s); depth++; pendingSpace = false;
                        // 괄호 바로 안이 SELECT 면 서브쿼리: 줄을 바꾼다
                        if (WordsAheadAfter(i + 1, tokens).FirstOrDefault() == "SELECT") NewLine();
                    }
                    else if (s == ")")
                    {
                        depth = Math.Max(0, depth - 1);
                        TrimTrailingSpaces(output);
                        pendingSpace = false;
                        output.Append(s); lineStart = false;
                    }
                    else if (s == ",")
                    {
                        TrimTrailingSpaces(output);
                        output.Append(s); pendingSpace = true; lineStart = false;
                    }
                    else if (s == ";")
                    {
                        TrimTrailingSpaces(output);
                        output.Append(s);
                        depth = 0;
                        output.Append('\n');
                        lineStart = true; pendingSpace = false;
                    }
                    else if (s == ".")
                    {
                        TrimTrailingSpaces(output);
                        output.Append(s); pendingSpace = false; lineStart = false;
                    }
                    else
                    {
                        Emit(s); pendingSpace = true;
                    }
                    i++;
                    break;
                }
                case Token.Word wd:
                {
                    var upper = wd.S.ToUpperInvariant();
                    var clause = ClauseStarts.FirstOrDefault(cl => WordsAhead(cl.Length).SequenceEqual(cl));
                    if (clause != null)
                    {
                        if (!(output.Length == 0 || lineStart)) NewLine();
                        else if (output.Length == 0) lineStart = true;
                        // 절 단어들을 한꺼번에 쓴다
                        var written = 0;
                        while (written < clause.Length && i < tokens.Count)
                        {
                            if (tokens[i] is Token.Word) { Emit(clause[written]); pendingSpace = true; written++; }
                            i++;
                        }
                        break;
                    }
                    if ((upper == "AND" || upper == "OR") && !lineStart && !InsideBetween(output.ToString()))
                        NewLine(1);
                    Emit(FormatKeywords.Contains(upper) ? upper : wd.S);
                    pendingSpace = true;
                    i++;
                    break;
                }
            }
        }
        while (EndsWith(output, ' ') || EndsWith(output, '\n')) output.Length--;
        return output.ToString();
    }

    internal static List<string> WordsAheadAfter(int start, List<Token> tokens)
    {
        var j = start;
        while (j < tokens.Count)
        {
            switch (tokens[j])
            {
                case Token.Word w: return [w.S.ToUpperInvariant()];
                case Token.Space or Token.Newline: j++; break;
                default: return [];
            }
        }
        return [];
    }

    /// BETWEEN a AND b 의 AND 는 줄을 바꾸지 않는다
    internal static bool InsideBetween(string output)
    {
        var tail = (output.Length > 80 ? output[^80..] : output).ToUpperInvariant();
        var b = tail.LastIndexOf("BETWEEN", StringComparison.Ordinal);
        if (b < 0) return false;
        return !tail[(b + "BETWEEN".Length)..].Contains(" AND ", StringComparison.Ordinal);
    }

    // MARK: 편집기 쪽 로직 (Mac: SQLEditorView.swift 의 HintTextView)

    /// 선택 영역이 있으면 그 부분, 없으면 커서가 있는 문장을 정렬한다. 바꿀 것이 없으면 null.
    public static TextEdit? FormatSelectionOrStatement(string text, int selectionStart, int selectionLength)
    {
        int start = selectionStart, length = selectionLength;
        if (length == 0)
        {
            var stmt = SqlText.Statements(text).Cast<SqlStatement?>().FirstOrDefault(s => s!.Value.Contains(start));
            if (stmt is not { } st) return null;
            start = st.Start;
            length = st.End - st.Start;
        }
        var original = text.Substring(start, length);
        var lead = new string(original.TakeWhile(char.IsWhiteSpace).ToArray());
        var trail = new string(original.Reverse().TakeWhile(char.IsWhiteSpace).Reverse().ToArray());
        var core = original.Trim();
        if (core.Length == 0) return null;
        var formatted = lead + Format(core) + trail;
        if (formatted == original) return null;
        return new TextEdit(start, length, formatted, start, formatted.Length);
    }

    /// 선택이 없고 문장이 여러 개면, Ctrl+Enter 로 실행될 커서 문장 범위 (앞뒤 공백 제외, [Start, End)). 칠할 것이 없으면 null.
    public static (int Start, int End)? StatementHighlight(string text, int selectionStart, int selectionLength)
    {
        var length = text.Length;
        if (selectionLength != 0 || length == 0 || length >= 100_000) return null;
        var statements = SqlText.Statements(text);
        if (statements.Count <= 1) return null;
        SqlStatement? found = null;
        foreach (var s in statements) if (s.Contains(selectionStart)) { found = s; break; }
        if (found is not { } stmt) return null;
        // 앞뒤 공백 줄은 칠하지 않는다
        int start = stmt.Start, end = Math.Min(stmt.End, length);
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return end > start ? (start, end) : null;
    }
}
