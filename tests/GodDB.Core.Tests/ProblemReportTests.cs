using GodDB.Core.Audit;

namespace GodDB.Core.Tests;

public class ProblemReportTests
{
    [Fact]
    public void RedactsCompanyInfo()
    {
        const string text = """
        연결 실패: shop-prd.cluster-abc123.ap-northeast-2.rds.amazonaws.com:3306 (SSH 10.20.30.40 경유)
        kim@MacBook-Pro 이 kim.dev@corp.example.com 로 접속 · 파일 /Users/kim/Library/Logs/x.log
        """;
        var r = ProblemReport.Redact(text, identity: "kim@MacBook-Pro", home: "/Users/kim");
        Assert.True(!r.Contains("amazonaws") && r.Contains("<host>:3306"));
        Assert.True(!r.Contains("10.20.30.40") && r.Contains("<ip>"));
        Assert.True(!r.Contains("corp.example.com") && r.Contains("<email>"));
        Assert.True(r.Contains("<user> 이") && r.Contains("~/Library/Logs/x.log"));
    }

    /// Windows 추가: 사용자 폴더 경로 (C:\Users\이름, / 로 적은 경로, 다른 계정 폴더)
    [Fact]
    public void RedactsWindowsUserFolders()
    {
        const string text = @"파일 C:\Users\kim\AppData\Local\GodDB\Logs\x.tsv · C:/Users/kim/Desktop/a.sql · D:\Users\lee\b.sql · kim@DESKTOP-AB12 · kim";
        var r = ProblemReport.Redact(text, identity: "kim@DESKTOP-AB12", home: @"C:\Users\kim", user: "kim");
        Assert.Contains(@"~\AppData\Local\GodDB\Logs\x.tsv", r);
        Assert.Contains("~/Desktop/a.sql", r);
        Assert.Contains(@"~\b.sql", r);
        Assert.DoesNotContain("kim", r);
        Assert.DoesNotContain("lee", r);
        Assert.EndsWith("<user> · <user>", r);
    }

    /// Mac 은 macOS 크래시 보고서(.ips)를 읽는다. Windows 는 앱이 쓴 크래시 파일 (같은 "머리 JSON 한 줄 + 본문 JSON" 모양).
    [Fact]
    public void ParsesCrashReport()
    {
        const string head = """{"app_name":"GodDB","timestamp":"2026-11-13 14:59:45.00 +0900","app_version":"0.9.0","os":"Windows 10.0.22631"}""";
        const string body = """
        {"exception":{"type":"System.InvalidOperationException","message":"Collection was modified"},
         "frames":["GodDB.App.ResultGrid.Render()","GodDB.App.MainWindow.OnTick()"],"detail":"…"}
        """;
        var c = ProblemReport.ParseCrash(head + "\n" + body);
        Assert.Equal("0.9.0", c?.AppVersion);
        Assert.Equal("2026-11-13 14:59:45.00 +0900", c?.Date);
        Assert.Equal("System.InvalidOperationException: Collection was modified", c?.Exception);
        Assert.Equal(["GodDB.App.ResultGrid.Render()", "GodDB.App.MainWindow.OnTick()"], c?.Frames);
        Assert.Null(ProblemReport.ParseCrash("not json"));
    }

    /// Windows 추가: CrashLog.Write 로 남긴 파일을 최근 3개까지 요약한다
    [Fact]
    public void CrashLogWritesAndReadsRecentThree()
    {
        using var dir = new TempDir();
        Exception error;
        try { throw new InvalidOperationException("boom"); } catch (Exception e) { error = e; }
        var start = new DateTimeOffset(2026, 11, 13, 14, 0, 0, TimeSpan.FromHours(9));
        for (int i = 0; i < 4; i++)
        {
            var path = CrashLog.Write(error, "1.0.0", dir.Path, start.AddMinutes(i));
            Assert.NotNull(path);
            File.SetLastWriteTimeUtc(path!, start.AddMinutes(i).UtcDateTime);
        }
        var crashes = ProblemReport.RecentCrashes(directory: dir.Path);
        Assert.Equal(3, crashes.Count);
        Assert.Equal("2026-11-13 14:03:00.00 +0900", crashes[0].Date);
        Assert.Equal("1.0.0", crashes[0].AppVersion);
        Assert.Equal("System.InvalidOperationException: boom", crashes[0].Exception);
        Assert.NotEmpty(crashes[0].Frames);
        Assert.Contains(nameof(CrashLogWritesAndReadsRecentThree), crashes[0].Frames[0]);
        Assert.DoesNotContain(crashes[0].Frames, f => f.Contains(".cs:"));   // 파일 경로는 남기지 않음
        var log = ProblemReport.LogText(["[접속] 실패"], crashes.Take(1));
        Assert.StartsWith("# 최근 오류\n[접속] 실패\n\n# 크래시 2026-11-13 14:03:00.00 +0900 · 1.0.0 · System.InvalidOperationException: boom", log);
        Assert.Empty(ProblemReport.RecentCrashes(directory: dir.File("none")));
    }

    [Fact]
    public void IssueURLIsPrefilledAndBounded()
    {
        var longLog = string.Concat(Enumerable.Repeat("오류 로그 줄\n", 3000));
        var url = ProblemReport.IssueUrl("앱이 종료돼요", "1.0.0", "Redis", "1. 키 트리 열기", "종료되지 않음", longLog);
        Assert.True(url.AbsoluteUri.Length <= 7_500);
        Assert.StartsWith("https://github.com/zio0911/god_db_win/issues/new?", url.AbsoluteUri);
        var q = ProblemReport.QueryItems(url);
        Assert.Equal("bug.yml", q.First(x => x.Key == "template").Value);
        Assert.Equal("1.0.0", q.First(x => x.Key == "version").Value);
        Assert.EndsWith("…(잘림)", q.First(x => x.Key == "log").Value);
        Assert.Equal("Redis", q.First(x => x.Key == "db").Value);
    }

    [Fact]
    public void EnvironmentLine()
    {
        var env = ProblemReport.Environment("1.0.0");
        Assert.StartsWith("GodDB 1.0.0 · ", env);
        if (OperatingSystem.IsWindows()) Assert.Contains("Windows", env);
    }
}
