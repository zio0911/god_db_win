using System.Text;
using System.Text.RegularExpressions;

namespace GodDB.Core.Ai;

/// AI 어시스턴트가 "이 앱" 에 대한 질문(누가 만들었나, 사용법, 단축키…)에 답할 수 있게
/// 기능 명세(docs/FEATURES.md, Core 에 리소스로 들어 있음) 에서 질문과 관련된 절을 골라 문맥으로 붙인다.
public sealed partial class AppGuide
{
    public sealed record Section(string Title, string Body);

    /// Mac 판의 "native macOS … Swift/SwiftUI" 를 Windows 판에 맞게 바꿨다.
    public static readonly string About = $"""
        GodDB is a free, open-source (MIT license) native Windows database client written in C#/.NET (Avalonia UI), ported from the macOS app.
        Author: zio0911 (GitHub). Source and releases: {AppInfo.RepositoryUrl}
        Supports MySQL 5.7/8.x, PostgreSQL 12+, SQL Server 2016+, Redis 6+. Local Ollama is used for AI (query tuning,
        diagram/modeling AI and this assistant); nothing is sent to external servers and row data is never shared with AI.
        Main modes (top segment): 쿼리 (query editor), 스키마 비교 (schema compare), 모델링 (data modeling).
        """;

    public IReadOnlyList<Section> Sections { get; }
    /// 버전별 변경 이력 (최신 먼저)
    public IReadOnlyList<global::GodDB.Core.Ai.ReleaseNotes.Entry> ReleaseNotes { get; }

    /// currentVersion 이 null 이면 앱 버전 (AppInfo.Version). Mac 은 번들 버전, 없으면 "dev".
    public AppGuide(string markdown, string? currentVersion = null)
    {
        Sections = Parse(markdown);
        ReleaseNotes = global::GodDB.Core.Ai.ReleaseNotes.Released(global::GodDB.Core.Ai.ReleaseNotes.Parse(markdown), currentVersion ?? AppInfo.Version);
    }

    private static readonly Lazy<string?> BundledText = new(() =>
    {
        using var s = typeof(AppGuide).Assembly.GetManifestResourceStream("GodDB.Core.FEATURES.md");
        if (s == null) return null;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    });

    private static readonly Lazy<AppGuide?> BundledGuide = new(() => BundledText.Value is { } md ? new AppGuide(md) : null);

    /// 앱 안에 넣은 FEATURES.md 원문 (없으면 null)
    public static string? BundledMarkdown => BundledText.Value;

    /// 앱 안에 넣은 FEATURES.md 로 만든 안내 (Mac: AppState.appGuide)
    public static AppGuide? Bundled => BundledGuide.Value;

    [GeneratedRegex(@"\d+\.\d+")] private static partial Regex VersionLike();

    /// 버전·업데이트·변경 내용을 묻는 질문인지
    public static bool AsksAboutVersions(string q)
    {
        var l = q.ToLowerInvariant();
        if (VersionLike().IsMatch(l)) return true;
        string[] words = ["버전", "업데이트", "릴리스", "릴리즈", "변경", "바뀐", "달라", "차이", "새로운 기능", "새 기능", "뭐가 생", "언제 생",
                          "version", "release", "changelog", "what's new", "update"];
        return words.Any(l.Contains);
    }

    /// `## ` 단위로 자른다. 변경 이력·목차는 빼고, 코드 블록 안의 `##` 는 무시한다.
    internal static List<Section> Parse(string md)
    {
        var out_ = new List<Section>();
        string? title = null;
        var body = new List<string>();
        var inCode = false;
        void Flush()
        {
            if (title is { } t && !Skip(t)) out_.Add(new Section(t, string.Join("\n", body).Trim()));
            body.Clear();
        }
        foreach (var line in md.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal)) inCode = !inCode;
            if (!inCode && line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                title = AiText.TrimSpaces(line[3..]);
            }
            else if (title != null)
            {
                body.Add(line);
            }
        }
        Flush();
        return out_;
    }

    internal static bool Skip(string title) => title.Contains("목차") || title.Contains("변경 이력");

    /// 짧은 개요: 절 제목 목록 + 단축키 절
    public string Overview
    {
        get
        {
            var s = "Feature sections of the GodDB manual: " + string.Join(" / ", Sections.Select(x => x.Title)) + "\n";
            if (Sections.FirstOrDefault(x => x.Title.Contains("단축키")) is { } keys) s += $"\n## {keys.Title}\n{keys.Body}\n";
            return s;
        }
    }

    /// 질문과 관련 있는 절 (점수 순, 글자 수 제한)
    public List<Section> Relevant(string question, int limit = 3, int maxChars = 9_000)
    {
        var words = Keywords(question);
        if (words.Count == 0) return [];
        var scored = Sections.Select(sec =>
        {
            string t = sec.Title.ToLowerInvariant(), b = sec.Body.ToLowerInvariant();
            var score = 0;
            foreach (var w in words)
            {
                if (t.Contains(w)) score += 6;
                score += Math.Min(AiText.Count(b, w), 4);
            }
            return (Section: sec, Score: score);
        });
        var out_ = new List<Section>();
        var total = 0;
        foreach (var (sec, _) in scored.Where(x => x.Score >= 3).OrderByDescending(x => x.Score).Take(limit))
        {
            var s = sec;
            if (s.Body.Length > maxChars - total) s = s with { Body = AiText.Prefix(s.Body, Math.Max(0, maxChars - total)) + "\n…" };
            out_.Add(s);
            total += s.Body.Length;
            if (total >= maxChars) break;
        }
        return out_;
    }

    /// 질문에서 뜻 있는 낱말만 (조사·흔한 말 제거)
    internal static List<string> Keywords(string q)
    {
        HashSet<string> stop = ["알려줘", "알려", "주세요", "해줘", "어떻게", "방법", "사용", "사용법", "사용방법", "뭐야", "뭐", "이거",
                                "그거", "있어", "있나", "하나", "하는", "하려면", "되나", "돼", "좀", "기능", "앱", "the", "how", "what",
                                "use", "can", "does", "is", "a", "to", "do", "in", "i", "goddb", "갓디비"];
        string[] particles = ["에서는", "에서", "으로", "에게", "하고", "까지", "부터", "는", "은", "이", "가", "을", "를", "에", "로", "와", "과", "도", "의", "만"];
        var out_ = new List<string>();
        var lower = q.ToLowerInvariant();
        var words = new List<string>();
        var cur = new StringBuilder();
        foreach (var c in lower)
        {
            if (char.IsLetterOrDigit(c) || char.IsNumber(c)) cur.Append(c);
            else if (cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) words.Add(cur.ToString());
        foreach (var raw in words)
        {
            var w = raw;
            foreach (var p in particles)
            {
                if (w.Length > p.Length + 1 && w.EndsWith(p, StringComparison.Ordinal)) { w = w[..^p.Length]; break; }
            }
            if (w.Length >= 2 && !stop.Contains(w) && !out_.Contains(w)) out_.Add(w);
        }
        return out_;
    }

    /// 어시스턴트 문맥에 붙일 글
    public string ContextText(string question, string appState)
    {
        var s = new StringBuilder("About this app:\n" + About + "\nCurrent app state:\n" + appState + "\n");
        if (AsksAboutVersions(question) && ReleaseNotes.Count > 0)
        {
            s.Append("\nRelease history (newest first; each version lists what changed in it):\n");
            foreach (var e in ReleaseNotes.Take(10)) s.Append($"\n### {e.Version}\n" + string.Join("\n", e.Lines) + "\n");
        }
        var rel = Relevant(question);
        if (rel.Count == 0)
        {
            s.Append("\n" + Overview);
        }
        else
        {
            s.Append("\nManual sections related to the question:\n");
            foreach (var sec in rel) s.Append($"\n## {sec.Title}\n{sec.Body}\n");
            s.Append("\n(Other sections: " + string.Join(" / ", Sections.Select(x => x.Title)) + ")\n");
        }
        return s.ToString();
    }
}
