using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Schema;

namespace GodDB.App.Tests;

/// 실제 앱 화면 확인에서 찾은 문제들의 회귀 테스트 (모두 고치기 전에는 실패)
public class VisualCheckRegressionTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static (AppState App, ConnectionProfile Profile, QueryWorkspaceView View, Window Window) QuerySetup(double width = 1100, double height = 800)
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var p = TestEnv.Profile("local");
        app.Profiles.Add(p);
        TestEnv.FakeConnect(app, p.Id);
        app.SelectedProfileId = p.Id;
        var view = new QueryWorkspaceView { DataContext = app };
        var w = new Window { Width = width, Height = height, Content = view };
        w.Show();
        Pump();
        return (app, p, view, w);
    }

    /// AppState.RunScriptAsync 의 Finish 와 같은 순서로 "N번째 문장 실패" 상태를 만든다
    private static void FailAt(QueryTab tab, IReadOnlyList<QueryResult> results, string error)
    {
        tab.Running = true;
        Pump();
        tab.Results = [.. results];
        var index = Math.Max(0, results.ToList().FindLastIndex(r => r.Columns.Count > 0));
        tab.ResultIndex = index;
        tab.Result = results[index];
        tab.Error = error;
        tab.Running = false;
        Pump();
    }

    private const string LongHint =
        "3번째 문장에서 멈췄어요. (앞의 2개는 실행됨)\n\n" +
        "쿼리 실패: Unknown column 'nope' in 'field list'\n\n" +
        "해결 방법: 컬럼 이름이 맞는지 확인하세요. 사이드바에서 테이블을 펼치면 컬럼 목록을 볼 수 있어요. " +
        "대소문자 · 따옴표 · 별칭을 함께 확인하고, 다른 DB 의 테이블이면 DB 이름을 앞에 붙이세요. 마지막 줄까지 보여야 해요.";

    /// 1. 여러 문장 중 N번째 실패: Mac ResultPane 처럼 결과 탭(앞 결과가 2개 이상일 때) + 오류 + 고른 앞 결과. 아래 줄(StatusBar)은 Mac 도 오류일 때 없다.
    [Fact]
    public Task ScriptFailure_ShowsResultTabs_Error_AndEarlierResult_LikeMac() => UiThread.Run(() =>
    {
        var (app, _, view, w) = QuerySetup();
        var id = app.AddTab("select 1;\nselect 2;\nselect nope")!.Value;
        Pump();
        var tab = app.Tab(id)!;
        var pane = view.TabView!.Results;
        var a = new QueryResult(["id"], [["1"], ["2"], ["3"]], 0.005) { Sql = "SELECT id FROM t" };
        var b = new QueryResult(["n"], [["1"]], 0.005) { Sql = "SELECT 2 AS n" };
        FailAt(tab, [a, b], LongHint);
        Assert.True(pane.TabsBar.IsEffectivelyVisible);
        Assert.Equal(["1 SELECT 3행", "2 SELECT 1행"], pane.TabsBar.Titles);
        Assert.True(pane.ErrorText.IsEffectivelyVisible);
        Assert.True(pane.Grid.IsEffectivelyVisible);
        Assert.Single(pane.Grid.Rows);   // 마지막 결과 집합(b)을 먼저 보여 준다
        Assert.False(pane.Status.IsVisible);

        // 탭을 눌러 앞 결과로 바꿀 수 있다 (오류는 그대로)
        pane.TabsBar.OnSelect!(0);
        Pump();
        Assert.Equal(3, pane.Grid.Rows.Count);
        Assert.True(pane.ErrorText.IsEffectivelyVisible);
        Assert.True(pane.TabsBar.IsEffectivelyVisible);

        // 둘째 문장에서 실패 (앞 결과 1개): Mac 도 results.count > 1 일 때만 탭을 그리므로 탭 없이 오류 + 앞 결과
        FailAt(tab, [a], "2번째 문장에서 멈췄어요. (앞의 1개는 실행됨)\n\nx");
        Assert.False(pane.TabsBar.IsVisible);
        Assert.True(pane.ErrorText.IsEffectivelyVisible);
        Assert.Equal(3, pane.Grid.Rows.Count);
        Assert.False(pane.Status.IsVisible);
        w.Close();
    });

    /// 2. 오류 글이 160 높이를 넘으면 마지막 줄(해결 방법)이 잘려 보였다: Mac 처럼 body 크기(13) 고정폭 글꼴로,
    ///    넘치면 스크롤 막대를 늘 보여 아래가 더 있다는 걸 알 수 있게 한다.
    [Fact]
    public Task ScriptFailure_ErrorBlock_NotSilentlyClipped() => UiThread.Run(() =>
    {
        var (app, _, view, w) = QuerySetup(width: 520, height: 800);
        var id = app.AddTab("select 1;\nselect 2;\nselect nope")!.Value;
        Pump();
        var tab = app.Tab(id)!;
        var pane = view.TabView!.Results;
        var a = new QueryResult(["id"], [["1"]], 0.005) { Sql = "SELECT 1" };
        var b = new QueryResult(["id"], [["1"]], 0.005) { Sql = "SELECT 2" };
        FailAt(tab, [a, b], LongHint);
        w.UpdateLayout();
        var sv = pane.ErrorText.FindAncestorOfType<ScrollViewer>()!;
        Assert.True(sv.Bounds.Height <= 160.5, $"오류 영역 {sv.Bounds.Height}");
        // 다 들어가거나, 아니면 스크롤 막대가 늘 보여야 한다 (자동 숨김이면 마지막 줄이 그냥 잘려 보임)
        var fits = sv.Extent.Height <= sv.Viewport.Height + 0.5;
        Assert.True(fits || (!sv.AllowAutoHide && sv.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled),
            $"extent {sv.Extent.Height} viewport {sv.Viewport.Height} autoHide {sv.AllowAutoHide}");
        // 맨 아래로 스크롤하면 마지막 줄까지 보인다
        sv.Offset = new Vector(0, sv.Extent.Height);
        w.UpdateLayout();
        Assert.True(sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 0.5);
        Assert.Equal(13, pane.ErrorText.FontSize);   // Mac .body
        w.Close();
    });

    /// 3. 접속 직후 자동 커밋 체크박스가 꺼져 보이던 문제: Mac 은 접속마다 자동 커밋 켜짐이 기본
    [Fact]
    public Task AutoCommit_CheckedRightAfterConnect() => UiThread.Run(() =>
    {
        Assert.True(new GodDB.Core.Drivers.TransactionState().AutoCommit);
        var (app, p, view, w) = QuerySetup();
        app.AddTab("select 1");
        Pump();
        view.TabView!.RefreshToolbar();
        Assert.True(view.TabView.Tx.IsVisible);
        Assert.True(app.TxState(p.Id).AutoCommit);
        Assert.True(view.TabView.Tx.AutoCommitChecked);
        Assert.True(app.TxState(Guid.NewGuid()).AutoCommit);
        w.Close();
    });

    /// 4. Redis: 접속 직후 사이드바 DB 선택 칸이 비어 있던 문제 (목록을 받은 뒤 현재 DB 를 골라 보여야 한다)
    [Fact]
    public Task RedisSidebar_DbBox_ShowsCurrentDbAfterListLoads() => UiThread.Run(() =>
    {
        var ws = RedisWorkspaceTests.Offline();
        var side = new RedisSidebarSection { DataContext = ws };
        var w = new Window { Width = 300, Height = 600, Content = side };
        w.Show();
        Pump();
        var box = side.FindControl<ComboBox>("DbBox")!;
        // LoadDatabasesAsync 와 같은 순서: 목록 → 현재 DB (db0 이라 Db 값은 그대로)
        ws.Databases = [new RedisDbItem(0, 0), new RedisDbItem(3, 1204)];
        ws.Db = 0;
        Pump();
        Assert.Equal(new RedisDbItem(0, 0), box.SelectedItem);
        Assert.Equal(new RedisDbItem(0, 0), ws.SelectedDatabase);
        // 다시 읽어도 (새 목록 객체) 선택이 유지된다
        ws.Databases = [new RedisDbItem(0, 5), new RedisDbItem(3, 1204)];
        Pump();
        Assert.Equal(new RedisDbItem(0, 5), ws.SelectedDatabase);
        Assert.Equal(new RedisDbItem(0, 5), box.SelectedItem);
        // 프로필 DB 가 db3 이면 db3 이 보인다
        ws.Databases = [new RedisDbItem(0, 5), new RedisDbItem(3, 1205)];
        ws.Db = 3;
        Pump();
        Assert.Equal(new RedisDbItem(3, 1205), box.SelectedItem);
        ws.Db = 0;   // 콘솔 SELECT 등으로 DB 가 바뀌어도 따라간다
        Pump();
        Assert.Equal(new RedisDbItem(0, 5), box.SelectedItem);
        w.Close();
    });

    /// 5. 쿼리 탭: 편집기와 결과 사이에 보이는 끌기 경계선 (Mac ResizeHandle: 1pt 구분선 + 위아래 3pt, 편집기 기본 높이 220)
    [Fact]
    public Task QueryTab_EditorResultsSplitter_VisibleAndDraggable() => UiThread.Run(() =>
    {
        var (app, _, view, w) = QuerySetup();
        app.AddTab("select 1");
        Pump();
        var tv = view.TabView!;
        var splitter = tv.GetVisualDescendants().OfType<GridSplitter>().First(s => s.ResizeDirection == GridResizeDirection.Rows);
        var grid = (Grid)splitter.Parent!;
        Assert.Equal(220, grid.RowDefinitions[0].Height.Value);
        Assert.Equal(80, grid.RowDefinitions[0].MinHeight);
        Assert.Equal(140, grid.RowDefinitions[2].MinHeight);
        Assert.True(splitter.Bounds.Height >= 5, $"잡을 수 있는 높이 {splitter.Bounds.Height}");
        // 구분선 색이 실제로 칠해져 있어야 한다 (배경과 같은 색 / 투명이면 경계가 안 보인다)
        var line = splitter.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Bounds.Height is > 0 and <= 1.5 && b.Bounds.Width > 100);
        Assert.NotNull(line);
        Assert.True(line!.TryFindResource("DividerBrush", line.ActualThemeVariant, out var d));
        var divider = Assert.IsAssignableFrom<ISolidColorBrush>(d);
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(line!.Background);
        Assert.Equal(divider.Color, brush.Color);
        w.Close();
    });
}
