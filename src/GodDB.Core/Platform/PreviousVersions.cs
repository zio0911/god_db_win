using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GodDB.Core.Ai;
using static GodDB.Core.L10n;

namespace GodDB.Core.Platform;

/// 이전 버전 설치 (Mac 1.4.0 F5): GitHub 릴리스 목록 · 변경 사항 추출 · 백업 이름 · 설치 확인.
/// 화면 · 내려받기 · 설치는 앱(PreviousVersionInstaller) 이 맡고, 여기에는 판단 로직만 둔다.
///
/// Windows 에서 다른 점 (Mac: DMG 를 받아 codesign · 공증 확인 후 도우미 스크립트로 .app 을 바꿔치기):
/// - 설치는 Velopack 이 한다. 고른 릴리스의 `releases.win.json`(Velopack 피드)과 `GodDB-<패키지 번호>-full.nupkg` 를
///   Velopack 이 받아 SHA-256 을 확인하고 앱을 끈 뒤 바꿔 넣고 다시 켠다 (버전 낮추기 허용). 앱 위치 권한 · 교체 · 실패 시 원래대로는 Velopack 몫.
/// - 버전 번호: 화면 · 태그는 `1.3.7.1` 처럼 네 자리, Velopack 패키지 번호는 SemVer 라 `1.3.8-r.1`
///   (.github/workflows/release.yml 과 같은 규칙, <see cref="PackVersion"/>).
/// - 코드 서명은 아직 없다 (docs/PLAN.md: 1.3.7 은 서명 없이 배포). 그래서 Mac 의 팀 ID 비교 대신 Velopack 피드의 SHA-256 확인만 한다.
public static partial class PreviousVersions
{
    public const string Repository = "zio0911/god_db_win";
    public static readonly Uri ReleasesApi = new($"https://api.github.com/repos/{Repository}/releases?per_page=100");
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases");
    /// 이보다 낮은 버전은 목록에 넣지 않는다 (Windows 판 첫 릴리스. Mac 은 1.1.0)
    public const string MinimumVersion = "1.3.7";
    /// 백업 폴더 이름 앞부분 (`GodDB-backup-<버전>-<yyyyMMdd-HHmmss>`)
    public const string BackupPrefix = "GodDB-backup-";
    public const int BackupsToKeep = 3;
    /// Velopack 피드 파일 이름 (채널 win)
    public const string FeedFileName = "releases.win.json";

    // MARK: - 릴리스 목록

    public sealed record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] Uri BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size);

    public sealed record Release
    {
        [JsonPropertyName("tag_name"), JsonRequired] public string TagName { get; init; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; init; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; init; }
        [JsonPropertyName("body")] public string? Body { get; init; }
        [JsonPropertyName("html_url")] public Uri? HtmlUrl { get; init; }
        [JsonPropertyName("assets")] public List<Asset> Assets { get; init; } = [];
    }

    /// 목록 한 줄
    public sealed record Entry(string Version, DateTimeOffset? Date, IReadOnlyList<string> Notes, Asset Package, Asset Feed, Uri? PageUrl)
    {
        /// Velopack 이 읽을 피드 주소 (그 릴리스의 내려받기 폴더, 끝 `/`)
        public string FeedBaseUrl => FeedBase(Feed.BrowserDownloadUrl);
        /// Velopack 패키지 번호 (예: 1.3.8-r.1)
        public string PackageVersion => PackVersion(Version);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false };

    /// GitHub API 응답(JSON 배열)을 읽는다
    public static List<Release> Decode(string json) => JsonSerializer.Deserialize<List<Release>>(json, Json) ?? [];

    [GeneratedRegex(@"^\d+(\.\d+){1,3}$")] private static partial Regex VersionPattern();

    /// `v1.3.7.1` → `1.3.7.1`. 숫자.숫자(.숫자(.숫자)) 꼴이 아니면 null
    public static string? VersionFromTag(string tag)
    {
        var v = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        return VersionPattern().IsMatch(v) ? v : null;
    }

    /// 1.10.0 > 1.9.2 처럼 숫자로 비교 (릴리스 노트와 같은 규칙)
    public static int Compare(string a, string b) => ReleaseNotes.Compare(a, b);

    /// 화면 버전 → Velopack 패키지 번호. `a.b.c.d` (Windows 수정판) → `a.b.(c+1)-r.d`, 그 밖은 그대로 (release.yml 과 같음)
    public static string PackVersion(string version)
    {
        var p = version.Split('.');
        if (p.Length == 4 && int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var c))
            return $"{p[0]}.{p[1]}.{c + 1}-r.{p[3]}";
        return version;
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)-r\.(\d+)$")] private static partial Regex RevisionPattern();

    /// Velopack 패키지 번호 → 화면 버전 (`1.3.8-r.1` → `1.3.7.1`). 수정판 꼴이 아니면 그대로
    public static string DisplayVersion(string packVersion)
    {
        var m = RevisionPattern().Match(packVersion);
        if (!m.Success) return packVersion;
        var c = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return c == 0 ? packVersion : $"{m.Groups[1].Value}.{m.Groups[2].Value}.{c - 1}.{m.Groups[4].Value}";
    }

    /// 보여 줄 버전: 초안 · 사전 릴리스 제외, 1.3.7 이상, 지금 버전 제외, Velopack 피드와 전체 패키지가 있는 것만. 최신순.
    public static List<Entry> Entries(IEnumerable<Release> releases, string current)
    {
        var seen = new HashSet<string>();
        var output = new List<Entry>();
        foreach (var r in releases)
        {
            if (r.Draft || r.Prerelease) continue;
            if (VersionFromTag(r.TagName) is not { } v) continue;
            if (Compare(v, MinimumVersion) < 0 || Compare(v, current) == 0) continue;
            if (PackageAsset(r.Assets, v) is not { } package || FeedAsset(r.Assets) is not { } feed) continue;
            if (!seen.Add(v)) continue;
            output.Add(new Entry(v, r.PublishedAt, ChangeNotes(r.Body ?? ""), package, feed, r.HtmlUrl));
        }
        return output.OrderByDescending(e => e.Version, Comparer<string>.Create(Compare)).ToList();
    }

    /// 설치할 자산: 정확히 `GodDB-<패키지 번호>-full.nupkg` (Mac: `GodDB-<버전>.dmg`)
    public static Asset? PackageAsset(IEnumerable<Asset> assets, string version) =>
        assets.FirstOrDefault(a => a.Name == $"GodDB-{PackVersion(version)}-full.nupkg");

    /// Velopack 피드 `releases.win.json`
    public static Asset? FeedAsset(IEnumerable<Asset> assets) => assets.FirstOrDefault(a => a.Name == FeedFileName);

    /// 내려받기 주소에서 파일 이름을 뗀 폴더 주소 (끝 `/`)
    public static string FeedBase(Uri download)
    {
        var s = download.GetLeftPart(UriPartial.Path);
        return s[..(s.LastIndexOf('/') + 1)];
    }

    /// 릴리스 노트에서 변경 사항 첫 줄들.
    /// `## 변경 사항` 아래 목록이 기준. 없으면 `## 설치` · `## 개발` 과 자동 생성 부분(`## What's Changed` 이후)을 뺀 목록을 쓰고,
    /// 그것도 없으면 자동 생성 목록(`* feat: … by @x in …`)의 제목만 쓴다.
    public static List<string> ChangeNotes(string body, int limit = 3)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim(' ', '\t'));

        static string? Bullet(string line)
        {
            if (!line.StartsWith("- ", StringComparison.Ordinal) && !line.StartsWith("* ", StringComparison.Ordinal)) return null;
            var text = line[2..].Trim(' ', '\t');
            return text.Length == 0 ? null : text;
        }

        var primary = new List<string>();
        var preferred = new List<string>();
        var generated = new List<string>();
        string? section = null;
        var afterGenerated = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var title = line[3..].Trim(' ', '\t');
                section = title;
                if (title.ToLowerInvariant().StartsWith("what's changed", StringComparison.Ordinal)) afterGenerated = true;
                continue;
            }
            if (Bullet(line) is not { } text) continue;
            if (afterGenerated) generated.Add(GeneratedSuffix().Replace(text, ""));
            else if (section == "변경 사항") preferred.Add(text);
            else if (section is { } s && s != "설치" && s != "개발") primary.Add(text);
        }
        var chosen = preferred.Count > 0 ? preferred : primary.Count > 0 ? primary : generated;
        return chosen.Take(limit).ToList();
    }

    [GeneratedRegex(@"\s+by @\S+ in \S+$")] private static partial Regex GeneratedSuffix();

    // MARK: - 백업

    /// `GodDB-backup-1.4.0-20261120-103000` (date 는 이 PC 시각)
    public static string BackupName(string current, DateTime date) =>
        $"{BackupPrefix}{current.Replace('/', '-').Replace('\\', '-')}-{date.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";

    [GeneratedRegex(@"-(\d{8}-\d{6})$")] private static partial Regex StampPattern();

    /// 백업 이름 끝의 시각 (`yyyyMMdd-HHmmss`). 백업 이름 꼴이 아니면 null
    public static string? BackupStamp(string name)
    {
        if (!name.StartsWith(BackupPrefix, StringComparison.Ordinal)) return null;
        var m = StampPattern().Match(name);
        if (!m.Success || m.Index <= BackupPrefix.Length) return null;
        return m.Groups[1].Value;
    }

    /// 지울 백업 이름들: 백업 꼴 이름 중 최신 keep 개를 뺀 나머지 (시각 기준, 같으면 이름 순). 그 밖의 이름은 건드리지 않는다.
    public static List<string> BackupsToPrune(IEnumerable<string> names, int keep = BackupsToKeep) =>
        names.Select(n => (Name: n, Stamp: BackupStamp(n)))
            .Where(x => x.Stamp != null)
            .OrderByDescending(x => x.Stamp, StringComparer.Ordinal).ThenByDescending(x => x.Name, StringComparer.Ordinal)
            .Skip(Math.Max(0, keep))
            .Select(x => x.Name)
            .ToList();

    /// `…\GodDB` → `…\GodDB-backup-<버전>-<시각>` 로 통째 복사하고, 오래된 백업을 지워 최근 3개만 남긴다. 데이터 폴더가 없으면 null.
    public static string? BackupDataFolder(string dataDirectory, string current, DateTime now)
    {
        if (!Directory.Exists(dataDirectory)) return null;
        var parent = Path.GetDirectoryName(Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                     ?? throw new IOException("no parent folder");
        var name = BackupName(current, now);
        CopyDirectory(dataDirectory, Path.Combine(parent, name));
        // 백업도 본인만 (원본 폴더와 같게)
        UserOnlyFile.RestrictDirectory(Path.Combine(parent, name));
        foreach (var old in BackupsToPrune(Directory.GetDirectories(parent).Select(d => Path.GetFileName(d)!)))
        {
            try { Directory.Delete(Path.Combine(parent, old), recursive: true); } catch { }
        }
        return name;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var f in Directory.GetFiles(source))
            File.Copy(f, Path.Combine(destination, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.GetDirectories(source))
            CopyDirectory(d, Path.Combine(destination, Path.GetFileName(d)));
    }

    // MARK: - 설치 확인

    /// 설치할 수 없는 이유. 실패하면 설치하지 않는다.
    public enum InstallErrorKind
    {
        /// 지금 앱이 Velopack 설치본이 아님 (개발 실행) — Mac: 서명 없는 개발 빌드
        NotInstalled,
        /// 피드에 고른 버전이 없음
        PackageNotFound,
        /// 받은 패키지 버전이 고른 버전과 다름
        VersionMismatch,
    }

    public sealed class InstallException(InstallErrorKind kind, string? expected = null, string? found = null) : Exception(Describe(kind, expected, found))
    {
        public InstallErrorKind Kind { get; } = kind;

        private static string Describe(InstallErrorKind kind, string? expected, string? found) => kind switch
        {
            InstallErrorKind.NotInstalled =>
                Tr("지금 앱이 설치본이 아니라(개발 실행) 이전 버전을 설치할 수 없어요. GitHub 에서 직접 받아 설치하세요."),
            InstallErrorKind.PackageNotFound => Tr("이 버전의 설치 파일을 릴리스에서 찾지 못했어요"),
            _ => Tr("받은 앱의 버전(%@)이 고른 버전(%@)과 달라요", found ?? "?", expected ?? "?"),
        };
    }

    /// Velopack 이 고른 패키지 번호가 기대한 버전인지 (아니면 InstallException)
    public static void EnsureVersion(string expectedVersion, string? foundPackVersion)
    {
        if (foundPackVersion == null) throw new InstallException(InstallErrorKind.PackageNotFound);
        if (foundPackVersion != PackVersion(expectedVersion))
            throw new InstallException(InstallErrorKind.VersionMismatch, expectedVersion, DisplayVersion(foundPackVersion));
    }

    // MARK: - 다음 실행 때 결과 확인

    /// 설치를 맡기기 직전에 쓰는 표시 파일 (고른 버전 한 줄). 다음 실행 때 읽고 지운다.
    /// Mac 은 도우미 스크립트가 실패할 때만 이유를 남기지만, Windows 는 Velopack 이 교체하므로 "시작했는데 버전이 그대로" 로 실패를 안다.
    public static string PendingMarker => Path.Combine(AppPaths.DataDirectory, "previous-version-install-pending.txt");

    public static void WritePending(string markerPath, string targetVersion)
    {
        if (Path.GetDirectoryName(markerPath) is { Length: > 0 } dir) AppPaths.Ensure(dir);
        File.WriteAllText(markerPath, targetVersion + "\n");
    }

    /// 표시 파일이 있으면 지우고 결과를 돌려준다: null = 표시 없음, (target, true) = 그 버전으로 바뀜, (target, false) = 바뀌지 않음
    public static (string Target, bool Succeeded)? TakePending(string markerPath, string currentVersion)
    {
        string raw;
        try
        {
            if (!File.Exists(markerPath)) return null;
            raw = File.ReadAllText(markerPath).Trim();
            File.Delete(markerPath);
        }
        catch { return null; }
        if (raw.Length == 0) return null;
        return (raw, raw == currentVersion);
    }
}
