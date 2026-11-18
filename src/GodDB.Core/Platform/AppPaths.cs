namespace GodDB.Core;

/// 앱 데이터 위치
/// - 설정 · 접속 목록 · 모델 · AI 대화: %APPDATA%\GodDB (Mac: ~/Library/Application Support/GodDB)
/// - 로그 · 크래시: %LOCALAPPDATA%\GodDB (Mac: ~/Library/Logs/GodDB)
/// 테스트나 화면 자동 테스트는 GODDB_DATA_DIR 로 다른 폴더를 쓴다 (실제 접속 목록을 건드리지 않음).
public static class AppPaths
{
    private static string? Override => Environment.GetEnvironmentVariable("GODDB_DATA_DIR") is { Length: > 0 } d ? d : null;

    /// 화면 자동 테스트 실행인지
    public static bool UiTest => Environment.GetEnvironmentVariable("GODDB_UI_TEST") == "1";

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

    /// 폴더를 만들고 (Windows 가 아니면) 권한을 700 으로
    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        return dir;
    }
}
