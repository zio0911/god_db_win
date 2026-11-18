using GodDB.Core.Dialects;
using GodDB.Core.Sql;

namespace GodDB.Core.Results;

/// 결과 표에서 고른 행을 클립보드용 글자로 만든다 (내보내기와 같은 형식 규칙)
public static class ResultCopy
{
    /// csv · tsv · json · sql(INSERT) · markdown. 내보내기 작성기로 같은 결과를 낸다.
    /// (Mac 은 임시 파일에 썼다가 읽었다. 여기서는 작성기를 문자열에 바로 쓴다 — 결과는 같다.)
    public static string Text(ExportFormat format, IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<string?>> rows,
        string tableName = "table", ISqlDialect? dialect = null)
    {
        if (format == ExportFormat.Xlsx) return "";
        try
        {
            var options = new ExportOptions { ExcelBom = false, TableName = tableName };
            using var sw = new StringWriter();
            var writer = new ResultExportWriter(format, columns, sw, options, dialect);
            writer.Write(rows);
            writer.Finish();
            return sw.ToString();
        }
        catch
        {
            return "";
        }
    }

    /// 한 컬럼 값을 SQL IN 목록으로: (1, 2, 'a''b'). NULL · 중복은 뺀다.
    public static string InList(IEnumerable<string?> values, Func<string, string>? quote = null)
    {
        quote ??= SqlText.QuoteString;
        var seen = new HashSet<string>();
        var items = values.OfType<string>().Where(seen.Add)
            .Select(v => ResultExportWriter.IsPlainNumber(v) ? v : quote(v));
        return "(" + string.Join(", ", items) + ")";
    }

    /// 빠른 필터: 모든 단어가 어느 칸에든 들어 있는 행 (대소문자 무시). NULL 은 "null" 로 찾는다.
    public static bool Matches(IReadOnlyList<string?> values, IReadOnlyList<string> words) =>
        words.All(w => values.Any(v => (v ?? "null").ToLowerInvariant().Contains(w, StringComparison.Ordinal)));

    public static List<string> Words(string query) =>
        [.. query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];
}
