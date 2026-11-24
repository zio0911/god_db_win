using System.Globalization;
using GodDB.Core.Platform;

namespace GodDB.Core.Tests;

/// 이전 버전 설치 (Mac PreviousVersionsTests). Mac 의 DMG 자산(`GodDB-<버전>.dmg`) 은 Windows 의 Velopack 피드 + 전체 패키지로 바꿨다.
public class PreviousVersionsTests
{
    private static Uri U(string s) => new(s);

    private static PreviousVersions.Asset[] Assets(string v) =>
    [
        new($"GodDB-{PreviousVersions.PackVersion(v)}-full.nupkg", U($"https://github.com/zio0911/god_db_win/releases/download/v{v}/GodDB-{PreviousVersions.PackVersion(v)}-full.nupkg"), 62_000_000),
        new("releases.win.json", U($"https://github.com/zio0911/god_db_win/releases/download/v{v}/releases.win.json"), 300),
        new("GodDB-win-Setup.exe", U($"https://github.com/zio0911/god_db_win/releases/download/v{v}/GodDB-win-Setup.exe"), 63_000_000),
    ];

    private static PreviousVersions.Release Release(string v, bool draft = false, bool prerelease = false, string body = "", bool package = true) =>
        new()
        {
            TagName = "v" + v,
            Draft = draft,
            Prerelease = prerelease,
            PublishedAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            Body = body,
            Assets = package ? [.. Assets(v)] : [],
        };

    /// 1.3.7 릴리스 노트 (scripts/release-notes.sh + GitHub 자동 생성 부분) — Mac 테스트와 같은 글
    private const string Body137 = """
    ## 변경 사항
    - ⌘↩ 는 커서가 있는 문장(강조된 문장)만 실행. 모든 문장은 새 **전체 실행** 버튼 또는 ⌥⌘↩
    - 끌어서 바꾼 화면 크기를 기억. 메뉴 보기 > **레이아웃 초기화** 로 처음 크기로
    - DB 종류를 로고 아이콘으로
    - 설정 > 일반 > 안전: 위험한 명령 확인을 켜고 끌 수 있음

    ## 설치
    `GodDB-*.dmg` 를 열고 GodDB 를 Applications 로 끌어다 놓은 뒤 실행하세요.
    이미 1.1.0 이상을 쓰고 있으면 앱이 자동으로 업데이트를 알려줘요.


    <!-- Release notes generated using configuration in .github/release.yml at v1.3.7 -->

    ## What's Changed
    ### 새 기능
    * feat: 화면 경계 크기 기억 by @zio0911 in https://github.com/zio0911/god_db/pull/100

    **Full Changelog**: https://github.com/zio0911/god_db/compare/v1.3.6...v1.3.7
    """;

    // MARK: - 목록

    [Fact]
    public void DecodesGitHubJSON()
    {
        const string json = """
        [{"tag_name":"v1.3.7","draft":false,"prerelease":false,"published_at":"2026-11-18T04:57:43Z",
          "html_url":"https://github.com/zio0911/god_db_win/releases/tag/v1.3.7","body":"## 변경 사항\r\n- 고침",
          "assets":[{"name":"RELEASES","browser_download_url":"https://x/v1.3.7/RELEASES","size":10},
                    {"name":"releases.win.json","browser_download_url":"https://x/v1.3.7/releases.win.json","size":300},
                    {"name":"GodDB-1.3.7-full.nupkg","browser_download_url":"https://x/v1.3.7/GodDB-1.3.7-full.nupkg","size":62698273}]}]
        """;
        var releases = PreviousVersions.Decode(json);
        Assert.Single(releases);
        Assert.NotNull(releases[0].PublishedAt);
        var entries = PreviousVersions.Entries(releases, "1.4.0");
        Assert.Equal(["1.3.7"], entries.Select(e => e.Version));
        Assert.Equal(62698273, entries[0].Package.Size);
        Assert.Equal("https://x/v1.3.7/GodDB-1.3.7-full.nupkg", entries[0].Package.BrowserDownloadUrl.ToString());
        Assert.Equal("https://x/v1.3.7/", entries[0].FeedBaseUrl);
        Assert.Equal(["고침"], entries[0].Notes);
    }

    [Fact]
    public void FiltersDraftPrereleaseOldAndCurrent()
    {
        var releases = new[]
        {
            Release("1.4.0"),                     // 지금 버전
            Release("1.3.7.1"),
            Release("1.3.8", draft: true),
            Release("1.5.0", prerelease: true),
            Release("1.3.6"),                     // 1.3.7 미만 (Windows 판 이전, Mac 은 1.1.0 미만)
            Release("0.9.1"),
            Release("1.3.7"),                     // 경계: 포함
            Release("1.3.9", package: false),     // 설치 파일 없음
            new PreviousVersions.Release { TagName = "nightly", Assets = [.. Assets("nightly")] },
        };
        Assert.Equal(["1.3.7.1", "1.3.7"], PreviousVersions.Entries(releases, "1.4.0").Select(e => e.Version));
    }

    [Fact]
    public void SortsNewestFirstNumerically()
    {
        var releases = new[] { "1.4.1", "1.10.0", "1.3.7", "1.9.2", "1.3.10", "1.3.7.1" }.Select(v => Release(v));
        Assert.Equal(["1.10.0", "1.9.2", "1.4.1", "1.3.10", "1.3.7.1", "1.3.7"],
                     PreviousVersions.Entries(releases, "dev").Select(e => e.Version));
    }

    /// Mac dmgAssetNeedsExactName: Windows 는 `GodDB-<패키지 번호>-full.nupkg` 정확히 (델타 · 다른 버전 · Setup.exe 는 아님)
    [Fact]
    public void PackageAssetNeedsExactName()
    {
        var assets = new PreviousVersions.Asset[]
        {
            new("GodDB-win-Setup.exe", U("https://x/a"), 1),
            new("GodDB-1.3.8-r.1-delta.nupkg", U("https://x/b"), 1),
            new("GodDB-1.3.7-full.nupkg", U("https://x/c"), 1),
            new("GodDB-1.3.8-r.1-full.nupkg", U("https://x/d"), 1),
        };
        Assert.Equal("https://x/d", PreviousVersions.PackageAsset(assets, "1.3.7.1")?.BrowserDownloadUrl.ToString());
        Assert.Equal("https://x/c", PreviousVersions.PackageAsset(assets, "1.3.7")?.BrowserDownloadUrl.ToString());
        Assert.Null(PreviousVersions.PackageAsset(assets, "1.3.7.2"));
        Assert.Null(PreviousVersions.FeedAsset(assets));
    }

    [Fact]
    public void VersionFromTag()
    {
        Assert.Equal("1.3.6", PreviousVersions.VersionFromTag("v1.3.6"));
        Assert.Equal("1.10.0", PreviousVersions.VersionFromTag("1.10.0"));
        Assert.Equal("1.3.7.1", PreviousVersions.VersionFromTag("v1.3.7.1"));
        Assert.Null(PreviousVersions.VersionFromTag("v1.4.0-beta"));
        Assert.Null(PreviousVersions.VersionFromTag("latest"));
    }

    /// Windows 추가: 화면 버전 ↔ Velopack 패키지 번호 (release.yml 과 같은 규칙: a.b.c.d → a.b.(c+1)-r.d)
    [Fact]
    public void PackVersionMatchesReleaseWorkflow()
    {
        Assert.Equal("1.3.8-r.1", PreviousVersions.PackVersion("1.3.7.1"));
        Assert.Equal("1.3.7", PreviousVersions.PackVersion("1.3.7"));
        Assert.Equal("1.4.1-r.12", PreviousVersions.PackVersion("1.4.0.12"));
        Assert.Equal("1.3.7.1", PreviousVersions.DisplayVersion("1.3.8-r.1"));
        Assert.Equal("1.3.7", PreviousVersions.DisplayVersion("1.3.7"));
        // release.yml 에 같은 규칙이 있다 (한쪽만 바뀌지 않게)
        var workflow = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, ".github", "workflows", "release.yml"));
        Assert.Contains("P=\"$A.$B.$((C + 1))-r.$D\"", workflow);
    }

    // MARK: - 변경 사항

    [Fact]
    public void ChangeNotesTakeFirstThreeUnderChanges()
    {
        Assert.Equal(
        [
            "⌘↩ 는 커서가 있는 문장(강조된 문장)만 실행. 모든 문장은 새 **전체 실행** 버튼 또는 ⌥⌘↩",
            "끌어서 바꾼 화면 크기를 기억. 메뉴 보기 > **레이아웃 초기화** 로 처음 크기로",
            "DB 종류를 로고 아이콘으로",
        ], PreviousVersions.ChangeNotes(Body137));
    }

    [Fact]
    public void ChangeNotesOlderHeadings()
    {
        // 1.1.0: 설치 안내가 먼저, 변경은 "## 주요 변경"
        const string body110 = """
        ## 설치
        `GodDB-1.1.0.dmg` 를 열고 끌어다 놓으세요.

        ## 주요 변경
        - **트랜잭션**: 자동 커밋 / 수동 커밋
        - **자동 업데이트**

        ## What's Changed
        * 트랜잭션 by @zio0911 in https://github.com/zio0911/god_db/pull/20
        """;
        Assert.Equal(["**트랜잭션**: 자동 커밋 / 수동 커밋", "**자동 업데이트**"], PreviousVersions.ChangeNotes(body110));
        // 1.3.1: "## 수정" 다음 "## 개발" (개발 항목은 뺀다)
        Assert.Equal(["고침 A"], PreviousVersions.ChangeNotes("## 수정\n- 고침 A\n\n## 개발\n- CI 정리\n- 빌드\n"));
    }

    [Fact]
    public void ChangeNotesFallBackToGeneratedList()
    {
        // 1.3.3: 자동 생성 부분만 있음
        const string body = """
        <!-- Release notes generated using configuration in .github/release.yml at v1.3.3 -->

        ## What's Changed
        ### 버그 수정
        * fix: ORDER BY 없는 결과 이어 받기 by @zio0911 in https://github.com/zio0911/god_db/pull/82
        """;
        Assert.Equal(["fix: ORDER BY 없는 결과 이어 받기"], PreviousVersions.ChangeNotes(body));
        Assert.Empty(PreviousVersions.ChangeNotes(""));
    }

    // MARK: - 백업

    [Fact]
    public void BackupNameUsesVersionAndLocalStamp()
    {
        var date = new DateTime(2026, 11, 18, 9, 30, 5);
        var name = PreviousVersions.BackupName("1.4.0", date);
        Assert.Equal("GodDB-backup-1.4.0-" + date.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), name);
        Assert.Equal("20261118-093005", PreviousVersions.BackupStamp(name));
    }

    [Fact]
    public void PruneKeepsThreeNewestBackupsOnly()
    {
        string[] names =
        [
            "GodDB",
            "GodDB-backup-1.3.7-20261118-090000",
            "GodDB-backup-1.4.0-20261120-101500",
            "GodDB-backup-1.4.0-20261120-101501",
            "GodDB-backup-1.3.6-20261101-235959",
            "GodDB-backup-1.4.0-20261119-120000",
            "GodDB-backup-notes",                 // 백업 꼴 아님 → 건드리지 않음
            "GodDB-backup--20261120-000000",      // 버전 없음 → 건드리지 않음
            "Other-20261120-000000",
        ];
        Assert.Equal(new HashSet<string> { "GodDB-backup-1.3.7-20261118-090000", "GodDB-backup-1.3.6-20261101-235959" },
                     PreviousVersions.BackupsToPrune(names).ToHashSet());
        Assert.Empty(PreviousVersions.BackupsToPrune(names.Take(3)));
        Assert.Equal(5, PreviousVersions.BackupsToPrune(names, keep: 0).Count);
    }

    /// Windows 추가: 데이터 폴더를 옆에 통째 복사하고 최근 3개만 남긴다 (Mac 은 화면 쪽 backupDataFolder)
    [Fact]
    public void BackupDataFolderCopiesAndPrunes()
    {
        using var tmp = new TempDir();
        var data = Path.Combine(tmp.Path, "GodDB");
        Directory.CreateDirectory(Path.Combine(data, "diagram-chats"));
        File.WriteAllText(Path.Combine(data, "profiles.json"), "[]");
        File.WriteAllText(Path.Combine(data, "diagram-chats", "a.json"), "x");
        foreach (var old in new[] { "20200101-000000", "20200102-000000", "20200103-000000" })
            Directory.CreateDirectory(Path.Combine(tmp.Path, "GodDB-backup-1.3.7-" + old));
        var name = PreviousVersions.BackupDataFolder(data, "1.4.0", new DateTime(2026, 11, 24, 10, 0, 0));
        Assert.Equal("GodDB-backup-1.4.0-20261124-100000", name);
        Assert.Equal("x", File.ReadAllText(Path.Combine(tmp.Path, name!, "diagram-chats", "a.json")));
        Assert.True(File.Exists(Path.Combine(data, "profiles.json")));   // 원본은 그대로
        var backups = Directory.GetDirectories(tmp.Path).Select(Path.GetFileName).Where(n => n!.StartsWith("GodDB-backup-")).Order().ToList();
        Assert.Equal(["GodDB-backup-1.3.7-20200102-000000", "GodDB-backup-1.3.7-20200103-000000", "GodDB-backup-1.4.0-20261124-100000"], backups);
        Assert.Null(PreviousVersions.BackupDataFolder(Path.Combine(tmp.Path, "none"), "1.4.0", DateTime.Now));
    }

    // MARK: - 설치 확인 (Mac: 서명 · 최소 OS · 서명 없는 빌드 거절 · 다른 팀 서명 거절 → Windows: Velopack 버전 확인)

    /// Mac unsignedCurrentBuildIsRefused 대응: 설치본이 아니면(개발 실행) 이유가 있는 오류
    [Fact]
    public void NotInstalledBuildIsRefusedWithReason()
    {
        var e = new PreviousVersions.InstallException(PreviousVersions.InstallErrorKind.NotInstalled);
        Assert.Contains("GitHub 에서 직접 받아 설치하세요", e.Message);
    }

    /// Mac rejectsAppSignedByAnotherTeam · versionMismatch 대응: 피드가 고른 버전과 다른 패키지를 주면 설치하지 않는다
    [Fact]
    public void RejectsPackageOfAnotherVersion()
    {
        PreviousVersions.EnsureVersion("1.3.7.1", "1.3.8-r.1");
        PreviousVersions.EnsureVersion("1.3.7", "1.3.7");
        var e = Assert.Throws<PreviousVersions.InstallException>(() => PreviousVersions.EnsureVersion("1.3.7", "1.3.8-r.1"));
        Assert.Equal(PreviousVersions.InstallErrorKind.VersionMismatch, e.Kind);
        Assert.Equal("받은 앱의 버전(1.3.7.1)이 고른 버전(1.3.7)과 달라요", e.Message);
        Assert.Equal(PreviousVersions.InstallErrorKind.PackageNotFound,
                     Assert.Throws<PreviousVersions.InstallException>(() => PreviousVersions.EnsureVersion("1.3.7", null)).Kind);
    }

    /// Mac 도우미 실패 표시 파일 대응: 설치 전에 고른 버전을 남기고, 다음 실행 때 그 버전이 아니면 실패로 본다
    [Fact]
    public void PendingMarkerReportsResultOnce()
    {
        using var tmp = new TempDir();
        var marker = Path.Combine(tmp.Path, "pending.txt");
        Assert.Null(PreviousVersions.TakePending(marker, "1.3.7"));
        PreviousVersions.WritePending(marker, "1.3.7");
        Assert.Equal(("1.3.7", true), PreviousVersions.TakePending(marker, "1.3.7"));
        Assert.False(File.Exists(marker));
        PreviousVersions.WritePending(marker, "1.3.7");
        Assert.Equal(("1.3.7", false), PreviousVersions.TakePending(marker, "1.4.0"));
        Assert.Null(PreviousVersions.TakePending(marker, "1.4.0"));
    }
}
