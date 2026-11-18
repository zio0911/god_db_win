using Avalonia.Controls;
using Avalonia.Threading;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;

namespace GodDB.App.Tests;

/// Docker 테스트 MySQL 로 쿼리 화면 흐름 (실행 → 표 → 헤더 정렬 → 결과 편집 적용 → 여러 문장 · 중간 실패 · 실행 계획).
/// GODDB_IT_PORT 가 없으면 건너뛴다. 테이블 이름에는 매번 다른 꼬리를 붙이고 끝나면 지운다.
public class QueryWorkspaceIntegrationTests
{
    private static async Task Until(Func<bool> done, int seconds = 20)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.Now > end) throw new TimeoutException();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task RunSortEditAndScripts_ThroughTheView() => UiThread.Run(async () =>
    {
        var port = Environment.GetEnvironmentVariable("GODDB_IT_PORT");
        if (string.IsNullOrEmpty(port)) Assert.Skip("GODDB_IT_PORT 없음 — scripts/test-dbs.sh test 로 실행");
        var env = new TestEnv();
        var app = env.NewApp();
        var p = new ConnectionProfile
        {
            Name = "it-qw", Kind = DatabaseKind.Mysql, Host = "127.0.0.1", Port = int.Parse(port!),
            Username = "root", Database = "shop", UseTLS = false, SavePassword = false,
        };
        app.Profiles.Add(p);
        app.SessionPasswords[p.Id] = Environment.GetEnvironmentVariable("GODDB_IT_PASSWORD") ?? "goddbtest";
        var table = "it_qw_" + Guid.NewGuid().ToString("N")[..10];
        await app.ConnectAsync(p.Id);
        Assert.True(app.ConnectionState(p.Id).IsConnected, app.LastLog?.Message);
        var view = new QueryWorkspaceView { DataContext = app };
        var w = new Window { Width = 1100, Height = 800, Content = view };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var tab = app.SelectedTab!;
        var tv = view.TabView!;
        try
        {
            await app.RunAsync($"CREATE TABLE {table} (id INT PRIMARY KEY, name VARCHAR(20))");
            await app.RunAsync($"INSERT INTO {table} VALUES (1,'a'),(2,'b'),(3,'c')");
            Assert.Null(tab.Error);

            // 편집기에서 실행 (선택 없음, 문장 하나)
            tv.Editor.SetSqlText($"SELECT * FROM {table} ORDER BY id");
            tv.Run(explain: false);
            await Until(() => !tab.Running && tab.Result?.Sql?.StartsWith("SELECT * FROM " + table) == true);
            var pane = tv.Results;
            Assert.Equal(3, pane.Grid.Rows.Count);
            Assert.StartsWith(table + " 편집 가능", pane.EditBar.Message);

            // 헤더 정렬: 쿼리를 다시 실행 (서버 정렬)
            pane.Grid.OnSort!("id", false);
            await Until(() => !tab.PageLoading && tab.Result?.ServerSort != null);
            Assert.Equal("3", pane.Grid.Rows[0].Values[0]);
            Assert.Equal("id ↓", pane.Grid.Grid.Columns[0].Header);

            // 결과 편집 → 적용 (한 트랜잭션) → 다시 읽기
            var rowId = pane.Grid.Rows[0].Id;
            app.SetCell(tab.Id, rowId, 1, "zz");
            Dispatcher.UIThread.RunJobs();
            app.RequestApplyEdits(tab.Id);
            var request = Assert.IsType<EditApplyRequest>(app.EditApply);
            Assert.Contains("UPDATE", request.Statements[0]);
            await app.ApplyEditsAsync(request);
            await Until(() => !tab.Running && !tab.PageLoading && tab.Edits.IsEmpty);
            Assert.Contains(pane.Grid.Rows, r => r.Values[1] == "zz");

            // 여러 문장 (조회만이라 확인 없이 실행) → 결과 탭
            tv.Editor.SetSqlText($"SELECT 1 AS x;\nSELECT name FROM {table} ORDER BY id");
            tv.Run(explain: false);
            await Until(() => !tab.Running && tab.Results.Count == 2);
            Assert.Equal(2, pane.TabsBar.Titles.Count);
            Assert.Equal("2 SELECT 3행", pane.TabsBar.Titles[1]);

            // 중간 실패
            tv.Editor.SetSqlText($"SELECT 1;\nSELECT * FROM {table}_none");
            tv.Run(explain: false);
            await Until(() => !tab.Running && tab.Error != null);
            Assert.StartsWith("2번째 문장에서 멈췄어요", pane.ErrorText.Text);

            // 실행 계획
            tv.Editor.SetSqlText($"SELECT * FROM {table} WHERE name = 'a'");
            tv.Run(explain: true);
            await Until(() => !tab.Running && tab.IsPlan);
            Assert.True(pane.Plan.IsVisible);
        }
        finally
        {
            await app.RunAsync($"DROP TABLE IF EXISTS {table}");
            w.Close();
            await app.DisconnectAllAsync();
        }
    });
}
