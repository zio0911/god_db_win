using System.Text;
using System.Text.RegularExpressions;
using GodDB.Core.Schema;

namespace GodDB.Core.Ai;

/// ER 다이어그램 AI 분석 (로컬 Ollama). 테이블 구조와 관계만 보내고 데이터 행은 보내지 않는다.
public static partial class DiagramAI
{
    /// 보낼 스키마 글자 수 상한 (넘으면 컬럼 목록을 줄인다)
    public const int MaxSchemaChars = 40_000;

    /// 다이어그램에 있는 테이블 구조와 관계를 모델이 읽기 좋은 글로 만든다.
    public static string SchemaText(IReadOnlyList<TableMeta> tables, IReadOnlyList<ForeignKeyMeta> inferred)
    {
        var names = tables.Select(t => t.Name).ToHashSet();
        static string Clip(string s, int n = 80)
        {
            var one = AiText.TrimSpaces(s.Replace("\n", " "));
            return one.Length > n ? AiText.Prefix(one, n) + "…" : one;
        }
        string Render(int? columnsLimit)
        {
            var s = new StringBuilder();
            foreach (var t in tables)
            {
                s.Append($"TABLE {t.Name}" + (t.Comment.Length == 0 ? "" : $"  -- {Clip(t.Comment)}") + "\n");
                var pk = (t.Indexes.FirstOrDefault(i => i.IsPrimary)?.Columns ?? []).Select(c => c.ToLowerInvariant()).ToHashSet();
                var cols = columnsLimit is int lim ? t.Columns.Take(lim).ToList() : t.Columns;
                foreach (var c in cols)
                {
                    var line = $"  {c.Name} {c.ColumnType}";
                    if (pk.Contains(c.Name.ToLowerInvariant())) line += " PK";
                    if (!c.Nullable) line += " NOT NULL";
                    if (c.Comment.Length > 0) line += $"  -- {Clip(c.Comment, 60)}";
                    s.Append(line + "\n");
                }
                if (cols.Count < t.Columns.Count) s.Append($"  … ({t.Columns.Count - cols.Count} more columns)\n");
                foreach (var idx in t.Indexes.Where(i => !i.IsPrimary))
                    s.Append($"  INDEX {idx.Name}({string.Join(", ", idx.Columns)}){(idx.Unique ? " UNIQUE" : "")}\n");
            }
            return s.ToString();
        }
        var body = Render(null);
        if (body.Length > MaxSchemaChars) body = Render(12);
        if (body.Length > MaxSchemaChars) body = Render(4);
        if (body.Length > MaxSchemaChars) body = AiText.Prefix(body, MaxSchemaChars) + "\n… (truncated)\n";

        static string Rel(ForeignKeyMeta f) =>
            $"  {f.Table}({string.Join(", ", f.Columns)}) -> {f.ReferencedTable}({string.Join(", ", f.ReferencedColumns)})";

        var result = $"Tables ({tables.Count}):\n" + body + "\n";
        var fks = tables.SelectMany(t => t.ForeignKeys).Where(f => names.Contains(f.ReferencedTable)).ToList();
        result += "Foreign keys (declared):\n";
        result += fks.Count == 0 ? "  (none)\n" : string.Join("\n", fks.Select(Rel)) + "\n";
        var guessed = inferred.Where(f => names.Contains(f.Table) && names.Contains(f.ReferencedTable)).ToList();
        if (guessed.Count > 0)
        {
            result += "Relations guessed from column names (no FK constraint, may be wrong):\n";
            result += string.Join("\n", guessed.Select(Rel)) + "\n";
        }
        return result;
    }

    public static string SystemPrompt(DatabaseKind kind, bool english) => $"""
        You are a senior {kind.Title()} data modeler helping a developer understand a database schema shown as an ER diagram.
        You only know the table structures and relations given below — there is no row data.
        Rules:
        - Base every statement on the given tables, columns, comments, keys and relations. Do not invent tables or columns.
        - Clearly separate declared foreign keys from relations you infer; say how confident you are when inferring.
        - Use table and column comments to explain business meaning when present.
        - When showing SQL, use {kind.Title()} syntax in ```sql blocks. Never suggest running DROP or DELETE.
        - Answer in {(english ? "English" : "Korean (한국어)")}, concise, using Markdown headings and bullet points.
        """;

    /// 처음 "관계 분석" 요청 문장
    public static string AnalysisQuestion(bool english)
    {
        if (english)
        {
            return """
                Analyze the relationships in this ER diagram:
                1. What the schema is for (domain) and the core tables
                2. Each relationship: parent → child, cardinality (1:1, 1:N, N:M via join tables)
                3. Relations that probably exist but have no FK (missing constraints) and FKs without a supporting index
                4. Modeling issues or risks (naming, nullable keys, duplicated data)
                5. A typical JOIN query that follows the main relationships
                """;
        }
        return """
            이 ER 다이어그램의 관계를 분석해 주세요:
            1. 스키마의 용도(업무 도메인)와 핵심 테이블
            2. 관계별 부모 → 자식, 카디널리티 (1:1, 1:N, 연결 테이블을 통한 N:M)
            3. FK 제약은 없지만 관계로 보이는 것(빠진 제약), 인덱스가 없는 FK 컬럼
            4. 모델링상 문제나 위험 (이름 규칙, NULL 허용 키, 중복 데이터)
            5. 주요 관계를 따라가는 대표 JOIN 쿼리 예시
            """;
    }

    /// 대화 전체를 Ollama 메시지로. 첫 user 메시지 앞에 스키마를 붙인다.
    public static List<OllamaMessage> Messages(DatabaseKind kind, string schema, IReadOnlyList<(string Role, string Content)> history, bool english)
    {
        var out_ = new List<OllamaMessage>
        {
            new("system", SystemPrompt(kind, english)),
            new("user", "Schema:\n" + schema),
            new("assistant", english ? "I have read the schema. What would you like to know?" : "스키마를 읽었어요. 무엇이 궁금하세요?"),
        };
        out_.AddRange(history.Select(h => new OllamaMessage(h.Role, h.Content)));
        return out_;
    }

    [GeneratedRegex(@"<think>[\s\S]*?(</think>|$)")] private static partial Regex Think();

    /// 모델 답에 섞여 온 <think> … </think> 를 뺀다.
    public static string Clean(string text) => Think().Replace(text, "").Trim();
}
