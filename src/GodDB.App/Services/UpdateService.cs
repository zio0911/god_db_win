using GodDB.Core;
using Velopack;
using Velopack.Sources;
using static GodDB.Core.L10n;

namespace GodDB.App.Services;

/// 업데이트 방식 (설정 > 업데이트)
public enum UpdateMode
{
    /// 받아서 자동으로 설치 (다음 실행부터 새 버전, 받은 뒤 지금 다시 시작할지 묻는다)
    Automatic,
    /// 확인만 하고 "새 버전이 있어요" 창으로 알림 (기본)
    Notify,
    /// 확인하지 않음 (메뉴 "업데이트 확인…" 으로만)
    Manual,
}

/// Velopack 자동 업데이트. 서버 없이 GitHub 릴리스(zio0911/god_db_win)의 Velopack 파일을 읽는다 (Mac: Sparkle + appcast.xml).
/// 설치본이 아닐 때(개발 실행, 포터블 zip 이 아닌 dotnet run)에는 아무것도 하지 않는다.
public sealed class UpdateService
{
    public const string ModeKey = "GodDB.update.mode";
    private readonly UpdateManager _manager = new(new GithubSource(AppInfo.RepositoryUrl, null, prerelease: false));
    private UpdateInfo? _pending;

    public static UpdateMode Mode
    {
        get => Enum.TryParse<UpdateMode>(Prefs.GetString(ModeKey), ignoreCase: true, out var m) ? m : UpdateMode.Notify;
        set => Prefs.Set(ModeKey, value.ToString().ToLowerInvariant());
    }

    public static string Title(UpdateMode m) => m switch
    {
        UpdateMode.Automatic => Tr("자동 업데이트 (받아서 설치)"),
        UpdateMode.Notify => Tr("확인하고 알림 (설치는 직접)"),
        _ => Tr("수동 (메뉴에서 확인할 때만)"),
    };

    /// 설치된 앱인지 (업데이트를 할 수 있는지)
    public bool Available => _manager.IsInstalled;

    /// 새 버전이 있으면 그 버전 문자열, 없으면 null
    public async Task<string?> CheckAsync(CancellationToken ct = default)
    {
        if (!Available) return null;
        _pending = await _manager.CheckForUpdatesAsync().WaitAsync(ct);
        return _pending?.TargetFullRelease.Version.ToString();
    }

    /// 받은 뒤 종료할 때 설치되도록 준비한다 (progress: 0~100)
    public async Task DownloadAsync(Action<int>? progress = null, CancellationToken ct = default)
    {
        if (_pending == null) return;
        await _manager.DownloadUpdatesAsync(_pending, progress, ct);
    }

    /// 받은 업데이트를 지금 설치하고 다시 시작
    public void ApplyAndRestart()
    {
        if (_pending != null) _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }

    /// 받아 둔 업데이트가 있으면 앱을 끈 뒤 설치되게 한다
    public void ApplyOnExit()
    {
        if (_pending != null) _manager.WaitExitThenApplyUpdates(_pending.TargetFullRelease, silent: true, restart: false);
    }
}
