using System.Globalization;
using System.Text.RegularExpressions;
using GodDB.Core.Schema;

namespace GodDB.Core.Sql;

/// 커서 앞에서 입력 중인 단어 범위 (Start, Length — C# 문자열 인덱스)와 바로 앞의 `qualifier.`
public readonly record struct CompletionContext(int Start, int Length, string? Qualifier)
{
    public int End => Start + Length;
}

/// Tab 키를 눌렀을 때 편집기가 할 일 (Mac: SQLEditorView Coordinator.textView(_:doCommandBy:))
public abstract record TabAction
{
    /// 입력 중인 단어가 없으면 커서 자리에 공백 4칸
    public sealed record Indent(int Position, string Text) : TabAction;
    /// [Start, Start+Length) 를 Text 로 바꾼다 (후보 하나 · 더 긴 공통 접두어)
    public sealed record Replace(int Start, int Length, string Text) : TabAction;
    /// 후보가 여럿이고 더 채울 접두어가 없으면 [Start, Start+Length) 단어에 대한 후보 목록을 띄운다
    public sealed record ShowList(int Start, int Length, IReadOnlyList<string> Candidates) : TabAction;
    /// 후보가 없음 (Mac 은 NSSound.beep())
    public sealed record Beep : TabAction;
}

/// 쿼리 편집기 Tab 자동완성 후보를 만든다.
public static partial class SqlCompleter
{
    public static readonly IReadOnlyList<string> Keywords =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "AS", "ON", "JOIN", "LEFT", "RIGHT",
        "INNER", "OUTER", "CROSS", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET", "UNION", "UNION ALL",
        "DISTINCT", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "CREATE TABLE", "ALTER TABLE",
        "DROP TABLE", "TRUNCATE TABLE", "SHOW", "SHOW TABLES", "SHOW CREATE TABLE", "SHOW INDEX FROM",
        "DESC", "DESCRIBE", "EXPLAIN", "USE", "WITH", "CASE", "WHEN", "THEN", "ELSE", "END", "LIKE",
        "BETWEEN", "EXISTS", "ASC", "COUNT(*)", "SUM(", "AVG(", "MIN(", "MAX(", "IFNULL(", "COALESCE(",
        "NOW()", "DATE_FORMAT(", "CONCAT(", "SUBSTRING(", "CAST(", "IF(", "LENGTH(", "TRIM(", "REPLACE(",
        "PRIMARY KEY", "FOREIGN KEY", "REFERENCES", "DEFAULT", "AUTO_INCREMENT", "COMMENT",
        "ADD COLUMN", "MODIFY COLUMN", "CHANGE COLUMN", "DROP COLUMN", "ADD INDEX", "DROP INDEX",
        "VARCHAR(", "CHAR(", "INT", "BIGINT", "DECIMAL(", "DATETIME", "TIMESTAMP", "DATE", "TEXT",
        "TINYINT", "CURRENT_TIMESTAMP", "FOR UPDATE", "CALL",
        // PostgreSQL
        "ILIKE", "RETURNING", "SERIAL", "BIGSERIAL", "BOOLEAN", "JSONB", "NOW()", "COALESCE(", "STRING_AGG(",
        "ON CONFLICT", "DO NOTHING", "INTERVAL", "TIMESTAMPTZ",
        // SQL Server
        "TOP", "EXEC", "NVARCHAR(", "DATETIME2", "GETDATE()", "SYSDATETIME()", "ISNULL(", "WITH (NOLOCK)",
        "OFFSET", "FETCH NEXT", "ROWS ONLY", "IDENTITY(1,1)", "UNIQUEIDENTIFIER", "NEWID()",
    ];

    /// 알리아스로 쓰일 수 없는 단어 (FROM t WHERE … 에서 WHERE 를 알리아스로 오인하지 않게)
    private static readonly HashSet<string> Reserved =
    [
        "WHERE", "ON", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "GROUP", "ORDER", "HAVING",
        "LIMIT", "SET", "VALUES", "UNION", "USING", "STRAIGHT_JOIN", "NATURAL", "FOR", "WINDOW", "AS",
    ];

    // 이름은 `t` · "t" · [t] 로 감쌀 수 있고 db.schema.t 처럼 점으로 이어질 수 있다 (Mac 1.4.2)
    private const string Ident = @"[`""\[]?[A-Za-z0-9_$]+[`""\]]?";
    private const string NamePattern = "(" + Ident + @"(?:\." + Ident + ")*)";
    private const string AliasPattern = @"(?:\s+(?:AS\s+)?[`""\[]?([A-Za-z0-9_$]+)[`""\]]?)?";

    [GeneratedRegex(@"\b(?:FROM|JOIN|UPDATE|INTO)\s+" + NamePattern + AliasPattern, RegexOptions.IgnoreCase)]
    private static partial Regex TableRegex();

    /// 쉼표로 이어진 FROM a x, b y 도 처리
    [GeneratedRegex(@",\s*" + NamePattern + AliasPattern, RegexOptions.IgnoreCase)]
    private static partial Regex CommaRegex();

    [GeneratedRegex(@"\b(FROM|JOIN|UPDATE|INTO|TABLE|DESC|DESCRIBE)\s+[A-Za-z0-9_$]*$", RegexOptions.IgnoreCase)]
    private static partial Regex AfterTableKeyword();

    /// 구문에서 "알리아스/테이블명(소문자) → 실제 테이블명" 을 찾는다.
    public static Dictionary<string, string> Aliases(string statement)
    {
        var map = new Dictionary<string, string>();
        foreach (var regex in new[] { TableRegex(), CommaRegex() })
        {
            foreach (Match m in regex.Matches(statement))
            {
                if (!m.Groups[1].Success) continue;
                var table = m.Groups[1].Value;
                var dot = table.LastIndexOf('.');
                if (dot >= 0) table = table[(dot + 1)..];
                table = table.Trim('`', '"', '[', ']');
                map[table.ToLowerInvariant()] = table;
                if (m.Groups[2].Success)
                {
                    var alias = m.Groups[2].Value;
                    if (!Reserved.Contains(alias.ToUpperInvariant())) map[alias.ToLowerInvariant()] = table;
                }
            }
        }
        return map;
    }

    /// <param name="prefix">커서 앞에서 입력 중인 단어</param>
    /// <param name="qualifier">`m.em` 처럼 점 앞의 단어 (없으면 null)</param>
    /// <param name="statement">커서가 있는 구문 전체 (알리아스 해석용)</param>
    public static List<string> Suggestions(string prefix, string? qualifier, string statement,
                                           IReadOnlyList<TableMeta> tables, IReadOnlyList<RoutineMeta>? routines = null, int limit = 50)
    {
        routines ??= [];
        var p = prefix.ToLowerInvariant();
        var byName = new Dictionary<string, TableMeta>();
        foreach (var t in tables) byName.TryAdd(t.Name.ToLowerInvariant(), t);
        var aliasMap = Aliases(statement);

        var output = new List<string>();
        var seen = new HashSet<string>();
        bool Matches(string s) => p.Length == 0 || s.ToLowerInvariant().StartsWith(p, StringComparison.Ordinal);
        void Add(string s)
        {
            if (Matches(s) && seen.Add(s.ToLowerInvariant())) output.Add(s);
        }
        List<string> Limited() => output.Take(limit).ToList();

        // 1) 테이블/알리아스 뒤의 점 → 그 테이블의 컬럼만
        if (qualifier != null)
        {
            var q = qualifier.ToLowerInvariant();
            var tableName = aliasMap.TryGetValue(q, out var a) ? a : byName.TryGetValue(q, out var bt) ? bt.Name : null;
            if (tableName != null && byName.TryGetValue(tableName.ToLowerInvariant(), out var t))
            {
                foreach (var c in t.Columns.OrderBy(c => c.Position)) Add(c.Name);
                // 잘못 쓴 이름(a.username)이라 맞는 게 없으면 그 테이블 컬럼을 비슷한 순서로
                if (output.Count == 0 && p.Length > 0)
                    return Closest(prefix, t.Columns.Select(c => c.Name).ToList()).Take(limit).ToList();
            }
            return Limited();
        }

        // 2) 구문에 등장한 테이블의 컬럼
        var usedTables = aliasMap.Values.Select(v => v.ToLowerInvariant()).Distinct()
            .Select(v => byName.GetValueOrDefault(v)).OfType<TableMeta>();
        foreach (var t in usedTables.OrderBy(t => t.Name, StringComparer.Ordinal))
            foreach (var c in t.Columns.OrderBy(c => c.Position)) Add(c.Name);
        // 3) FROM/JOIN 바로 뒤라면 테이블을 키워드보다 먼저
        if (AfterTableKeyword().IsMatch(statement))
        {
            foreach (var t in tables) Add(t.Name);
            foreach (var r in routines.Where(r => r.Kind == RoutineKind.View)) Add(r.Name);
        }
        // 4) 키워드 (입력이 소문자면 소문자로)
        var lowerCase = prefix.Length > 0 && char.IsLower(prefix[0]);
        foreach (var k in Keywords) Add(lowerCase ? k.ToLowerInvariant() : k);
        // 5) 나머지 테이블, 뷰, 프로시저/함수
        foreach (var t in tables) Add(t.Name);
        foreach (var r in routines.Where(r => r.Kind != RoutineKind.Trigger)) Add(r.Name);

        return Limited();
    }

    // MARK: 편집기 쪽 로직 (Mac: SQLEditorView.swift 의 EditorController · Coordinator)

    /// Mac: CharacterSet.alphanumerics ∪ "_$"
    private static bool IsWordChar(char c)
    {
        if (c == '_' || c == '$') return true;
        if (char.IsSurrogate(c)) return false;
        return char.GetUnicodeCategory(c) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
            _ => false,
        };
    }

    /// 커서 앞에서 입력 중인 단어 범위와, 바로 앞의 `qualifier.` 를 찾는다.
    public static CompletionContext Context(string text, int cursor)
    {
        bool IsWord(int i) => i >= 0 && i < text.Length && IsWordChar(text[i]);
        var start = cursor;
        while (IsWord(start - 1)) start--;
        string? qualifier = null;
        if (start > 0 && text[start - 1] == '.')
        {
            var qs = start - 1;
            while (IsWord(qs - 1)) qs--;
            if (qs < start - 1) qualifier = text[qs..(start - 1)];
        }
        return new CompletionContext(start, cursor - start, qualifier);
    }

    /// 편집기 글 전체에서 [start, start+length) 단어의 후보 (별칭 해석은 구문 전체, "FROM 뒤인지" 는 커서 앞부분 기준)
    public static List<string> EditorSuggestions(string text, int start, int length,
                                                 IReadOnlyList<TableMeta> tables, IReadOnlyList<RoutineMeta>? routines = null)
    {
        var cursor = start + length;
        var ctx = Context(text, cursor);
        var prefix = text.Substring(start, length);
        var upToCursor = text[..cursor];
        var statement = SqlText.Statement(text, cursor) ?? upToCursor;
        var lastStmtStart = SqlText.Statements(upToCursor).LastOrDefault().Start;
        var before = upToCursor[Math.Min(lastStmtStart, upToCursor.Length)..];
        var result = Suggestions(prefix, ctx.Qualifier, statement + "\n" + before, tables, routines);
        // "FROM 뒤" 판단을 위해 before 로 한 번 더 (테이블 우선 정렬)
        if (ctx.Qualifier == null)
        {
            var ordered = Suggestions(prefix, null, before, tables, routines);
            var extra = result.Where(r => !ordered.Contains(r));
            result = [.. ordered, .. extra];
        }
        return result;
    }

    /// Tab 키: 입력 중인 단어가 있으면 자동완성, 없으면 공백 4칸.
    /// 선택 영역이 있으면 null (편집기 기본 동작 — Mac 은 false 를 돌려 기본 처리).
    public static TabAction? Tab(string text, int selectionStart, int selectionLength,
                                 IReadOnlyList<TableMeta> tables, IReadOnlyList<RoutineMeta>? routines = null)
    {
        if (selectionLength != 0) return null;
        var ctx = Context(text, selectionStart);
        if (!(ctx.Length > 0 || ctx.Qualifier != null)) return new TabAction.Indent(selectionStart, "    ");
        var candidates = EditorSuggestions(text, ctx.Start, ctx.Length, tables, routines);
        switch (candidates.Count)
        {
            case 0: return new TabAction.Beep();
            case 1: return new TabAction.Replace(ctx.Start, ctx.Length, candidates[0]);
            default:
                // 공통 접두어가 더 길면 먼저 채우고, 그래도 여러 개면 목록을 띄운다.
                var common = CommonPrefix(candidates);
                // 잘못 쓴 이름의 "비슷한 후보" 목록이면 공통 접두어로 바꾸지 않고 목록만 띄운다 (Mac 1.4.2)
                var typed = text.Substring(ctx.Start, ctx.Length);
                return common.Length > ctx.Length && common.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
                    ? new TabAction.Replace(ctx.Start, ctx.Length, common)
                    : new TabAction.ShowList(ctx.Start, ctx.Length, candidates);
        }
    }

    /// 이름을 비슷한 순서로 (포함 관계 먼저, 그다음 편집 거리)
    public static List<string> Closest(string word, IReadOnlyList<string> names)
    {
        var w = word.ToLowerInvariant();
        int Score(string name)
        {
            var n = name.ToLowerInvariant();
            if (n.Contains(w, StringComparison.Ordinal) || w.Contains(n, StringComparison.Ordinal)) return Math.Abs(n.Length - w.Length);
            return 100 + EditDistance(w, n);
        }
        return names.Select((n, i) => (n, i, s: Score(n))).OrderBy(x => x.s).ThenBy(x => x.i).Select(x => x.n).ToList();
    }

    internal static int EditDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = cur;
        }
        return prev[b.Length];
    }

    /// 대소문자를 무시한 공통 접두어 (글자 모양은 첫 후보를 따른다)
    public static string CommonPrefix(IReadOnlyList<string> words)
    {
        if (words.Count == 0) return "";
        var prefix = words[0].ToLowerInvariant();
        foreach (var w in words.Skip(1))
        {
            var lw = w.ToLowerInvariant();
            while (!lw.StartsWith(prefix, StringComparison.Ordinal)) prefix = prefix[..^1];
        }
        return words[0][..prefix.Length];
    }
}
