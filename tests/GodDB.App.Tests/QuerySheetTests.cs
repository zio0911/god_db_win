using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using GodDB.App.Services;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Ai;
using GodDB.Core.Audit;
using GodDB.Core.Platform;
using GodDB.Core.Results;
using GodDB.Core.Schema;
using GodDB.Core.Sql;
using GodDB.Core.Dialects;
using static GodDB.Core.L10n;

namespace GodDB.App.Tests;

/// 쿼리 화면에서 여는 시트들 (내보내기 · 튜닝 · CSV 가져오기 · 히스토리 · 빠른 찾기). headless, 화면을 띄우지 않는다.
public class QuerySheetTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static List<string> Texts(Control root) =>
        root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    private static List<Button> Buttons(Control root) => root.GetLogicalDescendants().OfType<Button>().ToList();

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Key(Control target, Key key) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });

    private sealed class RecordingDialogs : NullDialogs
    {
        public List<string> SaveNames { get; } = [];
        public bool Confirm { get; set; }
        public override Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter> filters)
        {
            SaveNames.Add(suggestedName);
            return Task.FromResult<string?>(null);
        }
        public override Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel, bool destructive = false) =>
            Task.FromResult(Confirm);
    }

    private static (AppState App, ConnectionProfile Profile) Connected(TestEnv env, IReadOnlyList<TableMeta>? tables = null)
    {
        var app = env.NewApp();
        var p = TestEnv.Profile("p");
        app.Profiles.Add(p);
        TestEnv.FakeConnect(app, p.Id);
        if (tables != null) app.UpdateConnection(p.Id, c => c.Tables = [.. tables]);
        app.SelectedProfileId = p.Id;
        return (app, p);
    }

    private static TableMeta Member => new("tb_member", "회원",
        [new ColumnMeta("member_id", "int", false), new ColumnMeta("email", "varchar(100)", true)]);

    private static TableMeta Orders => new("orders", "", [new ColumnMeta("order_id", "int", false)]);

    [Fact]
    public void Registry_UsesRealSheets()
    {
        Assert.Equal(typeof(ExportSheet), SheetRegistry.Sheets[nameof(AppState.ExportRequest)]);
        Assert.Equal(typeof(TuningSheet), SheetRegistry.Sheets[nameof(AppState.TuningJob)]);
        Assert.Equal(typeof(CsvImportWindow), SheetRegistry.Sheets[nameof(AppState.ImportingTable)]);
        Assert.Equal(typeof(HistoryWindow), SheetRegistry.Sheets[nameof(AppState.ShowHistory)]);
        Assert.Equal(typeof(QuickOpenWindow), SheetRegistry.Sheets[nameof(AppState.ShowQuickOpen)]);
    }

    // MARK: - 내보내기

    private static TabExportRequest ExportReq(bool paged)
    {
        var result = new QueryResult(["member_id", "email"], [new string?[] { "1", "a" }, new string?[] { "2", null }], 0)
        {
            Sql = "SELECT member_id, email FROM tb_member",
            PageSize = paged ? 2 : 0,
            HasMore = paged,
        };
        var request = ExportRequest.Prepare(result, DatabaseKind.Mysql, [], new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero))!;
        return new TabExportRequest(Guid.NewGuid(), request);
    }

    [Fact]
    public Task Export_ChoiceDrivesOptions_AndContinueRemembersFormat() => UiThread.Run(() =>
    {
        Prefs.Set(ExportChoice.LastFormatKey, "csv");
        var env = new TestEnv();
        var app = env.NewApp();
        var dialogs = new RecordingDialogs();
        app.Dialogs = dialogs;
        var req = ExportReq(paged: true);
        app.ExportRequest = req;
        var w = new ExportSheet(app, req);
        w.Show();
        Pump();
        Assert.True(w.Bounds.Height > 0);
        Assert.Equal(ExportFormat.Csv, w.Choice.Format);
        Assert.True(w.Choice.FetchAll);
        // 정렬 기준이 없으니 전체 내보내기 경고
        Assert.True(w.UnstableWarningVisible);
        Assert.Contains(w.BomBox, w.OptionsPanel.Children);
        Assert.Equal(6, w.GetLogicalDescendants().OfType<SheetCard>().Count() - 2);

        w.SetFetchAll(false);
        Assert.False(w.UnstableWarningVisible);

        w.SetFormat(ExportFormat.Sql);
        Assert.DoesNotContain(w.BomBox, w.OptionsPanel.Children);
        Assert.Contains(w.TableNameBox, w.GetLogicalDescendants().OfType<TextBox>());
        Assert.Equal("tb_member", w.TableNameBox.Text);
        w.TableNameBox.Text = "  ";
        Assert.False(w.ContinueButton.IsEnabled);
        w.TableNameBox.Text = "t2";
        Assert.True(w.ContinueButton.IsEnabled);

        w.SetFormat(ExportFormat.Json);
        Assert.Contains(ExportFormat.Json.Hint()!, Texts(w.OptionsPanel));

        w.SetFormat(ExportFormat.Xlsx);
        w.Continue();
        Pump();
        Assert.False(w.IsVisible);
        Assert.Null(app.ExportRequest);
        Assert.Equal(["tb_member_20260102_120405.xlsx"], dialogs.SaveNames);
        Assert.Equal("xlsx", Prefs.GetString(ExportChoice.LastFormatKey));

        // 다음에 열면 마지막 형식 · 한 페이지뿐이면 전체 카드는 꺼짐
        var single = ExportReq(paged: false);
        var w2 = new ExportSheet(app, single);
        Assert.Equal(ExportFormat.Xlsx, w2.Choice.Format);
        Assert.False(w2.Choice.FetchAll);
        w2.SetFetchAll(true);
        Assert.False(w2.Choice.FetchAll);
        Prefs.Remove(ExportChoice.LastFormatKey);
    });

    // MARK: - 튜닝

    private const string Answer = """
        ## 요약
        email 에 인덱스가 없어 전체 스캔해요.

        ## 개선 SQL
        ```sql
        SELECT member_id FROM tb_member WHERE email = 'a'
        ```

        ## 추천 인덱스
        ```sql
        CREATE INDEX ix_member_email ON tb_member (email);
        ```

        ## 설명
        인덱스를 쓰면 빨라져요.
        """;

    private static void SetPrivate(object o, string name, object? value) =>
        o.GetType().GetProperty(name)!.SetValue(o, value);

    [Fact]
    public Task Tuning_RendersParsedResult_AndNeverRuns() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var (app, p) = Connected(env);
        var tabId = app.AddTab("select 1")!.Value;
        var job = new TuningJob(tabId, "SELECT * FROM tb_member WHERE email = 'a'", "", "p");
        app.TuningJob = job;
        var w = new TuningSheet(app, job);
        w.Show();
        Pump();
        // 진행 중
        Assert.Contains(Tr("테이블 구조와 실행 계획을 모으는 중…"), Texts(w.Body));
        Assert.Contains(Buttons(w), b => (b.Content as string) == Tr("취소"));

        SetPrivate(job, nameof(TuningJob.Result), TuningResult.Parse(Answer));
        SetPrivate(job, nameof(TuningJob.AfterWarnings), new List<PlanAnalyzer.Warning>());
        SetPrivate(job, nameof(TuningJob.Phase), TuningPhase.Done);
        SetPrivate(job, nameof(TuningJob.Finished), job.Started.AddSeconds(3));
        w.Render();
        Pump();
        var texts = Texts(w.Body);
        Assert.Contains("email 에 인덱스가 없어 전체 스캔해요.", texts);
        Assert.Contains(Tr("개선 SQL"), texts);
        Assert.Contains(Tr("추천 인덱스"), texts);
        Assert.Contains("CREATE INDEX ix_member_email ON tb_member (email);", texts);
        Assert.Contains("인덱스를 쓰면 빨라져요.", texts);
        Assert.Contains(Tr("실행 계획 주의사항 (전 → 후)"), texts);
        Assert.Contains(Tr("개선 SQL") + " · " + Tr("주의 없음"), texts);
        Assert.Contains(Buttons(w), b => (b.Content as string) == Tr("다시 튜닝"));

        // 새 탭으로: 실행하지 않고 SQL 만 새 탭에
        var before = app.Tabs.Count;
        var newTab = Buttons(w.Body).First(b => (b.Content as string)?.EndsWith(Tr("새 탭으로")) == true);
        Click(newTab);
        Assert.Equal(before + 1, app.Tabs.Count);
        var created = app.Tabs.Last();
        Assert.Equal("SELECT member_id FROM tb_member WHERE email = 'a';\n", created.Text);
        Assert.Null(created.Result);
        Assert.False(created.Running);

        // 편집기에 넣기: 원래 탭에 덧붙인다
        var insert = Buttons(w.Body).First(b => (b.Content as string)?.EndsWith(Tr("편집기에 넣기")) == true);
        Click(insert);
        Assert.Contains("SELECT member_id FROM tb_member WHERE email = 'a'", app.Tab(tabId)!.Text);
        Assert.False(app.Tab(tabId)!.Running);

        Click(Buttons(w).First(b => (b.Content as string) == Tr("닫기")));
        Pump();
        Assert.Null(app.TuningJob);
    });

    // MARK: - CSV 가져오기

    [Fact]
    public Task CsvImport_MappingAndPreview() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var (app, _) = Connected(env, [Member]);
        var w = new CsvImportWindow(app, Member);
        w.Show();
        Pump();
        Assert.True(w.Bounds.Height > 0);
        Assert.False(w.ImportButton.IsEnabled);

        w.LoadText("Member_ID,EMAIL,memo\n1,a@example.com,x\n2,,y\n");
        Pump();
        Assert.Equal(["member_id", "email", null], w.Doc.Mapping);
        var boxes = w.MappingBoxes;
        Assert.Equal(3, boxes.Count);
        Assert.Equal([1, 2, 0], boxes.Select(b => b.SelectedIndex));
        var texts = Texts(w.PreviewGrid);
        Assert.Contains("Member_ID", texts);   // 머리글
        Assert.Contains("a@example.com", texts);
        Assert.Contains("NULL", texts);        // 빈 칸은 NULL
        Assert.Equal(Tr("%@행 가져오기", "2"), w.ImportButton.Content);
        Assert.True(w.ImportButton.IsEnabled);

        // 빈 칸은 NULL 끄기 → 빈 글자 그대로
        w.EmptyAsNullBox.IsChecked = false;
        Pump();
        Assert.DoesNotContain("NULL", Texts(w.PreviewGrid));

        // 열 매핑 바꾸기
        w.MappingBoxes[1].SelectedIndex = 0;
        Assert.Null(w.Doc.Mapping[1]);
        w.MappingBoxes[0].SelectedIndex = 0;
        Assert.False(w.ImportButton.IsEnabled);   // 넣을 컬럼이 하나도 없음

        // 머리글 끄기 → 순서대로 다시 맞추고 3행
        w.HeaderBox.IsChecked = false;
        Pump();
        Assert.Equal(["member_id", "email", null], w.Doc.Mapping);
        Assert.Equal(Tr("%@행 가져오기", "3"), w.ImportButton.Content);

        // 구분자 바꾸기
        w.LoadText("member_id;email\n1;a\n");
        Assert.Equal(2, w.DelimiterBox.SelectedIndex);   // 세미콜론 자동 감지
        w.Close();
    });

    // MARK: - 히스토리

    [Fact]
    public Task History_FilterAndOpen() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var (app, p) = Connected(env);
        var other = Guid.NewGuid();
        env.History.Append(new HistoryEntry(p.Id, "p", "shop", "SELECT * FROM orders", 12, true, "3행"));
        env.History.Append(new HistoryEntry(p.Id, "p", "shop", "SELECT * FROM tb_member", 5, true));
        env.History.Append(new HistoryEntry(other, "q", "", "SELECT * FROM orders WHERE 1=0", 0, false));
        var tabId = app.AddTab("")!.Value;
        var w = new HistoryWindow(app, true);
        w.Show();
        Pump();
        Assert.True(w.Bounds.Height > 0);
        Assert.Equal(2, w.Visible.Count);          // 이 접속만
        w.OnlyThisConnection.IsChecked = false;
        Assert.Equal(3, w.Visible.Count);
        w.SearchBox.Text = "orders";
        Assert.Equal(2, w.Visible.Count);
        w.SearchBox.Text = "orders 1=0";           // 여러 단어는 모두 포함
        Assert.Single(w.Visible);
        w.SearchBox.Text = "없는말";
        Assert.Empty(w.Visible);
        Assert.False(w.List.IsVisible);

        w.SearchBox.Text = "tb_member";
        w.OnlyThisConnection.IsChecked = true;
        var e = Assert.Single(w.Visible);
        w.List.SelectedItem = e;
        w.Open(e);
        Pump();
        Assert.False(w.IsVisible);
        Assert.Contains("SELECT * FROM tb_member;", app.Tab(tabId)!.Text);
    });

    [Fact]
    public Task History_ClearAsksFirst() => UiThread.Run(async () =>
    {
        var env = new TestEnv();
        var (app, p) = Connected(env);
        var dialogs = new RecordingDialogs();
        app.Dialogs = dialogs;
        env.History.Append(new HistoryEntry(p.Id, "p", "shop", "SELECT 1", 1, true));
        var w = new HistoryWindow(app, true);
        w.Show();
        Pump();
        var clear = Buttons(w).First(b => (b.Content as string) == Tr("기록 지우기…"));
        Click(clear);
        await Task.Delay(10);
        Pump();
        Assert.Single(env.History.All());
        dialogs.Confirm = true;
        Click(clear);
        await Task.Delay(10);
        Pump();
        Assert.Empty(env.History.All());
        Assert.Empty(w.Visible);
        Assert.False(clear.IsEnabled);
        w.Close();
    });

    // MARK: - 빠른 찾기

    [Fact]
    public Task QuickOpen_KeyboardNavigationAndOpen() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var (app, _) = Connected(env, [Member, Orders]);
        app.ShowQuickOpen = true;
        var w = new QuickOpenWindow(app, true);
        w.Show();
        Pump();
        Assert.True(w.Bounds.Height > 0);
        // 검색어가 없으면 컬럼은 빼고
        Assert.Equal(["tb_member", "orders"], w.Results.Select(i => i.Name));
        Assert.Equal(w.Results[0].Id, w.SelectedId);

        w.SearchBox.Text = "id";
        Assert.All(w.Results, i => Assert.Equal(QuickOpen.Kind.Column, i.Kind));
        w.SearchBox.Text = "ord";
        Assert.Equal("orders", w.Results[0].Name);
        Assert.Equal(w.Results[0].Id, w.SelectedId);
        var count = w.Results.Count;
        Assert.True(count >= 2);

        Key(w.SearchBox, Avalonia.Input.Key.Down);
        Assert.Equal(w.Results[1].Id, w.SelectedId);
        Assert.Same(w.Results[1], w.List.SelectedItem);
        for (var i = 0; i < count + 3; i++) Key(w.SearchBox, Avalonia.Input.Key.Down);
        Assert.Equal(w.Results[^1].Id, w.SelectedId);   // 끝에서 멈춤
        for (var i = 0; i < count + 3; i++) Key(w.SearchBox, Avalonia.Input.Key.Up);
        Assert.Equal(w.Results[0].Id, w.SelectedId);

        Key(w.SearchBox, Avalonia.Input.Key.Enter);
        Pump();
        Assert.Equal("orders", w.OpenedItem?.Name);
        Assert.False(w.IsVisible);
        Assert.Contains(app.Tabs, t => t.Text.Contains("orders"));
    });

    [Fact]
    public Task QuickOpen_EscapeCloses_AndEmptyWhenDisconnected() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var w = new QuickOpenWindow(app, true);
        w.Show();
        Pump();
        Assert.Empty(w.Results);
        Assert.Contains(Tr("먼저 접속을 연결하세요"), Texts(w));
        Key(w.SearchBox, Avalonia.Input.Key.Escape);
        Pump();
        Assert.False(w.IsVisible);
    });

    // MARK: - 메인 창에서 AppState 로 열고 닫기

    [Fact]
    public Task Sheets_OpenFromMainWindow() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var (app, _) = Connected(env, [Member]);
        var w = new MainWindow(app);
        w.Show();
        Pump();
        app.ShowHistory = true;
        app.ShowQuickOpen = true;
        app.ImportingTable = Member;
        app.ExportRequest = ExportReq(paged: true);
        Pump();
        Assert.IsType<HistoryWindow>(w.OpenSheets[nameof(AppState.ShowHistory)]);
        Assert.IsType<QuickOpenWindow>(w.OpenSheets[nameof(AppState.ShowQuickOpen)]);
        Assert.IsType<CsvImportWindow>(w.OpenSheets[nameof(AppState.ImportingTable)]);
        Assert.IsType<ExportSheet>(w.OpenSheets[nameof(AppState.ExportRequest)]);
        foreach (var s in w.OpenSheets.Values.ToList()) s.Close();
        Pump();
        Assert.False(app.ShowHistory);
        Assert.False(app.ShowQuickOpen);
        Assert.Null(app.ImportingTable);
        Assert.Null(app.ExportRequest);
        w.Close();
    });
}
