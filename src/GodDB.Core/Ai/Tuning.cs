using System.Text.RegularExpressions;
using GodDB.Core.Dialects;
using GodDB.Core.Schema;

namespace GodDB.Core.Ai;

/// Ollama 대화 메시지 한 개 (Mac 판 OllamaClient.Message). Ollama 클라이언트를 옮길 때 그대로 쓴다.
public sealed record OllamaMessage(string Role, string Content);

/// 쿼리 튜닝에 필요한 정보. 실제 데이터 행은 담지 않는다.
/// (이것을 모으는 DBSession.tuningContext 는 DB 세션을 옮길 때 함께 옮긴다)
public sealed record TuningContext(
    DatabaseKind Kind,
    string Version,
    string Sql,
    IReadOnlyList<TuningContext.Table> Tables,
    QueryResult? Plan,
    string? PlanTree,
    IReadOnlyList<PlanAnalyzer.Warning> Warnings,
    // 실행 계획을 못 얻은 이유 (있으면)
    string? PlanError)
{
    public sealed record Table(TableMeta Meta, string Ddl, int? EstimatedRows);
}

/// Ollama 에 보낼 프롬프트
public static class TuningPrompt
{
    public static List<OllamaMessage> Messages(TuningContext c, bool english) =>
        [new("system", SystemText(c.Kind, english)), new("user", UserText(c, english))];

    internal static string SystemText(DatabaseKind kind, bool english)
    {
        var lang = english ? "English" : "Korean (한국어)";
        return $"""
            You are a senior {kind.Title()} performance tuning expert.
            Analyze the query, table structures, indexes, row estimates and execution plan given by the user.
            Rules:
            - The improved query MUST return exactly the same result as the original (same rows, columns and order semantics).
            - Use only tables and columns that appear in the given structures. Do not invent names.
            - Write SQL in {kind.Title()} syntax.
            - If the query is already fine, say so and repeat the original query.
            - Only suggest indexes that clearly help this query. Do not suggest dropping anything.
            - Answer in {lang}. Use exactly these four sections and headings, in this order:

            ## {(english ? "Summary" : "요약")}
            (1-3 sentences: the main problem and the expected effect)

            ## {(english ? "Improved SQL" : "개선 SQL")}
            ```sql
            (the full improved query)
            ```

            ## {(english ? "Recommended indexes" : "추천 인덱스")}
            ```sql
            (CREATE INDEX statements, or a SQL comment saying none are needed)
            ```

            ## {(english ? "Explanation" : "설명")}
            (bullet points: what changed and why, referring to the execution plan)
            """;
    }

    internal static string UserText(TuningContext c, bool english)
    {
        var s = $"Database: {c.Version}\n\n";
        s += $"Query to tune:\n```sql\n{c.Sql}\n```\n\n";
        if (c.Tables.Count == 0)
        {
            s += "Table structures: (not found in the current schema)\n\n";
        }
        else
        {
            s += "Table structures (DDL, including indexes):\n";
            foreach (var t in c.Tables)
            {
                s += $"```sql\n{t.Ddl}\n```\n";
                if (t.EstimatedRows is int n) s += $"Estimated rows in {t.Meta.Name}: {n}\n";
                s += "\n";
            }
        }
        if (c.Plan is QueryResult plan)
        {
            s += "Execution plan:\n" + Table(plan, maxRows: 60) + "\n";
            if (c.PlanTree is string tree && tree.Length > 0 && tree.Length < 6000)
                s += $"Execution plan (tree):\n```\n{tree}\n```\n";
        }
        else if (c.PlanError is string e)
        {
            s += $"Execution plan: unavailable ({e})\n";
        }
        if (c.Warnings.Count > 0)
            s += "\nAutomatic plan warnings:\n" + string.Join("\n", c.Warnings.Select(w => $"- [{w.Table}] {w.Message}")) + "\n";
        return s;
    }

    /// 결과를 사람이 읽는 표로 (값이 길면 자른다)
    internal static string Table(QueryResult r, int maxRows)
    {
        var lines = new List<string> { string.Join(" | ", r.Columns.Select(c => c.Name)) };
        foreach (var row in r.Rows.Take(maxRows))
        {
            lines.Add(string.Join(" | ", row.Values.Select(v =>
            {
                var s = (v ?? "NULL").Replace("\n", " ");
                return s.Length > 200 ? s[..200] + "…" : s;
            })));
        }
        return string.Join("\n", lines) + "\n";
    }
}

/// 모델 답변을 화면용으로 나눈다.
public sealed partial record TuningResult(string Summary, string? ImprovedSql, string? IndexSql, string Explanation, string Raw)
{
    [GeneratedRegex(@"CREATE\s+(UNIQUE\s+)?(NONCLUSTERED\s+|CLUSTERED\s+)?INDEX", RegexOptions.IgnoreCase)] private static partial Regex CreateIndex();
    [GeneratedRegex(@"<think>[\s\S]*?</think>")] private static partial Regex Think();
    [GeneratedRegex(@"```(?:sql|SQL)?\s*\n([\s\S]*?)```")] private static partial Regex Block();

    /// 추천 인덱스가 실제로 있는지 (주석만 있으면 없음)
    public bool HasIndexes => IndexSql is string sql && CreateIndex().IsMatch(sql);

    public static TuningResult Parse(string text)
    {
        // 혹시 섞여 온 <think> … </think> 는 버린다.
        var body = Think().Replace(text, "").Trim();

        // ```sql … ``` 블록
        var blocks = Block().Matches(body).Select(m => m.Groups[1].Value.Trim()).ToList();
        bool IsIndex(string s) => CreateIndex().IsMatch(s);

        // ## 제목 단위로 나눈다
        var sections = new List<(string Title, string Text)>();
        string currentTitle = "", currentText = "";
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith('#'))
            {
                if (currentTitle.Length > 0 || currentText.Trim().Length > 0)
                    sections.Add((currentTitle, currentText));
                currentTitle = line.Trim('#', ' ').ToLowerInvariant();
                currentText = "";
            }
            else
            {
                currentText += line + "\n";
            }
        }
        sections.Add((currentTitle, currentText));

        string? Section(string[] keys)
        {
            foreach (var s in sections)
                if (keys.Any(k => s.Title.Contains(k))) return s.Text.Trim();
            return null;
        }
        static string? FirstBlock(string? text)
        {
            if (text == null) return null;
            var m = Block().Match(text);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        var improved = FirstBlock(Section(["개선", "improved", "optimized"])) ?? blocks.FirstOrDefault(b => !IsIndex(b));
        var index = FirstBlock(Section(["인덱스", "index"])) ?? blocks.FirstOrDefault(IsIndex);
        var summary = Section(["요약", "summary"]) ?? body.Split("\n\n")[0];
        var explanation = Section(["설명", "explanation"]) ?? "";
        return new TuningResult(summary, string.IsNullOrEmpty(improved) ? null : improved,
                                string.IsNullOrEmpty(index) ? null : index, explanation, body);
    }
}
