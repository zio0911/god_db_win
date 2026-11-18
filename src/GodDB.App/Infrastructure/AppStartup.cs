using System.Threading.Tasks;
using Avalonia.Threading;
using GodDB.App.ViewModels;

namespace GodDB.App.Infrastructure;

/// 앱을 켤 때 Core 설정을 사용자 설정(Prefs, Mac 과 같은 키)에서 넣고, 처리 못 한 예외를 크래시 파일로 남긴다.
public static class AppStartup
{
    public const string LanguageKey = "GodDB.language";
    public const string LogFileEnabledKey = "GodDB.logFile.enabled";
    public const string LogFileKeepDaysKey = "GodDB.logFile.keepDays";

    private static bool _crashHooked;

    /// Prefs → LogFile · L10n
    public static void ApplyPreferences()
    {
        LogFile.Enabled = Prefs.Get(LogFileEnabledKey, true);
        LogFile.RetentionDays = Prefs.Get(LogFileKeepDaysKey, 0);
        L10n.SetLanguage(ParseLanguage(Prefs.GetString(LanguageKey)));
    }

    /// Mac AppLanguage.rawValue ("system" / "ko" / "en")
    public static AppLanguage ParseLanguage(string? raw) => raw switch
    {
        "ko" => AppLanguage.Ko,
        "en" => AppLanguage.En,
        _ => AppLanguage.System,
    };

    public static string RawValue(AppLanguage l) => l switch
    {
        AppLanguage.Ko => "ko",
        AppLanguage.En => "en",
        _ => "system",
    };

    /// 언어를 바꾸고 저장한다 (화면은 Loc 로 바로 바뀐다 — 접속·탭·입력 중인 SQL 은 그대로)
    public static void SetLanguage(AppLanguage language)
    {
        Prefs.Set(LanguageKey, RawValue(language));
        L10n.SetLanguage(language);
    }

    public static void SetLogFileEnabled(bool on)
    {
        Prefs.Set(LogFileEnabledKey, on);
        LogFile.Enabled = on;
    }

    public static void SetRetentionDays(int days)
    {
        LogFile.RetentionDays = days;
        Prefs.Set(LogFileKeepDaysKey, LogFile.RetentionDays);
        LogFile.Shared.CleanNow();
    }

    /// 처리 못 한 예외 → %LOCALAPPDATA%\GodDB\Crashes (문제 보고가 최근 3개를 요약해 담는다)
    public static void HookCrashLog()
    {
        if (_crashHooked) return;
        _crashHooked = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) CrashLog.Write(ex);
            try { AppState.Shared?.SaveTabsNow(); LogFile.Shared.Flush(); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            // 화면 작업 하나의 실패로 앱이 꺼지지 않게 하고, 상태창에 남긴다
            e.Handled = true;
            try { AppState.Shared?.LogError(e.Exception, LogSource.App, Tr("처리하지 못한 오류")); } catch { }
        };
    }
}
