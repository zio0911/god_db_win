using System.Reflection;

namespace GodDB.Core;

/// 앱 이름 · 버전 · 저장소 정보
public static class AppInfo
{
    public const string Name = "GodDB";
    public const string RepositoryUrl = "https://github.com/zio0911/god_db_win";

    /// 빌드할 때 VERSION 파일에서 들어온 버전 (예: 1.0.0)
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+')[0];
}
