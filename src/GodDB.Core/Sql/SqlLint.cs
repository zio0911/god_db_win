using GodDB.Core.Profiles;
using GodDB.Core.Schema;
using static GodDB.Core.L10n;

namespace GodDB.Core.Sql;

/// 쿼리 편집기에 빨간 밑줄로 보여 줄 문제 (Mac SQLIssue)
public sealed record SqlIssue
{
    public enum IssueSource { Local, Server }

    /// 편집기 전체 글자 기준 위치 (C# 문자열 인덱스 = Mac UTF-16)
    public int Start { get; init; }
    public int Length { get; init; }
    public string Message { get; init; }
    /// 덧붙일 안내 (예: 비슷한 컬럼)
    public string? Hint { get; init; }
    public IssueSource Source { get; init; }

    public SqlIssue(int start, int length, string message, string? hint = null, IssueSource source = IssueSource.Local)
    {
        Start = start;
        Length = length;
        Message = message;
        Hint = hint;
        Source = source;
    }

    public int End => Start + Length;
    public string Text => Hint is { } h ? Message + "\n" + h : Message;
}

/// 서버에 묻지 않고 바로 찾는 쿼리 문제 (Mac SQLLint).
/// 틀린 경고를 내지 않는 것을 우선한다 — 확실한 경우만 표시하고, 애매하면 넘어간다.
public static class SqlLint
{
    // MARK: - 토큰

    internal enum TokenKind { Word, Quoted, String, Number, Punct, Op }

    internal sealed record Token(TokenKind Kind, string Text, int Start, int Length)
    {
        public string Upper => Text.ToUpperInvariant();
        /// 따옴표를 벗긴 이름
        public string Name => Kind == TokenKind.Quoted && Text.Length >= 2 ? Text[1..^1] : Text;
        public bool IsPunct(string s) => Kind == TokenKind.Punct && Text == s;
        public bool IsName => Kind is TokenKind.Word or TokenKind.Quoted;
    }

    /// 닫히지 않은 따옴표/주석 위치
    internal readonly record struct Unclosed(int Location, bool IsComment);

    internal static (List<Token> Tokens, Unclosed? Unclosed) Tokenize(string s, DatabaseKind? kind)
    {
        var u = s;
        var tokens = new List<Token>();
        var i = 0;
        char Ch(int k) => k < u.Length ? u[k] : '\0';
        bool IsWordChar(char c)
        {
            if (c >= 0x80) return true;  // 한글 등
            return char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$' || c == '@' || (c == '#' && kind == DatabaseKind.Mssql);
        }
        void Add(TokenKind k, int start, int end) => tokens.Add(new Token(k, u[start..end], start, end - start));

        while (i < u.Length)
        {
            var c = u[i];
            if (c is ' ' or '\t' or '\n' or '\r') { i++; continue; }
            // 주석
            if (c == '-' && Ch(i + 1) == '-')
            {
                while (i < u.Length && u[i] != '\n') i++;
                continue;
            }
            if (c == '#' && kind == DatabaseKind.Mysql)
            {
                while (i < u.Length && u[i] != '\n') i++;
                continue;
            }
            if (c == '/' && Ch(i + 1) == '*')
            {
                var start = i;
                i += 2;
                while (i < u.Length && !(u[i] == '*' && Ch(i + 1) == '/')) i++;
                if (i >= u.Length) return (tokens, new Unclosed(start, true));
                i += 2;
                continue;
            }
            // PostgreSQL $태그$ … $태그$
            if (c == '$' && kind != DatabaseKind.Mysql && kind != DatabaseKind.Mssql)
            {
                var j = i + 1;
                while (j < u.Length && IsWordChar(u[j]) && u[j] != '$') j++;
                if (j < u.Length && u[j] == '$')
                {
                    var tag = u[i..(j + 1)];
                    var found = u.IndexOf(tag, j + 1, StringComparison.Ordinal);
                    if (found < 0) return (tokens, new Unclosed(i, false));
                    Add(TokenKind.String, i, found + tag.Length);
                    i = found + tag.Length;
                    continue;
                }
            }
            // 따옴표
            var doubleIsIdent = kind != DatabaseKind.Mysql;
            if (c is '\'' or '"' or '`' || (c == '[' && kind == DatabaseKind.Mssql))
            {
                var close = c == '[' ? ']' : c;
                var start = i;
                i++;
                var closed = false;
                while (i < u.Length)
                {
                    if (u[i] == '\\' && c != '[' && (kind == DatabaseKind.Mysql || kind == null)) { i += 2; continue; }
                    if (u[i] == close)
                    {
                        if (Ch(i + 1) == close && c != '[') { i += 2; continue; }  // '' 이스케이프
                        i++;
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed) return (tokens, new Unclosed(start, false));
                var isString = c == '\'' || (c == '"' && !doubleIsIdent);
                Add(isString ? TokenKind.String : TokenKind.Quoted, start, Math.Min(i, u.Length));
                continue;
            }
            if (c is >= '0' and <= '9')
            {
                var start = i;
                while (i < u.Length && (u[i] is >= '0' and <= '9' or '.' or 'e' or 'E' or 'x')) i++;
                Add(TokenKind.Number, start, i);
                continue;
            }
            if (IsWordChar(c) || (c == ':' && Ch(i + 1) != ':' && IsWordChar(Ch(i + 1))))
            {
                var start = i;
                i++;
                while (i < u.Length && IsWordChar(u[i])) i++;
                Add(TokenKind.Word, start, i);
                continue;
            }
            if (c is '(' or ')' or ',' or '.' or ';')
            {
                Add(TokenKind.Punct, i, i + 1);
                i++;
                continue;
            }
            // 연산자 (::, <=, >=, <>, !=, ->>, ->, ||)
            {
                var start = i;
                i++;
                while (i < u.Length && (u[i] is ':' or '<' or '>' or '=' or '!' or '|' or '-' or '&') && i - start < 3) i++;
                Add(TokenKind.Op, start, i);
            }
        }
        return (tokens, null);
    }

    // MARK: - 단어 목록

    internal static readonly HashSet<string> ClauseEnders =
    [
        "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "UNION", "INTERSECT", "EXCEPT", "INTO", "OFFSET",
        "FETCH", "FOR", "WINDOW", "RETURNING", "ON", "USING", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS",
        "FULL", "NATURAL", "SET", "VALUES", "QUALIFY", "OPTION",
    ];

    /// 컬럼 이름이 아닌 단어 (키워드 · 괄호 없이 쓰는 함수 · 단위 · 타입)
    internal static readonly HashSet<string> Keywords = [.. """
        SELECT FROM WHERE AND OR NOT IN IS NULL AS ON JOIN LEFT RIGHT INNER OUTER CROSS FULL NATURAL GROUP ORDER BY
        HAVING LIMIT OFFSET UNION ALL DISTINCT DISTINCTROW INTERSECT EXCEPT INSERT INTO VALUES VALUE UPDATE SET DELETE
        CREATE ALTER DROP TRUNCATE TABLE VIEW INDEX WITH RECURSIVE CASE WHEN THEN ELSE END LIKE ILIKE RLIKE REGEXP
        SIMILAR ESCAPE BETWEEN EXISTS ANY SOME ASC DESC NULLS FIRST LAST TRUE FALSE UNKNOWN XOR DIV MOD
        CURRENT_DATE CURRENT_TIME CURRENT_TIMESTAMP CURRENT_USER SESSION_USER SYSTEM_USER USER LOCALTIME
        LOCALTIMESTAMP SYSDATE INTERVAL DATE TIME TIMESTAMP YEAR MONTH WEEK DAY HOUR MINUTE SECOND MICROSECOND
        QUARTER EPOCH ZONE AT COLLATE BINARY OVER PARTITION WINDOW ROWS RANGE UNBOUNDED PRECEDING FOLLOWING CURRENT
        ROW FETCH NEXT ONLY TOP PERCENT TIES FOR SHARE LOCK NOWAIT SKIP LOCKED USING ROLLUP CUBE GROUPING SETS
        FILTER WITHIN LATERAL RETURNING DUAL ROWNUM IGNORE REPLACE HIGH_PRIORITY LOW_PRIORITY STRAIGHT_JOIN
        SQL_CALC_FOUND_ROWS SQL_NO_CACHE SQL_CACHE SQL_SMALL_RESULT SQL_BIG_RESULT INT INTEGER BIGINT SMALLINT
        TINYINT DECIMAL NUMERIC FLOAT DOUBLE REAL CHAR VARCHAR NVARCHAR NCHAR TEXT BOOLEAN BOOL JSON JSONB UUID
        SIGNED UNSIGNED PRECISION VARYING CHARACTER NOLOCK OPTION QUALIFY MATCH AGAINST MODE BOOLEAN EXPANSION
        LANGUAGE NATURAL DEFAULT IDENTITY KEY PRIMARY FOREIGN REFERENCES CHECK CONSTRAINT UNIQUE
        """.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)];

    internal static readonly string[] StatementStarters =
    [
        "SELECT", "INSERT", "UPDATE", "DELETE", "WITH", "CREATE", "ALTER", "DROP", "TRUNCATE", "SHOW", "DESC",
        "DESCRIBE", "EXPLAIN", "USE", "CALL", "EXEC", "EXECUTE", "SET", "BEGIN", "COMMIT", "ROLLBACK", "GRANT",
        "REVOKE", "MERGE", "REPLACE", "VALUES", "DECLARE", "IF", "PRINT", "ANALYZE", "VACUUM", "COPY", "LOCK",
        "UNLOCK", "START", "RENAME", "OPTIMIZE", "KILL", "SAVEPOINT", "RELEASE", "PREPARE", "DEALLOCATE", "LOAD",
        "HANDLER", "CHECKPOINT", "REINDEX", "CLUSTER", "COMMENT", "DO", "GO", "WHILE", "RETURN", "TABLE", "UNLOAD",
    ];

    internal static readonly string[] TypoTargets =
    [
        "SELECT", "FROM", "WHERE", "JOIN", "ORDER", "GROUP", "HAVING", "LIMIT", "UPDATE",
        "DELETE", "INSERT", "VALUES", "INNER", "LEFT", "RIGHT", "OUTER", "UNION", "DISTINCT",
    ];

    /// 옆 글자 바꿈도 1 로 세는 편집 거리
    internal static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }

    internal static string? Typo(string word, IReadOnlyList<string> targets, int maxDistance)
    {
        var w = word.ToUpperInvariant();
        if (w.Length < 4 || Keywords.Contains(w) || targets.Contains(w)) return null;
        return targets.FirstOrDefault(t => Math.Abs(t.Length - w.Length) <= 1 && Distance(w, t) <= maxDistance);
    }

    // MARK: - 검사

    /// <param name="tables">지금 스키마의 테이블 (컬럼 포함). 비어 있으면 컬럼 검사는 하지 않는다.</param>
    /// <param name="schema">지금 스키마 이름 (다른 스키마의 테이블을 잘못 검사하지 않게)</param>
    public static List<SqlIssue> Check(string text, IReadOnlyList<TableMeta> tables, DatabaseKind? kind, string? schema = null)
    {
        if (text.Length >= 200_000) return [];
        var output = new List<SqlIssue>();
        foreach (var stmt in SqlText.Statements(text))
        {
            var issues = CheckStatement(stmt.Sql, tables, kind, schema);
            // SqlText 는 구문 앞 공백을 잘라낸다 → 원문에서 구문 시작 위치를 다시 찾는다
            var searchStart = Math.Max(0, Math.Min(stmt.Start, text.Length));
            var found = text.IndexOf(stmt.Sql, searchStart, StringComparison.Ordinal);
            var @base = found < 0 ? stmt.Start : found;
            foreach (var i in issues) output.Add(i with { Start = i.Start + @base });
        }
        return output;
    }

    internal static List<SqlIssue> CheckStatement(string sql, IReadOnlyList<TableMeta> tables, DatabaseKind? kind, string? schema)
    {
        var (tokens, unclosed) = Tokenize(sql, kind);
        var output = new List<SqlIssue>();
        if (unclosed is { } uc)
        {
            var len = sql.Length - uc.Location;
            output.Add(new SqlIssue(uc.Location, Math.Max(1, len), uc.IsComment ? Tr("닫히지 않은 주석이에요.") : Tr("닫히지 않은 따옴표예요.")));
        }
        if (tokens.Count == 0) return output;
        var first = tokens[0];

        // 첫 단어 오타 (SELEC, UPDTAE …)
        if (first.Kind == TokenKind.Word && !StatementStarters.Contains(first.Upper)
            && Typo(first.Text, StatementStarters, 2) is { } fix0)
            output.Add(new SqlIssue(first.Start, first.Length, Tr("%@ 의 오타 같아요.", fix0)));

        // 괄호
        var stack = new List<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.IsPunct("(")) stack.Add(i);
            if (t.IsPunct(")"))
            {
                if (stack.Count == 0) output.Add(new SqlIssue(t.Start, t.Length, Tr("여는 괄호가 없어요.")));
                else stack.RemoveAt(stack.Count - 1);
            }
        }
        if (unclosed == null)
            foreach (var i in stack) output.Add(new SqlIssue(tokens[i].Start, tokens[i].Length, Tr("닫는 괄호가 없어요.")));

        // 콤마
        string[] afterCommaEnders = ["FROM", "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "UNION"];
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (!t.IsPunct(",")) continue;
            var prev = i > 0 ? tokens[i - 1] : null;
            var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
            if (next != null && next.IsPunct(","))
                output.Add(new SqlIssue(next.Start, next.Length, Tr("콤마가 두 번 있어요.")));
            else if (next == null || next.IsPunct(")") || next.IsPunct(";")
                     || (next.Kind == TokenKind.Word && afterCommaEnders.Contains(next.Upper)))
                output.Add(new SqlIssue(t.Start, t.Length, Tr("콤마 뒤에 항목이 없어요.")));
            else if (prev != null && (prev.IsPunct("(") || (prev.Kind == TokenKind.Word && prev.Upper is "SELECT" or "BY")))
                output.Add(new SqlIssue(t.Start, t.Length, Tr("콤마 앞에 항목이 없어요.")));
        }

        output.AddRange(MissingCommas(tokens));

        // 키워드 오타 (FORM, WEHRE …): 아는 테이블 · 컬럼 이름이 아닐 때만
        var known = new HashSet<string>(tables.SelectMany(t => new[] { t.Name.ToUpperInvariant() }.Concat(t.Columns.Select(c => c.Name.ToUpperInvariant()))));
        for (var i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.Word || known.Contains(t.Upper)) continue;
            var prev = tokens[i - 1];
            var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
            if (prev.IsPunct(".") || next?.IsPunct(".") == true || next?.IsPunct("(") == true || prev.Upper == "AS") continue;
            if (Typo(t.Text, TypoTargets, 1) is { } fix)
                output.Add(new SqlIssue(t.Start, t.Length, Tr("%@ 의 오타 같아요.", fix)));
        }

        if (tables.Count > 0 && unclosed == null) output.AddRange(UnknownColumns(tokens, tables, schema));
        // 같은 자리 중복 제거
        var seen = new HashSet<(int, int)>();
        return output.Where(o => seen.Add((o.Start, o.Length))).ToList();
    }

    /// 자리를 확실히 아는 경우만: 값/이름 뒤에 콤마 없이 `별칭.컬럼` 이 바로 오면 (`a.id a.name`)
    internal static List<SqlIssue> MissingCommas(List<Token> tokens)
    {
        var output = new List<SqlIssue>();
        // 깊이별 지금 절 (SELECT 목록 / GROUP BY / ORDER BY 만 검사)
        var clause = new Dictionary<int, string?>();
        var depth = 0;
        var i = 0;
        var listStart = -1;   // 목록 첫 항목 (앞에 수식어가 와도 콤마를 요구하지 않음)
        string? Clause(int d) => clause.TryGetValue(d, out var v) ? v : null;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.IsPunct("(")) { depth++; clause[depth] = null; i++; continue; }
            if (t.IsPunct(")")) { clause[depth] = null; depth = Math.Max(0, depth - 1); i++; continue; }
            if (t.Kind == TokenKind.Word)
            {
                var up = t.Upper;
                if (up == "SELECT")
                {
                    clause[depth] = "list";
                    // SELECT 수식어 건너뛰기: DISTINCT [ON (…)], ALL, TOP n / TOP (n) [PERCENT] [WITH TIES]
                    var j = i + 1;
                    while (j < tokens.Count)
                    {
                        var m = tokens[j].Upper;
                        if (m is "DISTINCT" or "ALL" or "DISTINCTROW" or "HIGH_PRIORITY" or "STRAIGHT_JOIN" or "PERCENT" || m.StartsWith("SQL_", StringComparison.Ordinal))
                        {
                            j++;
                            if (m == "DISTINCT" && j < tokens.Count && tokens[j].Upper == "ON" && j + 1 < tokens.Count && tokens[j + 1].IsPunct("("))
                                j = SkipParens(tokens, j + 1);
                        }
                        else if (m == "TOP")
                        {
                            j++;
                            if (j < tokens.Count && tokens[j].IsPunct("(")) j = SkipParens(tokens, j); else j++;
                        }
                        else if (m == "WITH" && j + 1 < tokens.Count && tokens[j + 1].Upper == "TIES")
                        {
                            j += 2;
                        }
                        else break;
                    }
                    i = j;
                    listStart = j;
                    continue;
                }
                if (up == "BY" && i > 0 && tokens[i - 1].Upper is "GROUP" or "ORDER")
                {
                    clause[depth] = "list";
                    i++;
                    listStart = i;
                    continue;
                }
                if (ClauseEnders.Contains(up) && up != "ORDER" && up != "GROUP") clause[depth] = null;
                if (up is "ORDER" or "GROUP") clause[depth] = null;
            }
            if (Clause(depth) == "list" && i > 0 && i != listStart && t.IsName && (!Keywords.Contains(t.Upper) || t.Kind == TokenKind.Quoted)
                && i + 2 < tokens.Count && tokens[i + 1].IsPunct(".") && (tokens[i + 2].IsName || tokens[i + 2].Kind == TokenKind.Op)
                && !tokens[i - 1].IsPunct("."))
            {
                var prev = tokens[i - 1];
                var prevEndsValue = (prev.Kind == TokenKind.Word && !Keywords.Contains(prev.Upper)) || prev.Kind == TokenKind.Quoted
                    || prev.Kind == TokenKind.Number || prev.Kind == TokenKind.String || prev.IsPunct(")");
                if (prevEndsValue)
                {
                    var end = tokens[i + 2];
                    output.Add(new SqlIssue(t.Start, end.Start + end.Length - t.Start, Tr("앞 항목과 사이에 콤마가 빠진 것 같아요.")));
                }
            }
            i++;
        }
        return output;
    }

    internal static int SkipParens(List<Token> tokens, int start)
    {
        var d = 0;
        var j = start;
        while (j < tokens.Count)
        {
            if (tokens[j].IsPunct("(")) d++;
            if (tokens[j].IsPunct(")")) { d--; if (d == 0) return j + 1; }
            j++;
        }
        return j;
    }

    // MARK: - 없는 컬럼

    internal sealed record TableRef(string Table, string? Alias, string? Qualifier)
    {
        public string? Alias { get; set; } = Alias;
    }

    /// FROM / JOIN / UPDATE / INTO / 쉼표 뒤 테이블과 별칭
    internal static (List<TableRef> Refs, bool HasSubquery, HashSet<string> CteNames) TableRefs(List<Token> tokens)
    {
        var refs = new List<TableRef>();
        var hasSubquery = false;
        var ctes = new HashSet<string>();
        var inFrom = false;
        var depth = 0;
        var fromDepth = 0;
        var i = 0;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.IsPunct("("))
            {
                depth++;
                if (i + 1 < tokens.Count && tokens[i + 1].Upper is "SELECT" or "WITH") hasSubquery = true;
            }
            if (t.IsPunct(")")) depth--;
            // WITH 이름 AS (
            if (t.IsName && i + 2 < tokens.Count && tokens[i + 1].Upper == "AS" && tokens[i + 2].IsPunct("(")
                && i > 0 && (tokens[i - 1].Upper == "WITH" || tokens[i - 1].IsPunct(",") || tokens[i - 1].Upper == "RECURSIVE"))
                ctes.Add(t.Name.ToLowerInvariant());
            var up = t.Kind == TokenKind.Word ? t.Upper : "";
            var startsRef = up is "FROM" or "JOIN" or "UPDATE" or "INTO" || (inFrom && depth == fromDepth && t.IsPunct(","));
            if ((up is "WHERE" or "GROUP" or "ORDER" or "HAVING" or "LIMIT" or "UNION" or "SET" or "ON" or "USING" or "VALUES" or "WINDOW") && depth == fromDepth)
                inFrom = false;
            if (startsRef)
            {
                if (up is "FROM" or "JOIN") { inFrom = true; fromDepth = depth; }
                var j = i + 1;
                var parts = new List<string>();
                while (j < tokens.Count && tokens[j].IsName
                       && !(tokens[j].Kind == TokenKind.Word && Keywords.Contains(tokens[j].Upper) && parts.Count == 0 && tokens[j].Upper != "USER"))
                {
                    parts.Add(tokens[j].Name);
                    if (j + 1 < tokens.Count && tokens[j + 1].IsPunct(".")) j += 2; else { j++; break; }
                }
                if (parts.Count > 0)
                {
                    var r = new TableRef(parts[^1], null, parts.Count > 1 ? parts[^2] : null);
                    if (j < tokens.Count && tokens[j].Upper == "AS") j++;
                    if (j < tokens.Count && tokens[j].IsName
                        && !(tokens[j].Kind == TokenKind.Word && (Keywords.Contains(tokens[j].Upper) || ClauseEnders.Contains(tokens[j].Upper))))
                    {
                        r.Alias = tokens[j].Name;
                        j++;
                    }
                    refs.Add(r);
                    i = j;
                    continue;
                }
            }
            i++;
        }
        return (refs, hasSubquery, ctes);
    }

    internal static List<SqlIssue> UnknownColumns(List<Token> tokens, IReadOnlyList<TableMeta> tables, string? schema)
    {
        var byName = new Dictionary<string, TableMeta>();
        foreach (var t in tables) byName.TryAdd(t.Name.ToLowerInvariant(), t);
        var (refs, hasSubquery, ctes) = TableRefs(tokens);
        // 별칭/테이블 이름 → 테이블 (다른 스키마 · CTE · 모르는 테이블은 빼서 검사하지 않음)
        var resolve = new Dictionary<string, TableMeta>();
        var blocked = new HashSet<string>(ctes);
        foreach (var r in refs)
        {
            var key = r.Table.ToLowerInvariant();
            var otherSchema = r.Qualifier is { } q && (schema == null || !q.Equals(schema, StringComparison.OrdinalIgnoreCase));
            if (ctes.Contains(key) || otherSchema || !byName.TryGetValue(key, out var t) || t.Columns.Count == 0)
            {
                blocked.Add(key);
                if (r.Alias is { } a0) blocked.Add(a0.ToLowerInvariant());
                continue;
            }
            if (r.Alias is { } a) resolve[a.ToLowerInvariant()] = t; else resolve[key] = t;
        }
        var output = new List<SqlIssue>();
        SqlIssue? ColumnIssue(Token name, TableMeta table)
        {
            var col = name.Name;
            if (table.Columns.Any(c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase))) return null;
            string? hint = null;
            if (SqlCompleter.Closest(col, table.Columns.Select(c => c.Name).ToList()).FirstOrDefault() is { } best)
            {
                var bl = best.ToLowerInvariant();
                var cl = col.ToLowerInvariant();
                if (bl.Contains(cl) || cl.Contains(bl) || Distance(bl, cl) <= Math.Max(2, col.Length / 3))
                    hint = Tr("비슷한 컬럼: %@", best);
            }
            return new SqlIssue(name.Start, name.Length, Tr("%@ 테이블에 %@ 컬럼이 없어요.", table.Name, col), hint);
        }

        // 1) 별칭.컬럼
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            Token q = tokens[i], dot = tokens[i + 1], c = tokens[i + 2];
            if (!q.IsName || !dot.IsPunct(".") || !c.IsName) continue;
            if (i > 0 && tokens[i - 1].IsPunct(".")) continue;                                                 // a.b.c
            if (i + 3 < tokens.Count && (tokens[i + 3].IsPunct(".") || tokens[i + 3].IsPunct("("))) continue;  // 스키마.테이블. / 함수
            if (i > 0 && tokens[i - 1].Upper is "FROM" or "JOIN" or "UPDATE" or "INTO" or "TABLE") continue;
            var key = q.Name.ToLowerInvariant();
            if (blocked.Contains(key) || !resolve.TryGetValue(key, out var table)) continue;
            if (ColumnIssue(c, table) is { } issue) output.Add(issue);
        }

        // 2) 테이블 하나짜리 단순 SELECT 의 이름만 쓴 컬럼
        if (tokens[0].Upper != "SELECT" || refs.Count != 1 || hasSubquery || ctes.Count > 0) return output;
        var only = refs[0];
        if (!resolve.TryGetValue((only.Alias ?? only.Table).ToLowerInvariant(), out var single)) return output;
        if (tokens.Any(t => t.Upper is "JOIN" or "UNION" or "INTO" or "INTERSECT" or "EXCEPT")) return output;
        // SELECT 목록에서 만든 별칭 (x AS y, x y) 은 컬럼이 아님
        var selectAliases = new HashSet<string>();
        for (var i = 1; i < tokens.Count; i++)
            if (tokens[i].IsName && tokens[i - 1].Upper == "AS") selectAliases.Add(tokens[i].Name.ToLowerInvariant());
        var clauseName = "";
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.IsPunct("(")) { depth++; continue; }
            if (t.IsPunct(")")) { depth--; continue; }
            if (depth != 0) continue;
            var up = t.Kind == TokenKind.Word ? t.Upper : "";
            if (up == "SELECT") { clauseName = "select"; continue; }
            if (up == "FROM") { clauseName = "from"; continue; }
            if (up is "WHERE" or "HAVING") { clauseName = "cond"; continue; }
            if (up == "BY" && i > 0 && tokens[i - 1].Upper is "GROUP" or "ORDER") { clauseName = "by"; continue; }
            if (up is "LIMIT" or "OFFSET" or "FETCH" or "FOR" or "WINDOW" or "OPTION") { clauseName = "other"; continue; }
            if (clauseName is not ("select" or "cond" or "by") || !t.IsName) continue;
            if (t.Kind == TokenKind.Word && (Keywords.Contains(up) || t.Text.StartsWith('@') || t.Text.StartsWith(':') || t.Text.StartsWith('#'))) continue;
            var prev = i > 0 ? tokens[i - 1] : null;
            var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
            if (prev?.IsPunct(".") == true || next?.IsPunct(".") == true || next?.IsPunct("(") == true) continue;
            if (prev?.Upper == "AS" || (prev?.Kind == TokenKind.Op && prev.Text == "::")) continue;
            var lower = t.Name.ToLowerInvariant();
            if (selectAliases.Contains(lower) || lower == only.Table.ToLowerInvariant() || lower == only.Alias?.ToLowerInvariant()) continue;
            // SELECT 목록의 "값 별칭" (a.x name, COUNT(*) cnt) 은 넘어간다
            if (clauseName == "select" && prev != null
                && ((prev.Kind == TokenKind.Word && !Keywords.Contains(prev.Upper)) || prev.Kind == TokenKind.Quoted
                    || prev.Kind == TokenKind.Number || prev.Kind == TokenKind.String || prev.IsPunct(")")))
                continue;
            if (ColumnIssue(t, single) is { } issue) output.Add(issue);
        }
        return output;
    }
}
