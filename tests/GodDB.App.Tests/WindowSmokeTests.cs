using Avalonia.Controls;
using Avalonia.Threading;
using GodDB.App.Controls;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Ai;
using GodDB.Core.Audit;
using GodDB.Core.Profiles;
using LogLevel = GodDB.Core.Audit.LogLevel;

namespace GodDB.App.Tests;

/// 화면을 실제로 띄우지 않고(headless) 창이 만들어지고 배치되는지만 본다
public class WindowSmokeTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    [Fact]
    public Task MainWindow_ConstructsAndLaysOut() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var a = TestEnv.Profile("a");
        app.Profiles.Add(a);
        app.Profiles.Add(TestEnv.Profile("b") with { Group = "운영", Environment = ConnectionEnvironment.Production });
        TestEnv.FakeConnect(app, a.Id);
        app.SelectedProfileId = a.Id;
        app.AddTab("select 1");
        var w = new MainWindow(app);
        w.Show();
        Pump();
        Assert.True(w.Bounds.Width > 0);
        Assert.IsType<QueryWorkspaceView>(w.FindControl<ContentControl>("ContentHost")!.Content);

        app.Mode = MainMode.Compare;
        Pump();
        Assert.IsType<CompareView>(w.FindControl<ContentControl>("ContentHost")!.Content);
        app.Mode = MainMode.Modeling;
        Pump();
        Assert.IsType<ModelingView>(w.FindControl<ContentControl>("ContentHost")!.Content);

        // 오류가 나면 상태창이 열린다
        app.LogError(new Exception("x"), LogSource.App, "테스트");
        Pump();
        Assert.True(w.FindControl<LogPanelView>("LogPanel")!.IsVisible);
        app.ToggleLog();
        Pump();
        Assert.False(w.FindControl<LogPanelView>("LogPanel")!.IsVisible);

        app.Assistant.Visible = true;
        Pump();
        Assert.Equal(TuningSettings.AssistantEnabled, w.FindControl<AssistantPanel>("Assistant")!.IsVisible);
        app.Assistant.Visible = false;
        w.Close();
    });

    [Fact]
    public Task SettingsWindow_ConstructsAndLaysOut() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var w = new SettingsWindow(app);
        w.Show();
        Pump();
        Assert.True(w.Bounds.Width > 0);
        // 탭을 모두 한 번씩 그려 본다
        var tabs = w.FindControl<TabControl>("Tabs")!;
        for (var i = 0; i < tabs.ItemCount; i++)
        {
            tabs.SelectedIndex = i;
            Pump();
        }
        w.Vm.SelectedLicense = "Npgsql";
        Assert.Equal("PostgreSQL · 10.0.3", w.Vm.LicenseSubtitle);
        Assert.Contains("Npgsql", w.Vm.LicenseText);
        Assert.Equal(11, w.Vm.LicenseItems.Count);   // GodDB + 오픈소스 10개 (Simple Icons 포함)
        w.Close();
    });

    [Fact]
    public Task Sheets_OpenAndCloseWithAppState() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var w = new MainWindow(app);
        w.Show();
        Pump();
        app.NewProfile();
        Pump();
        Assert.IsType<ConnectionEditorWindow>(w.OpenSheets[nameof(AppState.EditingProfile)]);
        app.EditingProfile = null;
        Pump();
        Assert.False(w.OpenSheets.ContainsKey(nameof(AppState.EditingProfile)));

        var p = TestEnv.Profile("p");
        app.PasswordPrompt = new PasswordPrompt(p, "틀림");
        Pump();
        var prompt = Assert.IsType<PasswordPromptWindow>(w.OpenSheets[nameof(AppState.PasswordPrompt)]);
        prompt.Close();
        Pump();
        // 창을 닫으면 요청도 지운다
        Assert.Null(app.PasswordPrompt);

        app.ShowExportConnections = true;
        Pump();
        Assert.IsType<ExportConnectionsWindow>(w.OpenSheets[nameof(AppState.ShowExportConnections)]);
        app.ShowExportConnections = false;
        app.GroupPrompt = new GroupPrompt(new GroupPromptAction.Rename("g"));
        app.ShowProblemReport = true;
        app.ImportRequest = new ImportRequest("f.json", new ConnectionBundle { ExportedAt = DateTimeOffset.Now, App = "GodDB", Profiles = [p] });
        app.ShowQuickOpen = true;   // 아직 포팅 안 된 창 자리
        Pump();
        Assert.IsType<GroupPromptWindow>(w.OpenSheets[nameof(AppState.GroupPrompt)]);
        Assert.IsType<ProblemReportWindow>(w.OpenSheets[nameof(AppState.ShowProblemReport)]);
        Assert.IsType<ImportConnectionsWindow>(w.OpenSheets[nameof(AppState.ImportRequest)]);
        Assert.True(w.OpenSheets.ContainsKey(nameof(AppState.ShowQuickOpen)));
        foreach (var s in w.OpenSheets.Values.ToList()) s.Close();
        Pump();
        Assert.False(app.ShowProblemReport);
        Assert.Null(app.GroupPrompt);

        // 입력창에서 확인: AppState 가 요청을 지우면 창이 닫히고, 창이 스스로 다시 닫아도 문제없다
        var a = TestEnv.Profile("a");
        app.Profiles.Add(a);
        app.GroupPrompt = new GroupPrompt(new GroupPromptAction.Move(a.Id));
        Pump();
        var gp = w.OpenSheets[nameof(AppState.GroupPrompt)];
        app.SubmitGroupPrompt(app.GroupPrompt!, "dev");
        Pump();
        gp.Close();
        Assert.Equal("dev", app.Profiles.Single(x => x.Id == a.Id).Group);
        Assert.False(w.OpenSheets.ContainsKey(nameof(AppState.GroupPrompt)));
        w.Close();
    });

    [Fact]
    public Task ConnectionEditor_KindChangesDefaultPort_AndWindowsAuthHidesCredentials() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var vm = new ConnectionEditorViewModel(app, new ConnectionProfile(), isNew: true);
        Assert.Equal(3306, vm.Port);
        vm.Kind = DatabaseKind.Postgres;
        Assert.Equal(5432, vm.Port);
        vm.Port = 6000;
        vm.Kind = DatabaseKind.Mssql;
        Assert.Equal(6000, vm.Port);   // 직접 바꾼 포트는 유지
        Assert.True(vm.ShowCredentials);
        vm.WindowsAuth = true;
        Assert.False(vm.ShowCredentials);
        Assert.True(vm.Draft.WindowsAuth);
        vm.Kind = DatabaseKind.Redis;
        Assert.False(vm.Draft.WindowsAuth);
        vm.Port = 6379;
        vm.RedisMode = RedisMode.Sentinel;
        Assert.Equal(26379, vm.Port);
        Assert.True(vm.IsSentinel);

        var w = new ConnectionEditorWindow(app, new ConnectionProfile());
        w.Show();
        Pump();
        Assert.True(w.Bounds.Height > 0);
        w.Close();
    });

    [Fact]
    public Task ConnectionEditor_SavedPasswordState() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var p = TestEnv.Profile("p");
        env.Secrets.Values[p.Id] = "pw";
        var vm = new ConnectionEditorViewModel(app, p, isNew: false);
        Assert.True(vm.HasSavedPassword);
        Assert.True(vm.ShowsSavedPassword);
        Assert.False(vm.PasswordChanged);
        // 한 글자 지워도 저장된 비밀번호로 본다
        vm.Password = "p";
        Assert.True(vm.ShowsSavedPassword);
        // 완전히 비우면 새로 입력한 값
        vm.Password = "";
        vm.Password = "new";
        Assert.False(vm.ShowsSavedPassword);
        vm.SaveCommand.Execute(null);
        Assert.Equal("new", env.Secrets.GetPassword(p.Id));
    });

    [Fact]
    public Task PasswordField_SavedPasswordNeedsVerification_AndLogsReveal() => UiThread.Run(async () =>
    {
        var verifier = new FakeVerifier(false);
        var old = PasswordField.Verifier;
        PasswordField.Verifier = verifier;
        try
        {
            var f = new PasswordField { NeedsAuthentication = true, Text = "pw" };
            var revealed = 0;
            f.AuthenticatedReveal += () => revealed++;
            await f.ToggleAsync();
            Assert.False(f.Revealed);
            Assert.Equal(1, verifier.Calls);
            PasswordField.Verifier = new FakeVerifier(true);
            await f.ToggleAsync();
            Assert.True(f.Revealed);
            Assert.Equal(1, revealed);
            await f.ToggleAsync();
            Assert.False(f.Revealed);
            // 새로 입력한 값은 확인 없이
            f.NeedsAuthentication = false;
            var plain = new FakeVerifier(false);
            PasswordField.Verifier = plain;
            await f.ToggleAsync();
            Assert.True(f.Revealed);
            Assert.Equal(0, plain.Calls);

            var app = new TestEnv().NewApp();
            var vm = new ConnectionEditorViewModel(app, TestEnv.Profile("p"), false);
            vm.OnAuthenticatedReveal();
            var e = app.Logs.Last();
            Assert.Equal("저장된 비밀번호 보기", e.Audit?.Result);
            Assert.DoesNotContain("pw", e.Message);
        }
        finally { PasswordField.Verifier = old; }
    });

    [Fact]
    public Task Splash_HidesWhenStartupFinishes() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var w = new MainWindow(app);
        w.Show();
        Pump();
        var splash = w.FindControl<SplashView>("Splash")!;
        Assert.True(splash.IsVisible);
        app.Startup.Finished = true;
        Pump();
        Assert.Equal(0.0, splash.GetBaseValue(Avalonia.Visual.OpacityProperty));
        w.Close();
    });
}
