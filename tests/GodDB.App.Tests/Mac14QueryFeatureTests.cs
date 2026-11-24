using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using GodDB.App.Controls;
using GodDB.App.Services;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Drivers;
using GodDB.Core.Platform;
using GodDB.Core.Schema;
using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.App.Tests;

/// Mac 1.4.0 ~ 1.4.2 쿼리 기능: 빨간 밑줄 · 별칭 뒤 Tab 목록 · 저장 쿼리 · ID 로 찾기 · 쿼리 튜닝 제거. headless, 화면을 띄우지 않는다.
public class Mac14QueryFeatureTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static readonly TableMeta Users = new("users", columns:
    [
        new ColumnMeta("id", "bigint", false, columnKey: "PRI", position: 1),
        new ColumnMeta("name", "varchar(50)", true, position: 2),
        new ColumnMeta("email", "varchar(100)", true, position: 3),
    ]);

    private static (AppState App, ConnectionProfile Profile) Connected(TestEnv env, params TableMeta[] tables)
    {
        var app = env.NewApp();
        app.SavedQueryFile = new SavedQueryStore(Path.Combine(env.Dir, "saved-queries.json"));
        app.IdSearchFile = new IdSearchStore(Path.Combine(env.Dir, "id-search.json"));
        var p = TestEnv.Profile("p");
        app.Profiles.Add(p);
        TestEnv.FakeConnect(app, p.Id);
        app.UpdateConnection(p.Id, c => c.Tables = [.. tables]);
        app.SelectedProfileId = p.Id;
        return (app, p);
    }

    private static (Window, SqlEditor) Editor(string text, Func<(IReadOnlyList<TableMeta>, DatabaseKind?, string?)> ctx)
    {
        var editor = new SqlEditor { LintContext = ctx, SchemaProvider = () => ([Users], []) };
        var w = new Window { Width = 700, Height = 400, Content = editor };
        w.Show();
        editor.SetSqlText(text);
        editor.CaretOffset = editor.SqlText.Length;
        Pump();
        return (w, editor);
    }

    // MARK: - 빨간 밑줄

    [Fact]
    public Task Lint_UnderlinesUnknownColumn_WithReasonForHoverAndScreenReader() => UiThread.Run(() =>
    {
        var (w, editor) = Editor("SELECT a.id, a.username FROM users a", () => ([Users], DatabaseKind.Mysql, "shop"));
        editor.RunLocalLint();
        var issue = Assert.Single(editor.Issues);
        Assert.Equal("username", editor.SqlText.Substring(issue.Start, issue.Length));
        Assert.Equal(Tr("%@ 테이블에 %@ 컬럼이 없어요.", "users", "username") + "\n" + Tr("비슷한 컬럼: %@", "name"), issue.Text);
        // 마우스를 올린 자리의 이유 · 화면 읽기 도움말
        Assert.Same(issue, editor.IssueAt(issue.Start + 2));
        Assert.Null(editor.IssueAt(0));
        Assert.Equal(issue.Text, AutomationProperties.GetHelpText(editor));
        // 고치면 사라진다
        editor.SetSqlText("SELECT a.id, a.name FROM users a");
        editor.RunLocalLint();
        Assert.Empty(editor.Issues);
        Assert.Null(AutomationProperties.GetHelpText(editor));
        // Redis 접속은 검사하지 않음
        editor.LintContext = () => ([Users], DatabaseKind.Redis, null);
        editor.SetSqlText("SELEC x");
        editor.RunLocalLint();
        Assert.Empty(editor.Issues);
        w.Close();
    });

    [Fact]
    public Task Lint_ServerCheck_AddsDbIssue_OnlyWhenLocalIsClean_AndClearsOnTyping() => UiThread.Run(async () =>
    {
        var asked = new List<string>();
        var (w, editor) = Editor("", () => ([Users], DatabaseKind.Mysql, "shop"));
        editor.ServerCheckDelay = TimeSpan.Zero;
        editor.ServerChecker = sql =>
        {
            asked.Add(sql);
            return Task.FromResult<DbSession.ServerCheckOutcome?>(new(new SqlServerCheck.Failure("Unknown column 'nick'", 7, 4)));
        };
        editor.SetSqlText("SELECT 1;\nSELECT nick FROM t");
        editor.CaretOffset = editor.SqlText.Length;
        editor.ScheduleLint();
        await editor.PendingServerCheck!;
        Pump();
        var server = Assert.Single(editor.Issues, i => i.Source == SqlIssue.IssueSource.Server);
        Assert.Equal(["SELECT nick FROM t"], asked);
        Assert.Equal("nick", editor.SqlText.Substring(server.Start, server.Length));
        Assert.Equal(Tr("DB 검사: %@", "Unknown column 'nick'"), server.Message);

        // 글을 바꾸면 DB 검사 결과는 바로 지운다
        editor.Document.Insert(editor.SqlText.Length, " ");
        Assert.DoesNotContain(editor.Issues, i => i.Source == SqlIssue.IssueSource.Server);

        // GodDB 검사에서 이미 문제가 보이면 DB 에 묻지 않는다
        asked.Clear();
        editor.SetSqlText("SELECT a.username FROM users a");
        editor.CaretOffset = editor.SqlText.Length;
        editor.ScheduleLint();
        await editor.PendingServerCheck!;
        Assert.Empty(asked);

        // 설정에서 끄면 아무것도 하지 않는다
        var old = EditorLint.Enabled;
        try
        {
            EditorLint.Enabled = false;
            editor.ScheduleLint();
            editor.RunLocalLint();
            Assert.Empty(editor.Issues);
        }
        finally { EditorLint.Enabled = old; }
        w.Close();
    });

    [Fact]
    public Task Lint_AppContext_SkipsColumnsWhenSidebarShowsOtherDb() => UiThread.Run(() =>
    {
        var (app, p) = Connected(new TestEnv(), Users);
        var tabId = app.AddTab("SELECT 1")!.Value;
        var ctx = app.LintContext(tabId);
        Assert.Equal(DatabaseKind.Mysql, ctx.Kind);
        Assert.Equal("shop", ctx.Schema);
        Assert.Single(ctx.Tables);
        app.Tab(tabId)!.Database = "other";
        Assert.Empty(app.LintContext(tabId).Tables);
        Assert.Equal("other", app.LintContext(tabId).Schema);
    });

    [Fact]
    public Task Lint_SettingsToggles() => UiThread.Run(() =>
    {
        var (old, oldServer) = (EditorLint.Enabled, EditorLint.ServerEnabled);
        try
        {
            var vm = new SettingsViewModel(null);
            vm.LintEnabled = false;
            Assert.False(EditorLint.Enabled);
            Assert.False(Prefs.Get(EditorLint.EnabledKey, true));
            vm.LintServer = false;
            Assert.False(EditorLint.ServerEnabled);
            vm.LintEnabled = true;
            vm.LintServer = true;
            Assert.True(EditorLint.Enabled && EditorLint.ServerEnabled);
            var w = new SettingsWindow((AppState?)null);
            Assert.NotNull(w.FindControl<CheckBox>("LintBox"));
            Assert.NotNull(w.FindControl<CheckBox>("LintServerBox"));
        }
        finally { (EditorLint.Enabled, EditorLint.ServerEnabled) = (old, oldServer); }
    });

    // MARK: - 별칭 뒤 Tab

    [Fact]
    public Task Tab_AfterAliasWithWrongName_ShowsClosestColumns_AndListIsNotEmpty() => UiThread.Run(() =>
    {
        var (w, editor) = Editor("SELECT a.mail FROM users a", () => ([], null, null));
        editor.CaretOffset = "SELECT a.mail".Length;
        editor.HandleTab();
        var list = Assert.IsType<TabAction.ShowList>(editor.LastTabAction);
        Assert.Equal("email", list.Candidates[0]);
        Assert.Equal(3, list.Candidates.Count);
        var win = (CompletionWindow?)typeof(SqlEditor).GetField("_completion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(editor);
        Assert.NotNull(win);
        // 입력한 글자(mail)로 거르면 name · id 가 빠지므로 거르지 않는다
        Assert.False(win!.CompletionList.IsFiltering);
        win.CompletionList.ApplyTemplate();
        win.CompletionList.SelectItem("mail");
        Assert.Equal(3, win.CompletionList.ListBox!.ItemCount);
        editor.CloseCompletion();

        // `a.` 바로 뒤: 그 테이블 컬럼 전부 (목록이 비지 않음)
        editor.SetSqlText("SELECT a. FROM users a");
        editor.CaretOffset = "SELECT a.".Length;
        editor.HandleTab();
        var all = Assert.IsType<TabAction.ShowList>(editor.LastTabAction);
        Assert.Equal(["id", "name", "email"], all.Candidates);
        Assert.Equal(("SELECT a.".Length, 0), (all.Start, all.Length));
        w.Close();
    });

    // MARK: - 저장 쿼리

    [Fact]
    public Task SavedQuery_SaveFromEditor_ListSearch_FillAndRemember() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var (app, p) = Connected(env, Users);
        var tabId = app.AddTab("SELECT 1;\nSELECT * FROM users WHERE id IN (:ids) AND name = :name")!.Value;
        // 편집기가 고른 문장 (Mac activeEditor.sqlToRun)
        app.ActiveEditorSql = () => "SELECT * FROM users WHERE id IN (:ids) AND name = :name";
        Assert.True(app.CanSaveQuery && app.CanOpenSavedQueries);
        app.OpenSaveQuery();
        var save = Assert.IsType<SavedQuerySheet.Save>(app.SavedQuerySheet);
        var sw = new SavedQueriesWindow(app, save);
        var sv = Assert.IsType<SaveQueryView>(sw.View);
        Assert.Equal("SELECT * FROM users WHERE id IN (:ids) A", sv.NameBox.Text);
        Assert.Equal(Tr("빈칸: %@", ":ids, :name"), sv.ParamsText.Text);
        sv.NameBox.Text = "회원 찾기";
        sv.FolderBox.Text = "회원";
        sv.ThisConnection.IsChecked = true;
        sv.Save();
        var q = Assert.Single(app.SavedQueries.Queries);
        Assert.Equal(("회원 찾기", "회원", SavedQuery.QueryScope.Profile, (Guid?)p.Id), (q.Name, q.Folder, q.Scope, q.ProfileId));
        Assert.True(File.Exists(app.SavedQueryFile.File));
        Assert.Contains(app.Logs, l => l.Message == Tr("저장 쿼리에 넣었어요: %@", "회원 찾기"));

        // 빈 편집기 → 저장할 쿼리가 없음 (경고만)
        app.ActiveEditorSql = () => "  ";
        app.Tab(tabId)!.Text = "";
        app.SavedQuerySheet = null;
        app.OpenSaveQuery();
        Assert.Null(app.SavedQuerySheet);
        Assert.Equal(Tr("저장할 쿼리가 없어요. 편집기에 쿼리를 쓰고 다시 저장하세요."), app.LastLog?.Message);

        // 목록: 파일에서 다시 읽고, 검색 · 폴더 묶음
        app.ChangeSavedQueries(l => l.Add(new SavedQuery("전체 수", "SELECT COUNT(*) FROM users")));
        app.SavedQueries = new SavedQueryLibrary();
        app.OpenSavedQueries();
        Assert.IsType<SavedQuerySheet.List>(app.SavedQuerySheet);
        Assert.Equal(2, app.SavedQueries.Queries.Count);
        var lw = new SavedQueriesWindow(app, app.SavedQuerySheet!);
        var list = Assert.IsType<SavedQueryListView>(lw.View);
        Assert.Equal(2, list.Ordered.Count);
        list.SearchBox.Text = "count";
        Assert.Equal(["전체 수"], list.Ordered.Select(o => o.Name));
        list.SearchBox.Text = "";
        list.Select(q.Id);

        // 빈칸이 있으면 입력 창
        app.UseSavedQuery(q.Id, run: false);
        var fill = Assert.IsType<SavedQuerySheet.Fill>(app.SavedQuerySheet);
        var fw = new SavedQueriesWindow(app, fill);
        var fv = Assert.IsType<SavedQueryFillView>(fw.View);
        Assert.Equal(["ids", "name"], fv.Names);
        fv.Field("ids").Value.Text = "1,2,3";
        fv.Field("name").Value.Text = "kim";
        Assert.Equal("SELECT * FROM users WHERE id IN (1, 2, 3) AND name = 'kim'", fv.PreviewText.Text);
        fv.Field("name").Null.IsChecked = true;
        Assert.False(fv.Field("name").Value.IsEnabled);
        Assert.EndsWith("name = NULL", fv.PreviewText.Text);
        fv.Field("name").Null.IsChecked = false;
        fv.Apply(run: false);
        Assert.Null(app.SavedQuerySheet);
        // 지금 탭이 비었으니 그 탭에 넣고 실행하지 않음
        Assert.Equal(tabId, app.SelectedTabId);
        Assert.Equal("SELECT * FROM users WHERE id IN (1, 2, 3) AND name = 'kim'", app.Tab(tabId)!.Text);
        Assert.Null(app.RunAllRequest);
        // 마지막 입력값을 기억해 다음에 채워 둔다
        var again = new SavedQueryFillView(app, q.Id, () => { });
        Assert.Equal("1,2,3", again.Field("ids").Value.Text);
        Assert.Equal("kim", again.Field("name").Value.Text);
        Assert.NotNull(app.SavedQueries[q.Id]!.LastUsedAt);
        Assert.Equal("kim", new SavedQueryStore(app.SavedQueryFile.File).Load()[q.Id]!.LastValues["name"]);

        // 빈칸이 없는 쿼리 [실행]: 탭이 비어 있지 않으면 새 탭(제목 = 이름), 평소 실행 경로로 실행 요청
        var count = app.SavedQueries.Queries.First(x => x.Name == "전체 수");
        app.UseSavedQuery(count.Id, run: true);
        Assert.NotEqual(tabId, app.SelectedTabId);
        Assert.Equal("전체 수", app.SelectedTab!.Title);
        Assert.Equal("SELECT COUNT(*) FROM users", app.SelectedTab.Text);
        Assert.Equal(app.SelectedTabId, app.RunAllRequest);

        // 이 접속만: 다른 접속에서는 보이지 않는다
        Assert.DoesNotContain(app.SavedQueries.Search("", Guid.NewGuid()), x => x.Id == q.Id);
        sw.Close(); lw.Close(); fw.Close();
    });

    [Fact]
    public Task SavedQuery_ListMenuActions_RenameFolderDuplicateDelete() => UiThread.Run(async () =>
    {
        var env = new TestEnv();
        var (app, _) = Connected(env, Users);
        var confirm = new ConfirmDialogs();
        app.Dialogs = confirm;
        app.ChangeSavedQueries(l => l.Add(new SavedQuery("a", "SELECT 1", "x")));
        var id = app.SavedQueries.Queries[0].Id;
        var view = new SavedQueryListView(app, () => { });
        var answers = new Queue<string?>(["b", "보고서"]);
        view.Prompt = (_, _, _) => Task.FromResult(answers.Dequeue());
        await view.RenameAsync(app.SavedQueries[id]!);
        await view.ChangeFolderAsync(app.SavedQueries[id]!);
        Assert.Equal(("b", "보고서"), (app.SavedQueries[id]!.Name, app.SavedQueries[id]!.Folder));
        view.Duplicate(app.SavedQueries[id]!);
        Assert.Contains(app.SavedQueries.Queries, q => q.Name == "b" + Tr(" 사본"));
        confirm.Answer = false;
        await view.DeleteAsync(app.SavedQueries[id]!);
        Assert.Equal(2, app.SavedQueries.Queries.Count);
        confirm.Answer = true;
        await view.DeleteAsync(app.SavedQueries[id]!);
        Assert.Single(app.SavedQueries.Queries);
        Assert.Equal(Tr("저장 쿼리를 지울까요?"), confirm.Titles.Last());
        // 메뉴 항목 (Mac 과 같은 순서)
        var headers = view.List.ContextMenu!.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.Equal([Tr("실행"), Tr("편집기에 넣기"), Tr("이름 바꾸기"), Tr("폴더 바꾸기"), Tr("복제"), Tr("삭제…")], headers);
    });

    private sealed class ConfirmDialogs : NullDialogs
    {
        public bool Answer { get; set; }
        public List<string> Titles { get; } = [];
        public override Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel, bool destructive = false)
        {
            Titles.Add(title);
            return Task.FromResult(Answer);
        }
    }

    [Fact]
    public Task SavedQuery_QuickOpenAndToolbarAndEditorMenu() => UiThread.Run(async () =>
    {
        var env = new TestEnv();
        var (app, _) = Connected(env, Users);
        app.ChangeSavedQueries(l => l.Add(new SavedQuery("회원 수", "SELECT COUNT(*) FROM users", "회원")));
        var quick = new QuickOpenWindow(app, true);
        var item = Assert.Single(quick.Items, i => i.Kind == QuickOpen.Kind.SavedQuery);
        Assert.Equal(("회원", "회원 수"), (item.Schema, item.Name));
        Assert.Contains(QuickOpen.Search("회원 수", quick.Items), i => i.Kind == QuickOpen.Kind.SavedQuery);
        await app.OpenQuickItemAsync(item);
        Assert.Equal("SELECT COUNT(*) FROM users", app.SelectedTab!.Text);
        Assert.Equal(app.SelectedTabId, app.RunAllRequest);
        quick.Close();

        // 쿼리 화면: 도구 막대 저장 쿼리 버튼, 편집기 우클릭 > 쿼리 저장…
        var view = new QueryWorkspaceView { DataContext = app };
        var w = new Window { Width = 1100, Height = 800, Content = view };
        w.Show();
        Pump();
        var tv = view.TabView!;
        app.SavedQuerySheet = null;
        Click(tv.SavedQueriesButton);
        Assert.IsType<SavedQuerySheet.List>(app.SavedQuerySheet);
        app.SavedQuerySheet = null;
        tv.Editor.SetSqlText("SELECT 1;\nSELECT 2");
        tv.Editor.CaretOffset = 2;
        var save = tv.Editor.ContextMenu!.Items.OfType<MenuItem>().First(m => m.Header as string == Tr("쿼리 저장… (Ctrl+Alt+S)"));
        save.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(new SavedQuerySheet.Save("SELECT 1"), app.SavedQuerySheet);
        w.Close();
    });

    // MARK: - ID 로 찾기

    [Fact]
    public Task IdSearch_WindowDefaults_Extract_AndRemember() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var orders = new TableMeta("orders", columns: [new ColumnMeta("code", "varchar(20)", false), new ColumnMeta("memo", "text", true)]);
        var (app, p) = Connected(env, Users, orders);
        Assert.True(app.CanSearchIds);
        app.OpenIdSearch("users");
        var request = Assert.IsType<IdSearchRequest>(app.IdSearchRequest);
        Assert.Equal(("users", "shop"), (request.Table, request.Database));
        var w = new IdSearchWindow(app, request);
        // 기본 키(단일 컬럼)가 기본 컬럼, 숫자 컬럼이면 숫자만
        Assert.Equal("users", w.TableBox.SelectedItem);
        Assert.Equal("id (bigint)", w.ColumnBox.SelectedItem);
        Assert.True(w.NumericOnly);
        Assert.False(w.SearchButton.IsEnabled);
        w.PasteBox.Text = "2026-11-20 10:01:02 ERROR memberNo=1023 failed\nmemberNo=1023 A-77 2044";
        Assert.Equal(["1023", "2044"], w.Found.Values);
        Assert.StartsWith(Tr("찾은 값 %@개", 2) + Tr(" (중복 %@개 제외)", 1) + " : 1023, 2044", w.SummaryText.Text);
        Assert.True(w.SearchButton.IsEnabled);
        // 문자 ID 포함
        w.IncludeTextBox.IsChecked = true;
        Assert.Contains("A-77", w.Found.Values);
        // 문자 컬럼이면 체크가 켜진 채로 바꿀 수 없다
        w.TableBox.SelectedItem = "orders";
        Assert.Equal("code (varchar(20))", w.ColumnBox.SelectedItem);
        Assert.True(w.IncludeTextBox.IsChecked == true && !w.IncludeTextBox.IsEnabled);
        // 조회: 마지막 선택을 기억 (연결이 가짜라 실행은 하지 않는다)
        w.Submit();
        var memory = app.IdSearchFile.Load();
        Assert.Equal("orders", memory.LastTable(p.Id, "shop"));
        Assert.Equal("code", memory.LastColumn(p.Id, "shop", "orders"));
        // 다음에 열면 마지막 테이블
        var w2 = new IdSearchWindow(app, new IdSearchRequest(p.Id, "shop", null));
        Assert.Equal("orders", w2.TableBox.SelectedItem);
        // 1,000개 넘으면 안내
        w2.PasteBox.Text = string.Join(" ", Enumerable.Range(1, 1005).Select(i => "c" + i));
        Assert.True(w2.OverflowText.IsVisible);
        w2.Close();
    });

    [Fact]
    public Task IdSearch_MissingBar_OnlyForCompleteResult() => UiThread.Run(async () =>
    {
        var run = new IdSearchRun("id", ["1023", "007", "99"], "SELECT * FROM `users` WHERE `id` IN (1023, 007, 99)");
        var result = new QueryResult(["ID", "name"], [["1023", "a"], ["7", "b"]], 0.01) { Sql = run.Sql };
        Assert.True(IdSearchMissingBar.Applies(run, result));
        Assert.False(IdSearchMissingBar.Applies(run, new QueryResult(["ID"], [], 0) { Sql = run.Sql, HasMore = true }));
        Assert.False(IdSearchMissingBar.Applies(run, new QueryResult(["ID"], [], 0) { Sql = "SELECT 1" }));
        Assert.False(IdSearchMissingBar.Applies(run, new QueryResult(["other"], [], 0) { Sql = run.Sql }));
        var bar = new IdSearchMissingBar();
        string? copied = null;
        bar.SetClipboard = t => { copied = t; return Task.CompletedTask; };
        bar.Update(run, result);
        Assert.Equal(Tr("요청 %@ · 찾음 %@ · 없음 %@", 3, 2, 1), bar.CountsText);
        bar.CopyMissing();
        Assert.Equal("99", copied);

        // 쿼리 화면: ID 로 찾기 탭의 결과 아래에 보인다
        var (app, _) = Connected(new TestEnv(), Users);
        var tabId = app.AddTab(run.Sql, IdSearch.TabTitle("users", "id", 3))!.Value;
        var view = new QueryWorkspaceView { DataContext = app };
        var w = new Window { Width = 1100, Height = 800, Content = view };
        w.Show();
        Pump();
        app.Update(tabId, t => { t.IdSearch = run; t.Results = [result]; t.Result = result; });
        Pump();
        Assert.True(view.TabView!.Results.IdMissing.IsVisible);
        Assert.Equal("ID: users.id (3)", app.Tab(tabId)!.Title);
        w.Close();
        await Task.CompletedTask;
    });

    [Fact]
    public Task IdSearch_RunsInNewTab_WithRaisedRowLimit_AgainstMySql() => UiThread.Run(async () =>
    {
        var port = Environment.GetEnvironmentVariable("GODDB_IT_PORT");
        if (string.IsNullOrEmpty(port)) Assert.Skip("GODDB_IT_PORT 없음 — scripts/test-dbs.sh test 로 실행");
        var env = new TestEnv();
        var app = env.NewApp();
        var p = new ConnectionProfile
        {
            Name = "it-ids", Kind = DatabaseKind.Mysql, Host = "127.0.0.1", Port = int.Parse(port!),
            Username = "root", Database = "shop", UseTLS = false, SavePassword = false,
        };
        app.Profiles.Add(p);
        app.SessionPasswords[p.Id] = Environment.GetEnvironmentVariable("GODDB_IT_PASSWORD") ?? "goddbtest";
        await app.ConnectAsync(p.Id);
        Assert.True(app.ConnectionState(p.Id).IsConnected, app.LastLog?.Message);
        var table = "it_ids_" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await app.RunAsync($"CREATE TABLE {table} (id INT PRIMARY KEY, name VARCHAR(20))");
            await app.RunAsync($"INSERT INTO {table} SELECT n, CONCAT('n', n) FROM (WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM s WHERE n < 700) SELECT n FROM s) x");
            app.RowLimit = 100;
            var values = Enumerable.Range(1, 600).Select(i => i.ToString()).Append("9999").ToList();
            await app.RunIdSearchAsync(p.Id, table, "id", values, numeric: true);
            var tab = app.SelectedTab!;
            Assert.Equal(IdSearch.TabTitle(table, "id", 601), tab.Title);
            Assert.Null(tab.Error);
            // 행 수 제한을 값 개수 이상으로 올려 한 번에 받는다
            Assert.Equal(600, tab.Result!.Rows.Count);
            Assert.False(tab.Result.HasMore);
            Assert.True(IdSearchMissingBar.Applies(tab.IdSearch!, tab.Result));
            var cmp = IdSearch.Compare(tab.IdSearch!.Values, tab.Result.Rows.Select(r => r.Values[0]));
            Assert.Equal(["9999"], cmp.Missing);
            Assert.Equal(100, app.RowLimit);
        }
        finally
        {
            await app.RunAsync($"DROP TABLE IF EXISTS {table}");
            if (app.Connections.TryGetValue(p.Id, out var c) && c.Session is { } s) await s.CloseAsync();
        }
    });

    // MARK: - 쿼리 튜닝 제거 (Mac 1.4.0)

    [Fact]
    public Task Tuning_IsRemoved_FromMenusToolbarPlanAndSettings() => UiThread.Run(() =>
    {
        Assert.DoesNotContain(SheetRegistry.Sheets.Keys, k => k.Contains("Tuning"));
        Assert.Null(typeof(AppState).GetProperty("TuningJob"));
        Assert.Null(typeof(AppState).Assembly.GetType("GodDB.App.Views.TuningSheet"));
        Assert.Null(typeof(GodDB.Core.Ai.OllamaMessage).Assembly.GetType("GodDB.Core.Ai.TuningJob"));
        var (app, _) = Connected(new TestEnv(), Users);
        app.AddTab("SELECT 1");
        var w = new MainWindow(app);
        w.Show();
        Pump();
        var headers = w.GetLogicalDescendants().OfType<MenuItem>().Select(m => m.Header as string ?? "").ToList();
        foreach (var m in w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>())
            headers.AddRange(m.Items.OfType<MenuItem>().Select(i => i.Header as string ?? ""));
        Assert.DoesNotContain(headers, h => h.Contains("튜닝") || h.Contains("Tuning"));
        Assert.Contains(Tr("ID 로 찾기…"), headers);
        Assert.Contains(Tr("쿼리 저장…"), headers);
        Assert.Contains(Tr("저장 쿼리…"), headers);
        var gestures = w.KeyBindings.Select(k => k.Gesture.ToString()).ToList();
        Assert.Contains("Ctrl+Alt+F", gestures);
        Assert.Contains("Ctrl+Alt+S", gestures);
        Assert.Contains("Ctrl+Shift+K", gestures);
        Assert.DoesNotContain("Ctrl+Alt+T", gestures);
        var texts = w.GetVisualDescendantsText();
        Assert.DoesNotContain(texts, t => t.Contains("튜닝"));
        // 실행 계획 화면은 주의사항만 (AI 로 튜닝하기 버튼 없음)
        var plan = new PlanWarningsView();
        plan.Update(new QueryResult(["id", "select_type", "table", "type"], [["1", "SIMPLE", "t", "ALL"]], 0), null);
        Assert.DoesNotContain(plan.GetLogicalDescendants().OfType<Button>(), b => (b.Content?.ToString() ?? "").Contains(Tr("AI 로 튜닝하기")));
        w.Close();
        var settings = new SettingsWindow((AppState?)null);
        Assert.DoesNotContain(settings.GetLogicalDescendants().OfType<CheckBox>(), c => (c.Content as string ?? "").Contains("튜닝"));
    });
}

internal static class VisualTextExtensions
{
    public static List<string> GetVisualDescendantsText(this Control root) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
}
