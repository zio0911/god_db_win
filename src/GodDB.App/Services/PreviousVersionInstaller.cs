using System.Net.Http;
using System.Net.Http.Headers;
using CommunityToolkit.Mvvm.ComponentModel;
using GodDB.App.ViewModels;
using Velopack;
using Velopack.Sources;

namespace GodDB.App.Services;

/// 이전 버전을 받아 설치하는 일 (Velopack). 테스트는 가짜를 넣는다.
public interface IPreviousVersionPackages
{
    /// Velopack 설치본인지 (개발 실행 · dotnet run 이면 false)
    bool IsInstalled { get; }
    /// 고른 릴리스의 피드를 읽어 Velopack 이 고른 패키지 번호 (없으면 null)
    Task<string?> CheckAsync(PreviousVersions.Entry entry, CancellationToken ct);
    /// 받기 (진행률 0~100). Velopack 이 SHA-256 을 확인한다.
    Task DownloadAsync(Action<int> progress, CancellationToken ct);
    /// 앱을 끄고 받은 버전을 설치한 뒤 다시 켠다
    void ApplyAndRestart();
}

/// Velopack: 고른 릴리스의 내려받기 폴더를 피드로 쓰고(SimpleWebSource), 버전 낮추기를 허용한다.
public sealed class VelopackPreviousVersionPackages : IPreviousVersionPackages
{
    private UpdateManager? _manager;
    private UpdateInfo? _info;

    public bool IsInstalled
    {
        get
        {
            try { return new UpdateManager(new SimpleWebSource(AppInfo.RepositoryUrl)).IsInstalled; }
            catch { return false; }
        }
    }

    public async Task<string?> CheckAsync(PreviousVersions.Entry entry, CancellationToken ct)
    {
        _manager = new UpdateManager(new SimpleWebSource(entry.FeedBaseUrl), new UpdateOptions { AllowVersionDowngrade = true });
        _info = await _manager.CheckForUpdatesAsync().WaitAsync(ct);
        return _info?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadAsync(Action<int> progress, CancellationToken ct) =>
        _manager != null && _info != null ? _manager.DownloadUpdatesAsync(_info, progress, ct) : Task.CompletedTask;

    public void ApplyAndRestart()
    {
        if (_manager != null && _info != null) _manager.ApplyUpdatesAndRestart(_info.TargetFullRelease);
    }
}

/// 이전 버전 설치 (Mac 1.4.0 PreviousVersionInstaller): 목록 가져오기 → 확인 → 열린 트랜잭션 확인 → 데이터 폴더 백업 → 확인 · 내려받기 → Velopack 으로 교체.
public sealed partial class PreviousVersionInstaller : ObservableObject
{
    public enum ListStatus { Loading, Loaded, Failed }
    public enum InstallPhase { Idle, BackingUp, Downloading, Verifying, Launching, Failed }

    private static readonly HttpClient Http = CreateHttp();
    private readonly IPreviousVersionPackages _packages;
    private readonly Func<CancellationToken, Task<string>> _fetch;
    private readonly string? _dataDirectory;
    private CancellationTokenSource? _cts;

    /// dataDirectory: 백업할 데이터 폴더 (기본 %APPDATA%\GodDB, 테스트는 임시 폴더)
    public PreviousVersionInstaller(IPreviousVersionPackages? packages = null, Func<CancellationToken, Task<string>>? fetchReleases = null,
                                    string? dataDirectory = null)
    {
        _packages = packages ?? new VelopackPreviousVersionPackages();
        _fetch = fetchReleases ?? FetchFromGitHubAsync;
        _dataDirectory = dataDirectory;
    }

    public static string CurrentVersion => AppInfo.Version;

    [ObservableProperty] private ListStatus _list = ListStatus.Loading;
    [ObservableProperty] private IReadOnlyList<PreviousVersions.Entry> _entries = [];
    [ObservableProperty] private string _listError = "";
    [ObservableProperty] private InstallPhase _phase = InstallPhase.Idle;
    /// 내려받기 진행률 0~1
    [ObservableProperty] private double _progress;
    /// 진행 중인 버전
    [ObservableProperty] private string? _target;
    [ObservableProperty] private string _failure = "";

    public bool Busy => Phase is not (InstallPhase.Idle or InstallPhase.Failed);
    partial void OnPhaseChanged(InstallPhase value) => OnPropertyChanged(nameof(Busy));

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GodDB", null));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    private static async Task<string> FetchFromGitHubAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync(PreviousVersions.ReleasesApi, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            throw new HttpRequestException($"GitHub {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync(ct);
    }

    // MARK: - 목록

    public async Task LoadAsync()
    {
        List = ListStatus.Loading;
        try
        {
            var json = await _fetch(CancellationToken.None);
            Entries = PreviousVersions.Entries(PreviousVersions.Decode(json), CurrentVersion);
            List = ListStatus.Loaded;
        }
        catch (Exception e)
        {
            ListError = e.Message;
            List = ListStatus.Failed;
        }
    }

    // MARK: - 설치

    /// 확인 창에서 [설치] 를 누른 뒤: 트랜잭션 확인 → 진행. stay = "이 버전에 머물기" (업데이트를 수동으로)
    public async Task InstallAsync(PreviousVersions.Entry entry, bool stay, AppState app)
    {
        if (Busy) return;
        // 열린 트랜잭션: 앱 종료와 같은 확인
        var open = app.OpenTransactions;
        if (open.Count > 0)
        {
            var ok = await app.Dialogs.ConfirmAsync(Tr("커밋하지 않은 트랜잭션이 있어요"),
                Tr("접속 %@개에 커밋하지 않은 변경 %@건이 있어요. 종료하면 되돌려져요(롤백).", open.Count, open.Sum(o => o.Changes)),
                Tr("롤백하고 설치"), Tr("취소"), destructive: true);
            if (!ok) return;
        }
        Target = entry.Version;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        foreach (var (id, _) in open) await app.RollbackTxAsync(id);
        if (app.OpenTransactions.Count > 0)
        {
            Fail(app, Tr("트랜잭션을 롤백하지 못해 설치를 멈췄어요"));
            return;
        }
        await RunAsync(entry, stay, app, ct);
    }

    public void Cancel() => _cts?.Cancel();

    private void Fail(AppState app, string message)
    {
        Failure = message;
        Phase = InstallPhase.Failed;
        app.Log(LogLevel.Error, LogSource.App, Tr("이전 버전 설치 실패 · %@", message));
    }

    private async Task RunAsync(PreviousVersions.Entry entry, bool stay, AppState app, CancellationToken ct)
    {
        var current = CurrentVersion;
        // 0) 미리 확인: Velopack 설치본이어야 한다 (Mac: 서명된 .app · 앱 위치에 쓰기 권한 — Velopack 은 사용자 폴더에 설치돼 권한 문제가 없다)
        if (!_packages.IsInstalled)
        {
            Fail(app, new PreviousVersions.InstallException(PreviousVersions.InstallErrorKind.NotInstalled).Message);
            return;
        }

        // 1) 데이터 폴더 백업 (최근 3개만)
        Phase = InstallPhase.BackingUp;
        app.SaveTabsNow();
        LogFile.Shared.Flush();
        try
        {
            var name = await Task.Run(() => PreviousVersions.BackupDataFolder(_dataDirectory ?? AppPaths.DataDirectory, current, DateTime.Now), ct);
            if (name != null) app.Log(LogLevel.Info, LogSource.App, Tr("데이터 폴더 백업 · %@", name));
        }
        catch (OperationCanceledException) { Phase = InstallPhase.Idle; return; }
        catch (Exception e)
        {
            Fail(app, Tr("데이터 폴더를 백업하지 못했어요: %@", e.Message));
            return;
        }
        if (ct.IsCancellationRequested) { Phase = InstallPhase.Idle; return; }

        // 2) 확인: 고른 릴리스의 Velopack 피드에 그 버전이 있는지 (Mac: DMG 서명 · 공증 확인)
        Phase = InstallPhase.Verifying;
        try
        {
            var found = await _packages.CheckAsync(entry, ct);
            PreviousVersions.EnsureVersion(entry.Version, found);
        }
        catch (OperationCanceledException) { Phase = InstallPhase.Idle; return; }
        catch (Exception e)
        {
            Fail(app, e.Message);
            return;
        }

        // 3) 내려받기 (Velopack 이 SHA-256 확인)
        Phase = InstallPhase.Downloading;
        Progress = 0;
        try
        {
            await _packages.DownloadAsync(p => Avalonia.Threading.Dispatcher.UIThread.Post(() => Progress = Math.Clamp(p / 100.0, 0, 1)), ct);
        }
        catch (Exception) when (ct.IsCancellationRequested) { Phase = InstallPhase.Idle; return; }
        catch (Exception e)
        {
            Fail(app, Tr("내려받지 못했어요: %@", e.Message));
            return;
        }
        if (ct.IsCancellationRequested) { Phase = InstallPhase.Idle; return; }

        // 4) 앱을 끄고 설치 (Velopack)
        Phase = InstallPhase.Launching;
        if (stay)
        {
            UpdateService.Mode = UpdateMode.Manual;
            app.RestartDailyUpdateCheck();
        }
        app.Log(LogLevel.Info, LogSource.App, Tr("이전 버전 설치 · %@ → %@", current, entry.Version),
                audit: new AuditRecord(AuditAction.Settings, Result: $"이전 버전 설치 {current} → {entry.Version}"));
        app.SaveTabsNow();
        LogFile.Shared.Flush();
        try { PreviousVersions.WritePending(PreviousVersions.PendingMarker, entry.Version); } catch { }
        try { _packages.ApplyAndRestart(); }
        catch (Exception e)
        {
            try { File.Delete(PreviousVersions.PendingMarker); } catch { }
            Fail(app, Tr("설치 도우미를 실행하지 못했어요: %@", e.Message));
        }
    }

    // MARK: - 다음 실행 때: 교체 결과

    /// 지난번 설치가 고른 버전으로 바뀌지 않았으면 이유를 보여 준다 (Mac reportHelperFailureIfAny)
    public static async Task ReportPendingResultAsync(AppState app, string? markerPath = null)
    {
        if (PreviousVersions.TakePending(markerPath ?? PreviousVersions.PendingMarker, CurrentVersion) is not { } r || r.Succeeded) return;
        var reason = Tr("앱을 바꾸지 못했어요");
        app.Log(LogLevel.Error, LogSource.App, Tr("이전 버전 설치 실패 · %@", reason),
                hint: Tr("지금 앱은 그대로예요. GitHub 에서 직접 받아 설치할 수 있어요."),
                audit: new AuditRecord(AuditAction.Settings, Result: $"실패: 이전 버전 설치 · {reason}"));
        await app.Dialogs.AlertAsync(Tr("이전 버전을 설치하지 못했어요"),
                                     reason + "\n" + Tr("지금 앱은 그대로예요. GitHub 에서 직접 받아 설치할 수 있어요."));
    }
}
