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
        UsePlatformDeviceKeys();
        LogFile.Enabled = Prefs.Get(LogFileEnabledKey, true);
        LogFile.RetentionDays = Prefs.Get(LogFileKeepDaysKey, 0);
        L10n.SetLanguage(ParseLanguage(Prefs.GetString(LanguageKey)));
    }

    /// 감사 로그 기기 키: Windows 는 TPM 키 우선, 없으면 DPAPI 로 잠근 소프트웨어 키 (Mac: Secure Enclave → 키체인)
    private static void UsePlatformDeviceKeys()
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows()) return;
        GodDB.Core.Audit.AuditChain.KeyStore = new GodDB.Platform.Windows.DpapiDeviceKeyStore();
        GodDB.Core.Audit.AuditChain.PlatformSigner = GodDB.Platform.Windows.TpmDeviceSigner.TryCreate;
        GodDB.Core.Audit.AuditChain.SystemLog = GodDB.Platform.Windows.SystemLog.Write;
        // 로컬 데이터(히스토리 · 탭 · AI 대화 · Redis Workbench) 암호화 키: DPAPI(현재 사용자)로 잠가 이 PC 에만 (Mac: 키체인)
        GodDB.Core.Platform.LocalDataCrypto.UsePlatformKeyStore(
            new GodDB.Platform.Windows.DpapiDeviceKeyStore(entropy: GodDB.Platform.Windows.DpapiDeviceKeyStore.LocalDataEntropy));
#endif
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
