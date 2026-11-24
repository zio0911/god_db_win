using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Ai;
using GodDB.Core.Drivers;
using GodDB.Core.Modeling;
using GodDB.Core.Schema;

namespace GodDB.App.Tests;

/// 스키마 비교 · 테이블 수정 · AI 어시스턴트 화면 (headless — 창을 실제로 띄우지 않는다)
public class CompareUiTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static string? ItPort => Environment.GetEnvironmentVariable("GODDB_IT_PORT");
    private static string ItPassword => Environment.GetEnvironmentVariable("GODDB_IT_PASSWORD") ?? "goddbtest";

    private static ConnectionProfile ItProfile(string name, string db) => new()
    {
        Name = name, Kind = DatabaseKind.Mysql, Host = "127.0.0.1", Port = int.Parse(ItPort!),
        Username = "root", Database = db, UseTLS = false, SavePassword = false,
    };

    // MARK: - 화면 생성

    [Fact]
    public Task Views_ConstructHeadlessly() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var a = TestEnv.Profile("a");
        app.Profiles.Add(a);
        TestEnv.FakeConnect(app, a.Id);
        app.SelectedProfileId = a.Id;

        var compare = new CompareView { DataContext = app };
        var assistant = new AssistantPanel { DataContext = app };
        foreach (Control c in new Control[] { compare, assistant })
        {
            var w = new Window { Width = 900, Height = 600, Content = c };
            w.Show();
            Pump();
            Assert.True(c.Bounds.Width > 0);
            w.Close();
        }
        Assert.NotNull(compare.Vm);
        Assert.NotNull(assistant.Vm);

        var table = new TableMeta("tb_member", "회원",
            [new ColumnMeta("id", "int", false, extra: "auto_increment", position: 1), new ColumnMeta("email", "varchar(100)", true, position: 2)],
            [new IndexMeta("PRIMARY", true, ["id"]), new IndexMeta("idx_email", false, ["email"])],
            [new ForeignKeyMeta("fk_x", "tb_member", ["email"], "tb_mail", ["email"])]);
        var editor = new TableEditorWindow(app, table);
        editor.Show();
        Pump();
        // 모든 종류의 항목을 한 번씩 골라 속성 칸을 그려 본다
        var vm = editor.Vm;
        vm.Selection = new TableEditorViewModel.Node.Column(vm.Columns[0].Id); Pump();
        vm.Selection = new TableEditorViewModel.Node.Index(vm.Indexes[1].Id); Pump();
        vm.Selection = new TableEditorViewModel.Node.ForeignKey("fk_x"); Pump();
        vm.RemoveSelected();   // 외래 키는 지울 수 없다
        Assert.Equal(2, vm.Columns.Count);
        vm.Selection = new TableEditorViewModel.Node.Column(vm.Columns[1].Id);
        vm.RemoveSelected();
        vm.Selection = new TableEditorViewModel.Node.DeletedColumn("email"); Pump();
        vm.Restore("email");
        Assert.Equal(["id", "email"], vm.Columns.Select(c => c.Name));
        editor.Close();
    });

    [Fact]
    public Task MainWindow_OpensTableEditorSheet() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var a = TestEnv.Profile("a");
        app.Profiles.Add(a);
        TestEnv.FakeConnect(app, a.Id);
        app.SelectedProfileId = a.Id;
        var w = new MainWindow(app);
        w.Show();
        Pump();
        app.AlteringTable = new TableMeta("t", columns: [new ColumnMeta("id", "int", false)]);
        Pump();
        Assert.IsType<TableEditorWindow>(w.OpenSheets[nameof(AppState.AlteringTable)]);
        app.AlteringTable = null;
        Pump();
        Assert.False(w.OpenSheets.ContainsKey(nameof(AppState.AlteringTable)));
        w.Close();
    });

    // MARK: - 스키마 비교

    [Fact]
    public Task Compare_ExcludesRedisAndNeedsBothSides() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var my = TestEnv.Profile("my");
        var redis = TestEnv.Profile("cache", DatabaseKind.Redis);
        app.Profiles.Add(my);
        app.Profiles.Add(redis);
        var vm = new CompareViewModel(app);
        Assert.Equal([null, my.Id], vm.Choices.Select(c => c.Id));
        Assert.False(vm.CanCompare);
        vm.Select(my.Id, redis.Id);
        Assert.False(vm.CanCompare);   // Redis 는 고를 수 없다
        vm.Select(my.Id, my.Id);
        Assert.True(vm.CanCompare);
        Assert.True(vm.ShowEmpty);
    });

    [Fact]
    public Task Compare_TwoMySqlDatabases_ProducesFixSql() => UiThread.Run(async () =>
    {
        if (string.IsNullOrEmpty(ItPort)) Assert.Skip("GODDB_IT_PORT 없음 — scripts/test-dbs.sh test 로 실행");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sdb = "goddb_cmp_s_" + suffix;
        var tdb = "goddb_cmp_t_" + suffix;
        var admin = await DbSession.ConnectAsync(ItProfile("admin", ""), ItPassword);
        try
        {
            foreach (var sql in new[]
            {
                $"CREATE DATABASE `{sdb}`", $"CREATE DATABASE `{tdb}`",
                $"CREATE TABLE `{sdb}`.tb_member (id INT NOT NULL AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50) NOT NULL, email VARCHAR(100) NULL COMMENT '메일', KEY idx_name (name))",
                $"CREATE TABLE `{sdb}`.tb_order (id INT NOT NULL PRIMARY KEY)",
                $"CREATE TABLE `{tdb}`.tb_member (id INT NOT NULL AUTO_INCREMENT PRIMARY KEY, name VARCHAR(20) NOT NULL)",
                $"CREATE TABLE `{tdb}`.tb_extra (id INT NOT NULL PRIMARY KEY)",
            })
                await admin.RawAsync(sql);

            var env = new TestEnv();
            var app = env.NewApp();
            var s = ItProfile("src", sdb);
            var t = ItProfile("dst", "");   // 기본 DB 없음 → 화면의 DB 칸으로
            app.Profiles.Add(s);
            app.Profiles.Add(t);
            app.SessionPasswords[s.Id] = ItPassword;
            app.SessionPasswords[t.Id] = ItPassword;
            // 쿼리 탭으로 열기는 사이드바에서 보고 있는 접속의 탭으로 (연결된 것처럼)
            TestEnv.FakeConnect(app, s.Id, sdb);
            app.SelectedProfileId = s.Id;
            var view = new CompareView { DataContext = app };
            var w = new Window { Width = 1000, Height = 700, Content = view };
            w.Show();
            var vm = view.Vm!;
            vm.Select(s.Id, t.Id);

            // 대상 DB 이름이 없으면 비교하지 않는다
            await vm.CompareAsync();
            Assert.Equal("비교할 DB 이름을 입력하거나 접속의 기본 DB를 설정해 주세요.", vm.Error);
            Assert.True(vm.ShowError);

            vm.TargetDb = tdb;
            await vm.CompareAsync();
            Assert.Null(vm.Error);
            var r = vm.Report!;
            Assert.Equal(0, r.MatchedTables);
            Assert.Equal(1, r.DifferentCount);
            Assert.Equal(3, r.MissingCount);
            Assert.Equal(1, r.ExtraCount);
            SchemaDiffItem Item(SchemaDiffKind k) => r.Items.Single(i => i.Kind == k);
            Assert.StartsWith("CREATE TABLE `tb_order`", Item(SchemaDiffKind.MissingTable).FixSql);   // SHOW CREATE TABLE
            Assert.EndsWith(";", Item(SchemaDiffKind.MissingTable).FixSql);
            Assert.Equal("ALTER TABLE `tb_member` MODIFY COLUMN `name` varchar(50) NOT NULL;", Item(SchemaDiffKind.DifferentColumn).FixSql);
            Assert.Equal("ALTER TABLE `tb_member` ADD COLUMN `email` varchar(100) NULL COMMENT '메일' AFTER `name`;", Item(SchemaDiffKind.MissingColumn).FixSql);
            Assert.Equal("ALTER TABLE `tb_member` ADD INDEX `idx_name` (`name`);", Item(SchemaDiffKind.MissingIndex).FixSql);
            Assert.DoesNotContain("DROP", r.FixScript);

            // 화면 표시
            Assert.True(vm.ShowResult);
            Assert.Equal("일치 테이블 0", vm.MatchedText);
            Assert.Equal("누락 3", vm.MissingText);
            Assert.Equal("대상에만 있는 항목 1", vm.ExtraText);
            Assert.DoesNotContain(vm.Visible, i => i.Severity == SchemaDiffSeverity.Extra);
            vm.ShowExtra = true;
            Assert.Contains(vm.Visible, i => i.Kind == SchemaDiffKind.ExtraTable && i.Table == "tb_extra");
            Assert.Equal("왼쪽에서 항목을 선택하세요.", vm.SelectedSql);
            vm.Selection = vm.Visible.First(i => i.Kind == SchemaDiffKind.ExtraTable);
            Assert.Equal("-- 참고용 항목이라 SQL을 만들지 않아요.", vm.SelectedSql);
            vm.Selection = vm.Visible.First(i => i.Kind == SchemaDiffKind.MissingIndex);
            // Mac 1.4.0: 고른 항목의 SQL 은 테이블마다 "-- 테이블" 머리줄 뒤에
            Assert.Equal("-- tb_member\nALTER TABLE `tb_member` ADD INDEX `idx_name` (`name`);", vm.SelectedSql);
            Pump();

            // 복사 · 쿼리 탭으로 열기 (실행하지 않는다)
            await vm.CopyAllCommand.ExecuteAsync(null);
            Assert.Equal(r.FixScript, ((GodDB.App.Services.NullDialogs)app.Dialogs).Clipboard);
            vm.OpenInTabCommand.Execute(null);
            Assert.Equal(MainMode.Query, app.Mode);
            Assert.EndsWith(r.FixScript, app.Tabs[^1].Text);
            Assert.StartsWith("-- 스키마 보정 SQL", app.Tabs[^1].Text);
            Assert.Contains(app.Logs, l => l.Message.StartsWith($"src/{sdb} → dst/{tdb} 비교 완료 · 누락 3, 다름 1"));
            var after = await admin.LoadSchemaAsync(tdb);
            Assert.Equal(2, after.Single(x => x.Name == "tb_member").Columns.Count);   // 대상은 그대로
            w.Close();
        }
        finally
        {
            try { await admin.RawAsync($"DROP DATABASE IF EXISTS `{sdb}`"); } catch { }
            try { await admin.RawAsync($"DROP DATABASE IF EXISTS `{tdb}`"); } catch { }
            await admin.CloseAsync();
        }
    });

    // MARK: - 테이블 수정

    private static TableMeta Member() => new("tb_member", "회원",
        [
            new ColumnMeta("id", "bigint", false, extra: "auto_increment", columnKey: "PRI", position: 1),
            new ColumnMeta("email", "varchar(100)", true, comment: "메일", position: 2),
            new ColumnMeta("reg_dt", "datetime", false, defaultValue: "CURRENT_TIMESTAMP", position: 3),
        ],
        [new IndexMeta("PRIMARY", true, ["id"]), new IndexMeta("idx_email", false, ["email"])]);

    [Fact]
    public Task TableEditor_PreviewMatchesCore_ValidationBlocksExecute() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var p = TestEnv.Profile("dev");
        app.Profiles.Add(p);
        TestEnv.FakeConnect(app, p.Id);
        app.SelectedProfileId = p.Id;
        var table = Member();
        app.AlteringTable = table;
        var w = new TableEditorWindow(app, table);
        w.Show();
        var vm = w.Vm;
        string? Core() => TableAlter.Statement(table, vm.Columns, vm.Indexes, vm.Comment, app.Dialect);

        Assert.Null(vm.SqlText);
        Assert.Equal("-- 변경 사항이 없어요.", vm.PreviewText);
        Assert.Equal("변경 없음", vm.ChangeSummary);
        Assert.False(vm.CanExecute);

        // 이름 변경 · 새 컬럼 · 인덱스 · 코멘트
        vm.Columns[1].Name = "mail";
        vm.Touch();
        // Mac 과 같이 인덱스의 컬럼 이름은 따라 바뀌지 않아 검증 오류가 보인다
        Assert.Equal("-- idx_email 인덱스의 컬럼 email 이(가) 테이블에 없어요.", vm.PreviewText);
        vm.Indexes[1].Columns[0] = "mail";
        vm.Touch();
        Assert.Equal(TableEditorViewModel.Mark.Changed, vm.ColumnMark(vm.Columns[1]));
        vm.Selection = new TableEditorViewModel.Node.Column(vm.Columns[1].Id);
        vm.AddColumn();
        var added = vm.SelectedColumn!;
        Assert.Equal("new_column", added.Name);
        Assert.Equal(2, vm.Columns.IndexOf(added));   // 고른 컬럼 다음에
        Assert.Equal(TableEditorViewModel.Mark.New, vm.ColumnMark(added));
        added.ColumnType = "int";
        vm.EditExtra(added, e => e.Rest = "");
        vm.AddIndex();
        Assert.Equal(["new_column"], vm.SelectedIndex!.Columns);
        vm.Comment = "회원 테이블";
        Pump();
        Assert.True(vm.SqlText != null, vm.PreviewText);
        Assert.Equal(Core(), vm.SqlText);
        Assert.Contains("CHANGE COLUMN `email` `mail`", vm.SqlText);
        Assert.True(vm.SqlText!.Contains("ADD COLUMN `new_column` int NULL AFTER `mail`"), vm.SqlText);
        Assert.Contains("COMMENT = '회원 테이블'", vm.SqlText);
        Assert.True(vm.CanExecute);

        // 복제 · 이동 · 기본 키 · 삭제 예정
        vm.Selection = new TableEditorViewModel.Node.Column(vm.Columns[0].Id);
        vm.DuplicateSelected();
        Assert.Equal("id_copy", vm.SelectedColumn!.Name);
        Assert.Equal("", vm.SelectedColumn.Extra);   // 자동 증가는 복제하지 않는다
        vm.MoveSelected(1);
        vm.SetPK("id_copy", true);
        Assert.True(vm.IsPK("id_copy"));
        Assert.False(vm.SelectedColumn.Nullable);
        vm.Selection = new TableEditorViewModel.Node.Column(vm.Columns.First(c => c.Name == "reg_dt").Id);
        vm.RemoveSelected();
        Assert.Equal(["reg_dt"], vm.DeletedColumns.Select(c => c.Name));
        Pump();
        Assert.Equal(Core(), vm.SqlText);
        Assert.Contains("DROP COLUMN `reg_dt`", vm.SqlText);

        // 검증 오류: 빈 이름 → 빨간 미리보기, 실행 불가
        vm.Columns[0].Name = "";
        vm.Touch();
        Pump();
        Assert.Null(vm.SqlText);
        Assert.Equal("-- 이름이 비어 있는 컬럼이 있어요.", vm.PreviewText);
        Assert.False(vm.CanExecute);
        Assert.False(await vm.ExecuteAsync());
        Assert.Same(table, app.AlteringTable);
        Assert.DoesNotContain(app.Logs, l => l.Message.Contains("테이블 수정"));

        // 중복 이름
        vm.Columns[0].Name = "mail";
        vm.Touch();
        Assert.Equal("-- 컬럼 이름 mail 이(가) 중복돼요.", vm.PreviewText);

        // 고치면 쿼리 탭으로 열기 (실행하지 않음)
        vm.Columns[0].Name = "id";
        vm.Touch();
        var sql = vm.SqlText!;
        Assert.True(vm.OpenInTab());
        Assert.Null(app.AlteringTable);
        Assert.Equal(sql, app.Tabs[^1].Text);
        Assert.Equal("ALTER · tb_member", app.Tabs[^1].Title);
        Assert.Equal("dev / shop 에서 바로 실행돼요. 컬럼 삭제는 되돌릴 수 없어요.", vm.ConfirmMessage);
        w.Close();
    });

    [Fact]
    public Task TableEditor_ReadOnlyCannotExecute() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var p = TestEnv.Profile("prod") with { ReadOnly = true };
        app.Profiles.Add(p);
        TestEnv.FakeConnect(app, p.Id);
        app.SelectedProfileId = p.Id;
        var vm = new TableEditorViewModel(app, Member()) { Comment = "x" };
        Assert.NotNull(vm.SqlText);
        Assert.False(vm.CanExecute);
    });

    [Fact]
    public Task TableEditor_ExecutesAgainstMySql() => UiThread.Run(async () =>
    {
        if (string.IsNullOrEmpty(ItPort)) Assert.Skip("GODDB_IT_PORT 없음 — scripts/test-dbs.sh test 로 실행");
        var app = new TestEnv().NewApp();
        var p = ItProfile("it-alter", "shop");
        app.Profiles.Add(p);
        app.SessionPasswords[p.Id] = ItPassword;
        var name = "it_alter_" + Guid.NewGuid().ToString("N")[..10];
        await app.ConnectAsync(p.Id);
        app.SelectedProfileId = p.Id;
        try
        {
            await app.Session!.RawAsync($"CREATE TABLE `{name}` (id INT NOT NULL PRIMARY KEY, old_col VARCHAR(10))");
            await app.RefreshSchemaAsync();
            var table = app.Tables.Single(t => t.Name == name);
            app.AlteringTable = table;
            var vm = new TableEditorViewModel(app, table);
            vm.Selection = new TableEditorViewModel.Node.Column(vm.Columns[1].Id);
            vm.RemoveSelected();
            vm.AddColumn();
            vm.SelectedColumn!.Name = "memo";
            vm.SelectedColumn.Comment = "메모";
            vm.Touch();
            Assert.True(vm.CanExecute);
            Assert.True(await vm.ExecuteAsync(), vm.ExecError);
            Assert.Null(app.AlteringTable);
            var after = app.Tables.Single(t => t.Name == name);
            Assert.Equal(["id", "memo"], after.Columns.Select(c => c.Name));
            Assert.Contains(app.Logs, l => l.Message == $"{name} 테이블 수정 완료");

            // 실패하면 창에 오류를 보이고 닫지 않는다
            app.AlteringTable = after;
            var bad = new TableEditorViewModel(app, after);
            bad.Columns[1].ColumnType = "no_such_type";
            bad.Touch();
            Assert.False(await bad.ExecuteAsync());
            Assert.NotNull(bad.ExecError);
            Assert.Same(after, app.AlteringTable);
        }
        finally
        {
            try { await app.Session!.RawAsync($"DROP TABLE IF EXISTS `{name}`"); } catch { }
            await app.DisconnectAsync(p.Id);
        }
    });

    // MARK: - AI 어시스턴트

    /// 요청을 기록하고 정해 둔 응답을 돌려주는 가짜 Ollama
    private sealed class FakeOllama(Func<string> answer, bool running = true) : HttpMessageHandler
    {
        public List<string> ChatBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!running) throw new HttpRequestException("connection refused");
            if (request.RequestUri!.AbsolutePath == "/api/tags")
                return Text("""{"models":[{"name":"test-model","size":1000000000}]}""");
            ChatBodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var text = answer();
            var sb = new StringBuilder();
            // 몇 조각으로 나눠 스트리밍
            for (var i = 0; i < text.Length; i += 7)
                sb.Append(JsonSerializer.Serialize(new { message = new { content = text.Substring(i, Math.Min(7, text.Length - i)) } })).Append('\n');
            sb.Append("""{"done":true}""").Append('\n');
            return Text(sb.ToString());
        }

        private static HttpResponseMessage Text(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };
    }

    private static async Task WaitAsync(AiChat chat)
    {
        for (var i = 0; i < 500 && chat.Running; i++) await Task.Delay(20);
        Assert.False(chat.Running);
    }

    [Fact]
    public Task Assistant_QueryScope_StreamsAnswer_InsertsSql_NoRows() => UiThread.Run(async () =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var p = TestEnv.Profile("dev");
        app.Profiles.Add(p);
        TestEnv.FakeConnect(app, p.Id);
        app.SelectedProfileId = p.Id;
        var tabId = app.AddTab("SELECT * FROM tb_member")!.Value;
        // 결과 행은 절대 보내지 않는다
        app.Tab(tabId)!.Result = new QueryResult(["id", "email"], [["1", "secret-row@example.com"]], 0.1);

        var panel = new AssistantPanel { DataContext = app };
        var w = new Window { Width = 420, Height = 700, Content = panel };
        w.Show();
        var vm = panel.Vm!;
        Assert.Equal($"assistant|{p.Id.ToString().ToUpperInvariant()}|shop", vm.Scope);
        Assert.True(vm.IsSql);
        Assert.Contains(vm.Chips(), c => c.Text == "SQL · " + app.Tab(tabId)!.Title);
        Assert.Equal(5, vm.QuickActions.Count);

        var fake = new FakeOllama(() => "설명이에요.\n```sql\nSELECT id FROM tb_member\n```");
        vm.Chat.ClientFactory = () => new OllamaClient(null, fake);
        vm.Chat.ModelSetting = () => "";
        vm.ClientFactory = () => new OllamaClient(null, fake);
        await vm.CheckOllamaAsync();
        Assert.Null(vm.OllamaHint);

        vm.Input = "tb_member 설명해 줘";
        Assert.True(vm.CanSend);
        vm.Send();
        Assert.Equal("", vm.Input);
        await WaitAsync(vm.Chat);
        Pump();
        Assert.Equal(["tb_member 설명해 줘", "설명이에요.\n```sql\nSELECT id FROM tb_member\n```"], vm.Chat.Messages.Select(m => m.Text));
        var body = Assert.Single(fake.ChatBodies);
        Assert.Contains("SELECT * FROM tb_member", body);              // 편집기 SQL 은 보낸다
        Assert.DoesNotContain("secret-row@example.com", body);         // 데이터 행은 보내지 않는다
        Assert.Contains(app.Logs, l => l.Message.StartsWith("AI 어시스턴트 · test-model · dev / shop"));

        // 답의 SQL 을 편집기에 넣기
        var answer = vm.Chat.Messages[^1];
        Assert.Equal("SELECT id FROM tb_member", AssistantPanelViewModel.SqlOf(answer));
        Assert.True(vm.IsFinished(answer));
        Assert.True(vm.InsertSql(answer));
        Assert.EndsWith("-- AI 어시스턴트\nSELECT id FROM tb_member;\n", app.Tab(tabId)!.Text);
        Assert.False(vm.CanApplyPhysicalNames(answer));   // 쿼리 범위에서는 없다

        // 새 대화 · 기록 · 삭제
        vm.StartNew();
        Assert.Empty(vm.Chat.Messages);
        Assert.Single(vm.Chat.History);
        vm.Open(vm.Chat.History[0]);
        Assert.Equal(2, vm.Chat.Messages.Count);
        vm.DeleteAll();
        Assert.Empty(vm.Chat.History);
        Pump();
        w.Close();
    });

    [Fact]
    public Task Assistant_ModelingScope_AppliesPhysicalNames() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var model = new DataModel(name: "쇼핑", tables: [new ModelTable(logicalName: "회원", columns: [new ModelColumn(logicalName: "이메일")])]);
        app.Modeling.UseForPreview([model]);
        app.Mode = MainMode.Modeling;
        var vm = new AssistantPanelViewModel(app);
        vm.Refresh();
        Assert.True(vm.IsModeling);
        Assert.Equal($"assistant|model|{model.Id.ToString().ToUpperInvariant()}", vm.Scope);
        Assert.Equal("모델 쇼핑 · 테이블 1개", Assert.Single(vm.Chips()).Text);
        Assert.StartsWith("논리명을 보고", vm.QuickActions[0]);

        var json = """{"tables":[{"key":"T1","physical":"tb_member","columns":[{"key":"T1.C1","physical":"email"}]}]}""";
        var fake = new FakeOllama(() => "추천이에요.\n```json\n" + json + "\n```");
        vm.Chat.ClientFactory = () => new OllamaClient(null, fake);
        vm.Chat.ModelSetting = () => "";
        vm.AskQuick(vm.QuickActions[0]);
        await WaitAsync(vm.Chat);
        Assert.Contains("```json", vm.Chat.Messages[0].Text);   // 물리명 질문은 JSON 지시까지 보낸다
        Assert.Contains("Current model", fake.ChatBodies[0]);
        var answer = vm.Chat.Messages[^1];
        Assert.False(answer.Failed, answer.Text);
        Assert.True(vm.CanApplyPhysicalNames(answer));
        var changed = 0;
        app.ModelEditedExternally += _ => changed++;
        Assert.Equal(2, vm.ApplyPhysicalNames(answer));
        Assert.Equal("tb_member", model.Tables[0].PhysicalName);
        Assert.Equal("email", model.Tables[0].Columns[0].PhysicalName);
        Assert.Equal(1, changed);
        Assert.Contains(app.Logs, l => l.Message == "AI 물리명 2개를 모델에 넣었어요.");
        // 두 번째는 빈 칸이 없어서 넣지 않는다
        Assert.Equal(0, vm.ApplyPhysicalNames(answer));
    });

    [Fact]
    public Task Assistant_OllamaNotRunning_ShowsHint() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var vm = new AssistantPanelViewModel(app) { ClientFactory = () => new OllamaClient(null, new FakeOllama(() => "", running: false)) };
        await vm.CheckOllamaAsync();
        Assert.Equal("Ollama 가 실행 중이 아니에요. 설정 > AI 에서 확인해 주세요.", vm.OllamaHint);
        Assert.NotEqual(AssistantPanelViewModel.OllamaAction.None, vm.OllamaFix);
        Assert.Equal("접속이 없어서 일반 질문만 할 수 있어요", Assert.Single(vm.Chips()).Text);
        Assert.Equal(["MySQL 과 PostgreSQL 의 트랜잭션 격리 수준 차이를 알려 줘"], vm.QuickActions);
    });
}
