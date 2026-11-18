using System.Globalization;
using static GodDB.Core.L10n;

namespace GodDB.Core.Profiles;

/// 사이드바 접속 그룹 (한 단계만). Mac 판 ConnectionGroups.swift 의 화면과 무관한 부분.
public static class ConnectionGroups
{
    /// 이름순 그룹 목록 (비어 있는 그룹 이름은 없음). Finder 처럼 숫자는 숫자 크기로, 대소문자 무시.
    public static List<string> Groups(IEnumerable<ConnectionProfile> profiles) =>
        profiles.Select(p => p.Group).Where(g => g.Length > 0).Distinct(StringComparer.Ordinal)
            .Order(NaturalComparer.Instance).ToList();

    /// 그룹 없는 접속 (사이드바 맨 위)
    public static List<ConnectionProfile> Ungrouped(IEnumerable<ConnectionProfile> profiles) =>
        profiles.Where(p => p.Group.Length == 0).ToList();

    public static List<ConnectionProfile> Members(IEnumerable<ConnectionProfile> profiles, string group) =>
        profiles.Where(p => p.Group == group).ToList();

    /// 접속을 그룹으로 옮긴다 (빈 이름이면 그룹에서 뺀다). 바뀌었으면 상태창에 남길 문장, 아니면 null.
    public static string? Move(IList<ConnectionProfile> profiles, Guid id, string group)
    {
        var p = profiles.FirstOrDefault(x => x.Id == id);
        if (p == null) return null;
        var g = TrimSpaces(group);
        if (p.Group == g) return null;
        p.Group = g;
        return g.Length == 0 ? Tr("%@ 접속을 그룹에서 뺐어요", p.Name) : Tr("%@ 접속을 %@ 그룹으로 옮겼어요", p.Name, g);
    }

    /// 그룹 이름을 바꾼다 (빈 이름이면 그룹을 푼다 — 접속은 남는다). 바뀌었으면 상태창에 남길 문장, 아니면 null.
    public static string? Rename(IList<ConnectionProfile> profiles, string oldName, string newName)
    {
        var n = TrimSpaces(newName);
        if (oldName.Length == 0 || oldName == n) return null;
        foreach (var p in profiles.Where(p => p.Group == oldName)) p.Group = n;
        return n.Length == 0 ? Tr("%@ 그룹을 풀었어요", oldName) : Tr("%@ 그룹 이름을 %@ 로 바꿨어요", oldName, n);
    }

    /// 접힌 그룹 저장 값 (줄바꿈으로 구분, 이름순)
    public static string EncodeCollapsed(IEnumerable<string> groups) =>
        string.Join("\n", groups.Order(StringComparer.Ordinal));

    public static HashSet<string> DecodeCollapsed(string raw) =>
        raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    /// 앞뒤 공백·탭만 자른다 (Swift .whitespaces — 줄바꿈은 남김)
    internal static string TrimSpaces(string s)
    {
        static bool IsSpace(char c) => c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
        int start = 0, end = s.Length;
        while (start < end && IsSpace(s[start])) start++;
        while (end > start && IsSpace(s[end - 1])) end--;
        return s[start..end];
    }
}

/// Finder 이름 정렬 (localizedStandardCompare 와 비슷하게): 숫자 덩어리는 크기로, 나머지는 대소문자 무시 문화권 비교
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var c = string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            else
            {
                int si = i, sj = j;
                while (i < x.Length && !char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && !char.IsAsciiDigit(y[j])) j++;
                var c = string.Compare(x[si..i], y[sj..j], CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
                if (c != 0) return c;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
