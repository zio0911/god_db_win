using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GodDB.Core.Audit;

/// 앱 안 "문제 보고": 버전·환경·최근 오류·크래시 요약을 모아 GitHub 이슈 작성 화면을 미리 채운다 (서버 없음).
/// 회사 정보가 새지 않도록 호스트·IP·이메일·계정·홈 경로를 가린다. 사용자가 보내기 전에 고칠 수 있다.
public static partial class ProblemReport
{
    public const string Repo = "zio0911/god_db_win";

    public sealed record Crash(string Date, string AppVersion, string Exception, IReadOnlyList<string> Frames);

    // MARK: 가리기

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")] private static partial Regex Email();
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")] private static partial Regex Ipv4();
    // 점이 두 개 이상인 호스트 이름 (db.example.co.kr, xxx.rds.amazonaws.com)
    [GeneratedRegex(@"\b(?:[A-Za-z0-9-]+\.){2,}[A-Za-z]{2,}\b")] private static partial Regex Host();
    // Windows 사용자 폴더 (C:\Users\이름, C:/Users/이름) — 다른 계정 경로도 가린다
    [GeneratedRegex(@"\b[A-Za-z]:[\\/]Users[\\/][^\\/\s]+", RegexOptions.IgnoreCase)] private static partial Regex WindowsUserDir();

    public static string DefaultHome => System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    /// 공개 이슈에 올라가면 안 되는 값을 가린다
    public static string Redact(string text, string? identity = null, string? home = null, string? user = null)
    {
        identity ??= ClientInfo.Identity;
        home ??= DefaultHome;
        user ??= System.Environment.UserName;
        var s = text;
        if (home.Length > 0)
        {
            // Windows 경로는 대소문자를 가리지 않고, / 로 적힌 경로도 있다
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            s = s.Replace(home, "~", cmp);
            if (home.Contains('\\')) s = s.Replace(home.Replace('\\', '/'), "~", cmp);
        }
        s = WindowsUserDir().Replace(s, "~");
        if (identity.Length > 0) s = s.Replace(identity, "<user>");
        s = Email().Replace(s, "<email>");
        s = Ipv4().Replace(s, "<ip>");
        s = Host().Replace(s, "<host>");
        if (user.Length >= 3)
            s = Regex.Replace(s, $@"\b{Regex.Escape(user)}\b", "<user>");
        return s;
    }

    // MARK: 크래시 요약

    public static string CrashDirectory => AppPaths.CrashDirectory;

    /// GodDB 크래시 파일 최근 것부터
    public static List<Crash> RecentCrashes(int limit = 3, string? directory = null)
    {
        var dir = directory ?? CrashDirectory;
        if (!Directory.Exists(dir)) return [];
        return new DirectoryInfo(dir).GetFiles()
            .Where(f => f.Name.StartsWith("GodDB", StringComparison.Ordinal) && f.Extension == CrashLog.FileExtension)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(limit)
            .Select(f => { try { return File.ReadAllText(f.FullName, Encoding.UTF8); } catch { return null; } })
            .Where(t => t != null)
            .Select(t => ParseCrash(t!))
            .OfType<Crash>()
            .ToList();
    }

    /// 크래시 파일 (<see cref="CrashLog"/>): 첫 줄은 머리 JSON, 나머지는 본문 JSON
    public static Crash? ParseCrash(string text)
    {
        var nl = text.IndexOf('\n');
        if (nl < 0) return null;
        try
        {
            using var head = JsonDocument.Parse(text[..nl]);
            using var body = JsonDocument.Parse(text[(nl + 1)..]);
            if (head.RootElement.ValueKind != JsonValueKind.Object || body.RootElement.ValueKind != JsonValueKind.Object) return null;
            var exception = "";
            if (body.RootElement.TryGetProperty("exception", out var exc) && exc.ValueKind == JsonValueKind.Object)
            {
                exception = string.Join(": ", new[] { Str(exc, "type"), Str(exc, "message") }.Where(s => !string.IsNullOrEmpty(s)));
            }
            var frames = new List<string>();
            if (body.RootElement.TryGetProperty("frames", out var fs) && fs.ValueKind == JsonValueKind.Array)
                frames = fs.EnumerateArray().Take(CrashLog.MaxFrames).Select(f => f.ValueKind == JsonValueKind.String ? f.GetString()! : "?").ToList();
            return new Crash(Str(head.RootElement, "timestamp") ?? "", Str(head.RootElement, "app_version") ?? "", exception, frames);
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // MARK: 본문

    /// "GodDB 1.0.0 · Windows 10.0.22631 · X64" (Mac: macOS 버전 · 칩)
    public static string Environment(string appVersion) =>
        $"GodDB {appVersion} · {ClientInfo.OsVersion} · {RuntimeInformation.OSArchitecture}";

    public static string LogText(IEnumerable<string> errors, IEnumerable<Crash> crashes)
    {
        var output = new List<string>();
        var errorList = errors.ToList();
        if (errorList.Count > 0)
        {
            output.Add("# 최근 오류");
            output.AddRange(errorList);
        }
        foreach (var c in crashes)
        {
            output.Add("");
            output.Add($"# 크래시 {c.Date} · {c.AppVersion} · {c.Exception}");
            output.AddRange(c.Frames);
        }
        return string.Join('\n', output).Trim();
    }

    /// 버그 신고 템플릿(.github/ISSUE_TEMPLATE/bug.yml)의 칸 id 로 미리 채운 이슈 작성 주소.
    /// 주소 길이 제한 때문에 로그는 앞부분만 넣는다.
    public static Uri IssueUrl(string title, string version, string? db, string steps, string expected, string log, int maxLength = 7_500)
    {
        string Build(string logBody)
        {
            var q = new List<(string, string)>
            {
                ("template", "bug.yml"), ("title", title), ("labels", "type:bug"), ("version", version),
                ("steps", steps), ("expected", expected), ("log", logBody),
            };
            if (db != null) q.Add(("db", db));
            return $"https://github.com/{Repo}/issues/new?" + string.Join("&", q.Select(kv => kv.Item1 + "=" + Uri.EscapeDataString(kv.Item2)));
        }
        var body = log;
        var url = Build(body);
        while (url.Length > maxLength && body.Length > 0)
        {
            var cut = body.Length * 3 / 4;
            if (cut > 0 && char.IsHighSurrogate(body[cut - 1])) cut--;
            body = body[..cut] + "\n…(잘림)";
            if (body.Length < 40) body = "";
            url = Build(body);
        }
        return new Uri(url);
    }

    /// 주소 query 를 (이름, 값) 으로 (테스트·화면용)
    public static List<KeyValuePair<string, string>> QueryItems(Uri url) =>
        url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(p => new KeyValuePair<string, string>(Uri.UnescapeDataString(p[0]), p.Length > 1 ? Uri.UnescapeDataString(p[1]) : ""))
            .ToList();
}
