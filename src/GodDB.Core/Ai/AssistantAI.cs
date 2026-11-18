using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GodDB.Core.Schema;
using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.Core.Ai;

/// 테이블 크기 통계 (메타데이터만, 데이터 행 아님)
/// Rows: 추정 행 수 (MySQL TABLE_ROWS / PG reltuples 는 근사값)
public sealed record TableStats(string Table, long? Rows, long? DataBytes, long? IndexBytes, string? Engine = null, string? Updated = null);

/// 메인 창 오른쪽 AI 어시스턴트 (로컬 Ollama)
/// 보내는 것: 테이블 구조·코멘트·인덱스·관계, 테이블 크기 통계, 자동 점검 결과, 편집기의 SQL. 데이터 행은 보내지 않는다.
public static class AssistantAI
{
    public const int MaxContextChars = 48_000;

    // MARK: 통계

    /// [테이블, 행 수, 데이터 바이트, 인덱스 바이트, 엔진, 마지막 수정] — 실패해도 괜찮은 보조 정보
    public static string? StatsSql(DatabaseKind kind, string schema)
    {
        var s = SqlText.QuoteString(schema);
        return kind switch
        {
            DatabaseKind.Mysql => $"""
                SELECT TABLE_NAME, TABLE_ROWS, DATA_LENGTH, INDEX_LENGTH, ENGINE, UPDATE_TIME
                  FROM information_schema.TABLES
                 WHERE TABLE_SCHEMA = {s} AND TABLE_TYPE = 'BASE TABLE'
                """,
            DatabaseKind.Postgres => $"""
                SELECT c.relname, c.reltuples::bigint, pg_table_size(c.oid), pg_indexes_size(c.oid), NULL, NULL
                  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = {s} AND c.relkind IN ('r', 'p')
                """,
            DatabaseKind.Mssql => """
                SELECT CASE WHEN s.name = 'dbo' THEN t.name ELSE s.name + '.' + t.name END,
                       SUM(CASE WHEN p.index_id IN (0, 1) THEN p.row_count ELSE 0 END),
                       SUM(CASE WHEN p.index_id IN (0, 1) THEN p.used_page_count ELSE 0 END) * 8192,
                       SUM(CASE WHEN p.index_id > 1 THEN p.used_page_count ELSE 0 END) * 8192, NULL, NULL
                  FROM sys.dm_db_partition_stats p
                  JOIN sys.tables t ON t.object_id = p.object_id
                  JOIN sys.schemas s ON s.schema_id = t.schema_id
                 GROUP BY s.name, t.name
                """,
            _ => null,
        };
    }

    public static Dictionary<string, TableStats> ParseStats(IEnumerable<IReadOnlyList<string?>> rows)
    {
        var out_ = new Dictionary<string, TableStats>();
        static long? N(string? v)
        {
            if (v == null) return null;
            if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)) return (long)d;
            return null;
        }
        foreach (var r in rows)
        {
            if (r.Count < 4 || r[0] is not string name) continue;
            out_[name.ToLowerInvariant()] = new TableStats(name, N(r[1]) is long n ? Math.Max(0, n) : null, N(r[2]), N(r[3]),
                                                          r.Count > 4 ? r[4] : null, r.Count > 5 ? r[5] : null);
        }
        return out_;
    }

    /// 바이트 크기 (Mac ByteCountFormatter .binary 와 비슷하게)
    internal static string Size(long? b)
    {
        if (b is not long v) return "?";
        if (v == 0) return "Zero KB";
        if (Math.Abs(v) < 1024) return $"{v} bytes";
        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        double x = v;
        var i = -1;
        do { x /= 1024; i++; } while (Math.Abs(x) >= 1024 && i < units.Length - 1);
        var digits = i == 0 ? 0 : i == 1 ? 1 : 2;
        return x.ToString("F" + digits, CultureInfo.InvariantCulture) + " " + units[i];
    }

    // MARK: 자동 점검 (특이사항)

    /// 규칙으로 찾은 특이사항 (AI 없이도 보여줄 수 있는 것)
    public static List<string> Notes(TableMeta t, TableStats? stats)
    {
        var out_ = new List<string>();
        var pk = t.Indexes.FirstOrDefault(i => i.IsPrimary);
        if (pk == null && !t.Columns.Any(c => c.IsPrimaryKey)) out_.Add("no primary key");
        if (AiText.TrimSpaces(t.Comment).Length == 0) out_.Add("no table comment");
        var noComment = t.Columns.Where(c => AiText.TrimSpaces(c.Comment).Length == 0).ToList();
        if (noComment.Count > 0 && t.Columns.Count > 0)
            out_.Add($"{noComment.Count}/{t.Columns.Count} columns without comment");
        // FK 컬럼이 인덱스 맨 앞에 없음
        foreach (var fk in t.ForeignKeys)
        {
            var first = fk.Columns.FirstOrDefault()?.ToLowerInvariant() ?? "";
            if (!t.Indexes.Any(i => i.Columns.FirstOrDefault()?.ToLowerInvariant() == first))
                out_.Add($"FK {fk.Name}({string.Join(",", fk.Columns)}) has no index starting with it");
            var nullable = fk.Columns.Where(c => t.Columns.FirstOrDefault(x => x.Name.ToLowerInvariant() == c.ToLowerInvariant())?.Nullable == true).ToList();
            if (nullable.Count > 0) out_.Add($"nullable FK column {string.Join(",", nullable)}");
        }
        // 다른 인덱스의 앞부분과 같은 인덱스 (중복 가능성)
        foreach (var a in t.Indexes.Where(i => !i.IsPrimary))
        {
            foreach (var b in t.Indexes.Where(b => b.Name != a.Name && b.Columns.Count > a.Columns.Count))
            {
                if (b.Columns.Take(a.Columns.Count).Select(c => c.ToLowerInvariant()).SequenceEqual(a.Columns.Select(c => c.ToLowerInvariant())) && !a.Unique)
                {
                    out_.Add($"index {a.Name} is a prefix of {b.Name} (possibly redundant)");
                    break;
                }
            }
        }
        if (t.Indexes.Count(i => !i.IsPrimary) >= 8) out_.Add($"many indexes ({t.Indexes.Count}) — write cost");
        if (stats?.Rows is long rows && rows >= 1_000_000) out_.Add($"large table (~{rows} rows)");
        string[] lobTypes = ["text", "longtext", "mediumtext", "blob", "longblob", "json", "jsonb", "bytea"];
        var lob = t.Columns.Where(c => lobTypes.Contains(c.ColumnType.ToLowerInvariant()) || c.ColumnType.ToLowerInvariant().EndsWith("(max)", StringComparison.Ordinal)).ToList();
        if (lob.Count >= 3) out_.Add($"{lob.Count} large-object columns ({string.Join(",", lob.Select(c => c.Name))})");
        return out_;
    }

    // MARK: 문맥

    /// 질문·선택에서 자세히 볼 테이블 고르기: 이름(물리명) 또는 코멘트 첫 줄(논리명)이 질문에 나오면
    public static List<TableMeta> MentionedTables(string text, IReadOnlyList<TableMeta> tables, int limit = 12)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Length == 0) return [];
        var hits = new List<(TableMeta Table, int Pos)>();
        foreach (var t in tables)
        {
            var name = t.Name.ToLowerInvariant();
            var bare = name.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? name;
            var r = bare.Length == 0 ? -1 : lower.IndexOf(bare, StringComparison.Ordinal);
            if (r >= 0 && IsBoundary(lower, r, bare.Length))
            {
                hits.Add((t, r));
                continue;
            }
            var logical = AiText.FirstLine(t.Comment) is { } l ? AiText.TrimSpaces(l) : "";
            if (logical.Length >= 2 && logical.Length <= 30)
            {
                var p = lower.IndexOf(logical.ToLowerInvariant(), StringComparison.Ordinal);
                if (p >= 0) hits.Add((t, p));
            }
        }
        return hits.OrderBy(h => h.Pos).Take(limit).Select(h => h.Table).ToList();
    }

    internal static bool IsBoundary(string s, int start, int length)
    {
        static bool Word(char c) => (char.IsLetter(c) && c < 128) || char.IsNumber(c) || c == '_';
        var end = start + length;
        var before = start == 0 ? (char?)null : s[start - 1];
        var after = end >= s.Length ? (char?)null : s[end];
        return !(before is char b && Word(b)) && !(after is char a && Word(a));
    }

    public sealed record Context(
        DatabaseKind Kind,
        string Connection,
        string Database,
        IReadOnlyList<TableMeta> Tables,
        IReadOnlyList<TableMeta> Focus,
        IReadOnlyDictionary<string, TableStats>? Stats = null,
        string? Sql = null,
        string? LastError = null);

    /// 모델이 읽을 문맥 글
    public static string ContextText(Context c)
    {
        var stats = c.Stats ?? new Dictionary<string, TableStats>();
        var s = new StringBuilder($"Connection: {c.Connection} ({c.Kind.Title()})\nDatabase/schema: {c.Database}\n");
        s.Append($"Tables in this database: {c.Tables.Count}\n");
        // 전체 목록 (이름 + 논리명 + 대략 크기)
        var list = new StringBuilder();
        foreach (var t in c.Tables)
        {
            var line = $"  {t.Name}";
            if (AiText.FirstLine(t.Comment) is { Length: > 0 } l) line += $" — {AiText.Prefix(l, 40)}";
            if (stats.TryGetValue(t.Name.ToLowerInvariant(), out var st) && st.Rows is long r) line += $"  (~{r} rows)";
            list.Append(line + "\n");
            if (list.Length > 12_000) { list.Append($"  … ({c.Tables.Count} tables total, list truncated)\n"); break; }
        }
        s.Append(list);
        if (c.Focus.Count > 0)
        {
            s.Append("\nDetailed structure of the relevant tables:\n");
            var inferred = DiagramLayout.InferredRelations(c.Tables);
            s.Append(DiagramAI.SchemaText(c.Focus, inferred));
            s.Append("\nSize and automatic checks:\n");
            foreach (var t in c.Focus)
            {
                stats.TryGetValue(t.Name.ToLowerInvariant(), out var st);
                var line = $"  {t.Name}:";
                if (st != null)
                {
                    line += $" rows≈{(st.Rows is long rr ? rr.ToString(CultureInfo.InvariantCulture) : "?")}, data {Size(st.DataBytes)}, index {Size(st.IndexBytes)}";
                    if (st.Engine is { Length: > 0 } e) line += $", engine {e}";
                    if (st.Updated is { Length: > 0 } u) line += $", updated {u}";
                }
                var n = Notes(t, st);
                line += n.Count == 0 ? " no issues found" : "\n    - " + string.Join("\n    - ", n);
                s.Append(line + "\n");
            }
            // 참조하는 쪽 (다른 테이블이 이 테이블을 가리킴)
            var names = c.Focus.Select(t => t.Name.ToLowerInvariant()).ToHashSet();
            var incoming = c.Tables.SelectMany(t => t.ForeignKeys)
                .Where(f => names.Contains(f.ReferencedTable.ToLowerInvariant()) && !names.Contains(f.Table.ToLowerInvariant())).ToList();
            if (incoming.Count > 0)
                s.Append("Tables referencing them (FK): "
                         + string.Join(", ", incoming.Select(f => $"{f.Table}({string.Join(",", f.Columns)})→{f.ReferencedTable}")) + "\n");
        }
        if (c.Sql?.Trim() is { Length: > 0 } sql)
            s.Append($"\nSQL currently in the editor:\n```sql\n{AiText.Prefix(sql, 8_000)}\n```\n");
        if (c.LastError is { Length: > 0 } err)
            s.Append($"\nLast error in the editor:\n{AiText.Prefix(err, 2_000)}\n");
        var text = s.ToString();
        if (text.Length > MaxContextChars) text = AiText.Prefix(text, MaxContextChars) + "\n… (truncated)\n";
        return text;
    }

    public static string SystemPrompt(DatabaseKind kind, bool english) => $"""
        You are GodDB's database assistant, helping a developer working with a {kind.Title()} database.
        You receive metadata only: table structures, comments (business names), keys, indexes, relations, approximate sizes,
        automatic checks and the SQL in the editor. You never see row data — if the answer needs data, give a SELECT the user can run.
        Rules:
        - Base statements on the given metadata. Do not invent tables or columns; say when something is not in the context.
        - Use comments to explain business meaning. Mention notable points (missing keys/indexes, nullable FKs, large tables).
        - SQL in {kind.Title()} syntax inside ```sql blocks. Never run or recommend DROP/TRUNCATE/DELETE without a WHERE and a warning.
        - You also get a short GodDB manual and the current app state. For questions about GodDB itself (who made it,
          how to use a feature, shortcuts, where settings are) answer from that manual; do not invent menus or features.
        - Answer in {(english ? "English" : "Korean (한국어)")}, concise, using Markdown headings and bullet points.
        """;

    public static List<OllamaMessage> Messages(DatabaseKind kind, string context, IReadOnlyList<(string Role, string Content)> history, bool english)
    {
        var out_ = new List<OllamaMessage> { new("system", SystemPrompt(kind, english)) };
        // 대화 기록 앞부분은 그대로, 마지막 질문 바로 앞에 최신 문맥을 넣는다 (테이블을 바꿔 물어도 맞는 정보)
        out_.AddRange(history.Take(Math.Max(0, history.Count - 1)).Select(h => new OllamaMessage(h.Role, h.Content)));
        out_.Add(new("user", "Context (current state, metadata only):\n" + context));
        out_.Add(new("assistant", english ? "Got the context." : "문맥을 확인했어요."));
        if (history.Count > 0) out_.Add(new(history[^1].Role, history[^1].Content));
        return out_;
    }

    // MARK: 패널 로직 (Mac 판 AssistantPanel.swift 의 AppState 확장에서 화면과 무관한 부분)

    /// 대화 범위 (모델 / 접속·DB / 일반) — 범위마다 대화 기록이 따로 저장된다
    public static string Scope(Guid? modelId, Guid? connectedProfileId, string currentDatabase)
    {
        if (modelId is Guid m) return $"assistant|model|{m.ToString().ToUpperInvariant()}";
        if (connectedProfileId is Guid p) return $"assistant|{p.ToString().ToUpperInvariant()}|{currentDatabase}";
        return "assistant|general";
    }

    /// 패널 머리글 (Mac: assistantTitle)
    public static string ScopeTitle(string? modelName, string? connectedProfileName, bool isRedis, string currentDatabase)
    {
        if (modelName != null) return Tr("모델 · %@", modelName);
        if (connectedProfileName != null) return isRedis ? connectedProfileName : $"{connectedProfileName} / {currentDatabase}";
        return Tr("접속 안 됨");
    }

    /// 자세히 볼 테이블: 질문에 나온 것 + 사이드바에서 고른 것 + 지금 탭의 테이블 (+ 편집기 SQL 에 나온 것)
    public static List<TableMeta> Focus(string question, IReadOnlyList<TableMeta> tables, IEnumerable<string> selectedTables,
                                        string? tabTable = null, string? editorSql = null)
    {
        var out_ = MentionedTables(question, tables);
        var names = out_.Select(t => t.Name).ToHashSet();
        var extra = selectedTables.ToList();
        if (tabTable != null) extra.Add(tabTable);
        if (editorSql != null) extra.AddRange(MentionedTables(editorSql, tables, limit: 8).Select(t => t.Name));
        foreach (var n in extra)
        {
            if (names.Contains(n)) continue;
            if (tables.FirstOrDefault(t => t.Name == n) is { } t) { out_.Add(t); names.Add(n); }
        }
        return out_.Take(12).ToList();
    }

    /// 지금 앱 상태 (질문 "지금 어디 붙어있어?" 같은 것에 답하도록). 접속 정보(호스트·계정)는 넣지 않는다.
    public sealed record AppState(
        string Version,
        string Mode,
        int SavedConnections,
        IReadOnlyList<string> Groups,
        string? SelectedConnection = null,
        DatabaseKind SelectedKind = DatabaseKind.Mysql,
        bool ReadOnly = false,
        bool Connected = false,
        string CurrentDatabase = "",
        int TableCount = 0,
        IReadOnlyList<string>? SelectedTables = null,
        int OpenTabs = 0,
        string? ModelName = null,
        int ModelTables = 0,
        int ModelRelations = 0);

    public static string AppStateText(AppState a)
    {
        var s = $"  GodDB version: {a.Version}\n  Mode: {a.Mode}\n";
        s += $"  Saved connections: {a.SavedConnections}";
        var groups = a.Groups.Where(g => g.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList();
        if (groups.Count > 0) s += $" in groups {string.Join(", ", groups)}";
        s += "\n";
        if (a.SelectedConnection is { } name)
        {
            s += $"  Selected connection: {name} ({a.SelectedKind.Title()}){(a.ReadOnly ? ", read-only" : "")}, ";
            s += a.Connected ? $"connected, database/schema {a.CurrentDatabase}, {a.TableCount} tables\n" : "not connected\n";
        }
        else
        {
            s += "  No connection selected\n";
        }
        if (a.SelectedTables is { Count: > 0 } sel)
            s += $"  Tables selected in the sidebar: {string.Join(", ", sel.Order(StringComparer.Ordinal).Take(10))}\n";
        s += $"  Open query tabs: {a.OpenTabs}\n";
        if (a.ModelName != null) s += $"  Open model: {a.ModelName} ({a.ModelTables} tables, {a.ModelRelations} relations)\n";
        return s;
    }

    /// 앱 정보 + 앱 상태 + 질문과 관련된 기능 명세 절 (앱 안에 넣은 FEATURES.md)
    public static string GuideText(string question, string appState, AppGuide? guide = null)
    {
        var g = guide ?? AppGuide.Bundled;
        if (g == null) return "About this app:\n" + AppGuide.About + "\nCurrent app state:\n" + appState;
        return g.ContextText(question, appState);
    }

    /// 모델링 모드: 지금 모델을 문맥으로 (modelText 는 모델링 포팅의 ModelAI.modelText 결과)
    public static List<OllamaMessage> ModelingMessages(DatabaseKind kind, string guide, string modelText,
                                                       IReadOnlyList<(string Role, string Content)> history, bool english)
    {
        var out_ = new List<OllamaMessage> { new("system", ModelAI.SystemPrompt(kind, english)) };
        out_.AddRange(history.Take(Math.Max(0, history.Count - 1)).Select(h => new OllamaMessage(h.Role, h.Content)));
        out_.Add(new("user", guide + "\nCurrent model:\n" + modelText));
        out_.Add(new("assistant", english ? "Got the model." : "모델을 확인했어요."));
        out_.Add(new("user", history.Count > 0 ? history[^1].Content : ""));
        return out_;
    }

    /// Redis 접속: 키 값은 보내지 않는다
    public static string RedisContextText(string guide, string connectionName, string redisModeTitle) =>
        guide + $"\nConnection: {connectionName} (Redis, {redisModeTitle}). Key values are not shared with you.\n";

    /// 자주 쓰는 질문 (모델링 / SQL 접속 / 접속 없음)
    public static List<string> QuickActions(bool modeling, bool sqlConnected, IEnumerable<string> selectedTables,
                                            DatabaseKind modelKind = DatabaseKind.Mysql, bool? english = null)
    {
        if (modeling)
        {
            return [AiText.FirstLine(ModelAI.PhysicalNameQuestion(english ?? IsEnglish, modelKind)) ?? "",
                    Tr("이 모델의 정규화와 키 설계를 검토해 줘"),
                    Tr("빠진 관계나 필요한 테이블·컬럼을 제안해 줘"),
                    Tr("공통 컬럼(등록일시, 수정일시, 등록자 등) 규칙을 제안해 줘")];
        }
        if (sqlConnected)
        {
            var selected = string.Join(", ", selectedTables.Order(StringComparer.Ordinal).Take(3));
            return [selected.Length == 0 ? Tr("이 DB 의 핵심 테이블과 업무 도메인을 요약해 줘") : Tr("%@ 테이블을 설명하고 특이사항을 정리해 줘", selected),
                    Tr("지금 편집기 SQL 을 설명하고 성능 문제를 점검해 줘"),
                    Tr("마지막 오류 원인과 고치는 방법을 알려 줘"),
                    Tr("인덱스가 빠졌거나 중복된 테이블을 찾아 줘"),
                    Tr("코멘트(논리명)가 없는 테이블·컬럼을 정리해 줘")];
        }
        return [Tr("MySQL 과 PostgreSQL 의 트랜잭션 격리 수준 차이를 알려 줘")];
    }

    /// 모델링에서 첫 번째 빠른 질문은 물리명 추천 (JSON 형식 지시 포함 전체 문장을 보낸다)
    public static string ExpandQuickAction(string question, bool modeling, DatabaseKind modelKind = DatabaseKind.Mysql, bool? english = null)
    {
        var en = english ?? IsEnglish;
        if (modeling && question == AiText.FirstLine(ModelAI.PhysicalNameQuestion(en, modelKind)))
            return ModelAI.PhysicalNameQuestion(en, modelKind);
        return question;
    }

    /// 테이블 통계를 못 가져왔을 때 상태창 문장
    public static string StatsFailedMessage => Tr("AI 어시스턴트: 테이블 통계를 못 가져와서 구조만 보내요.");
}

// MARK: - 모델링 AI

/// 물리명 추천을 받을 대상 (모델링 포팅의 DataModel 이 구현한다)
/// Mac 판은 DataModel 을 직접 받는다. Windows 판은 모델링 영역이 따로 포팅되므로 이 인터페이스로 잇는다.
public interface IPhysicalNameTarget
{
    int TableCount { get; }
    int ColumnCount(int table);
    string TablePhysicalName(int table);
    void SetTablePhysicalName(int table, string name);
    string ColumnPhysicalName(int table, int column);
    void SetColumnPhysicalName(int table, int column, string name);
    /// 바뀐 것이 있을 때 한 번 (Mac: m.touch() — 수정 시각 갱신)
    void Touch();
}

public static partial class ModelAI
{
    public static string PhysicalNameQuestion(bool english, DatabaseKind kind)
    {
        const string rules = """
            Suggest physical (database) names for tables and columns whose physical name is "-".
            Use English snake_case, lowercase, common abbreviations (e.g. 회원→member, 일시→_at or _dt, 여부→is_/yn, 코드→_cd).
            Table names start with "tb_". Keep existing physical names unchanged and keep naming consistent across tables.
            Reply with a short explanation, then exactly one ```json block in this form:
            {"tables":[{"key":"T1","physical":"tb_member","columns":[{"key":"T1.C2","physical":"email"}]}]}
            Include only items that had no physical name.
            """;
        return english ? rules : "논리명을 보고 물리명이 비어 있는(-) 테이블·컬럼의 물리명을 추천해 주세요.\n" + rules;
    }

    /// AI 답의 JSON 한 항목 (Column 이 null 이면 테이블 물리명). 0 부터 센 위치.
    public sealed record PhysicalName(int Table, int? Column, string Physical);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")] private static partial Regex Ident();
    [GeneratedRegex(@"```json\s*\n([\s\S]*?)```")] private static partial Regex JsonFence();

    /// AI 답의 JSON 을 읽는다 (키 모양만 확인, 범위·식별자 확인은 적용할 때). 형식이 틀리면 빈 목록.
    public static List<PhysicalName> ParsePhysicalNames(string answer)
    {
        var out_ = new List<PhysicalName>();
        if (JsonBlock(answer) is not { } json) return out_;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var tables = Req(doc.RootElement, "tables");
            if (tables.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
            // Swift Decodable 처럼 한 항목이라도 형식이 틀리면 전체를 버린다
            var parsed = new List<(string Key, string? Physical, List<(string Key, string Physical)> Columns)>();
            foreach (var t in tables.EnumerateArray())
            {
                var key = Str(Req(t, "key"));
                string? physical = t.TryGetProperty("physical", out var p) && p.ValueKind != System.Text.Json.JsonValueKind.Null ? Str(p) : null;
                var cols = new List<(string, string)>();
                if (t.TryGetProperty("columns", out var cs) && cs.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    if (cs.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
                    foreach (var c in cs.EnumerateArray()) cols.Add((Str(Req(c, "key")), Str(Req(c, "physical"))));
                }
                parsed.Add((key, physical, cols));
            }
            foreach (var (key, physical, cols) in parsed)
            {
                if (!key.StartsWith('T') || !TryIndex(key[1..], out var ti)) continue;
                if (physical != null) out_.Add(new PhysicalName(ti, null, physical));
                foreach (var (ck, cp) in cols)
                {
                    var parts = ck.Split('.', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || !parts[1].StartsWith('C') || !TryIndex(parts[1][1..], out var ci)) continue;
                    out_.Add(new PhysicalName(ti, ci, cp));
                }
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return [];
        }
        return out_;

        static System.Text.Json.JsonElement Req(System.Text.Json.JsonElement e, string name) =>
            e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : throw new KeyNotFoundException(name);
        static string Str(System.Text.Json.JsonElement e) =>
            e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString()! : throw new InvalidOperationException("not a string");
        static bool TryIndex(string s, out int index)
        {
            var ok = int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n);
            index = n - 1;
            return ok;
        }
    }

    /// AI 답의 JSON 을 모델에 적용. overwrite 가 false 면 비어 있는 물리명만. 바뀐 개수를 돌려준다.
    /// (화면의 "물리명 적용 (빈 칸만)" 버튼)
    public static int ApplyPhysicalNames(string answer, IPhysicalNameTarget m, bool overwrite = false)
    {
        var changed = 0;
        foreach (var n in ParsePhysicalNames(answer))
        {
            if (n.Table < 0 || n.Table >= m.TableCount) continue;
            var p = AiText.TrimSpaces(n.Physical);
            if (n.Column is not int ci)
            {
                if (Ident().IsMatch(p) && (overwrite || m.TablePhysicalName(n.Table).Length == 0) && m.TablePhysicalName(n.Table) != p)
                {
                    m.SetTablePhysicalName(n.Table, p);
                    changed++;
                }
                continue;
            }
            if (ci < 0 || ci >= m.ColumnCount(n.Table)) continue;
            if (Ident().IsMatch(p) && (overwrite || m.ColumnPhysicalName(n.Table, ci).Length == 0) && m.ColumnPhysicalName(n.Table, ci) != p)
            {
                m.SetColumnPhysicalName(n.Table, ci, p);
                changed++;
            }
        }
        if (changed > 0) m.Touch();
        return changed;
    }

    /// 적용 결과 상태창 문장
    public static string ApplyResultMessage(int changed) =>
        changed > 0 ? Tr("AI 물리명 %@개를 모델에 넣었어요.", changed) : Tr("넣을 물리명이 없어요. (비어 있는 칸만 채워요)");

    internal static string? JsonBlock(string text)
    {
        var m = JsonFence().Match(text);
        if (m.Success) return m.Groups[1].Value;
        int a = text.IndexOf('{'), b = text.LastIndexOf('}');
        return a >= 0 && b >= 0 && a < b ? text[a..(b + 1)] : null;
    }

    public static string SystemPrompt(DatabaseKind kind, bool english) => $"""
        You are a senior data modeler reviewing a logical/physical data model that will be generated as {kind.Title()} DDL.
        Tables are referenced as T1, T2…, columns as T1.C1… Logical names are business names (often Korean).
        - Review normalization, keys, relationships (identifying vs non-identifying, cardinality), naming and data types.
        - Be concrete: name the table/column and what to change. Do not invent requirements the model does not imply.
        - SQL in {kind.Title()} syntax inside ```sql blocks.
        - A short GodDB manual is included; answer questions about the app itself (author, how to use modeling) from it.
        - Answer in {(english ? "English" : "Korean (한국어)")}, concise, Markdown headings and bullets.
        """;
}
