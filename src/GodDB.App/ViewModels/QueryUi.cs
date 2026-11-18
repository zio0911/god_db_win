using System.Globalization;

namespace GodDB.App.ViewModels;

/// 셀 글자 색 (Mac: .primary / .secondary / .tertiary / .orange / .green / .red)
public enum CellTone { Primary, Secondary, Tertiary, Orange, Green, Red }

/// 결과 표 셀 하나의 모양 (글자, 색, 굵게, 취소선)
public readonly record struct CellLook(string Text, CellTone Tone, bool Bold, bool Strike);

/// 쿼리 화면의 글자 만들기 (Mac QueryWorkspaceView 의 결과 탭 · 페이지 막대 · 셀 표시). 화면 없이 시험할 수 있게 따로 둔다.
public static class QueryUi
{
    /// Swift Int.formatted() — 자릿수 구분 쉼표
    public static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// 한 줄 미리보기 (줄바꿈은 ⏎ 로 표시, 200자 넘으면 …)
    public static string Preview(string value)
    {
        var flat = value.Replace("\r\n", "\n").Replace("\n", " ⏎ ");
        return flat.Length > 200 ? flat[..200] + "…" : flat;
    }

    /// 셀 표시 (Mac ResultGrid.cell): (기본값) · NULL · 바뀐 칸 주황 굵게 · 새 행 초록 · 삭제 빨간 취소선
    public static CellLook Cell(CellState state, string? value)
    {
        var tone = state switch
        {
            CellState.Modified => CellTone.Orange,
            CellState.Inserted => CellTone.Green,
            CellState.Deleted => CellTone.Red,
            _ => CellTone.Primary,
        };
        var bold = state == CellState.Modified;
        var strike = state == CellState.Deleted;
        if (state == CellState.InsertedDefault) return new CellLook(Tr("(기본값)"), CellTone.Tertiary, false, false);
        if (value == null) return new CellLook("NULL", state == CellState.Normal ? CellTone.Tertiary : CellTone.Secondary, bold, strike);
        return new CellLook(Preview(value), tone, bold, strike);
    }

    /// 여러 문장 결과 탭의 글자: 번호, 첫 키워드, 요약 (Mac ResultTabsBar)
    public static (string Number, string Keyword, string Summary) ResultTab(QueryResult r, int index) =>
        ((index + 1).ToString(CultureInfo.InvariantCulture), SqlText.FirstKeyword(r.Sql ?? ""),
         r.Columns.Count == 0 ? (r.Message ?? Tr("완료")) : Tr("%@행", r.Rows.Count));

    public static string ResultTabTitle(QueryResult r, int index)
    {
        var (n, k, s) = ResultTab(r, index);
        return $"{n} {k} {s}";
    }

    /// 마지막 페이지 번호 (전체 행 수를 셌을 때만)
    public static int? LastPage(QueryResult r) =>
        r.TotalCount is { } total && r.PageSize > 0 ? Math.Max(0, (total - 1) / r.PageSize) : null;

    /// 페이지 막대 가운데 글자: "501–1,000행" (빈 페이지면 "0행")
    public static string PageRange(QueryResult r)
    {
        if (r.Rows.Count == 0) return Tr("0행");
        var first = r.RowOffset + 1;
        var last = r.RowOffset + r.Rows.Count;
        return Tr("%@–%@행", N(first), N(last));
    }

    /// 페이지 막대 오른쪽 글자: "/ 전체 N행 · 2/12페이지" 또는 "2페이지 · 더 있음/끝"
    public static string PageInfo(QueryResult r)
    {
        if (r.TotalCount is { } total)
            return Tr("/ 전체 %@행 · %@/%@페이지", N(total), r.Page + 1, (LastPage(r) ?? 0) + 1);
        return Tr("%@페이지", r.Page + 1) + (r.HasMore ? Tr(" · 더 있음") : Tr(" · 끝"));
    }

    /// "제한 없음" 결과의 아래 줄 안내
    public static string AppendInfo(QueryResult r, bool capped) =>
        !r.HasMore ? Tr("· 끝")
        : capped ? Tr("· 화면 최대 %@행 — 전체는 [내보내기]", N(AppState.MaxRowsInMemory))
        : Tr("· 스크롤하면 더 불러와요");

    /// 내보내기 진행 글자
    public static string ExportText(ExportProgress p) =>
        p.Total is { } t
            ? Tr("%@ 내보내는 중 %@ / %@행", p.Format.Title(), N(p.Written), N(t))
            : Tr("%@ 내보내는 중 %@행", p.Format.Title(), N(p.Written));

    /// 실행 시간 (Mac String(format: "%.0fms"))
    public static string Elapsed(QueryResult r) => ((long)Math.Round(r.Elapsed * 1000)).ToString(CultureInfo.InvariantCulture) + "ms";

    /// 탭 이름 (파일이 바뀌었으면 앞에 ●)
    public static string TabTitle(QueryTab tab) => tab.IsDirty ? "● " + tab.Title : tab.Title;

    /// 탭 아래 작은 글자: "접속명 / DB"
    public static string TabSubtitle(string? profileName, string database) =>
        (profileName ?? Tr("삭제된 접속")) + (database.Length == 0 ? "" : $" / {database}");

    /// 빠른 필터 적용 (새 행은 항상 보인다)
    public static List<QueryResult.Row> Filter(List<QueryResult.Row> rows, string filter)
    {
        var words = ResultCopy.Words(filter);
        if (words.Count == 0) return rows;
        return rows.Where(r => r.Id >= PendingEdits.NewRowBase || ResultCopy.Matches(r.Values, words)).ToList();
    }
}
