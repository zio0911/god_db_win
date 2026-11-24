namespace GodDB.Core;

/// 앱 데이터 위치
/// - 설정 · 접속 목록 · 모델 · AI 대화: %APPDATA%\GodDB (Mac: ~/Library/Application Support/GodDB)
/// - 로그 · 크래시: %LOCALAPPDATA%\GodDB (Mac: ~/Library/Logs/GodDB)
/// 테스트나 화면 자동 테스트는 GODDB_DATA_DIR 로 다른 폴더를 쓴다 (실제 접속 목록을 건드리지 않음).
public static class AppPaths
{
    /// Debug 빌드인지. 배포본(Setup.exe · Portable.zip)은 Release 빌드다
#if DEBUG
    internal static readonly bool DebugBuild = true;
#else
    internal static readonly bool DebugBuild = false;
#endif

    /// 테스트용 데이터 폴더. Debug 빌드나 테스트 프로세스에서만 (Mac: 화면 테스트 복사본에서만, #197)
    private static string? Override => (DebugBuild || Platform.LocalDataCrypto.IsTestProcess)
        && Environment.GetEnvironmentVariable("GODDB_DATA_DIR") is { Length: > 0 } d ? d : null;

    /// 화면 자동 테스트 실행인지.
    /// 환경변수만으로는 켜지지 않는다 — 정식 앱을 GODDB_UI_TEST=1 로 띄워 감사 로그를 끄거나
    /// 데이터 폴더 · 비밀번호를 바꾸지 못하게, Debug 빌드일 때만 (#197).
    /// Mac 은 테스트 복사본 번들 ID 로 가른다. Windows 는 번들 ID 가 없어 빌드 구성으로 가른다.
    public static bool UiTest => IsUiTest(Environment.GetEnvironmentVariable, DebugBuild);

    internal static bool IsUiTest(Func<string, string?> env, bool debugBuild) =>
        debugBuild && env("GODDB_UI_TEST") == "1";

    public static string DataDirectory => Override ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GodDB");

    public static string LocalDirectory => Override != null ? Path.Combine(Override, "local") : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GodDB");

    public static string LogDirectory => Path.Combine(LocalDirectory, "Logs");
    public static string CrashDirectory => Path.Combine(LocalDirectory, "Crashes");
    public static string ProfilesFile => Path.Combine(DataDirectory, "profiles.json");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string ModelsDirectory => Path.Combine(DataDirectory, "models");
    public static string ChatsDirectory => Path.Combine(DataDirectory, "diagram-chats");

    /// 화면 자동 테스트에서만: 로컬 Docker 테스트 DB 비밀번호 (자격 증명 관리자를 쓰지 않음)
    public static string? UiTestPassword => UiTest ? Environment.GetEnvironmentVariable("GODDB_UI_TEST_PASSWORD") : null;

    private static readonly HashSet<string> Restricted = new(StringComparer.OrdinalIgnoreCase);

    /// 폴더를 만들고 본인만 접근하게 한다.
    /// - Windows: 앱 데이터 폴더(%APPDATA%\GodDB · %LOCALAPPDATA%\GodDB) 안이면 그 최상위 폴더 ACL 을 현재 사용자만으로
    ///   (상속을 끊고, 하위 폴더 · 파일에 상속). 앱 폴더 밖(사용자가 고른 곳)은 건드리지 않는다.
    /// - 그 밖: 권한 700 (Mac 과 같음)
    public static string Ensure(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { DataDirectory, LocalDirectory })
            {
                if (!IsInside(dir, root)) continue;
                Directory.CreateDirectory(root);
                bool first;
                lock (Restricted) first = Restricted.Add(Path.GetFullPath(root));
                if (first) Platform.UserOnlyFile.RestrictDirectory(root);
            }
        }
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        return dir;
    }

    /// dir 가 root 이거나 그 안인지
    internal static bool IsInside(string dir, string root)
    {
        try
        {
            var d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return d.Equals(r, cmp) || d.StartsWith(r + Path.DirectorySeparatorChar, cmp);
        }
        catch { return false; }
    }
}
