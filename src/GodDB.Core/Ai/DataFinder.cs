using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GodDB.Core.Dialects;
using GodDB.Core.Schema;
using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.Core.Ai;

/// AI 데이터 찾기 (Mac 1.4.0 DataFinder.swift): "학년 정보는 어디 있어?" → 그 데이터가 든 테이블 · 컬럼을 찾는다.
/// 1) 테이블 설명의 찾는 말 먼저 → 2) 로컬 AI 가 후보를 고르고 → 3) 앱이 후보 컬럼 값 몇 개를 조회 → 4) 로컬 AI 가 판단.
/// AI 는 모두 로컬 Ollama(OllamaClient, localhost 만)로만 보낸다. 샘플 값은 이 기능에서만 보낸다.
public static partial class DataFinder
{
    /// 후보 최대 개수
    public const int MaxCandidates = 5;
    /// 후보 고르기에 보낼 구조 글의 글자 예산 (넘으면 핵심어와 겹치는 테이블 먼저)
    public const int DefaultBudget = 20_000;
    /// 샘플 조회: 가져올 행 수 · 남길 값 수 · 값 길이 · 제한 시간
    public const int SampleRows = 200;
    public const int MaxSamples = 20;
    public const int MaxSampleLength = 50;
    public const double SampleTimeout = 10;
    /// 큰 스키마: 1단계(테이블 목록)에서 AI 가 고를 테이블 수, 사전 점수로 늘 함께 넣을 테이블 수
    public const int MaxStage1Tables = 10;
    public const int ScorerExtraTables = 3;

    // MARK: - 질문 판단

    [GeneratedRegex(@"\b(select|insert|update|delete)\b")] private static partial Regex SqlWord();

    private static readonly string[] Unrelated =
    [
        "느려", "느린", "오류", "에러", "문제", "원인", "잘못", "인덱스", "가장", "제일", "크기",
        "error", "slow", "wrong", "bug", "index", "most", "largest", "biggest", "fail",
    ];

    private static readonly Regex[] FindPatterns =
    [
        new(@"(은|는|이|가)?\s*어디\s*(에서|에|서)?\s*(있|저장|들어)"),
        new("(어디야|어딨어|어딨나|어딨지|어딨니|어딨어요|어디예요|어디에요)"),
        new(@"^\s*where\s+(is|are)\b"),
        new(@"\bwhich\s+tables?\b"),
    ];

    /// "…어디 있어" "…어딨어" "where is …" "which table …" 꼴인지. 애매하면 false (일반 질문으로).
    public static bool IsFindQuestion(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Length > 80 || t.Contains('\n')) return false;
        var lower = t.ToLowerInvariant();
        // SQL 이 들어 있거나 성능 · 오류 · 크기를 묻는 질문은 일반 질문
        if (SqlWord().IsMatch(lower)) return false;
        if (Unrelated.Any(u => lower.Contains(u, StringComparison.Ordinal))) return false;
        if (!FindPatterns.Any(p => p.IsMatch(lower))) return false;
        return KeyTerms(t).Count > 0;
    }

    // MARK: - 핵심어

    /// 조사 (긴 것부터)
    internal static readonly string[] Particles =
        ["에서는", "에서", "으로", "에는", "이랑", "까지", "부터", "은", "는", "이", "가", "을", "를", "의", "에", "도", "만", "로", "와", "과", "랑"];

    /// 핵심어가 아닌 말
    internal static readonly HashSet<string> Fillers =
    [
        "정보", "데이터", "자료", "값", "내용", "위치", "어디", "어느", "무슨", "어떤", "테이블", "컬럼", "칼럼", "필드", "관련",
        "있어", "있나", "있니", "있지", "있는지", "있어요", "있나요", "저장", "좀", "찾아", "찾아줘", "알려", "알려줘", "혹시",
        "where", "is", "are", "the", "a", "an", "which", "table", "tables", "column", "columns", "stored", "store", "kept",
        "data", "info", "information", "of", "in", "for", "find", "does", "do", "what", "live", "lives", "located", "i",
        "can", "me", "my", "it", "has", "have", "holds", "hold", "contains", "contain", "field", "fields", "value", "values",
    ];

    private static readonly string[] VerbStems = ["있", "저장", "들어", "보관", "찾"];

    /// 질문의 핵심어 (조사 · 군말 · "어디/있어" 같은 물음말을 뺀 명사). "학년 정보는 어딨어?" → ["학년"]
    public static List<string> KeyTerms(string text)
    {
        var tokens = new List<string>();
        var cur = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetter(ch) || char.IsNumber(ch) || ch == '_') cur.Append(ch);
            else if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        var seen = new HashSet<string>();
        var output = new List<string>();
        foreach (var raw in tokens)
        {
            if (raw.StartsWith("어디", StringComparison.Ordinal) || raw.StartsWith("어딨", StringComparison.Ordinal)) continue;
            if (VerbStems.Any(v => raw.StartsWith(v, StringComparison.Ordinal))) continue;
            var word = raw;
            if (IsHangul(word))
            {
                foreach (var p in Particles)
                {
                    if (word.EndsWith(p, StringComparison.Ordinal) && word.Length - p.Length >= 2)
                    {
                        word = word[..^p.Length];
                        break;
                    }
                }
            }
            if (Fillers.Contains(word) || Fillers.Contains(raw)) continue;
            if (!IsHangul(word) && word.Length < 2) continue;
            if (seen.Add(word)) output.Add(word);
        }
        return output;
    }

    internal static bool IsHangul(string s) => s.Any(c => c is >= '가' and <= '힣');

    /// 비교용 정규화: 공백 제거 · 소문자 (한글은 그대로)
    public static string Normalize(string s) => new(s.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// 저장용 찾는 말: 핵심어를 한 말로 ("회원 주소")
    public static string Phrase(IEnumerable<string> terms) => string.Join(" ", terms);

    // MARK: - 테이블 설명(찾는 말) 먼저

    /// 이 접속 · DB 의 위치 중 질문 핵심어와 맞는 것 (확인된 것 먼저, 최근 순).
    /// 맞음: 찾는 말 하나가 핵심어 전체(공백 없이)와 같거나, 핵심어가 모두 찾는 말에 있음.
    public static List<TableNote> MatchNotes(IEnumerable<string> terms, IEnumerable<TableNote> notes)
    {
        var q = terms.Select(Normalize).Where(t => t.Length > 0).ToList();
        if (q.Count == 0) return [];
        var joined = string.Concat(q);
        return notes.Where(n =>
            {
                var nt = n.Terms.Select(Normalize).ToHashSet();
                return nt.Contains(joined) || q.All(nt.Contains);
            })
            .OrderByDescending(n => n.Confirmed)
            .ThenByDescending(n => n.UpdatedAt)
            .ToList();
    }

    // MARK: - 후보 고르기 (로컬 AI 1차)

    public sealed record Candidate(string Table, IReadOnlyList<string> Columns, string Reason = "")
    {
        public string Location => $"{Table}.{string.Join(", ", Columns)}";

        public bool Equals(Candidate? other) =>
            other is not null && Table == other.Table && Columns.SequenceEqual(other.Columns) && Reason == other.Reason;
        public override int GetHashCode() => HashCode.Combine(Table, Columns.Count, Reason);
    }

    /// 테이블이 핵심어와 얼마나 겹치는지 (이름 · 코멘트 3, 컬럼 이름 · 코멘트 2)
    internal static int Relevance(TableMeta t, IEnumerable<string> terms)
    {
        var name = Normalize(t.Name);
        var comment = Normalize(t.Comment);
        var score = 0;
        foreach (var term in terms.Select(Normalize).Where(x => x.Length > 0))
        {
            if (name.Contains(term, StringComparison.Ordinal) || comment.Contains(term, StringComparison.Ordinal)) score += 3;
            if (t.Columns.Any(c => Normalize(c.Name).Contains(term, StringComparison.Ordinal) || Normalize(c.Comment).Contains(term, StringComparison.Ordinal)))
                score += 2;
        }
        return score;
    }

    internal static string FirstLine(string s, int max)
    {
        var line = s.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return line.Length > max ? line[..max] : line;
    }

    internal static string TableBlock(TableMeta t)
    {
        var s = new StringBuilder(t.Name);
        var c = FirstLine(t.Comment, 80);
        if (c.Length > 0) s.Append($" — {c}");
        s.Append('\n');
        foreach (var col in t.Columns)
        {
            var line = $"  {col.Name} {col.ColumnType}";
            var cc = FirstLine(col.Comment, 60);
            if (cc.Length > 0) line += $" — {cc}";
            s.Append(line).Append('\n');
        }
        return s.ToString();
    }

    /// 보낼 구조 글. 예산을 넘으면 핵심어와 겹치는 테이블 먼저, 자리가 모자라면 이름 줄만.
    public static (string Text, int Count) SchemaText(IReadOnlyList<TableMeta> tables, IReadOnlyList<string> terms, int budget = DefaultBudget)
    {
        var ranked = tables.Select((t, i) => (I: i, T: t, Score: Relevance(t, terms)))
            .OrderByDescending(r => r.Score).ThenBy(r => r.I).ToList();
        var text = new StringBuilder();
        var count = 0;
        foreach (var r in ranked)
        {
            var block = TableBlock(r.T);
            if (text.Length + block.Length <= budget)
            {
                text.Append(block);
            }
            else
            {
                var line = r.T.Name;
                var c = FirstLine(r.T.Comment, 40);
                if (c.Length > 0) line += $" — {c}";
                line += "\n";
                if (text.Length + line.Length > budget) break;
                text.Append(line);
            }
            count++;
        }
        if (count < tables.Count) text.Append($"… ({tables.Count - count} more tables not shown)\n");
        return (text.ToString(), count);
    }

    // MARK: - 큰 스키마: 1단계 (테이블만)

    /// 모든 테이블의 컬럼까지 예산에 들어가지 않으면 두 단계로 (1단계: 테이블 목록, 2단계: 고른 테이블의 컬럼)
    public static bool NeedsTwoStages(IReadOnlyList<TableMeta> tables, int budget = DefaultBudget)
    {
        var total = 0;
        foreach (var t in tables)
        {
            total += TableBlock(t).Length;
            if (total > budget) return true;
        }
        return false;
    }

    /// 1단계에 보낼 테이블 목록 (이름 — 코멘트, 한 줄씩). 사전 점수가 있는 테이블을 먼저 두고 맞은 컬럼 이름을 붙인다
    /// ([matching columns: schyr]) — 목록이 잘려도 이 테이블들은 늘 들어간다.
    public static (string Text, int Count) TableListText(IReadOnlyList<TableMeta> tables, IReadOnlyList<string> terms, int budget = DefaultBudget)
    {
        var ranked = RankTables(terms, tables);
        var hits = new Dictionary<string, List<string>>();
        foreach (var r in ranked) hits.TryAdd(r.Table, r.Columns);
        var byName = new Dictionary<string, TableMeta>();
        foreach (var t in tables) byName.TryAdd(t.Name, t);
        var order = ranked.Select(r => byName.GetValueOrDefault(r.Table)).OfType<TableMeta>()
            .Concat(tables.Where(t => !hits.ContainsKey(t.Name))).ToList();
        var text = new StringBuilder();
        var count = 0;
        foreach (var t in order)
        {
            var line = t.Name;
            var c = FirstLine(t.Comment, 60);
            if (c.Length > 0) line += $" — {c}";
            if (hits.TryGetValue(t.Name, out var cols)) line += $"  [matching columns: {string.Join(", ", cols.Take(4))}]";
            line += "\n";
            if (text.Length + line.Length > budget) break;
            text.Append(line);
            count++;
        }
        if (count < tables.Count) text.Append($"… ({tables.Count - count} more tables not shown)\n");
        return (text.ToString(), count);
    }

    internal static string TableSystemPrompt(DatabaseKind kind) => $$"""
        You help a developer find where data lives in a large {{kind.Title()}} database.
        You receive a question, the key terms with the English names and abbreviations they are often stored under,
        and the list of tables (name — comment). Table and column names are often English abbreviations
        (for example stdnt = student, schl = school, schyr = school year/grade, mbr = member, nm = name, tel = phone).
        "[matching columns: …]" lists columns whose names match the key terms.
        Pick up to {{MaxStage1Tables}} tables that most likely hold the data in the question, best first. Use only tables from the list.
        Reply with JSON only, no other text, in exactly this form:
        {"tables":["…"]}
        """;

    public const string TableRetryPrompt = """
        That reply was not in the required form. Reply again with JSON only, no other text:
        {"tables":["…"]}
        """;

    /// 1단계 요청 메시지와 보낸 테이블 수
    public static (List<OllamaMessage> Messages, int TablesSent) TableMessages(string question, IReadOnlyList<string> terms, DatabaseKind kind,
        IReadOnlyList<TableMeta> tables, IReadOnlyList<TableNote> notes, int budget = DefaultBudget)
    {
        var list = TableListText(tables, terms, budget);
        var user = $"Question: {question}\nKey terms: {string.Join(", ", terms)}\n";
        var expansion = AbbreviationDictionary.ExpansionText(terms);
        if (expansion.Length > 0) user += $"Key terms as English DB names:\n{expansion}\n";
        user += $"\nTables ({tables.Count}):\n{list.Text}";
        if (notes.Count > 0)
        {
            user += "\nLocations saved by the team:\n";
            foreach (var n in notes.Take(50)) user += $"  - {string.Join(", ", n.Terms)} → {n.Location}\n";
        }
        return ([new("system", TableSystemPrompt(kind)), new("user", user)], list.Count);
    }

    /// 1단계 답 읽기. 형식이 틀리면 null, 맞으면 실제 있는 테이블 이름 (최대 10개).
    public static List<string>? ParseTables(string reply, IReadOnlyList<TableMeta> tables)
    {
        if (JsonObject(reply) is not { } obj) return null;
        List<string> raw;
        if (obj.TryGetProperty("tables", out var tl) && tl.ValueKind == JsonValueKind.Array)
            raw = tl.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()
                    : e.ValueKind == JsonValueKind.Object && e.TryGetProperty("table", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null)
                .OfType<string>().ToList();
        else if (obj.TryGetProperty("candidates", out var cl) && cl.ValueKind == JsonValueKind.Array)
            raw = cl.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("table", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null)
                .OfType<string>().ToList();
        else
            return null;
        var output = new List<string>();
        foreach (var name in raw)
        {
            if (ResolveTable(name, tables) is { } t && !output.Contains(t.Name)) output.Add(t.Name);
            if (output.Count == MaxStage1Tables) break;
        }
        return output;
    }

    /// 2단계로 보낼 테이블: AI 가 고른 것 + 사전 점수 상위 몇 개 (빠졌으면), 원래 TableMeta 로
    public static List<TableMeta> Stage2Tables(IReadOnlyList<string> picked, IReadOnlyList<string> terms, IReadOnlyList<TableMeta> tables)
    {
        var names = picked.ToList();
        foreach (var r in RankTables(terms, tables).Take(ScorerExtraTables))
            if (!names.Contains(r.Table)) names.Add(r.Table);
        return names.Select(n => tables.FirstOrDefault(t => t.Name == n)).OfType<TableMeta>().ToList();
    }

    internal static string CandidateSystemPrompt(DatabaseKind kind, bool english) => $$"""
        You help a developer find where data lives in a {{kind.Title()}} database.
        You receive a question, the tables (name — comment) with their columns (name type — comment), and locations the team saved before.
        Pick at most {{MaxCandidates}} candidate tables/columns that most likely hold the data in the question, best first.
        Use only tables and columns from the list. Comments are business names (often Korean) — rely on them.
        Names are often English abbreviations (stdnt = student, schl = school, schyr = school year/grade, mbr = member,
        nm = name, tel = phone, cd = code, no = number). Use the English names given for the key terms.
        Reply with JSON only, no other text, in exactly this form:
        {"candidates":[{"table":"…","columns":["…"],"reason":"…"}]}
        "reason" is one short sentence in {{(english ? "English" : "Korean (한국어)")}}.
        """;

    public const string RetryPrompt = """
        That reply was not in the required form. Reply again with JSON only, no other text:
        {"candidates":[{"table":"…","columns":["…"],"reason":"…"}]}
        """;

    /// 1차 요청 메시지와 보낸 테이블 수
    public static (List<OllamaMessage> Messages, int TablesSent) CandidateMessages(string question, IReadOnlyList<string> terms, DatabaseKind kind,
        IReadOnlyList<TableMeta> tables, IReadOnlyList<TableNote> notes, bool english, int budget = DefaultBudget)
    {
        var schema = SchemaText(tables, terms, budget);
        var user = $"Question: {question}\nKey terms: {string.Join(", ", terms)}\n";
        var expansion = AbbreviationDictionary.ExpansionText(terms);
        if (expansion.Length > 0) user += $"Key terms as English DB names:\n{expansion}\n";
        user += $"\nTables ({tables.Count}):\n{schema.Text}";
        if (notes.Count > 0)
        {
            user += "\nLocations saved by the team:\n";
            foreach (var n in notes.Take(50))
                user += $"  - {string.Join(", ", n.Terms)} → {n.Location}{(n.Confirmed ? "" : " (unconfirmed)")}\n";
        }
        return ([new("system", CandidateSystemPrompt(kind, english)), new("user", user)], schema.Count);
    }

    private static readonly char[] QuoteChars = ['`', '"', '[', ']'];

    /// 대소문자 · 스키마 접두어를 무시하고 실제 테이블 찾기
    internal static TableMeta? ResolveTable(string name, IReadOnlyList<TableMeta> tables)
    {
        var n = name.Trim().Trim(QuoteChars).ToLowerInvariant();
        if (n.Length == 0) return null;
        if (tables.FirstOrDefault(t => t.Name.ToLowerInvariant() == n) is { } exact) return exact;
        var bare = n.Split('.').LastOrDefault() ?? n;
        var hits = tables.Where(t => (t.Name.ToLowerInvariant().Split('.').LastOrDefault() ?? "") == bare).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    internal static List<string> ResolveColumns(IEnumerable<string> names, TableMeta t)
    {
        var output = new List<string>();
        foreach (var raw in names)
        {
            var n = (raw.Split('.').LastOrDefault() ?? raw).Trim('`', '"', '[', ']', ' ').ToLowerInvariant();
            if (t.Columns.FirstOrDefault(c => c.Name.ToLowerInvariant() == n) is { } c && !output.Contains(c.Name)) output.Add(c.Name);
        }
        return output;
    }

    /// 답에서 JSON 객체 꺼내기 (```json 블록 또는 첫 { ~ 마지막 })
    internal static JsonElement? JsonObject(string reply)
    {
        if (ModelAI.JsonBlock(reply) is not { } block) return null;
        try
        {
            using var doc = JsonDocument.Parse(block);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    internal static List<string> StringList(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v)) return [];
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        if (v.ValueKind == JsonValueKind.String) return v.GetString()!.Split(',').Select(s => s.Trim(' ', '\t')).ToList();
        return [];
    }

    internal static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// 1차 답 읽기. 형식이 틀리면 null (다시 요청), 맞으면 없는 테이블 · 컬럼을 버린 후보 (최대 5개, 비어 있을 수 있음).
    public static List<Candidate>? ParseCandidates(string reply, IReadOnlyList<TableMeta> tables)
    {
        if (JsonObject(reply) is not { } obj || !obj.TryGetProperty("candidates", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var output = new List<Candidate>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (Str(item, "table") is not { } name || ResolveTable(name, tables) is not { } t) continue;
            var cols = StringList(item, "columns");
            if (Str(item, "column") is { } one) cols.Add(one);
            var resolved = ResolveColumns(cols, t);
            if (resolved.Count == 0) continue;
            var c = new Candidate(t.Name, resolved, Str(item, "reason") ?? "");
            if (!output.Any(o => o.Table == c.Table && o.Columns.SequenceEqual(c.Columns))) output.Add(c);
            if (output.Count == MaxCandidates) break;
        }
        return output;
    }

    /// AI 없이: 약어 사전 · 이름 · 코멘트 점수 순 (ScoreColumns). 테이블마다 컬럼 3개까지.
    public static List<Candidate> FallbackCandidates(IReadOnlyList<string> terms, IReadOnlyList<TableMeta> tables, int limit = MaxCandidates) =>
        RankTables(terms, tables).Take(limit)
            .Select(r => new Candidate(r.Table, r.Columns.Take(3).ToList(), Tr("이름 · 코멘트 · 약어 사전 일치"))).ToList();

    /// AI 없이 고른 후보를 샘플 값 모양으로 다시 줄 세우기 (예: 학년 → 값이 1~12 인 컬럼 먼저)
    public static List<SampledCandidate> RankSampled(IReadOnlyList<string> terms, IReadOnlyList<SampledCandidate> sampled, IReadOnlyList<TableMeta> tables)
    {
        var scores = ScoreColumns(terms, tables);
        double ColScore(SampledCandidate s, string col) =>
            (scores.FirstOrDefault(x => x.Table == s.Candidate.Table && x.Column == col)?.Score ?? 0)
            + (s.Samples.TryGetValue(col, out var v) && v != null ? AbbreviationDictionary.ValueFit(terms, v) : 0) * 3;
        double Score(SampledCandidate s) =>
            s.Candidate.Columns.Select(c => ColScore(s, c)).DefaultIfEmpty(double.NegativeInfinity).Max();
        return sampled.Select((s, i) => (I: i, S: s, Score: Score(s)))
            .OrderByDescending(r => r.Score).ThenBy(r => r.I)
            .Select(r =>
            {
                // 후보 안에서도 값 모양이 맞는 컬럼을 앞으로
                var s = r.S.Clone();
                var cols = s.Candidate.Columns.Select((c, i) => (C: c, I: i))
                    .OrderByDescending(x => ColScore(r.S, x.C)).ThenBy(x => x.I).Select(x => x.C).ToList();
                s.Candidate = s.Candidate with { Columns = cols };
                return s;
            }).ToList();
    }

    // MARK: - 샘플 조회 (앱이 직접)

    /// 후보 컬럼 하나의 값 조회 SQL (MySQL · PostgreSQL LIMIT, SQL Server TOP)
    public static string SampleSql(string table, string column, ISqlDialect dialect)
    {
        var c = dialect.QuoteIdent(column);
        var t = dialect.QuoteTable(table);
        if (dialect.Kind == DatabaseKind.Mssql)
            // WITH (NOLOCK): 바쁜 운영 테이블의 잠금에 막혀 시간 초과되지 않게. 값 몇 개를 훑어보는 읽기 전용 조회라
            // 커밋 전 값(dirty read)이 섞여도 괜찮다.
            return $"SELECT TOP {SampleRows} {c} FROM {t} WITH (NOLOCK) WHERE {c} IS NOT NULL";
        return $"SELECT {c} FROM {t} WHERE {c} IS NOT NULL LIMIT {SampleRows}";
    }

    /// 조회한 값 정리: NULL · 빈 값 빼고, 50자로 자르고, 중복 빼고, 최대 20개
    public static List<string> CleanSamples(IEnumerable<string?> values)
    {
        var seen = new HashSet<string>();
        var output = new List<string>();
        foreach (var v in values)
        {
            if (v == null) continue;
            var one = v.Replace("\n", " ").Trim(' ', '\t');
            if (one.Length == 0) continue;
            var cut = one.Length > MaxSampleLength ? one[..MaxSampleLength] : one;
            if (seen.Add(cut)) output.Add(cut);
            if (output.Count == MaxSamples) break;
        }
        return output;
    }

    /// 제한 시간 안에 끝나면 결과, 넘으면 null (onTimeout 을 부르고 기다리지 않는다). 실패도 null.
    public static async Task<T?> WithTimeout<T>(double seconds, Func<Task<T>> operation, Func<Task>? onTimeout = null) where T : struct
    {
        var work = Task.Run(operation);
        var timer = Task.Delay(TimeSpan.FromSeconds(seconds));
        if (await Task.WhenAny(work, timer) != work)
        {
            _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            if (onTimeout != null) { try { await onTimeout(); } catch { } }
            return null;
        }
        try { return await work; }
        catch { return null; }
    }

    // MARK: - 판단 (로컬 AI 2차)

    /// 샘플 조회 하나의 결과
    public abstract record SampleOutcome
    {
        public sealed record Values(IReadOnlyList<string> List) : SampleOutcome
        {
            public bool Equals(Values? other) => other is not null && List.SequenceEqual(other.List);
            public override int GetHashCode() => List.Count;
        }
        public sealed record TimedOut : SampleOutcome;
        public sealed record Failed(string Message) : SampleOutcome;

        public IReadOnlyList<string>? ValueList => this is Values v ? v.List : null;
    }

    /// 후보 + 컬럼별 샘플 (값이 null = 샘플 없음: 시간 초과 · 실패, 키가 없음 = 조회 안 함)
    public sealed class SampledCandidate
    {
        public Candidate Candidate { get; set; }
        public Dictionary<string, List<string>?> Samples { get; }
        /// 컬럼별 조회 결과 (카드의 "값 N개 / 시간 초과 / 조회 실패")
        public Dictionary<string, SampleOutcome> Outcomes { get; }

        public SampledCandidate(Candidate candidate, Dictionary<string, List<string>?>? samples = null,
                                Dictionary<string, SampleOutcome>? outcomes = null)
        {
            Candidate = candidate;
            Samples = samples ?? [];
            Outcomes = outcomes ?? [];
        }

        public SampledCandidate Clone() => new(Candidate, new(Samples), new(Outcomes));

        /// 결과 하나 넣기 (값이면 Samples 에도)
        public void Record(string column, SampleOutcome outcome)
        {
            Outcomes[column] = outcome;
            Samples[column] = outcome.ValueList?.ToList();
        }

        /// 카드에 보일 조회 상태: 값이 있으면 "값 N개", 아니면 시간 초과 · 조회 실패. 조회 안 했으면 null.
        public string? SampleStatus
        {
            get
            {
                var list = Candidate.Columns.Where(Outcomes.ContainsKey).Select(c => Outcomes[c]).ToList();
                if (list.Count == 0) return null;
                if (list.Select(o => o.ValueList).FirstOrDefault(v => v is { Count: > 0 }) is { } v)
                    return Tr("값 %@개", v.Count);
                if (list.Any(o => o is SampleOutcome.TimedOut)) return Tr("시간 초과");
                if (list.OfType<SampleOutcome.Failed>().FirstOrDefault() is { } f) return Tr("조회 실패: %@", f.Message);
                return Tr("값 %@개", "0");
            }
        }

        /// 카드에 보여줄 값: 값이 있는 첫 컬럼의 값. 조회한 컬럼이 하나도 없으면 null.
        public List<string>? DisplaySamples
        {
            get
            {
                var lists = Candidate.Columns.Select(c => Samples.TryGetValue(c, out var v) ? v : null).OfType<List<string>>().ToList();
                if (lists.Count == 0) return Samples.Count == 0 ? null : [];
                return lists.FirstOrDefault(l => l.Count > 0) ?? [];
            }
        }
    }

    public enum Confidence { High, Medium, Low }

    public static string Label(this Confidence c) => c switch
    {
        Confidence.High => Tr("높음"),
        Confidence.Medium => Tr("중간"),
        _ => Tr("낮음"),
    };

    public sealed record Verdict(string Table, IReadOnlyList<string> Columns, Confidence Confidence, string Explain, string Sql, bool Fallback = false)
    {
        public bool Equals(Verdict? other) =>
            other is not null && Table == other.Table && Columns.SequenceEqual(other.Columns) && Confidence == other.Confidence
            && Explain == other.Explain && Sql == other.Sql && Fallback == other.Fallback;
        public override int GetHashCode() => HashCode.Combine(Table, Columns.Count, Confidence, Sql);
    }

    internal static string JudgeSystemPrompt(DatabaseKind kind, bool english) => $$"""
        You decide where the data a developer asks about lives in a {{kind.Title()}} database.
        You receive the question and candidate tables/columns with their comments. In this data-finding mode you also see
        small samples of the candidate columns (at most {{MaxSamples}} distinct values each, read by the app and sent only to this local model).
        Choose the single best table and its columns. Judge by comments, names and what the sample values look like
        (for example a school grade is 1–6 or 1–12, not VIP/GOLD). Names are often English abbreviations.
        Reply with JSON only, no other text, in exactly this form:
        {"table":"…","columns":["…"],"confidence":"high|medium|low","explain":"…","sql":"SELECT …"}
        - "explain": one or two sentences in {{(english ? "English" : "Korean (한국어)")}} — what the column holds, citing the comment and the values.
        - "sql": one simple {{kind.Title()}} SELECT that shows this data. Never write anything but SELECT.
        """;

    public static List<OllamaMessage> JudgeMessages(string question, IReadOnlyList<SampledCandidate> sampled, IReadOnlyList<TableMeta> tables,
                                                    DatabaseKind kind, bool english, IReadOnlyList<string>? terms = null)
    {
        terms ??= [];
        var user = new StringBuilder($"Question: {question}\n\nCandidates:\n");
        for (var i = 0; i < sampled.Count; i++)
        {
            var s = sampled[i];
            var t = tables.FirstOrDefault(x => x.Name == s.Candidate.Table);
            user.Append($"{i + 1}. {s.Candidate.Table}");
            if (t != null && FirstLine(t.Comment, 80) is { Length: > 0 } c) user.Append($" — {c}");
            user.Append('\n');
            foreach (var name in s.Candidate.Columns)
            {
                var col = t?.Columns.FirstOrDefault(x => x.Name == name);
                var line = $"   {name} {col?.ColumnType ?? ""}";
                if (col != null && FirstLine(col.Comment, 60) is { Length: > 0 } cc) line += $" — {cc}";
                user.Append(line).Append('\n');
                if (s.Samples.TryGetValue(name, out var values) && values != null)
                {
                    user.Append(values.Count == 0 ? "     samples: (no non-null values)\n" : "     samples: " + string.Join(" | ", values) + "\n");
                    if (AbbreviationDictionary.ValueHintText(terms, values) is { } hint) user.Append($"     hint: {hint}\n");
                }
                else
                {
                    user.Append("     samples: (not available)\n");
                }
            }
            if (s.Candidate.Reason.Length > 0) user.Append($"   why picked: {s.Candidate.Reason}\n");
        }
        return [new("system", JudgeSystemPrompt(kind, english)), new("user", user.ToString())];
    }

    [GeneratedRegex(@"\bINTO\b")] private static partial Regex IntoWord();
    [GeneratedRegex(@"\bFOR\s+(UPDATE|SHARE|NO\s+KEY\s+UPDATE|KEY\s+SHARE)\b")] private static partial Regex ForLock();
    [GeneratedRegex(@"\bLOCK\s+IN\s+SHARE\s+MODE\b")] private static partial Regex LockShare();

    /// 조회(SELECT) 하나만인지: 여러 문장 · SELECT INTO · 잠금(FOR UPDATE) · 쓰기 WITH 는 안 된다 (Mac 1.4.1: DB 별 읽기전용 검사)
    public static bool IsSelectOnly(string sql, DatabaseKind kind)
    {
        var body = SqlText.StripComments(sql).Trim().Trim(';').Trim();
        while (body.Length > 0 && (char.IsWhiteSpace(body[0]) || body[0] == ';')) body = body[1..];
        while (body.Length > 0 && (char.IsWhiteSpace(body[^1]) || body[^1] == ';')) body = body[..^1];
        if (body.Length == 0 || body.Contains(';')) return false;
        var kw = SqlText.FirstKeyword(body);
        if (kw is not ("SELECT" or "WITH") || !SqlText.IsReadOnly(sql, kind)) return false;
        var upper = body.ToUpperInvariant();
        return !IntoWord().IsMatch(upper) && !ForLock().IsMatch(upper) && !LockShare().IsMatch(upper);
    }

    /// 기본 SQL: SELECT * FROM <table> (실행할 때 평소처럼 LIMIT 이 붙는다)
    public static string DefaultSql(string table, ISqlDialect dialect) => $"SELECT * FROM {dialect.QuoteTable(table)}";

    /// 2차 답 읽기. 형식이 틀리거나 없는 테이블이면 null. 컬럼이 없거나 틀리면 그 테이블의 후보 컬럼, sql 은 SELECT 만.
    public static Verdict? ParseVerdict(string reply, IReadOnlyList<TableMeta> tables, IReadOnlyList<Candidate> candidates, ISqlDialect dialect)
    {
        if (JsonObject(reply) is not { } obj || Str(obj, "table") is not { } name || ResolveTable(name, tables) is not { } t) return null;
        IReadOnlyList<string> cols = ResolveColumns(StringList(obj, "columns").Concat(StringList(obj, "column")), t);
        if (cols.Count == 0) cols = candidates.FirstOrDefault(c => c.Table == t.Name)?.Columns ?? [];
        if (cols.Count == 0) return null;
        var confidence = (Str(obj, "confidence") ?? "").ToLowerInvariant() switch
        {
            "high" => Confidence.High,
            "medium" => Confidence.Medium,
            _ => Confidence.Low,
        };
        var explain = (Str(obj, "explain") ?? "").Trim();
        var sql = (Str(obj, "sql") ?? "").Trim();
        if (!IsSelectOnly(sql, dialect.Kind)) sql = DefaultSql(t.Name, dialect);
        sql = sql.Trim(';', ' ', '\n');
        return new Verdict(t.Name, cols, confidence, explain, sql);
    }

    // MARK: - 감사 로그

    /// 데이터 찾기 · 후보 N · 샘플 조회 M (질문 · 값은 남기지 않는다)
    public static string AuditResult(int candidates, int sampleQueries) => $"데이터 찾기 · 후보 {candidates} · 샘플 조회 {sampleQueries}";
}

// MARK: - 로컬 AI 호출 (가짜 전송으로 바꿔 시험할 수 있게)

/// 후보 고르기 · 판단 두 번의 로컬 AI 호출. Complete 는 메시지를 보내고 답 글을 받는다 (실제로는 OllamaClient).
public sealed class DataFinderAI
{
    public DatabaseKind Kind { get; }
    public bool English { get; }
    public Func<IReadOnlyList<OllamaMessage>, CancellationToken, Task<string>> Complete { get; }

    public DataFinderAI(DatabaseKind kind, bool english, Func<IReadOnlyList<OllamaMessage>, CancellationToken, Task<string>> complete)
    {
        Kind = kind;
        English = english;
        Complete = complete;
    }

    /// 로컬 Ollama 로 (OllamaClient 가 localhost 만 허용)
    public static DataFinderAI Ollama(OllamaClient client, string model, DatabaseKind kind, bool english) =>
        new(kind, english, (messages, ct) => client.ChatTextAsync(model, messages, 0.1, ct));

    /// 모델이 답을 만들다 실패함 (Ollama 오류 응답). 다시 물어도 소용없으므로 AI 없이 이어 간다.
    public sealed class UnavailableException(string reason) : Exception(reason)
    {
        public string Reason { get; } = reason;
    }

    /// 한 번 묻기. Ollama 오류 응답은 Unavailable 로 바꾼다. Ollama 가 꺼짐 · 원격 주소 차단 · 취소는 그대로 던진다.
    internal async Task<string> AskAsync(IReadOnlyList<OllamaMessage> messages, CancellationToken ct)
    {
        try
        {
            return await Complete(messages, ct);
        }
        catch (OllamaException e) when (e.Kind == OllamaErrorKind.Http)
        {
            throw new UnavailableException(ModelProbe.ShortReason(e));
        }
    }

    /// 묻고 읽기. 형식이 틀리면 한 번 더 묻는다. 두 번 다 틀리면 null.
    internal async Task<T?> AskParsedAsync<T>(List<OllamaMessage> first, string retry, Func<string, T?> parse, CancellationToken ct) where T : class
    {
        var messages = first.ToList();
        var reply = await AskAsync(messages, ct);
        if (parse(reply) is { } v) return v;
        messages.Add(new("assistant", reply));
        messages.Add(new("user", retry));
        return parse(await AskAsync(messages, ct));
    }

    public sealed record Picked(IReadOnlyList<DataFinder.Candidate> Candidates, int TablesSent, bool Fallback, string? AiError = null, bool TwoStage = false);

    /// 후보 고르기. 큰 스키마면 1단계(테이블 목록 → 최대 10개) 뒤 2단계(그 테이블의 컬럼 → 후보).
    /// 형식이 틀리면 한 번 더 묻고, 그래도 안 되거나 남는 후보가 없거나 모델이 실패하면 약어 사전 점수로.
    public async Task<Picked> PickCandidatesAsync(string question, IReadOnlyList<string> terms, IReadOnlyList<TableMeta> tables,
                                                  IReadOnlyList<TableNote> notes, int budget = DataFinder.DefaultBudget, CancellationToken ct = default)
    {
        var twoStage = DataFinder.NeedsTwoStages(tables, budget);
        var tablesSent = tables.Count;
        Picked Fallback(string? error) =>
            new(DataFinder.FallbackCandidates(terms, tables), tablesSent, true, error, twoStage);
        try
        {
            IReadOnlyList<TableMeta> focus = tables;
            if (twoStage)
            {
                var stage1 = DataFinder.TableMessages(question, terms, Kind, tables, notes, budget);
                tablesSent = stage1.TablesSent;
                var names = await AskParsedAsync(stage1.Messages, DataFinder.TableRetryPrompt, r => DataFinder.ParseTables(r, tables), ct) ?? [];
                focus = DataFinder.Stage2Tables(names, terms, tables);
                if (focus.Count == 0) return Fallback(null);
            }
            var prompt = DataFinder.CandidateMessages(question, terms, Kind, focus, notes, English, budget);
            if (!twoStage) tablesSent = prompt.TablesSent;
            var parsed = await AskParsedAsync(prompt.Messages, DataFinder.RetryPrompt, r => DataFinder.ParseCandidates(r, tables), ct);
            if (parsed is { Count: > 0 }) return new Picked(parsed, tablesSent, false, null, twoStage);
            return Fallback(null);
        }
        catch (UnavailableException e)
        {
            return Fallback(e.Reason);
        }
    }

    /// 2차: 판단. 답이 형식에 안 맞거나 모델이 실패하면 이름 · 코멘트 · 값 점수가 가장 높은 후보 (신뢰도 낮음, fallback).
    public async Task<DataFinder.Verdict?> JudgeAsync(string question, IReadOnlyList<DataFinder.SampledCandidate> sampled, IReadOnlyList<TableMeta> tables,
                                                      IReadOnlyList<string>? terms = null, CancellationToken ct = default)
    {
        terms ??= [];
        if (sampled.Count == 0) return null;
        var dialect = ISqlDialect.Of(Kind);
        try
        {
            var reply = await AskAsync(DataFinder.JudgeMessages(question, sampled, tables, Kind, English, terms), ct);
            if (DataFinder.ParseVerdict(reply, tables, sampled.Select(s => s.Candidate).ToList(), dialect) is { } v) return v;
        }
        catch (UnavailableException) { }
        return FallbackVerdict(terms, sampled, tables, dialect);
    }

    /// AI 없이 판단: 값 모양까지 본 점수 1등 (신뢰도 낮음)
    public static DataFinder.Verdict? FallbackVerdict(IReadOnlyList<string> terms, IReadOnlyList<DataFinder.SampledCandidate> sampled,
                                                      IReadOnlyList<TableMeta> tables, ISqlDialect dialect)
    {
        var ranked = terms.Count == 0 ? sampled : DataFinder.RankSampled(terms, sampled, tables);
        if (ranked.FirstOrDefault()?.Candidate is not { } first) return null;
        return new DataFinder.Verdict(first.Table, first.Columns, DataFinder.Confidence.Low, first.Reason,
                                      DataFinder.DefaultSql(first.Table, dialect), Fallback: true);
    }

    // MARK: - 한 번에 (후보 → 샘플 → 판단)

    public enum Step { Picking, Sampling, Judging }

    public sealed class Found
    {
        public required Picked Picked { get; init; }
        /// 판단 순서 (판단으로 고른 후보가 앞)
        public List<DataFinder.SampledCandidate> Sampled { get; set; } = [];
        public DataFinder.Verdict? Verdict { get; set; }
        public int SampleQueries { get; set; }
        /// AI 없이 찾은 결과인지 (후보 또는 판단)
        public bool UsedFallback => Picked.Fallback || (Verdict?.Fallback ?? false);
        /// 모델 오류로 AI 를 못 쓴 이유
        public string? AiError { get; init; }
    }

    /// 찾기 전체. allowSamples 가 false 면 취소(OperationCanceledException), sample 은 컬럼 하나의 값을 조회한다 (앱 · 시험이 넣는다).
    /// 후보 고르기에서 모델이 실패하면 판단도 AI 없이 한다.
    public async Task<Found> FindAsync(string question, IReadOnlyList<string> terms, IReadOnlyList<TableMeta> tables, IReadOnlyList<TableNote> notes,
                                       Func<string, string, Task<DataFinder.SampleOutcome>> sample,
                                       int budget = DataFinder.DefaultBudget,
                                       Func<Step, Task>? progress = null,
                                       Func<Task<bool>>? allowSamples = null,
                                       CancellationToken ct = default)
    {
        if (progress != null) await progress(Step.Picking);
        var picked = await PickCandidatesAsync(question, terms, tables, notes, budget, ct);
        ct.ThrowIfCancellationRequested();
        var found = new Found { Picked = picked, AiError = picked.AiError };
        if (picked.Candidates.Count == 0) return found;
        if (allowSamples != null && !await allowSamples()) throw new OperationCanceledException();
        if (progress != null) await progress(Step.Sampling);
        var sampled = new List<DataFinder.SampledCandidate>();
        foreach (var c in picked.Candidates)
        {
            var s = new DataFinder.SampledCandidate(c);
            foreach (var col in c.Columns)
            {
                ct.ThrowIfCancellationRequested();
                found.SampleQueries++;
                s.Record(col, await sample(c.Table, col));
            }
            sampled.Add(s);
        }
        ct.ThrowIfCancellationRequested();
        DataFinder.Verdict? verdict;
        if (picked.AiError != null)
        {
            verdict = FallbackVerdict(terms, sampled, tables, ISqlDialect.Of(Kind));
        }
        else
        {
            if (progress != null) await progress(Step.Judging);
            verdict = await JudgeAsync(question, sampled, tables, terms, ct);
        }
        if (verdict != null)
        {
            var chosen = sampled.FirstOrDefault(s => s.Candidate.Table == verdict.Table && s.Candidate.Columns.SequenceEqual(verdict.Columns))
                         ?? sampled.FirstOrDefault(s => s.Candidate.Table == verdict.Table);
            if (chosen != null)
            {
                sampled.Remove(chosen);
                sampled.Insert(0, chosen);
            }
            if (verdict.Fallback && picked.Fallback)
                // AI 없이: 값 모양까지 본 순서로 다른 후보도
                sampled = DataFinder.RankSampled(terms, sampled, tables);
        }
        found.Sampled = sampled;
        found.Verdict = verdict;
        return found;
    }
}
