using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Audit;
using GodDB.Core.Drivers;
using GodDB.Core.Platform;

namespace GodDB.App.Tests;

/// Mac 1.4.0 · 1.4.1 접속 보안 화면: TLS 인증서 · SSH 호스트 키 확인 창 (SecurityPrompts.swift), 접속 편집의 서버 인증서 확인,
/// 저장된 비밀번호를 메모리에 두지 않기, 운영 접속 자동 끊기 (ProdIdleWatcher.swift), 설정 > 안전.
public class SecurityTests
{
    private static readonly TlsCertInfo Info = new("CN=db.example.com", "CN=Example CA", null, null, "AA:BB:CC");

    private static string? FindText(Control root, Func<string, bool> match) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => t != null && match(t));

    // MARK: - 확인 창 고르기 (SecurityPrompt.from)

    [Fact]
    public void PromptFromErrors()
    {
        var u = Assert.IsType<SecurityPrompt.TlsUntrusted>(SecurityPrompt.From(TlsCertificateException.Untrusted(Info, [TlsFailureReason.NameMismatch])));
        Assert.Equal([TlsFailureReason.NameMismatch], u.Reasons);
        Assert.Equal("이 인증서 신뢰 (고정)", u.AcceptTitle);
        Assert.False(u.IsRemoval);
        var m = Assert.IsType<SecurityPrompt.TlsPinMismatch>(SecurityPrompt.From(TlsCertificateException.PinMismatch(Info, "00")));
        Assert.Equal("저장된 인증서 지우기", m.AcceptTitle);
        Assert.True(m.IsRemoval);
        Assert.Null(SecurityPrompt.From(TlsCertificateException.ServerNoTls()));

        var scan = new SshHostScan("bastion", 22, "bastion", ["bastion ssh-ed25519 AAAA"], [new SshHostKeyFingerprint("ED25519", "SHA256:x")]);
        var s = Assert.IsType<SecurityPrompt.SshUnknown>(SecurityPrompt.From(SshHostKeyException.Unknown(scan)));
        Assert.Equal("신뢰하고 연결", s.AcceptTitle);
        Assert.Equal("처음 접속하는 SSH 서버예요 — bastion:22", s.Title("p"));
        var c = Assert.IsType<SecurityPrompt.SshChanged>(SecurityPrompt.From(SshHostKeyException.Changed("bastion", 2222, "[bastion]:2222")));
        Assert.Equal("[bastion]:2222", c.Pattern);
        Assert.Equal("저장된 키 지우기", c.AcceptTitle);
        Assert.True(c.IsRemoval);
        Assert.Null(SecurityPrompt.From(SshHostKeyException.ScanFailed("b", 22, "x")));
        // 감싼 예외 안에 있어도 찾는다
        Assert.NotNull(SecurityPrompt.From(new AggregateException(TlsCertificateException.PinMismatch(Info, "00"))));
        Assert.Null(SecurityPrompt.From(new InvalidOperationException("x")));
    }

    // MARK: - 메인 창 확인 창 → 고정 지문 저장 · 감사 로그

    [Fact]
    public Task TrustPinsCertificateAndAudits() => UiThread.Run(async () =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var p = TestEnv.Profile("prod-db");
        app.Profiles.Add(p);
        app.SessionPasswords[p.Id] = "pw";
        Assert.True(app.PresentSecurityPrompt(TlsCertificateException.Untrusted(Info, [TlsFailureReason.UntrustedIssuer]), p));
        var request = Assert.IsType<SecurityPromptRequest>(app.SecurityPrompt);
        Assert.Equal(p.Id, request.ProfileId);

        await app.ResolveSecurityPromptAsync(request, accept: true);
        Assert.Null(app.SecurityPrompt);
        Assert.Equal("AA:BB:CC", app.Profiles.Single().TlsPinSHA256);
        Assert.Equal("AA:BB:CC", env.Store.Load().Single().TlsPinSHA256);
        var audit = app.Logs.Select(l => l.Audit).OfType<AuditRecord>().First(a => a.Result.StartsWith("TLS 인증서 신뢰", StringComparison.Ordinal));
        Assert.Equal(AuditAction.Settings, audit.Action);
        Assert.Equal("prod-db", audit.Target);
        Assert.Equal("TLS 인증서 신뢰 SHA256:AA:BB:CC", audit.Result);

        // 바뀐 인증서 → [저장된 인증서 지우기]
        app.PresentSecurityPrompt(TlsCertificateException.PinMismatch(Info, "AA:BB:CC"), app.Profiles.Single());
        await app.ResolveSecurityPromptAsync(app.SecurityPrompt!, accept: true);
        Assert.Null(app.Profiles.Single().TlsPinSHA256);
        Assert.Contains(app.Logs, l => l.Audit?.Result == "인증서 고정 지움");

        // 취소는 아무것도 바꾸지 않는다
        app.PresentSecurityPrompt(TlsCertificateException.Untrusted(Info, [TlsFailureReason.UntrustedIssuer]), app.Profiles.Single());
        await app.ResolveSecurityPromptAsync(app.SecurityPrompt!, accept: false);
        Assert.Null(app.Profiles.Single().TlsPinSHA256);
    });

    [Fact]
    public Task TrustSshHostKeyWritesGodDBFile() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var line = $"[bastion-{Guid.NewGuid():N}]:2222 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIChtIjIrffCgho75EL9ZKPIMs9wEuDDnaOjuD4Q/Ep5M";
        var scan = new SshHostScan("bastion", 2222, line.Split(' ')[0], [line], [new SshHostKeyFingerprint("ED25519", "SHA256:x")]);
        Assert.Equal(PinChange.None.Instance, app.ApplySecurityAccept(new SecurityPrompt.SshUnknown(scan), "p"));
        // GodDB 전용 known_hosts (테스트는 GODDB_DATA_DIR 아래) 에만 쓴다
        Assert.Contains(line, File.ReadAllText(SshHostKeys.GodDBFile));
        Assert.True(UserOnlyFile.IsRestricted(SshHostKeys.GodDBFile));
        Assert.Contains(app.Logs, l => l.Audit?.Result == "SSH 호스트 키 신뢰 bastion:2222 SHA256:x");
    });

    [Fact]
    public Task SaveProfileAuditsPinChange() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var p = TestEnv.Profile("db");
        app.Profiles.Add(p);
        app.SaveProfile(p with { TlsPinSHA256 = "11:22" }, null);
        Assert.Contains(app.Logs, l => l.Audit?.Result == "TLS 인증서 신뢰 SHA256:11:22");
        var before = app.Logs.Count(l => l.Audit?.Result?.Contains("인증서") == true);
        app.SaveProfile(app.Profiles.Single(), null);   // 그대로면 남기지 않음
        Assert.Equal(before, app.Logs.Count(l => l.Audit?.Result?.Contains("인증서") == true));
    });

    // MARK: - 확인 창 화면

    [Fact]
    public Task PromptWindowShowsCertificateAndResolves() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var p = TestEnv.Profile("db");
        app.Profiles.Add(p);
        app.SessionPasswords[p.Id] = "pw";
        app.PresentSecurityPrompt(TlsCertificateException.Untrusted(Info, [TlsFailureReason.UntrustedIssuer, TlsFailureReason.NameMismatch]), p);
        var w = new SecurityPromptWindow(app, app.SecurityPrompt!);
        w.Show();
        Assert.Equal("서버 인증서를 확인할 수 없어요 — db", w.Title);
        Assert.Equal("이 인증서 신뢰 (고정)", w.AcceptButton.Content);
        var texts = w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("AA:BB:CC", texts);
        Assert.Contains("CN=Example CA", texts);
        Assert.Contains("신뢰할 수 없는 발급자 / 이름이 다름", texts);
        w.AcceptButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 50 && app.Profiles.Single().TlsPinSHA256 == null; i++) await Task.Delay(20);
        Assert.Equal("AA:BB:CC", app.Profiles.Single().TlsPinSHA256);
        Assert.Null(app.SecurityPrompt);
    });

    [Fact]
    public Task ChangedSshKeyWindowSaysWindowsPath() => UiThread.Run(() =>
    {
        var w = new SecurityPromptWindow(new SecurityPrompt.SshChanged("bastion", 22, "bastion"), "p");
        w.Show();
        Assert.Equal("저장된 키 지우기", w.AcceptButton.Content);
        Assert.Contains("danger", w.AcceptButton.Classes);
        Assert.NotNull(FindText(w, t => t.StartsWith("%USERPROFILE%\\.ssh\\known_hosts", StringComparison.Ordinal)));
        w.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(w.Accepted);
        Assert.False(w.IsVisible);
    });

    // MARK: - 접속 편집: 서버 인증서 확인 · 고정한 인증서

    [Fact]
    public Task EditorTlsRows() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var vm = new ConnectionEditorViewModel(app, TestEnv.Profile("db") with { UseTLS = true, TlsPinSHA256 = "AA:BB" }, isNew: false);
        Assert.True(vm.TlsVerify);
        Assert.True(vm.HasTlsPin);
        Assert.True(vm.ShowTlsVerifyHelp);
        Assert.False(vm.ShowTlsUncheckedWarning);
        Assert.StartsWith("시스템 신뢰 저장소로", vm.TlsVerifyHelp);
        vm.UseSsh = true;
        Assert.StartsWith("SSH 터널 안쪽은 127.0.0.1", vm.TlsVerifyHelp);
        vm.TlsVerify = false;
        Assert.True(vm.ShowTlsUncheckedWarning);
        Assert.False(vm.Draft.TlsVerify);
        Assert.Equal("AA:BB", vm.Draft.TlsPinSHA256);
        vm.ClearTlsPinCommand.Execute(null);
        Assert.False(vm.HasTlsPin);
        Assert.Null(vm.Draft.TlsPinSHA256);
        // TLS 를 끄면 인증서 줄은 숨는다
        vm.UseTls = false;
        Assert.False(vm.ShowTlsUncheckedWarning);
    });

    [Fact]
    public Task EditorWindowShowsPinAndMssqlNote() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var w = new ConnectionEditorWindow(app, TestEnv.Profile("ms", DatabaseKind.Mssql) with { UseTLS = true, ReadOnly = true, TlsPinSHA256 = "AA:BB" });
        w.Show();
        Assert.Equal("서버 인증서 확인", w.FindControl<CheckBox>("TlsVerifyBox")!.Content);
        Assert.Equal("AA:BB", w.FindControl<SelectableTextBlock>("TlsPinText")!.Text);
        var note = w.FindControl<TextBlock>("MssqlReadOnlyNote")!;
        Assert.True(note.IsVisible);
        Assert.Contains("앱에서만 막아요(서버에서 강제되지 않음)", note.Text);
        w.Close();
    });

    [Fact]
    public Task EditorTestAsksAndPinsOnTrust() => UiThread.Run(async () =>
    {
        // 연결 테스트가 인증서 확인을 만나면 확인 창 → 신뢰하면 편집 중인 값에 지문을 넣고 다시 테스트
        var app = new TestEnv().NewApp();
        var vm = new ConnectionEditorViewModel(app, TestEnv.Profile("db") with { Host = "127.0.0.1", Port = 1, UseTLS = true }, isNew: true);
        var asked = 0;
        vm.AskSecurity = _ => { asked++; return Task.FromResult(false); };
        await vm.TestCommand.ExecuteAsync(null);   // 닫힌 포트: 인증서 오류가 아니라 확인 창 없음
        Assert.Equal(0, asked);
        Assert.False(vm.TestOk);
    });

    // MARK: - 저장된 비밀번호를 메모리에 두지 않기 (1.4.1)

    [Fact]
    public Task PasswordProviderOnlyForStoredPasswords() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var saved = TestEnv.Profile("saved") with { SavePassword = true };
        env.Secrets.SetPassword(saved.Id, "pw-1");
        var provider = app.PasswordProvider(saved);
        Assert.NotNull(provider);
        Assert.Equal("pw-1", provider!());
        env.Secrets.SetPassword(saved.Id, "pw-2");   // 쓸 때마다 다시 읽는다
        Assert.Equal("pw-2", provider());
        // 이번 실행 중에만 기억하는 비밀번호 · 저장 안 함 · Windows 인증은 제공자 없음 (세션이 들고 있어야 다시 연결할 수 있다)
        app.SessionPasswords[saved.Id] = "typed";
        Assert.Null(app.PasswordProvider(saved));
        Assert.Null(app.PasswordProvider(TestEnv.Profile("x") with { SavePassword = false }));
        Assert.Null(app.PasswordProvider(TestEnv.Profile("w", DatabaseKind.Mssql) with { WindowsAuth = true }));
    });

    [Fact]
    public Task SaveProfileDoesNotKeepStoredPasswordInMemory() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var p = TestEnv.Profile("db") with { SavePassword = true };
        app.SaveProfile(p, "pw");
        Assert.Equal("pw", env.Secrets.GetPassword(p.Id));
        Assert.False(app.SessionPasswords.ContainsKey(p.Id));
        // 보관소 저장이 실패하면 이번 실행 중에만 메모리에
        env.Secrets.FailWrites = true;
        app.SaveProfile(p, "pw2");
        Assert.Equal("pw2", app.SessionPasswords[p.Id]);
    });

    // MARK: - 운영 접속 자동 끊기 · 설정 > 안전

    [Fact]
    public Task SettingsProdIdlePicker() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        Prefs.Remove(AppState.ProdIdleMinutesKey);
        var vm = new SettingsViewModel(app);
        Assert.Equal(["끔", "10분", "30분", "60분"], vm.ProdIdleLabels);
        Assert.Equal(2, vm.ProdIdleIndex);   // 기본 30분
        Assert.Equal(TimeSpan.FromMinutes(30), AppState.ProdIdleLimit);
        vm.ProdIdleIndex = 0;
        Assert.Equal(0, Prefs.Get(AppState.ProdIdleMinutesKey, 30));
        Assert.Equal(TimeSpan.Zero, AppState.ProdIdleLimit);
        Assert.Contains(app.Logs, l => l.Message == "운영 접속 자동 끊기를 껐어요" && l.Audit?.Result == "운영 자동 끊기 끔");
        vm.ProdIdleIndex = 1;
        Assert.Contains(app.Logs, l => l.Message == "운영 접속 자동 끊기: 10분" && l.Audit?.Result == "운영 자동 끊기 10분");
        Prefs.Remove(AppState.ProdIdleMinutesKey);
    });

    [Fact]
    public Task SettingsWindowHasProdIdleBox() => UiThread.Run(() =>
    {
        var w = new SettingsWindow(new TestEnv().NewApp());
        w.Show();
        Assert.NotNull(w.FindControl<ComboBox>("ProdIdleBox"));
        Assert.NotNull(FindText(w, t => t == "운영 접속 자동 끊기"));
        w.Close();
    });

    private static (TestEnv Env, AppState App, ConnectionProfile Profile) ProductionSetup()
    {
        var port = Environment.GetEnvironmentVariable("GODDB_IT_PORT");
        if (string.IsNullOrEmpty(port)) Assert.Skip("GODDB_IT_PORT 없음 — scripts/test-dbs.sh test 로 실행");
        var env = new TestEnv();
        var app = env.NewApp();
        var p = new ConnectionProfile
        {
            Name = "prod-it", Kind = DatabaseKind.Mysql, Host = "127.0.0.1", Port = int.Parse(port!),
            Username = "root", Database = "shop", UseTLS = false, SavePassword = false, Environment = ConnectionEnvironment.Production,
        };
        app.Profiles.Add(p);
        app.SessionPasswords[p.Id] = Environment.GetEnvironmentVariable("GODDB_IT_PASSWORD") ?? "goddbtest";
        return (env, app, p);
    }

    [Fact]
    public Task IdleProductionConnectionIsClosed() => UiThread.Run(async () =>
    {
        var (_, app, p) = ProductionSetup();
        await app.ConnectAsync(p.Id);
        Assert.True(app.ConnectionState(p.Id).IsConnected, app.LastLog?.Message);
        // TLS 상태 한 줄
        Assert.Contains(app.Logs, l => l.Message == "prod-it · TLS 안 씀");
        // 아직 시간이 안 됐으면 그대로
        await app.CheckIdleProductionAsync(TimeSpan.FromMinutes(30));
        Assert.True(app.ConnectionState(p.Id).IsConnected);
        // 30분 뒤 → 끊고 경고 + 감사 로그 (탭은 남는다)
        await app.CheckIdleProductionAsync(TimeSpan.FromMinutes(30), DateTime.UtcNow.AddMinutes(31));
        Assert.False(app.ConnectionState(p.Id).IsConnected);
        var log = app.Logs.Last(l => l.Source == LogSource.Connection);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal("운영 접속 prod-it 을 30분 동안 쓰지 않아 끊었어요", log.Message);
        Assert.Equal(AuditAction.Disconnect, log.Audit?.Action);
        Assert.Equal("자동 (30분 미사용)", log.Audit?.Result);
        Assert.Contains(app.Tabs, t => t.ProfileId == p.Id);
    });

    [Fact]
    public Task IdleProductionWithOpenTransactionWarnsOnce() => UiThread.Run(async () =>
    {
        var (_, app, p) = ProductionSetup();
        await app.ConnectAsync(p.Id);
        var session = app.Connections[p.Id].Session!;
        // 수동 커밋 + 쓰기 문장(DO 1) → 트랜잭션이 열린다
        session.SetAutoCommit(false);
        await session.ExecuteAsync("DO 1", 0);
        Assert.True(session.InTransaction);
        try
        {
            var later = DateTime.UtcNow.AddHours(1);
            await app.CheckIdleProductionAsync(TimeSpan.FromSeconds(10), later);
            await app.CheckIdleProductionAsync(TimeSpan.FromSeconds(10), later);
            Assert.True(app.ConnectionState(p.Id).IsConnected);
            Assert.Single(app.Logs, l => l.Message == "운영 접속 prod-it 에 커밋하지 않은 변경이 있어 자동으로 끊지 않았어요");
            // 운영이 아니거나 끔(0)이면 보지 않는다
            await app.CheckIdleProductionAsync(TimeSpan.Zero, later);
            Assert.True(app.ConnectionState(p.Id).IsConnected);
        }
        finally
        {
            try { await session.RollbackAsync(); } catch { }
            await app.DisconnectAsync(p.Id, force: true);
        }
    });

    [Fact]
    public Task SelfSignedMysqlAsksThenConnectsPinned() => UiThread.Run(async () =>
    {
        // 실제 흐름: 자체 서명 인증서 → 연결 실패 + 확인 창 → [이 인증서 신뢰 (고정)] → 다시 연결 · "고정 인증서"
        var (_, app, p) = ProductionSetup();
        var i = app.Profiles.IndexOf(p);
        app.Profiles[i] = p with { UseTLS = true, TlsVerify = true, Environment = ConnectionEnvironment.None };
        await app.ConnectAsync(p.Id);
        Assert.False(app.ConnectionState(p.Id).IsConnected);
        var request = Assert.IsType<SecurityPromptRequest>(app.SecurityPrompt);
        Assert.IsType<SecurityPrompt.TlsUntrusted>(request.Prompt);
        Assert.Null(app.PasswordPrompt);   // 비밀번호 창이 아니라 확인 창
        try
        {
            await app.ResolveSecurityPromptAsync(request, accept: true);
            Assert.True(app.ConnectionState(p.Id).IsConnected, app.LastLog?.Message);
            Assert.NotNull(app.Profiles[i].TlsPinSHA256);
            Assert.Contains(app.Logs, l => l.Message == "prod-it · TLS 사용 · 고정 인증서");
        }
        finally { await app.DisconnectAsync(p.Id, force: true); }
    });
}
