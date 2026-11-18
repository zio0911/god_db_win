using GodDB.App.ViewModels;
using GodDB.Core;
using GodDB.Core.Audit;

namespace GodDB.App.Tests;

/// Docker 테스트 MySQL (scripts/test-dbs.sh) 로 AppState 흐름을 확인한다. GODDB_IT_PORT 가 없으면 건너뛴다.
/// 다른 테스트가 같은 DB 를 함께 쓰므로 테이블 이름에는 매번 다른 꼬리를 붙이고 끝나면 지운다.
public class AppStateIntegrationTests
{
    private static (TestEnv Env, AppState App, ConnectionProfile Profile) Setup(string? password = null)
    {
        var port = Environment.GetEnvironmentVariable("GODDB_IT_PORT");
        if (string.IsNullOrEmpty(port)) Assert.Skip("GODDB_IT_PORT 없음 — scripts/test-dbs.sh test 로 실행");
        var env = new TestEnv();
        var app = env.NewApp();
        var p = new ConnectionProfile
        {
            Name = "it-app", Kind = DatabaseKind.Mysql, Host = "127.0.0.1", Port = int.Parse(port!),
            Username = "root", Database = "shop", UseTLS = false, SavePassword = false,
        };
        app.Profiles.Add(p);
        app.SessionPasswords[p.Id] = password ?? Environment.GetEnvironmentVariable("GODDB_IT_PASSWORD") ?? "goddbtest";
        return (env, app, p);
    }

    [Fact]
    public Task Connect_LoadsSchema_Executes_Pages() => UiThread.Run(async () =>
    {
        var (_, app, p) = Setup();
        var table = "it_app_" + Guid.NewGuid().ToString("N")[..10];
        await app.ConnectAsync(p.Id);
        Assert.True(app.ConnectionState(p.Id).IsConnected, app.LastLog?.Message);
        Assert.Contains("shop", app.Databases);
        Assert.Equal("shop", app.CurrentDatabase);
        // 연결하면 그 접속의 쿼리 탭이 생긴다
        var tab = Assert.Single(app.Tabs);
        Assert.Equal(p.Id, tab.ProfileId);
        Assert.Equal("shop", tab.Database);
        try
        {
            // CREATE 뒤에는 사이드바 스키마를 다시 읽는다
            await app.RunAsync($"CREATE TABLE {table} (id INT PRIMARY KEY, name VARCHAR(20))");
            Assert.Null(tab.Error);
            Assert.Contains(app.Tables, t => t.Name == table);

            var values = string.Join(",", Enumerable.Range(1, 1200).Select(i => $"({i},'n{i}')"));
            await app.RunAsync($"INSERT INTO {table} VALUES {values}");
            Assert.Null(tab.Error);

            // 페이지 (500행)
            app.RowLimit = 500;
            await app.RunAsync($"SELECT * FROM {table} ORDER BY id");
            var r = tab.Result!;
            Assert.Equal(500, r.Rows.Count);
            Assert.True(r.HasMore);
            Assert.Equal(1, app.History.All().Count(h => h.Sql.StartsWith("SELECT * FROM " + table)));
            await app.LoadPageAsync(2, tab.Id);
            Assert.Equal(200, tab.Result!.Rows.Count);
            Assert.Equal("1001", tab.Result.Rows[0].Values[0]);
            await app.CountRowsAsync(tab.Id);
            Assert.Equal(1200, tab.Result!.TotalCount);

            // 서버 정렬
            await app.SortResultAsync(tab.Id, "id", ascending: false);
            Assert.Equal("1200", tab.Result!.Rows[0].Values[0]);
            Assert.Equal(0, tab.Result.Page);

            // 제한 없음: 1,000행씩 이어 붙이기
            app.RowLimit = 0;
            await app.RunAsync($"SELECT * FROM {table} ORDER BY id");
            Assert.True(tab.Result!.AppendMode);
            Assert.Equal(1000, tab.Result.Rows.Count);
            var appended = 0;
            tab.RowsAppended += (_, _, n) => appended += n;
            await app.LoadMoreAsync(tab.Id);
            Assert.Equal(1200, tab.Result!.Rows.Count);
            Assert.Equal(200, appended);
            Assert.False(tab.Result.HasMore);
            Assert.Equal(1199, tab.Result.Rows[^1].Id);

            // 여러 문장: 결과 탭, 실패하면 그 문장에서 멈춘다
            app.RowLimit = 500;
            await app.RunScriptAsync([$"SELECT COUNT(*) FROM {table}", $"SELECT * FROM {table} WHERE id < 3", "SELECT * FROM no_such_table_x", "SELECT 1"]);
            Assert.Equal(2, tab.Results.Count);
            Assert.Contains("3번째 문장에서 멈췄어요", tab.Error);
            Assert.Equal(2, tab.Result!.Rows.Count);

            // 실행 계획
            await app.RunAsync($"SELECT * FROM {table} WHERE name = 'x'", explain: true);
            Assert.True(tab.IsPlan);
            Assert.Null(tab.Error);
        }
        finally
        {
            await app.RunAsync($"DROP TABLE IF EXISTS {table}");
            await app.DisconnectAllAsync();
        }
        Assert.False(app.ConnectionState(p.Id).IsConnected);
        Assert.Single(app.Tabs);   // 끊어도 탭은 남는다
    });

    [Fact]
    public Task WrongPassword_PromptsAgain() => UiThread.Run(async () =>
    {
        var (_, app, p) = Setup(password: "definitely-wrong");
        await app.ConnectAsync(p.Id);
        Assert.True(app.ConnectionState(p.Id).IsFailed);
        Assert.NotNull(app.PasswordPrompt);
        Assert.Equal("비밀번호가 틀렸어요. 다시 입력해 주세요.", app.PasswordPrompt!.Message);
        Assert.False(app.SessionPasswords.ContainsKey(p.Id));
        Assert.True(app.ShowLog);
    });

    [Fact]
    public Task ManualCommit_TracksTransaction_AndRollback() => UiThread.Run(async () =>
    {
        var (_, app, p) = Setup();
        var table = "it_app_tx_" + Guid.NewGuid().ToString("N")[..10];
        await app.ConnectAsync(p.Id);
        var tab = app.Tabs.Single();
        try
        {
            await app.RunAsync($"CREATE TABLE {table} (id INT PRIMARY KEY)");
            await app.SetAutoCommitAsync(p.Id, false);
            Assert.False(app.TxState(p.Id).AutoCommit);
            await app.RunAsync($"INSERT INTO {table} VALUES (1)");
            Assert.True(app.TxState(p.Id).Open);
            // 열린 트랜잭션이 있으면 끊기 전에 묻는다
            await app.DisconnectAsync(p.Id);
            Assert.Equal(TxPromptReason.Disconnect, app.TxPrompt?.Reason);
            Assert.True(app.ConnectionState(p.Id).IsConnected);
            app.TxPrompt = null;
            await app.RollbackTxAsync(p.Id);
            Assert.False(app.TxState(p.Id).Open);
            await app.RunAsync($"SELECT COUNT(*) FROM {table}");
            Assert.Equal("0", tab.Result!.Rows[0].Values[0]);
            await app.SetAutoCommitAsync(p.Id, true);
        }
        finally
        {
            await app.RunAsync($"DROP TABLE IF EXISTS {table}");
            await app.DisconnectAsync(p.Id, force: true);
        }
    });
}
