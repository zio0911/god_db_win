using GodDB.Core.Results;

namespace GodDB.Core.Schema;

public sealed class QueryResult
{
    public sealed record Column(int Id, string Name);

    public sealed record Row(int Id, IReadOnlyList<string?> Values);

    public Guid Id { get; } = Guid.NewGuid();
    public List<Column> Columns { get; set; }
    public List<Row> Rows { get; set; }
    /// 걸린 시간 (초). Swift TimeInterval 과 같은 단위.
    public double Elapsed { get; set; }
    public string? Message { get; set; }
    /// 이 결과를 만든 SQL (여러 문장 실행 시 결과 탭 이름에 사용)
    public string? Sql { get; set; }
    /// 페이징: 0부터 시작하는 페이지 번호, 페이지 크기(0 이면 페이징 안 함), 다음 페이지 존재 여부, 전체 행 수(센 경우)
    public int Page { get; set; }
    public int PageSize { get; set; }
    public bool HasMore { get; set; }
    /// "제한 없음": 보이지 않게 페이지를 나눠 받아 스크롤할 때마다 이어 붙인다 (행 번호는 0부터 계속)
    public bool AppendMode { get; set; }
    /// 값이 너무 커서 앞부분만 담은 칸 수 (메모리 보호). 0 이 아니면 결과 편집을 막는다.
    public int TruncatedCells { get; set; }
    public int? TotalCount { get; set; }
    /// DB 에서 정렬해 가져온 경우 (페이지를 넘겨도 유지)
    public ResultSort? ServerSort { get; set; }
    /// 앱 안에서 정렬한 경우 (한 페이지짜리 결과)
    public ResultSort? ClientSort { get; set; }

    /// 헤더에 표시할 정렬
    public ResultSort? ActiveSort => ServerSort ?? ClientSort;

    public bool IsPaged => PageSize > 0;
    /// 이 페이지 첫 행의 전체 기준 번호 (0부터)
    public int RowOffset => AppendMode ? 0 : Page * PageSize;

    public QueryResult(IEnumerable<string> columns, IEnumerable<IReadOnlyList<string?>> rows, double elapsed, string? message = null)
    {
        Columns = [.. columns.Select((name, i) => new Column(i, name))];
        Rows = [.. rows.Select((values, i) => new Row(i, values))];
        Elapsed = elapsed;
        Message = message;
    }

    /// RFC 4180 CSV 로 변환 (파일 저장은 lineEnding "\r\n" — 엑셀 호환)
    public string Csv(string lineEnding = "\n")
    {
        static string Esc(string? v)
        {
            if (v == null) return "";
            if (v.IndexOfAny([',', '"', '\n', '\r']) >= 0)
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }
        var lines = new List<string> { string.Join(",", Columns.Select(c => Esc(c.Name))) };
        foreach (var r in Rows) lines.Add(string.Join(",", r.Values.Select(Esc)));
        return string.Join(lineEnding, lines);
    }
}
