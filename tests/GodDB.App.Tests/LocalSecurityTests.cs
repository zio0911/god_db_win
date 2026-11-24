using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using GodDB.App.Services;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Audit;
using GodDB.Core.Platform;

namespace GodDB.App.Tests;

/// Mac 1.4.0 ~ 1.4.3 로컬 저장 보안 · 이전 버전 설치 화면 (headless)
public class LocalSecurityTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    // MARK: - 히스토리 보관 기간 (설정 > 일반 > 히스토리)

    [Fact]
    public Task HistoryKeepDaysSettingSavesAndPrunes() => UiThread.Run(async () =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var keep = QueryHistory.KeepDays;
        try
        {
            QueryHistory.KeepDays = 0;
            env.History.Append(new HistoryEntry(Guid.NewGuid(), "p", "shop", "SELECT old", 1, true, date: DateTime.Now.AddDays(-40)));
            env.History.Append(new HistoryEntry(Guid.NewGuid(), "p", "shop", "SELECT new", 1, true));
            var w = new SettingsWindow(app);
            w.Show();
            Pump();
            var box = w.FindControl<ComboBox>("HistoryKeepDaysBox")!;
            Assert.Equal(4, box.ItemCount);                              // 7 · 30 · 90 · 영구
            Assert.Equal(3, w.Vm.HistoryKeepDaysIndex);                  // 영구
            Assert.Contains("지우지 않고", w.Vm.HistoryKeepNote);
            w.Vm.HistoryKeepDaysIndex = 1;                               // 30일
            Assert.Equal(30, QueryHistory.KeepDays);
            Assert.Contains("30일이 지난 쿼리 기록은 자동으로 지워요", w.Vm.HistoryKeepNote);
            Assert.Contains("DPAPI", w.Vm.HistoryKeepNote);
            // 바로 정리된다 (뒤에서)
            for (var i = 0; i < 100 && env.History.All().Count != 1; i++) await Task.Delay(20);
            Assert.Equal(["SELECT new"], env.History.All().Select(e => e.Sql));
            w.Close();
        }
        finally { QueryHistory.KeepDays = keep; }
    });

    [Fact]
    public void HistoryKeepDaysDefaultsToThirty()
    {
        var keep = Prefs.Contains(QueryHistory.KeepDaysKey) ? QueryHistory.KeepDays : (int?)null;
        try
        {
            Prefs.Remove(QueryHistory.KeepDaysKey);
            Assert.Equal(30, QueryHistory.KeepDays);
            Assert.Equal(1, new SettingsViewModel(null).HistoryKeepDaysIndex);
        }
        finally { if (keep is { } k) QueryHistory.KeepDays = k; }
    }

    // MARK: - 로컬 데이터 암호화가 화면 저장소에 붙어 있는지

    [Fact]
    public Task TabsAndHistoryOnDiskAreEncrypted() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var p = TestEnv.Profile("dev");
        app.Profiles.Add(p);
        TestEnv.FakeConnect(app, p.Id);
        app.AddTab("SELECT secret_marker_19 FROM member", profileId: p.Id);
        app.SaveTabsNow();
        env.History.Append(new HistoryEntry(p.Id, "dev", "shop", "SELECT secret_marker_19", 1, true));
        foreach (var f in new[] { env.Tabs.File, env.History.File })
        {
            var raw = File.ReadAllBytes(f);
            Assert.True(LocalDataCrypto.IsEncrypted(raw), f);
            Assert.DoesNotContain("secret_marker_19", System.Text.Encoding.UTF8.GetString(raw));
        }
        Assert.Contains(env.Tabs.Load(), t => t.Text.Contains("secret_marker_19"));
    });

    [Fact]
    public void RedisWorkbenchIsEncryptedInSettings()
    {
        var profileId = Guid.NewGuid();
        var key = "GodDB.redis.workbench." + profileId.ToString().ToUpperInvariant();
        try
        {
            // 예전 평문 값은 읽을 때 암호화된다
            Prefs.Set(key, "SET k secret_value_7");
            Assert.Equal("SET k secret_value_7", LocalDataCrypto.Shared.PrefsString(key));
            Assert.StartsWith("GDBENC1:", Prefs.GetString(key));
            LocalDataCrypto.Shared.SetPrefsString(key, "GET k");
            Assert.DoesNotContain("GET", Prefs.GetString(key));
            Assert.Equal("GET k", LocalDataCrypto.Shared.PrefsString(key));
        }
        finally { Prefs.Remove(key); }
    }

    // MARK: - 이전 버전 설치

    private const string ReleasesJson = """
    [{"tag_name":"v1.9.0.2","draft":false,"prerelease":false,"published_at":"2026-11-18T05:42:50Z","body":"## 변경 사항\n- **설정** 정보 줄\n- 로그 기록 보완",
      "assets":[{"name":"releases.win.json","browser_download_url":"https://x/v1.9.0.2/releases.win.json","size":300},
                {"name":"GodDB-1.9.1-r.2-full.nupkg","browser_download_url":"https://x/v1.9.0.2/GodDB-1.9.1-r.2-full.nupkg","size":62701828}]},
     {"tag_name":"v1.3.7","draft":false,"prerelease":false,"published_at":"2026-11-18T04:57:43Z","body":"",
      "assets":[{"name":"releases.win.json","browser_download_url":"https://x/v1.3.7/releases.win.json","size":300},
                {"name":"GodDB-1.3.7-full.nupkg","browser_download_url":"https://x/v1.3.7/GodDB-1.3.7-full.nupkg","size":62698273}]}]
    """;

    private sealed class FakePackages(bool installed = true, string? found = null) : IPreviousVersionPackages
    {
        public bool IsInstalled => installed;
        public List<string> Calls { get; } = [];
        public string? Feed;
        public Task<string?> CheckAsync(PreviousVersions.Entry entry, CancellationToken ct)
        {
            Calls.Add("check");
            Feed = entry.FeedBaseUrl;
            return Task.FromResult(found ?? entry.PackageVersion);
        }
        public Task DownloadAsync(Action<int> progress, CancellationToken ct) { Calls.Add("download"); progress(100); return Task.CompletedTask; }
        public void ApplyAndRestart() => Calls.Add("apply");
    }

    private static PreviousVersionsWindow Window(AppState app, FakePackages packages, string json = ReleasesJson)
    {
        // 백업할 데이터 폴더도 테스트마다 따로 (실제 · 공용 테스트 폴더를 복사하지 않게)
        var data = Path.Combine(TestSetup.Root, "pv-" + Guid.NewGuid().ToString("N")[..8], "GodDB");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "profiles.json"), "[]");
        return new(app, true, new PreviousVersionInstaller(packages, _ => Task.FromResult(json), data));
    }

    [Fact]
    public Task PreviousVersionsWindowListsReleases() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var w = Window(app, new FakePackages());
        w.Show();
        await w.Installer.LoadAsync();
        Pump();
        Assert.Equal(PreviousVersionInstaller.ListStatus.Loaded, w.Installer.List);
        var installs = w.GetLogicalDescendants().OfType<Button>().Where(b => b.Tag is string).Select(b => (string)b.Tag!).ToList();
        Assert.Equal(w.Installer.Entries.Select(e => e.Version), installs);
        Assert.DoesNotContain(PreviousVersionInstaller.CurrentVersion, installs);
        // Markdown 굵게 표시는 떼고 보인다
        Assert.Contains(w.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "• 설정 정보 줄");
        w.Close();
    });

    [Fact]
    public Task PreviousVersionsWindowShowsLoadFailure() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var w = new PreviousVersionsWindow(app, true, new PreviousVersionInstaller(new FakePackages(), _ => throw new HttpRequestException("offline")));
        w.Show();
        await w.Installer.LoadAsync();
        Pump();
        Assert.Equal(PreviousVersionInstaller.ListStatus.Failed, w.Installer.List);
        Assert.Contains(w.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "릴리스 목록을 가져오지 못했어요");
        w.Close();
    });

    [Fact]
    public Task InstallRunsBackupCheckDownloadApplyAndAudits() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var packages = new FakePackages();
        var w = Window(app, packages);
        w.Confirm = _ => Task.FromResult((true, false));
        await w.Installer.LoadAsync();
        var target = w.Installer.Entries.First(e => e.Version == "1.3.7");
        var mode = UpdateService.Mode;
        try
        {
            await w.ChooseAsync(target);
            Assert.True(packages.Calls.Count == 3, w.Installer.Failure);
            Assert.Equal(["check", "download", "apply"], packages.Calls);
            Assert.Equal("https://x/v1.3.7/", packages.Feed);
            Assert.Equal(PreviousVersionInstaller.InstallPhase.Launching, w.Installer.Phase);
            Assert.Equal(mode, UpdateService.Mode);   // 머물기를 고르지 않음
            // 감사 로그 "설정 | 이전 버전 설치 A → B"
            Assert.Contains(app.Logs, l => l.Audit is { Action: AuditAction.Settings } a
                                           && a.Result == $"이전 버전 설치 {PreviousVersionInstaller.CurrentVersion} → 1.3.7");
            // 데이터 폴더 백업 · 결과 표시 파일
            Assert.Contains(app.Logs, l => l.Message.StartsWith("데이터 폴더 백업 · GodDB-backup-", StringComparison.Ordinal));
            Assert.Equal("1.3.7", File.ReadAllText(PreviousVersions.PendingMarker).Trim());
        }
        finally
        {
            UpdateService.Mode = mode;
            try { File.Delete(PreviousVersions.PendingMarker); } catch { }
        }
    });

    [Fact]
    public Task StayOnVersionSetsManualUpdates() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var w = Window(app, new FakePackages());
        w.Confirm = _ => Task.FromResult((true, true));
        await w.Installer.LoadAsync();
        var mode = UpdateService.Mode;
        try
        {
            await w.ChooseAsync(w.Installer.Entries[0]);
            Assert.Equal(UpdateMode.Manual, UpdateService.Mode);
        }
        finally
        {
            UpdateService.Mode = mode;
            try { File.Delete(PreviousVersions.PendingMarker); } catch { }
        }
    });

    [Fact]
    public Task InstallRefusedWhenNotInstalledOrVersionDiffers() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        // 개발 실행: 백업 · 내려받기 없이 멈춘다
        var dev = new FakePackages(installed: false);
        var w = Window(app, dev);
        w.Confirm = _ => Task.FromResult((true, false));
        await w.Installer.LoadAsync();
        await w.ChooseAsync(w.Installer.Entries[0]);
        Assert.Empty(dev.Calls);
        Assert.Equal(PreviousVersionInstaller.InstallPhase.Failed, w.Installer.Phase);
        Assert.Contains("개발 실행", w.Installer.Failure);
        Pump();
        Assert.Contains(w.GetLogicalDescendants().OfType<SelectableTextBlock>(), t => t.Text!.Contains("설치하지 않았어요"));

        // 피드가 다른 버전을 주면 받지 않는다
        var other = new FakePackages(found: "1.4.0");
        var w2 = Window(app, other);
        w2.Confirm = _ => Task.FromResult((true, false));
        await w2.Installer.LoadAsync();
        await w2.ChooseAsync(w2.Installer.Entries.First(e => e.Version == "1.3.7"));
        Assert.Equal(["check"], other.Calls);
        Assert.Contains("받은 앱의 버전(1.4.0)", w2.Installer.Failure);
        Assert.False(File.Exists(PreviousVersions.PendingMarker));
    });

    [Fact]
    public Task CancelledConfirmDoesNothing() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var packages = new FakePackages();
        var w = Window(app, packages);
        w.Confirm = _ => Task.FromResult((false, true));
        await w.Installer.LoadAsync();
        await w.ChooseAsync(w.Installer.Entries[0]);
        Assert.Empty(packages.Calls);
        Assert.Equal(PreviousVersionInstaller.InstallPhase.Idle, w.Installer.Phase);
    });

    [Fact]
    public Task MenuOpensPreviousVersionsSheet() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var w = new MainWindow(app);
        w.Show();
        Pump();
        app.ShowPreviousVersions = true;
        Pump();
        Assert.IsType<PreviousVersionsWindow>(w.OpenSheets[nameof(AppState.ShowPreviousVersions)]);
        w.OpenSheets[nameof(AppState.ShowPreviousVersions)].Close();
        Pump();
        Assert.False(app.ShowPreviousVersions);
        w.Close();
    });

    [Fact]
    public Task FailedInstallIsReportedOnNextStart() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var dialogs = new NullDialogs();
        app.Dialogs = dialogs;
        var marker = Path.Combine(TestSetup.Root, "pending-" + Guid.NewGuid() + ".txt");
        PreviousVersions.WritePending(marker, "0.0.1");
        await PreviousVersionInstaller.ReportPendingResultAsync(app, marker);
        Assert.Contains(dialogs.Alerts, a => a.Title == "이전 버전을 설치하지 못했어요");
        Assert.Contains(app.Logs, l => l.Audit?.Result == "실패: 이전 버전 설치 · 앱을 바꾸지 못했어요");
        // 성공(지금 버전) 이면 아무것도 묻지 않는다
        PreviousVersions.WritePending(marker, PreviousVersionInstaller.CurrentVersion);
        dialogs.Alerts.Clear();
        await PreviousVersionInstaller.ReportPendingResultAsync(app, marker);
        Assert.Empty(dialogs.Alerts);
    });
}
