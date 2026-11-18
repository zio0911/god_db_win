using System.Globalization;
using GodDB.Core.Schema;

namespace GodDB.Core.Results;

/// 결과 정렬 (헤더 클릭)
public sealed record ResultSort(string Column, bool Ascending);

public static class ResultSorting
{
    /// 앱 안에서 값 비교: NULL 이 가장 앞, 둘 다 숫자면 숫자 크기, 아니면 사람이 읽는 순서 (숫자 섞인 글자도 자연스럽게)
    /// 결과는 -1 / 0 / 1 (Swift ComparisonResult 의 orderedAscending / orderedSame / orderedDescending)
    public static int Compare(string? a, string? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        if (TryNumber(a, out var dx) && TryNumber(b, out var dy))
            return dx < dy ? -1 : (dx > dy ? 1 : 0);
        // Swift localizedStandardCompare (Finder 정렬) 대응: 대소문자 무시 + 숫자는 크기로
        return Math.Sign(CultureInfo.CurrentCulture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering));
    }

    /// Swift Double(String) 처럼 앞뒤 공백 없는 숫자만
    private static bool TryNumber(string s, out double d) =>
        double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                        CultureInfo.InvariantCulture, out d);

    /// 행을 정렬한다 (같은 값이면 원래 순서 유지)
    public static List<QueryResult.Row> Sorted(IReadOnlyList<QueryResult.Row> rows, int column, bool ascending)
    {
        var indexed = rows.Select((r, i) => (r, i)).ToList();
        indexed.Sort((l, r) =>
        {
            var c = Compare(l.r.Values[column], r.r.Values[column]);
            if (c == 0) return l.i.CompareTo(r.i);
            return ascending ? c : -c;
        });
        return [.. indexed.Select(x => x.r)];
    }
}
