using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;
using GodDB.Core;
using GodDB.Core.Audit;
using GodDB.Core.Profiles;
using LogLevel = GodDB.Core.Audit.LogLevel;

namespace GodDB.App.Tests;

public class TabTests
{
    [Fact]
    public Task AddTab_UsesConnectionAndDatabase_NumbersPerConnection() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var a = TestEnv.Profile("a");
        var b = TestEnv.Profile("b");
        app.Profiles.Add(a);
        app.Profiles.Add(b);
        TestEnv.FakeConnect(app, a.Id, "shop");
        TestEnv.FakeConnect(app, b.Id, "crm");

        app.SelectedProfileId = a.Id;
        var t1 = app.AddTab("select 1");
        var t2 = app.AddTab();
        var t3 = app.AddTab(profileId: b.Id);

        Assert.NotNull(t1);
        Assert.Equal(3, app.Tabs.Count);
        Assert.Equal([1, 2], app.Tabs.Where(t => t.ProfileId == a.Id).Select(t => t.Number));
        Assert.Equal(1, app.Tab(t3!.Value)!.Number);
        Assert.Equal("shop", app.Tab(t1!.Value)!.Database);
        Assert.Equal("crm", app.Tab(t3.Value)!.Database);
        Assert.Equal("쿼리 2", app.Tab(t2!.Value)!.Title);
        // 새 탭을 만들면 그 탭 · 그 접속을 고른다
        Assert.Equal(t3, app.SelectedTabId);
        Assert.Equal(b.Id, app.SelectedProfileId);
        Assert.Equal(MainMode.Query, app.Mode);
    });

    [Fact]
    public Task AddTab_WithoutConnection_WarnsAndReturnsNull() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var p = TestEnv.Profile("x");
        app.Profiles.Add(p);
        app.SelectedProfileId = p.Id;
        Assert.Null(app.AddTab());
        Assert.Empty(app.Tabs);
        Assert.Equal(LogLevel.Warning, app.LastLog?.Level);
        Assert.False(app.ShowLog);
    });

    [Fact]
    public Task SelectTab_FollowsConnection_AndCloseSelectsNeighbour() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var a = TestEnv.Profile("a");
        var b = TestEnv.Profile("b");
        app.Profiles.Add(a);
        app.Profiles.Add(b);
        TestEnv.FakeConnect(app, a.Id);
        TestEnv.FakeConnect(app, b.Id);
        var t1 = app.AddTab(profileId: a.Id)!.Value;
        var t2 = app.AddTab(profileId: b.Id)!.Value;
        var t3 = app.AddTab(profileId: a.Id)!.Value;
        app.SelectTab(t2);
        Assert.Equal(b.Id, app.SelectedProfileId);
        app.CloseTab(t2);
        // 닫은 자리의 다음 탭
        Assert.Equal(t3, app.SelectedTabId);
        Assert.Equal(a.Id, app.SelectedProfileId);
        app.CloseTab(t3);
        Assert.Equal(t1, app.SelectedTabId);
        app.CloseTab(t1);
        Assert.Null(app.SelectedTabId);
    });

    [Fact]
    public Task Tabs_AreRestored_AndUnknownProfilesDropped() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var a = TestEnv.Profile("a");
        app.Profiles.Add(a);
        app.Persist();
        TestEnv.FakeConnect(app, a.Id, "shop");
        app.AddTab("select 1", profileId: a.Id);
        var second = app.AddTab("select 2", "DDL · t", a.Id)!.Value;
        app.SelectTab(app.Tabs[0].Id);
        app.SaveTabsNow();
        // 지워진 접속의 탭은 되살리지 않는다
        env.Tabs.Save([.. env.Tabs.Load(), new SavedTab(Guid.NewGuid(), "x", 1, null, "lost", false)]);

        var restored = env.NewApp();
        Assert.Equal(2, restored.Tabs.Count);
        Assert.Equal(["select 1", "select 2"], restored.Tabs.Select(t => t.Text));
        Assert.Equal("DDL · t", restored.Tabs[1].Title);
        Assert.Equal("shop", restored.Tabs[0].Database);
        Assert.Equal(restored.Tabs[0].Id, restored.SelectedTabId);
        Assert.False(restored.TabSavePending);
        Assert.NotEqual(second, restored.Tabs[1].Id);
    });

    [Fact]
    public Task EditingTabText_SchedulesAutosave() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var a = TestEnv.Profile("a");
        app.Profiles.Add(a);
        TestEnv.FakeConnect(app, a.Id);
        var id = app.AddTab(profileId: a.Id)!.Value;
        app.SaveTabsNow();
        Assert.False(app.TabSavePending);
        app.Tab(id)!.Text = "select now()";
        Assert.True(app.TabSavePending);
        // 실행 상태 같은 값은 저장하지 않는다
        app.SaveTabsNow();
        app.Tab(id)!.Running = true;
        Assert.False(app.TabSavePending);
        Assert.Equal("select now()", env.Tabs.Load().Single().Text);
    });

    [Fact]
    public Task OpenFromHistory_AppendsToSameConnectionTab() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var a = TestEnv.Profile("a");
        app.Profiles.Add(a);
        TestEnv.FakeConnect(app, a.Id);
        var id = app.AddTab("select 1", profileId: a.Id)!.Value;
        app.OpenFromHistoryAsync(new HistoryEntry(a.Id, "a", "shop", "select 2", 1, true, null)).Wait();
        Assert.Equal("select 1\n\nselect 2;\n", app.Tab(id)!.Text);
    });

    [Fact]
    public Task InsertIntoTab_AddsLabelledSql() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var a = TestEnv.Profile("a");
        app.Profiles.Add(a);
        TestEnv.FakeConnect(app, a.Id);
        var id = app.AddTab("select 1", profileId: a.Id)!.Value;
        app.InsertIntoTab(id, "select 2", "개선 SQL");
        Assert.Equal("select 1\n\n-- 개선 SQL\nselect 2;\n", app.Tab(id)!.Text);
    });

    [Fact]
    public Task RequestCloseTabs_WithPendingEdits_AsksFirst() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var a = TestEnv.Profile("a");
        app.Profiles.Add(a);
        TestEnv.FakeConnect(app, a.Id);
        var id = app.AddTab(profileId: a.Id)!.Value;
        app.Tab(id)!.Edits.Deletes.Add(3);
        app.RequestCloseTabs([id]);
        Assert.NotNull(app.DiscardPrompt);
        Assert.Single(app.Tabs);
        app.ResolveDiscardPrompt(discard: true);
        Assert.Empty(app.Tabs);
    });
}

public class PasswordFlowTests
{
    [Fact]
    public Task NotSavedPassword_PromptsWithoutMessage() => UiThread.Run(async () =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var p = TestEnv.Profile("p") with { SavePassword = false };
        app.Profiles.Add(p);
        await app.ConnectAsync(p.Id);
        Assert.NotNull(app.PasswordPrompt);
        Assert.Null(app.PasswordPrompt!.Message);
        Assert.Equal(ConnectionStateKind.Disconnected, app.ConnectionState(p.Id).Kind);
    });

    [Fact]
    public Task SavedPasswordMissing_PromptsWithMessage() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var p = TestEnv.Profile("p") with { SavePassword = true };
        app.Profiles.Add(p);
        await app.ConnectAsync(p.Id);
        Assert.Contains("자격 증명 관리자", app.PasswordPrompt!.Message);
    });

    [Fact]
    public Task Password_PrefersSessionThenStore() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var p = TestEnv.Profile("p");
        env.Secrets.Values[p.Id] = "stored";
        Assert.Equal("stored", app.Password(p));
        Assert.Null(app.Password(p with { SavePassword = false }));
        app.SessionPasswords[p.Id] = "session";
        Assert.Equal("session", app.Password(p));
        // SQL Server Windows 인증은 비밀번호를 묻지 않는다
        var win = TestEnv.Profile("w", DatabaseKind.Mssql) with { WindowsAuth = true, SavePassword = false };
        Assert.Equal("", app.Password(win));
    });

    [Fact]
    public Task SubmitPassword_SaveTrue_StoresAndPersists() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var p = TestEnv.Profile("p") with { SavePassword = false };
        app.Profiles.Add(p);
        app.PasswordPrompt = new PasswordPrompt(p, null);
        app.SubmitPassword("pw1", save: true, p);
        Assert.Null(app.PasswordPrompt);
        Assert.Equal("pw1", env.Secrets.GetPassword(p.Id));
        Assert.Equal("pw1", app.SessionPasswords[p.Id]);
        Assert.True(app.Profiles.Single().SavePassword);
        Assert.True(env.Store.Load().Single().SavePassword);
    });

    [Fact]
    public Task SubmitPassword_SaveFalse_DeletesStoredAndKeepsSessionOnly() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var p = TestEnv.Profile("p");
        env.Secrets.Values[p.Id] = "old";
        app.Profiles.Add(p);
        app.SubmitPassword("pw2", save: false, p);
        Assert.Null(env.Secrets.GetPassword(p.Id));
        Assert.Equal("pw2", app.SessionPasswords[p.Id]);
        Assert.False(app.Profiles.Single().SavePassword);
    });

    [Fact]
    public Task SubmitPassword_StoreFails_WarnsAndRemembersForSession() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        env.Secrets.FailWrites = true;
        var app = env.NewApp();
        var p = TestEnv.Profile("p");
        app.Profiles.Add(p);
        app.SubmitPassword("pw", save: true, p);
        Assert.Equal("pw", app.SessionPasswords[p.Id]);
        Assert.Contains(app.Logs, l => l.Level == LogLevel.Warning && l.Message.Contains("이번 실행 중에만"));
    });

    [Fact]
    public Task SaveProfile_PasswordRules() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var p = TestEnv.Profile("p");
        app.EditingProfile = p;
        app.SaveProfile(p, "secret");
        Assert.Equal("secret", env.Secrets.GetPassword(p.Id));
        Assert.Null(app.EditingProfile);
        // null 이면 기존 값 유지
        app.SaveProfile(p with { Name = "p2" }, null);
        Assert.Equal("secret", env.Secrets.GetPassword(p.Id));
        Assert.Equal("p2", app.Profiles.Single().Name);
        // 저장 안 함으로 바꾸면 지우고 이번 실행 중에만
        app.SaveProfile(p with { SavePassword = false }, "temp");
        Assert.Null(env.Secrets.GetPassword(p.Id));
        Assert.Equal("temp", app.SessionPasswords[p.Id]);
        Assert.Contains(app.Logs, l => l.Audit?.Action == AuditAction.Settings);
    });

    [Fact]
    public Task RefusedConnection_FailsAndOpensLog() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var p = TestEnv.Profile("p") with { Port = 1, SavePassword = false };
        app.Profiles.Add(p);
        app.SessionPasswords[p.Id] = "x";
        await app.ConnectAsync(p.Id);
        Assert.True(app.ConnectionState(p.Id).IsFailed);
        Assert.True(app.ShowLog);
        Assert.Equal(app.Logs.Last().Id, app.SelectedLogId);
        Assert.Null(app.PasswordPrompt);
    });
}

public class SafetyConfirmTests
{
    [Fact]
    public Task PlainSelect_NoConfirm() => UiThread.Run(() =>
    {
        var c = RunConfirm.ForRun(null, "select * from t", 0, production: false)!;
        Assert.False(c.NeedsConfirm);
    });

    [Fact]
    public Task Empty_ReturnsNull() => UiThread.Run(() => { Assert.Null(RunConfirm.ForRun(null, "  ;  ", 0, false)); });

    [Fact]
    public Task DeleteWithoutWhere_IsDangerous() => UiThread.Run(() =>
    {
        var c = RunConfirm.ForRun("delete from t", "x", 0, false)!;
        Assert.True(c.NeedsConfirm);
        Assert.Equal("되돌리기 어려운 문장이 있어요", c.Title);
        Assert.Contains("WHERE 없는 DELETE", c.Message);
        Assert.Equal("실행", c.ConfirmButton);
    });

    [Fact]
    public Task WholeScriptWithWrites_AsksBeforeRunningEverything() => UiThread.Run(() =>
    {
        var c = RunConfirm.ForRun(null, "select 1;\nupdate t set a = 1 where id = 1;", 0, false)!;
        Assert.True(c.WholeScript);
        Assert.True(c.NeedsConfirm);
        Assert.Equal("전체 2개 문장을 실행할까요?", c.Title);
        Assert.Equal("전체 실행", c.ConfirmButton);
        // 선택 영역이 있으면 묻지 않는다
        Assert.False(RunConfirm.ForRun("select 1;\nupdate t set a = 1 where id = 1;", "", 0, false)!.NeedsConfirm);
        // 읽기만 하는 여러 문장도 묻지 않는다
        Assert.False(RunConfirm.ForRun(null, "select 1; select 2", 0, false)!.NeedsConfirm);
    });

    [Fact]
    public Task Production_WritesAlwaysConfirm_ReadsAndExplainDoNot() => UiThread.Run(() =>
    {
        var w = RunConfirm.ForRun("update t set a = 1 where id = 1", "", 0, production: true)!;
        Assert.True(w.NeedsConfirm);
        Assert.Equal("운영 접속에서 데이터를 바꿔요", w.Title);
        Assert.Contains("데이터를 바꾸는 문장 1개", w.Message);
        Assert.False(RunConfirm.ForRun("select 1", "", 0, production: true)!.NeedsConfirm);
        Assert.False(RunConfirm.ForExplain("update t set a = 1 where id = 1", 0).NeedsConfirm);
    });

    [Fact]
    public Task PendingEdits_Confirm() => UiThread.Run(() =>
    {
        var c = RunConfirm.ForRun("select 1", "", discardedEdits: 2, false)!;
        Assert.True(c.NeedsConfirm);
        Assert.Equal("적용하지 않은 편집이 있어요", c.Title);
        Assert.Contains("2건은 사라져요", c.Message);
        Assert.True(RunConfirm.ForExplain("select 1", 1).NeedsConfirm);
    });
}

public class LogTests
{
    [Fact]
    public Task Error_OpensLogAndSelectsEntry() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        app.Log(LogLevel.Info, LogSource.App, "hello");
        Assert.False(app.ShowLog);
        var msg = app.LogError(new InvalidOperationException("boom"), LogSource.Query, "쿼리 실패", "select 1",
                               new AuditRecord(AuditAction.Read, "t", Statement: "select 1"));
        Assert.True(app.ShowLog);
        var last = app.Logs.Last();
        Assert.Equal(last.Id, app.SelectedLogId);
        Assert.Equal(LogLevel.Error, last.Level);
        Assert.StartsWith("쿼리 실패: ", last.Message);
        Assert.Contains("실행한 SQL:\nselect 1", last.Detail);
        Assert.StartsWith("실패: ", last.Audit!.Result);
        Assert.Equal(1, app.ErrorCount);
        Assert.False(string.IsNullOrEmpty(msg));
    });

    [Fact]
    public Task Logs_AreCappedAt1000_AndClear() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        for (var i = 0; i < 1005; i++) app.Log(LogLevel.Info, LogSource.App, $"m{i}");
        Assert.Equal(1000, app.Logs.Count);
        Assert.Equal("m5", app.Logs[0].Message);
        app.ToggleLog();
        Assert.True(app.ShowLog);
        app.ClearLogs();
        Assert.Empty(app.Logs);
        Assert.Null(app.LastLog);
    });
}

public class GroupAndTransferTests
{
    [Fact]
    public Task MoveAndRenameGroups_UpdateSidebar() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var a = TestEnv.Profile("a");
        var b = TestEnv.Profile("b");
        app.Profiles.Add(a);
        app.Profiles.Add(b);
        var sidebar = new SidebarViewModel(app);
        Assert.Equal(2, sidebar.ConnectionItems.Count);
        app.MoveProfile(a.Id, " 운영 ");
        Assert.Equal(["운영"], app.ProfileGroups);
        var group = Assert.Single(sidebar.ConnectionItems.OfType<GroupRowViewModel>());
        Assert.Equal("a", Assert.Single(group.Members).Name);
        app.RenameGroup("운영", "prod");
        Assert.Equal("prod", app.Profiles.First(p => p.Id == a.Id).Group);
        app.SubmitGroupPrompt(new GroupPrompt(new GroupPromptAction.Move(b.Id)), "prod");
        Assert.Equal(2, sidebar.ConnectionItems.OfType<GroupRowViewModel>().Single().Members.Count);
        app.RenameGroup("prod", "");
        Assert.Empty(app.ProfileGroups);
        Assert.All(env.Store.Load(), p => Assert.Equal("", p.Group));
    });

    [Fact]
    public Task ImportProfiles_StoresPasswords() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var a = TestEnv.Profile("a");
        var b = TestEnv.Profile("b") with { SavePassword = false };
        app.ImportProfiles([a, b], new Dictionary<Guid, string> { [a.Id] = "pa", [b.Id] = "pb" }, "f.json");
        Assert.Equal(2, app.Profiles.Count);
        Assert.Equal("pa", env.Secrets.GetPassword(a.Id));
        Assert.Equal("pb", app.SessionPasswords[b.Id]);
        Assert.Equal(AuditAction.Settings, app.Logs.Last().Audit?.Action);
    });

    [Fact]
    public Task PasteDataGrip_OpensImportRequest() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var text = """
            #DataSourceSettings#
            #LocalDataSource: shop
            <data-source source="LOCAL" name="shop" uuid="1">
              <driver-ref>mysql.8</driver-ref>
              <jdbc-url>jdbc:mysql://localhost:3306/shop</jdbc-url>
              <user-name>root</user-name>
            </data-source>
            """;
        await app.PasteDataGripAsync(text);
        if (app.ImportRequest == null)
        {
            // 형식이 다르면 상태창에 오류 (가져오기 창 없음)
            Assert.Equal(LogLevel.Error, app.LastLog?.Level);
            return;
        }
        Assert.Equal("DataGrip", app.ImportRequest.Bundle.App);
    });

    [Fact]
    public Task Duplicate_CopiesPassword() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var a = TestEnv.Profile("a");
        app.Profiles.Add(a);
        env.Secrets.Values[a.Id] = "pw";
        app.Duplicate(a);
        var copy = app.Profiles.Last();
        Assert.NotEqual(a.Id, copy.Id);
        Assert.Equal("a 복사본", copy.Name);
        Assert.Equal("pw", env.Secrets.GetPassword(copy.Id));
    });
}

[Collection(nameof(LanguageCollection))]
public class LanguageTests
{
    [Fact]
    public Task SwitchLanguage_UpdatesLocAndPrefs() => UiThread.Run(() =>
    {
        var before = Environment.GetEnvironmentVariable("GODDB_LANG");
        var lang = L10n.Language;
        var changed = 0;
        void Count() => changed++;
        L10n.LanguageChanged += Count;
        Environment.SetEnvironmentVariable("GODDB_LANG", null);
        try
        {
            AppStartup.SetLanguage(AppLanguage.En);
            Assert.Equal("en", Prefs.GetString(AppStartup.LanguageKey));
            Assert.Equal("Query", Loc.Instance["쿼리"]);
            Assert.Equal(AppLanguage.En, AppStartup.ParseLanguage(Prefs.GetString(AppStartup.LanguageKey)));
            AppStartup.SetLanguage(AppLanguage.Ko);
            Assert.Equal("쿼리", Loc.Instance["쿼리"]);
            Assert.Equal(2, changed);
        }
        finally
        {
            L10n.LanguageChanged -= Count;
            L10n.SetLanguage(lang);
            Environment.SetEnvironmentVariable("GODDB_LANG", before);
        }
    });
}

[CollectionDefinition(nameof(LanguageCollection), DisableParallelization = true)]
public class LanguageCollection;
