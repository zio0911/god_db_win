using System.Globalization;
using System.Text;
using static GodDB.Core.L10n;

namespace GodDB.Core.Sql;

/// 읽기전용 검사에 걸린 이유 (Mac SQLText.ReadOnlyViolation)
public sealed record ReadOnlyViolation(ReadOnlyViolationKind Kind, string Text)
{
    /// 첫 단어가 허용 목록에 없음 (빈 문자열이면 첫 단어가 없음)
    public static ReadOnlyViolation Leading(string s) => new(ReadOnlyViolationKind.Leading, s);
    /// 문장 안의 쓰기 · DDL · 세션 변경 단어
    public static ReadOnlyViolation Keyword(string s) => new(ReadOnlyViolationKind.Keyword, s);
    /// 상태를 바꾸는 함수 호출 (소문자 이름)
    public static ReadOnlyViolation Function(string s) => new(ReadOnlyViolationKind.Function, s);
    /// 행 잠금 절 (FOR UPDATE 등)
    public static ReadOnlyViolation Lock(string s) => new(ReadOnlyViolationKind.Lock, s);

    /// 걸린 단어 · 함수 · 절
    public string Trigger => Kind == ReadOnlyViolationKind.Function ? Text + "()" : Text;

    public string Message => Kind switch
    {
        ReadOnlyViolationKind.Leading when Text.Length == 0 =>
            Tr("읽기전용 접속이라 이 쿼리는 실행할 수 없어요. (SELECT / SHOW / DESC / EXPLAIN 만 허용)"),
        ReadOnlyViolationKind.Leading => Tr("읽기전용 접속에서는 실행할 수 없어요: %@ (SELECT / SHOW / DESC / EXPLAIN 만 허용)", Text),
        ReadOnlyViolationKind.Keyword => Tr("읽기전용 접속에서는 실행할 수 없어요: %@ (문장 안에 있음)", Text),
        ReadOnlyViolationKind.Function => Tr("읽기전용 접속에서는 실행할 수 없어요: %@ (상태를 바꾸는 함수)", Trigger),
        _ => Tr("읽기전용 접속에서는 실행할 수 없어요: %@ (행을 잠금)", Text),
    };
}

public enum ReadOnlyViolationKind { Leading, Keyword, Function, Lock }

/// 읽기전용 접속의 문장 검사 (Mac SQLReadOnlyGuard.swift).
/// DB 별 규칙으로 토큰을 나눈 뒤(문자열 · 따옴표 식별자 · 주석 · 달러 인용은 코드가 아님) 문장 전체를 본다.
/// - 첫 단어가 SELECT / SHOW / DESC / EXPLAIN / USE / WITH 중 하나여야 하고
/// - 쓰기 · DDL · 세션 변경 단어가 어디에도 없어야 하며 (SQL Server 는 `;` 없이 문장을 이어 쓸 수 있다)
/// - 상태를 바꾸는 함수 호출 · 행 잠금(FOR UPDATE 등)이 없어야 한다.
/// 애매하면 막는다 (멀쩡한 조회를 막는 쪽이 쓰기를 통과시키는 쪽보다 낫다).
public static partial class SqlText
{
    /// 읽기전용 접속에서 실행 가능한 구문인지 판별한다. (DB 종류에 따라 주석 · 문자열 규칙이 다르다)
    public static bool IsReadOnly(string sql, DatabaseKind kind) => ReadOnlyViolationOf(sql, kind) == null;

    /// 읽기전용 접속에서 막아야 하면 그 이유. 실행해도 되면 null.
    public static ReadOnlyViolation? ReadOnlyViolationOf(string sql, DatabaseKind kind)
    {
        if (kind == DatabaseKind.Mssql) return ScanReadOnly(SqlTokens(sql, kind, backslash: false), kind);
        // MySQL NO_BACKSLASH_ESCAPES · PostgreSQL standard_conforming_strings 설정에 따라 \ 의 뜻이 달라지므로 둘 다 본다
        return ScanReadOnly(SqlTokens(sql, kind, backslash: true), kind)
            ?? ScanReadOnly(SqlTokens(sql, kind, backslash: false), kind);
    }

    // MARK: - 토큰

    internal enum SqlTokenKind { Word, Quoted, String, Number, Symbol }

    /// word 는 대문자, quoted 는 따옴표 안 글자, symbol 은 그 글자
    internal readonly record struct SqlToken(SqlTokenKind Kind, string Text)
    {
        public bool IsWord(string w) => Kind == SqlTokenKind.Word && Text == w;
        public bool IsSymbol(string s) => Kind == SqlTokenKind.Symbol && Text == s;
        public bool IsIdentifier => Kind is SqlTokenKind.Word or SqlTokenKind.Quoted;
    }

    /// DB 별 규칙으로 토큰을 나눈다. 주석과 공백은 버린다. (Mac 처럼 유니코드 코드 포인트 단위로 본다)
    /// - backslash: 일반 문자열 안의 `\` 를 이스케이프로 볼지 (PostgreSQL 의 E'…' 는 항상 이스케이프)
    internal static List<SqlToken> SqlTokens(string sql, DatabaseKind kind, bool backslash)
    {
        var c = sql.EnumerateRunes().Select(r => r.Value).ToArray();
        var n = c.Length;
        var mysql = kind is DatabaseKind.Mysql or DatabaseKind.Redis;
        var pg = kind == DatabaseKind.Postgres;
        var ms = kind == DatabaseKind.Mssql;
        var output = new List<SqlToken>();
        var i = 0;
        var inExecutableComment = false;  // MySQL /*! … */ 안 (서버가 실행하는 주석)

        int? At(int k) => k < n ? c[k] : null;
        static bool IsAsciiLetter(int u) => u is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
        static bool IsDigit(int u) => u is >= '0' and <= '9';
        static bool IsLetterOrMark(int u)
        {
            if (!Rune.IsValid(u)) return false;
            var cat = Rune.GetUnicodeCategory(new Rune(u));
            return Rune.IsLetter(new Rune(u)) || cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
        }
        static bool IsSpaceOrControl(int u)
        {
            if (!Rune.IsValid(u)) return false;
            var r = new Rune(u);
            return Rune.IsWhiteSpace(r) || Rune.IsControl(r) || Rune.GetUnicodeCategory(r) == UnicodeCategory.Format
                   || u is 0x85 or 0x2028 or 0x2029;
        }
        // 식별자 글자. ASCII 밖은 글자(letter)만 — 다른 기호는 구분자로 본다 (더 많이 잘라 보는 쪽이 안전)
        bool IsWordChar(int u)
        {
            if (IsAsciiLetter(u) || IsDigit(u) || u == '_' || u == '$') return true;
            if (ms && (u == '#' || u == '@')) return true;
            if (mysql && u == '@') return true;
            return u > 0x7F && IsLetterOrMark(u);
        }
        bool IsWordStart(int u)
        {
            if (IsAsciiLetter(u) || u == '_') return true;
            if ((ms || mysql) && (u == '@' || u == '$')) return true;
            if (ms && u == '#') return true;
            return u > 0x7F && IsLetterOrMark(u);
        }
        string Text(int from, int to)
        {
            var sb = new StringBuilder();
            for (var k = from; k < Math.Min(to, n); k++) sb.Append(char.ConvertFromUtf32(Rune.IsValid(c[k]) ? c[k] : 0xFFFD));
            return sb.ToString();
        }
        // start 는 여는 따옴표. 닫는 따옴표 다음 위치를 돌려준다. (겹쳐 쓴 따옴표는 글자 하나)
        int SkipQuoted(int start, int close, bool escapes)
        {
            var j = start + 1;
            while (j < n)
            {
                if (escapes && c[j] == '\\') { j += 2; continue; }
                if (c[j] == close)
                {
                    if (At(j + 1) == close) { j += 2; continue; }
                    return j + 1;
                }
                j++;
            }
            return n;
        }
        // PostgreSQL `$tag$` 의 끝 `$` 위치 (달러 인용 시작이 아니면 null)
        int? DollarTagEnd(int start)
        {
            if (start > 0 && IsWordChar(c[start - 1])) return null;
            var j = start + 1;
            while (j < n)
            {
                if (c[j] == '$') return j;
                if (!(IsAsciiLetter(c[j]) || c[j] == '_' || c[j] > 0x7F || (j > start + 1 && IsDigit(c[j])))) return null;
                j++;
            }
            return null;
        }

        while (i < n)
        {
            var u = c[i];
            // 공백 (유니코드 공백 · 제어 문자 포함)
            if (IsSpaceOrControl(u)) { i++; continue; }
            // MySQL 실행 주석의 끝
            if (inExecutableComment && u == '*' && At(i + 1) == '/')
            {
                inExecutableComment = false;
                i += 2;
                continue;
            }
            // 줄 주석: --  (MySQL 은 뒤에 공백 · 제어 문자 · 끝이 와야 주석)
            if (u == '-' && At(i + 1) == '-')
            {
                var next = At(i + 2);
                var isComment = !mysql || next == null || IsSpaceOrControl(next.Value);
                if (isComment)
                {
                    while (i < n && c[i] != '\n') i++;
                    continue;
                }
            }
            // # 줄 주석은 MySQL 만 (SQL Server 는 #임시테이블, PostgreSQL 은 연산자)
            if (mysql && u == '#')
            {
                while (i < n && c[i] != '\n') i++;
                continue;
            }
            // 블록 주석
            if (u == '/' && At(i + 1) == '*')
            {
                if (mysql && !inExecutableComment && (At(i + 2) == '!' || (At(i + 2) == 'M' && At(i + 3) == '!')))
                {
                    // /*!50000 … */, /*M! … */ 는 MySQL · MariaDB 가 실행하는 코드
                    i += At(i + 2) == '!' ? 3 : 4;
                    while (i < n && IsDigit(c[i])) i++;
                    inExecutableComment = true;
                    continue;
                }
                if (mysql)
                {
                    i += 2;
                    while (i < n && !(c[i] == '*' && At(i + 1) == '/')) i++;
                    i = Math.Min(n, i + 2);
                }
                else
                {
                    // PostgreSQL · SQL Server 는 블록 주석을 겹쳐 쓸 수 있다
                    var depth = 1;
                    i += 2;
                    while (i < n && depth > 0)
                    {
                        if (c[i] == '/' && At(i + 1) == '*') { depth++; i += 2; }
                        else if (c[i] == '*' && At(i + 1) == '/') { depth--; i += 2; }
                        else i++;
                    }
                }
                continue;
            }
            // 문자열
            if (u == '\'')
            {
                var end = SkipQuoted(i, '\'', (mysql || pg) && backslash);
                output.Add(new(SqlTokenKind.String, Text(i + 1, end - 1)));
                i = end;
                continue;
            }
            // 따옴표 식별자 (MySQL 의 "…" 은 문자열일 수도 있다 — 어느 쪽이든 코드가 아님)
            if (u == '"')
            {
                var end = SkipQuoted(i, '"', mysql && backslash);
                output.Add(new(SqlTokenKind.Quoted, Text(i + 1, end - 1)));
                i = end;
                continue;
            }
            if (mysql && u == '`')
            {
                var end = SkipQuoted(i, '`', false);
                output.Add(new(SqlTokenKind.Quoted, Text(i + 1, end - 1)));
                i = end;
                continue;
            }
            if (ms && u == '[')
            {
                var end = SkipQuoted(i, ']', false);
                output.Add(new(SqlTokenKind.Quoted, Text(i + 1, end - 1)));
                i = end;
                continue;
            }
            // PostgreSQL 달러 인용 $$ … $$, $tag$ … $tag$
            if (pg && u == '$' && DollarTagEnd(i) is int tagEnd)
            {
                var tagLen = tagEnd - i + 1;
                var j = tagEnd + 1;
                var bodyEnd = n;
                while (j + tagLen <= n)
                {
                    if (c.AsSpan(j, tagLen).SequenceEqual(c.AsSpan(i, tagLen))) { bodyEnd = j; j += tagLen; break; }
                    j++;
                }
                if (bodyEnd == n) j = n;
                output.Add(new(SqlTokenKind.String, Text(tagEnd + 1, bodyEnd)));
                i = j;
                continue;
            }
            // 숫자: 숫자 · 소수점 · 지수까지만. 뒤에 붙은 글자는 새 단어로 본다 (SQL Server 는 1DELETE 를 1 과 DELETE 로 읽는다)
            if (IsDigit(u) || (u == '.' && At(i + 1) is int d && IsDigit(d)))
            {
                var j = i;
                while (j < n && (IsDigit(c[j]) || c[j] == '.')) j++;
                if (j < n && (c[j] == 'e' || c[j] == 'E'))
                {
                    var k = j + 1;
                    if (k < n && (c[k] == '+' || c[k] == '-')) k++;
                    if (k < n && IsDigit(c[k]))
                    {
                        j = k;
                        while (j < n && IsDigit(c[j])) j++;
                    }
                }
                output.Add(new(SqlTokenKind.Number, Text(i, j)));
                i = j;
                continue;
            }
            // 단어
            if (IsWordStart(u))
            {
                var j = i + 1;
                while (j < n && IsWordChar(c[j])) j++;
                var word = Text(i, j).ToUpperInvariant();
                // PostgreSQL E'…' 는 항상 \ 이스케이프
                if (pg && word == "E" && At(j) == '\'')
                {
                    var end = SkipQuoted(j, '\'', true);
                    output.Add(new(SqlTokenKind.String, Text(j + 1, end - 1)));
                    i = end;
                    continue;
                }
                output.Add(new(SqlTokenKind.Word, word));
                i = j;
                continue;
            }
            // 그 밖의 글자는 기호 하나
            output.Add(new(SqlTokenKind.Symbol, Text(i, i + 1)));
            i++;
        }
        return output;
    }

    // MARK: - 검사

    internal static readonly HashSet<string> ReadOnlyLeading = ["SELECT", "SHOW", "DESC", "DESCRIBE", "EXPLAIN", "USE", "WITH"];

    /// 문장 어디에 있어도 막는 단어
    internal static readonly HashSet<string> ReadOnlyBlockedWords =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "REPLACE", "INTO",
        "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME", "GRANT", "REVOKE", "DENY",
        "EXEC", "EXECUTE", "CALL", "DO", "COPY", "LOAD", "LOCK", "UNLOCK",
        "VACUUM", "ANALYZE", "CLUSTER", "REINDEX", "REFRESH", "COMMENT", "SET", "DECLARE",
        "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT",
        "KILL", "SHUTDOWN", "HANDLER", "OPTIMIZE", "REPAIR", "FLUSH", "RESET", "PURGE", "INSTALL", "UNINSTALL",
        // PostgreSQL
        "NOTIFY", "LISTEN", "UNLISTEN", "DISCARD", "PREPARE", "REASSIGN", "IMPORT", "CHECKPOINT",
        // SQL Server
        "DBCC", "BACKUP", "RESTORE", "BULK", "RECONFIGURE", "WRITETEXT", "UPDATETEXT", "SETUSER", "ENABLE", "DISABLE",
    ];

    /// 상태를 바꾸는 함수 (대문자). 스키마를 붙여 불러도 (pg_catalog.set_config) 잡는다.
    internal static readonly HashSet<string> ReadOnlyBlockedFunctions =
    [
        // PostgreSQL
        "SET_CONFIG", "NEXTVAL", "SETVAL", "PG_TERMINATE_BACKEND", "PG_CANCEL_BACKEND",
        "PG_RELOAD_CONF", "PG_ROTATE_LOGFILE", "PG_SWITCH_WAL", "PG_PROMOTE", "PG_NOTIFY",
        "PG_READ_FILE", "PG_READ_BINARY_FILE", "PG_LS_DIR", "PG_STAT_FILE", "PG_LOGICAL_EMIT_MESSAGE",
        "PG_FILE_WRITE", "PG_FILE_UNLINK", "PG_FILE_RENAME", "PG_IMPORT_SYSTEM_COLLATIONS", "LOWRITE",
        "QUERY_TO_XML", "QUERY_TO_XMLSCHEMA", "QUERY_TO_XML_AND_XMLSCHEMA", "CURSOR_TO_XML",
        // MySQL
        "GET_LOCK", "RELEASE_LOCK", "RELEASE_ALL_LOCKS", "LOAD_FILE",
        // SQL Server
        "OPENQUERY", "OPENROWSET", "OPENDATASOURCE",
    ];

    internal static readonly string[] ReadOnlyBlockedFunctionPrefixes =
    [
        "PG_ADVISORY_", "PG_TRY_ADVISORY_", "PG_STAT_RESET", "PG_CREATE_", "PG_DROP_", "PG_REPLICATION_",
        "PG_COPY_", "PG_WAL_REPLAY_", "DBLINK", "LO_",
    ];

    internal static bool IsBlockedFunction(string name)
    {
        var u = name.ToUpperInvariant();
        return ReadOnlyBlockedFunctions.Contains(u) || ReadOnlyBlockedFunctionPrefixes.Any(p => u.StartsWith(p, StringComparison.Ordinal));
    }

    internal static ReadOnlyViolation? ScanReadOnly(List<SqlToken> t, DatabaseKind kind)
    {
        if (t.Count == 0 || t[0].Kind != SqlTokenKind.Word) return ReadOnlyViolation.Leading("");
        var first = t[0];
        if (!ReadOnlyLeading.Contains(first.Text)) return ReadOnlyViolation.Leading(first.Text);
        // EXPLAIN 의 옵션 범위: EXPLAIN ANALYZE … / EXPLAIN (ANALYZE, …) …
        var explainOptionsEnd = 0;
        if (first.Text == "EXPLAIN" && t.Count > 1)
        {
            if (t[1].IsSymbol("("))
            {
                var close = t.FindIndex(x => x.IsSymbol(")"));
                explainOptionsEnd = close >= 0 ? close : t.Count;
            }
            else explainOptionsEnd = 1;
        }

        string? Word(int k) => k < t.Count && t[k].Kind == SqlTokenKind.Word ? t[k].Text : null;
        // 단어면 그 글자, 아니면 토큰 글자 (대문자) — Mac `word(k)?.uppercased() ?? t[k].text.uppercased()`
        string? AnyText(int k) => Word(k)?.ToUpperInvariant() ?? (k < t.Count ? t[k].Text.ToUpperInvariant() : null);

        for (var idx = 0; idx < t.Count; idx++)
        {
            var tok = t[idx];
            SqlToken? prev = idx > 0 ? t[idx - 1] : null;
            SqlToken? next = idx + 1 < t.Count ? t[idx + 1] : null;

            // 행 잠금
            if (tok.IsWord("FOR"))
            {
                switch (Word(idx + 1), Word(idx + 2), Word(idx + 3))
                {
                    case ("UPDATE", _, _): return ReadOnlyViolation.Lock("FOR UPDATE");
                    case ("SHARE", _, _): return ReadOnlyViolation.Lock("FOR SHARE");
                    case ("NO", "KEY", "UPDATE"): return ReadOnlyViolation.Lock("FOR NO KEY UPDATE");
                    case ("KEY", "SHARE", _): return ReadOnlyViolation.Lock("FOR KEY SHARE");
                }
            }
            if (tok.IsWord("LOCK") && Word(idx + 1) == "IN" && Word(idx + 2) == "SHARE" && Word(idx + 3) == "MODE")
                return ReadOnlyViolation.Lock("LOCK IN SHARE MODE");
            // 함수 호출 name(
            if (tok.IsIdentifier && next?.IsSymbol("(") == true && IsBlockedFunction(tok.Text))
                return ReadOnlyViolation.Function(tok.Text.ToLowerInvariant());
            // SQL Server 시스템 · 확장 프로시저
            if (kind == DatabaseKind.Mssql && tok.IsIdentifier)
            {
                var u = tok.Text.ToUpperInvariant();
                if (u.StartsWith("SP_", StringComparison.Ordinal) || u.StartsWith("XP_", StringComparison.Ordinal))
                    return ReadOnlyViolation.Function(tok.Text.ToLowerInvariant());
            }
            // SQL Server 상태를 바꾸는 문장 · 식 (#193). send · receive 는 열 이름일 수 있어 문장 자리에서만 막는다
            if (kind == DatabaseKind.Mssql && tok.IsIdentifier)
            {
                var u = tok.Text.ToUpperInvariant();
                var statementStart = prev == null || prev.Value.IsSymbol(";");
                var n1 = AnyText(idx + 1);
                // Service Broker: 큐에서 메시지를 꺼내 지움 / 메시지 보내기
                if (u == "RECEIVE" && statementStart) return ReadOnlyViolation.Keyword("RECEIVE");
                if (u == "SEND" && n1 == "ON") return ReadOnlyViolation.Keyword("SEND");
                // 시퀀스 값 늘리기: NEXT VALUE FOR seq
                if (u == "NEXT" && n1 == "VALUE" && AnyText(idx + 2) == "FOR") return ReadOnlyViolation.Keyword("NEXT VALUE FOR");
            }
            if (tok.Kind != SqlTokenKind.Word || !ReadOnlyBlockedWords.Contains(tok.Text)) continue;
            // 예외: 열 이름 · 별칭으로 쓴 경우 (t.comment, AS lock)
            if (prev is { } p1 && p1.IsSymbol(".") && idx >= 2 && t[idx - 2].IsIdentifier) continue;
            if (prev is { } p2 && p2.IsWord("AS")) continue;
            switch (tok.Text)
            {
                case "REPLACE" when next?.IsSymbol("(") == true: continue;     // REPLACE(col, 'a', 'b') 문자열 함수
                case "ANALYZE" when idx <= explainOptionsEnd: continue;        // EXPLAIN ANALYZE SELECT (뒤 문장도 검사한다)
                case "CREATE" when first.Text == "SHOW" && idx == 1: continue; // SHOW CREATE TABLE
                case "SET" when kind is DatabaseKind.Mysql or DatabaseKind.Redis && prev?.IsWord("CHARACTER") == true: continue;  // CHARACTER SET utf8mb4
                case "COMMENT" when next?.IsWord("ON") != true: continue;      // COMMENT ON … 만 문장 (comment 열 이름은 흔하다)
            }
            return ReadOnlyViolation.Keyword(tok.Text);
        }
        return null;
    }
}
