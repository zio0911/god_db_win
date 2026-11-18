using System.Text.RegularExpressions;
using static GodDB.Core.L10n;

namespace GodDB.Core.Ai;

/// 기능 명세(FEATURES.md)의 "변경 이력" 절에서 버전별 변경 내용을 읽는다.
/// 새로운 기능 창, AI 어시스턴트의 "버전별 차이" 답변에 쓴다.
/// (Windows 판은 Core 에 리소스로 넣은 docs/FEATURES.md 를 읽는다 — AppGuide.BundledMarkdown)
public static partial class ReleaseNotes
{
    public sealed record Entry(string Version, IReadOnlyList<string> Lines)
    {
        public string Id => Version;
    }

    /// 앱 안에 넣은 FEATURES.md 의 변경 이력 (지금 버전 이하)
    public static IReadOnlyList<Entry> Bundled => AppGuide.Bundled?.ReleaseNotes ?? [];

    public static List<Entry> Parse(string markdown)
    {
        var inLog = false;
        var out_ = new List<(string Version, List<string> Lines)>();
        foreach (var raw in markdown.Split('\n'))
        {
            var line = AiText.TrimSpaces(raw);
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inLog = line.Contains("변경 이력") || line.ToLowerInvariant().Contains("changelog");
                continue;
            }
            if (!inLog) continue;
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                var v = AiText.TrimSpaces(line[4..].Replace("(개발 중)", ""));
                out_.Add((v, []));
            }
            else if (line.Length > 0 && out_.Count > 0)
            {
                out_[^1].Lines.Add(line);
            }
        }
        return out_.Where(e => e.Lines.Count > 0).Select(e => new Entry(e.Version, e.Lines)).ToList();
    }

    /// 지금 앱 버전 이하만 (아직 릴리스 안 한 "개발 중" 이력은 숨긴다). 버전을 모르면(dev) 그대로.
    public static List<Entry> Released(IReadOnlyList<Entry> entries, string current)
    {
        if (current.Length == 0 || !char.IsDigit(current[0])) return [.. entries];
        return entries.Where(e => Compare(e.Version, current) <= 0).ToList();
    }

    /// old 보다 새롭고 new 이하인 버전들 (예: 1.0.0 → 1.1.0 사이 변경)
    public static List<Entry> Between(IReadOnlyList<Entry> entries, string after, string upTo) =>
        entries.Where(e => Compare(e.Version, after) > 0 && Compare(e.Version, upTo) <= 0).ToList();

    /// 1.10.0 > 1.9.2 처럼 숫자로 비교 (음수: a 가 이전, 0: 같음, 양수: a 가 나중)
    public static int Compare(string a, string b)
    {
        static int[] Parts(string s) => s.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
        int[] pa = Parts(a), pb = Parts(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex Bold();
    [GeneratedRegex(@"`(.+?)`")] private static partial Regex InlineCode();

    /// 업데이트 창용 간단한 HTML
    public static string Html(Entry entry)
    {
        static string Inline(string s)
        {
            var t = s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            t = Bold().Replace(t, "<b>$1</b>");
            t = InlineCode().Replace(t, "<code>$1</code>");
            return t;
        }
        var items = string.Concat(entry.Lines.Select(l => $"<li>{Inline(l.StartsWith("- ", StringComparison.Ordinal) ? l[2..] : l)}</li>"));
        return $"<h3>GodDB {entry.Version}</h3><ul>{items}</ul>";
    }

    // MARK: 새 버전을 처음 켰을 때 (Mac 판 UpdateAndStartup.swift 의 showReleaseNotesIfUpdated)

    public const string LastSeenVersionKey = "GodDB.lastSeenVersion";

    /// 지난번 켠 버전 이후 바뀐 것이 있으면 그 버전을 돌려준다 (새로운 기능 창을 한 번 띄울 때). 지금 버전을 기록한다.
    public static string? CheckUpdated(string? current = null, IReadOnlyList<Entry>? entries = null)
    {
        var cur = current ?? AppInfo.Version;
        var last = Prefs.GetString(LastSeenVersionKey);
        Prefs.Set(LastSeenVersionKey, cur);
        return UpdatedSince(last, cur, entries ?? Bundled) ? last : null;
    }

    public static bool UpdatedSince(string? last, string current, IReadOnlyList<Entry> entries) =>
        last != null && last != current && current != "dev" && Between(entries, last, current).Count > 0;

    /// 새로운 기능 창의 [AI 에게 물어보기] 질문
    public static string AskQuestion(string? since, string current) =>
        since != null ? Tr("%@ 에서 %@ 로 바뀐 점을 정리해줘", since, current) : Tr("최근 버전들에서 바뀐 점을 정리해줘");
}
