using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodDB.Core.Platform;

namespace GodDB.Core.Audit;

/// 앱이 처리하지 못한 예외를 %LOCALAPPDATA%\GodDB\Crashes 에 남긴다 (문제 보고가 요약해 담는다).
/// Mac 판은 macOS 가 만드는 크래시 보고서(.ips)를 읽는다. Windows 판은 앱이 직접 쓴다.
/// 파일 형식 (.ips 와 비슷): 첫 줄 머리 JSON, 나머지 본문 JSON
///   {"app_name":"GodDB","timestamp":"2026-11-13 14:59:45.00 +0900","app_version":"1.0.0","os":"Windows 10.0.22631"}
///   {"exception":{"type":"System.InvalidOperationException","message":"…"},"frames":["Ns.Type.Method(…)", …],"detail":"…"}
public static class CrashLog
{
    public const string FilePrefix = "GodDB-crash-";
    public const string FileExtension = ".txt";

    /// 남길 스택 줄 수 (Mac 판 요약과 같음)
    public const int MaxFrames = 12;

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// 예외를 크래시 파일로 쓴다. 파일 경로 (실패하면 null). 예외를 던지지 않는다.
    public static string? Write(Exception error, string? appVersion = null, string? directory = null, DateTimeOffset? now = null)
    {
        try
        {
            var dir = AppPaths.Ensure(directory ?? AppPaths.CrashDirectory);
            var at = now ?? DateTimeOffset.Now;
            var path = Path.Combine(dir, FilePrefix + at.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + FileExtension);
            var text = Format(error, appVersion ?? AppInfo.Version, at);
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                UserOnlyFile.Restrict(path);
                fs.Write(new UTF8Encoding(false).GetBytes(text));
            }
            return path;
        }
        catch { return null; }
    }

    /// 크래시 파일 내용
    public static string Format(Exception error, string appVersion, DateTimeOffset at)
    {
        var head = new JsonObject
        {
            ["app_name"] = AppInfo.Name,
            ["timestamp"] = Timestamp(at),
            ["app_version"] = appVersion,
            ["os"] = ClientInfo.OsVersion,
        };
        var frames = new JsonArray();
        foreach (var f in Frames(error.StackTrace).Take(MaxFrames)) frames.Add(f);
        var body = new JsonObject
        {
            ["exception"] = new JsonObject { ["type"] = error.GetType().FullName, ["message"] = error.Message },
            ["frames"] = frames,
            ["detail"] = error.ToString(),
        };
        return head.ToJsonString(Json) + "\n" + body.ToJsonString(Json) + "\n";
    }

    /// .ips 와 같은 시각 형식 "2026-11-13 14:59:45.00 +0900"
    internal static string Timestamp(DateTimeOffset at)
    {
        var off = at.Offset;
        var sign = off < TimeSpan.Zero ? "-" : "+";
        off = off.Duration();
        return at.ToString("yyyy-MM-dd HH:mm:ss.ff", CultureInfo.InvariantCulture) + $" {sign}{off.Hours:00}{off.Minutes:00}";
    }

    /// 스택 줄 → "Ns.Type.Method(…)" (파일 경로·줄 번호는 뺀다: 개인 경로가 새지 않게)
    internal static IEnumerable<string> Frames(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace)) yield break;
        foreach (var raw in stackTrace.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("---", StringComparison.Ordinal)) continue;
            if (line.StartsWith("at ", StringComparison.Ordinal)) line = line[3..];
            var inFile = line.IndexOf(" in ", StringComparison.Ordinal);
            if (inFile > 0 && line.IndexOf(':', inFile) > 0) line = line[..inFile];
            yield return line;
        }
    }
}
