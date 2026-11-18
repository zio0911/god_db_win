using System.Diagnostics;
using System.Runtime.Versioning;

namespace GodDB.Platform.Windows;

/// Windows 이벤트 로그(응용 프로그램)에 한 줄 남긴다 (Mac: os_log, subsystem local.goddb.app · category audit).
/// 원본 "GodDB" 가 등록돼 있으면 그것을, 없으면 관리자 권한 없이 쓸 수 있는 ".NET Runtime" 원본을 쓴다
/// (새 원본 등록에는 관리자 권한이 필요한데, 설치 프로그램은 사용자 폴더 설치라 관리자 권한이 없다).
[SupportedOSPlatform("windows")]
public static class SystemLog
{
    public const string Source = "GodDB";
    private const string Fallback = ".NET Runtime";

    public static void Write(string message)
    {
        foreach (var source in new[] { Source, Fallback })
        {
            try
            {
                if (!EventLog.SourceExists(source)) continue;
                EventLog.WriteEntry(source, message, EventLogEntryType.Information, 1000);
                return;
            }
            catch { /* 권한 없음 등 — 다음 원본 */ }
        }
    }
}
