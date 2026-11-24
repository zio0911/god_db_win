using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using GodDB.App.Controls;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Results;
using GodDB.Core.Schema;
using GodDB.Core.Sql;

namespace GodDB.App.Tests;

/// SQL 편집기 (Tab 자동완성 · LF · INSERT 힌트 · 주석 · 정렬 · 문장 강조)
public class SqlEditorTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static readonly List<TableMeta> Tables =
    [
        new("tb_member", columns: [new ColumnMeta("id", "int", false, columnKey: "PRI"), new ColumnMeta("email", "varchar(50)", true)], indexes: [new IndexMeta("PRIMARY", true, ["id"], primary: true)]),
        new("tb_order", columns: [new ColumnMeta("order_id", "int", false), new ColumnMeta("member_id", "int", true)]),
    ];

    internal static (Window, SqlEditor) HostFor(string text = "") => Host(text);
    internal static void PressTabFor(SqlEditor editor) => PressTab(editor);

    private static (Window, SqlEditor) Host(string text = "")
    {
        var editor = new SqlEditor { SchemaProvider = () => (Tables, []) };
        var w = new Window { Width = 700, Height = 400, Content = editor };
        w.Show();
        editor.SetSqlText(text);
        editor.CaretOffset = editor.SqlText.Length;
        Pump();
        return (w, editor);
    }

    private static void PressTab(SqlEditor editor) =>
        editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, Source = editor.TextArea });

    [Fact]
    public Task Tab_IndentsWhenNothingTyped() => UiThread.Run(() =>
    {
        var (w, editor) = Host("SELECT 1\n");
        PressTab(editor);
        Assert.IsType<TabAction.Indent>(editor.LastTabAction);
        Assert.Equal("SELECT 1\n    ", editor.SqlText);
        Assert.Equal(editor.SqlText.Length, editor.CaretOffset);
        w.Close();
    });

    [Fact]
    public Task Tab_CompletesSingleCandidate_KeepsCase() => UiThread.Run(() =>
    {
        var (w, editor) = Host("sel");
        PressTab(editor);
        Assert.IsType<TabAction.Replace>(editor.LastTabAction);
        Assert.Equal("select", editor.SqlText);
        Assert.Equal(6, editor.CaretOffset);

        editor.SetSqlText("SELECT * FROM tb_m");
        editor.CaretOffset = editor.SqlText.Length;
        PressTab(editor);
        Assert.Equal("SELECT * FROM tb_member", editor.SqlText);

        // 별칭 뒤 점: 그 테이블의 컬럼
        editor.SetSqlText("SELECT m.em FROM tb_member m");
        editor.CaretOffset = "SELECT m.em".Length;
        PressTab(editor);
        Assert.Equal("SELECT m.email FROM tb_member m", editor.SqlText);
        w.Close();
    });

    [Fact]
    public Task Tab_ShowsList_ThenCommits() => UiThread.Run(() =>
    {
        var (w, editor) = Host("SELECT * FROM tb_");
        PressTab(editor);
        var list = Assert.IsType<TabAction.ShowList>(editor.LastTabAction);
        Assert.Contains("tb_member", list.Candidates);
        Assert.Contains("tb_order", list.Candidates);
        Assert.NotNull(editor.CompletionCandidates);
        var index = editor.CompletionCandidates!.ToList().IndexOf("tb_order");
        editor.CommitCompletion(index);
        Assert.Equal("SELECT * FROM tb_order", editor.SqlText);
        Assert.Null(editor.CompletionCandidates);
        w.Close();
    });

    [Fact]
    public Task Tab_BeepsWithoutCandidates_AndLeavesSelectionToDefault() => UiThread.Run(() =>
    {
        var (w, editor) = Host("SELECT zzqqxx");
        var beeps = 0;
        editor.Beeped += () => beeps++;
        PressTab(editor);
        Assert.IsType<TabAction.Beep>(editor.LastTabAction);
        Assert.Equal(1, beeps);
        Assert.Equal("SELECT zzqqxx", editor.SqlText);
        // 선택 영역이 있으면 편집기 기본 동작
        editor.Select(0, 6);
        Assert.Null(editor.HandleTab());
        w.Close();
    });

    [Fact]
    public Task Document_StaysLf_OnInsertPasteAndSet() => UiThread.Run(() =>
    {
        var (w, editor) = Host("");
        editor.Document.Insert(0, "SELECT 1;\r\nSELECT 2;\rSELECT 3;");
        Assert.DoesNotContain('\r', editor.Document.Text);
        Assert.Equal("SELECT 1;\nSELECT 2;\nSELECT 3;", editor.SqlText);
        // 줄바꿈 정리는 같은 되돌리기 단위에 들어간다
        editor.Document.UndoStack.Undo();
        Assert.Equal("", editor.Document.Text);
        editor.SetSqlText("a\r\nb");
        Assert.Equal("a\nb", editor.Document.Text);
        // 새 문서로 바꿔도 붙는다
        var doc = new TextDocument("x\r\ny");
        editor.Document = doc;
        Assert.Equal("x\ny", editor.SqlText);
        Assert.Equal("x\ny", LineEndings.Normalize("x\r\ny"));
        w.Close();
    });

    [Fact]
    public Task InsertHints_AreVisualOnly() => UiThread.Run(() =>
    {
        {
            const string sql = "INSERT INTO tb_member (id, email) VALUES (1, 'a@example.com')";
            var (w, editor) = Host(sql);
            editor.RefreshDecorations();
            Pump();
            Assert.Equal(["id", "email"], editor.CurrentHints.Select(h => h.Column));
            Assert.Equal(sql.IndexOf("1,", StringComparison.Ordinal), editor.CurrentHints[0].Location);
            // 화면 줄에는 길이 0 짜리 요소로 들어가고, 문서 글은 그대로
            var line = editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
            Assert.Equal(2, line.Elements.Count(e => e.DocumentLength == 0));
            Assert.Equal(sql, editor.Document.Text);
            editor.SelectAll();
            Assert.Equal(sql, editor.TextArea.Selection.GetText());
            // 컬럼 목록이 없으면 스키마의 컬럼 순서
            editor.SetSqlText("INSERT INTO tb_order VALUES (1, 2)");
            Assert.Equal(["order_id", "member_id"], editor.CurrentHints.Select(h => h.Column));
            // Mac 과 같이 끄는 설정이 없다 (예전 설정 값이 남아 있어도 항상 보인다)
            Prefs.Set("GodDB.editor.insertHints", false);
            editor.SetSqlText(sql);
            Assert.Equal(["id", "email"], editor.CurrentHints.Select(h => h.Column));
            Prefs.Remove("GodDB.editor.insertHints");
            w.Close();
        }
    });

    [Fact]
    public Task CommentFormatAndStatementHighlight() => UiThread.Run(() =>
    {
        var (w, editor) = Host("SELECT 1;\nselect a,b from t where x=1;");
        editor.CaretOffset = 2;
        editor.ToggleComment();
        Assert.StartsWith("-- SELECT 1;", editor.SqlText);
        editor.ToggleComment();
        Assert.StartsWith("SELECT 1;", editor.SqlText);
        // 커서가 둘째 문장에 있으면 그 문장만 칠하고 정렬한다
        editor.Select(0, 0);
        editor.CaretOffset = editor.SqlText.Length - 3;
        var range = editor.StatementRange();
        Assert.NotNull(range);
        Assert.Equal("select a,b from t where x=1", editor.SqlText[range!.Value.Start..range.Value.End]);
        editor.FormatSql();
        Assert.StartsWith("SELECT 1;\n", editor.SqlText);
        Assert.Contains("FROM", editor.SqlText);
        // Ctrl+/ 키
        editor.CaretOffset = 0;
        editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Oem2, KeyModifiers = KeyModifiers.Control, Source = editor.TextArea });
        Assert.StartsWith("-- ", editor.SqlText);
        // 선택이 있으면 강조 없음
        editor.Select(0, 3);
        Assert.Null(editor.StatementRange());
        Assert.Equal("-- ", editor.SelectedSql());
        w.Close();
    });

    [Fact]
    public void Highlighter_TokensPerDialect()
    {
        var my = SqlHighlighter.Tokens("SELECT `a`, \"s\", 'x' -- c\n# m\n12 FROM t", DatabaseKind.Mysql);
        Assert.Contains(my, t => t.Kind == SqlTokenKind.Identifier && t.Length == 3);
        Assert.Equal(2, my.Count(t => t.Kind == SqlTokenKind.String));
        Assert.Equal(2, my.Count(t => t.Kind == SqlTokenKind.Comment));
        Assert.Contains(my, t => t.Kind == SqlTokenKind.Number);
        Assert.Equal(2, my.Count(t => t.Kind == SqlTokenKind.Keyword));
        var ms = SqlHighlighter.Tokens("SELECT TOP 5 [a b], \"c\" FROM #tmp", DatabaseKind.Mssql);
        Assert.Equal(2, ms.Count(t => t.Kind == SqlTokenKind.Identifier));
        Assert.Contains(ms, t => t.Kind == SqlTokenKind.Keyword && t.Length == 3);   // TOP
        Assert.DoesNotContain(ms, t => t.Kind == SqlTokenKind.Comment);
        var pg = SqlHighlighter.Tokens("SELECT $$ select 1 $$, x ILIKE 'a'", DatabaseKind.Postgres);
        Assert.Equal(2, pg.Count(t => t.Kind == SqlTokenKind.String));
        Assert.Equal(2, pg.Count(t => t.Kind == SqlTokenKind.Keyword));   // SELECT, ILIKE
    }
}

/// 결과 표 글자 (페이지 막대 · 결과 탭 · 셀 모양)
public class QueryUiTests
{
    private static QueryResult Paged(int page, int rows, bool more, int? total = null)
    {
        var r = new QueryResult(["id"], Enumerable.Range(0, rows).Select(i => (IReadOnlyList<string?>)[$"{i}"]), 0.0123)
        {
            Page = page, PageSize = 500, HasMore = more, TotalCount = total, Sql = "SELECT id FROM t",
        };
        return r;
    }

    [Fact]
    public void PagingText()
    {
        var r = Paged(1, 500, more: true);
        Assert.Equal("501–1,000행", QueryUi.PageRange(r));
        Assert.Equal("2페이지 · 더 있음", QueryUi.PageInfo(r));
        r.TotalCount = 12_000;
        Assert.Equal("/ 전체 12,000행 · 2/24페이지", QueryUi.PageInfo(r));
        Assert.Equal(23, QueryUi.LastPage(r));
        var end = Paged(2, 3, more: false);
        Assert.Equal("1,001–1,003행", QueryUi.PageRange(end));
        Assert.Equal("3페이지 · 끝", QueryUi.PageInfo(end));
        Assert.Equal("0행", QueryUi.PageRange(Paged(0, 0, false)));
        Assert.Equal("12ms", QueryUi.Elapsed(r));
        var a = Paged(0, 10, more: true);
        a.AppendMode = true;
        Assert.Equal("· 스크롤하면 더 불러와요", QueryUi.AppendInfo(a, false));
        Assert.Equal("· 화면 최대 200,000행 — 전체는 [내보내기]", QueryUi.AppendInfo(a, true));
        a.HasMore = false;
        Assert.Equal("· 끝", QueryUi.AppendInfo(a, false));
        Assert.Equal("CSV 내보내는 중 1,500 / 3,000행", QueryUi.ExportText(new ExportProgress(ExportFormat.Csv, 1500, 3000)));
    }

    [Fact]
    public void ResultTabNames()
    {
        var select = new QueryResult(["a"], [["1"], ["2"], ["3"]], 0) { Sql = "select a from t" };
        var update = new QueryResult([], [], 0, "2행 변경") { Sql = "UPDATE t SET a = 1" };
        Assert.Equal("1 SELECT 3행", QueryUi.ResultTabTitle(select, 0));
        Assert.Equal("2 UPDATE 2행 변경", QueryUi.ResultTabTitle(update, 1));
        Assert.Equal("3 USE 완료", QueryUi.ResultTabTitle(new QueryResult([], [], 0) { Sql = "use shop" }, 2));
    }

    [Fact]
    public void CellLooks()
    {
        Assert.Equal(new CellLook("abc", CellTone.Primary, false, false), QueryUi.Cell(CellState.Normal, "abc"));
        Assert.Equal(new CellLook("NULL", CellTone.Tertiary, false, false), QueryUi.Cell(CellState.Normal, null));
        Assert.Equal(new CellLook("x", CellTone.Orange, true, false), QueryUi.Cell(CellState.Modified, "x"));
        Assert.Equal(new CellLook("NULL", CellTone.Secondary, true, false), QueryUi.Cell(CellState.Modified, null));
        Assert.Equal(new CellLook("n", CellTone.Green, false, false), QueryUi.Cell(CellState.Inserted, "n"));
        Assert.Equal(new CellLook("(기본값)", CellTone.Tertiary, false, false), QueryUi.Cell(CellState.InsertedDefault, null));
        Assert.Equal(new CellLook("d", CellTone.Red, false, true), QueryUi.Cell(CellState.Deleted, "d"));
        Assert.Equal("a ⏎ b", QueryUi.Preview("a\nb"));
        var big = new string('x', 300);
        Assert.Equal(new string('x', 200) + "…", QueryUi.Preview(big));
    }

    [Fact]
    public void TabLabels()
    {
        var tab = new QueryTab(Guid.NewGuid(), "shop", 3);
        Assert.Equal("쿼리 3", QueryUi.TabTitle(tab));
        Assert.Equal("local / shop", QueryUi.TabSubtitle("local", "shop"));
        Assert.Equal("삭제된 접속", QueryUi.TabSubtitle(null, ""));
        tab.FilePath = "/tmp/a.sql";
        tab.SavedText = "x";
        tab.Text = "y";
        Assert.Equal("● 쿼리 3", QueryUi.TabTitle(tab));
    }
}

/// 쿼리 화면 전체 (탭 바 · 탭 바꾸기 · 결과 영역 · 편집 막대 · 시트)
public class QueryWorkspaceViewTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static (AppState App, ConnectionProfile Profile, QueryWorkspaceView View, Window Window) Setup(Action<AppState, ConnectionProfile>? before = null)
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var p = TestEnv.Profile("local");
        app.Profiles.Add(p);
        TestEnv.FakeConnect(app, p.Id);
        app.SelectedProfileId = p.Id;
        before?.Invoke(app, p);
        var view = new QueryWorkspaceView { DataContext = app };
        var w = new Window { Width = 1100, Height = 800, Content = view };
        w.Show();
        Pump();
        return (app, p, view, w);
    }

    [Fact]
    public Task EmptyState_ThenTabs_WithChips() => UiThread.Run(() =>
    {
        var (app, p, view, w) = Setup();
        Assert.Empty(view.Chips);
        Assert.False(view.FindControl<ContentControl>("Body")!.IsVisible);
        Assert.Equal("열린 쿼리 탭이 없어요", view.FindControl<TextBlock>("EmptyTitle")!.Text);
        app.AddTab("select 1");
        app.AddTab("select 2");
        Pump();
        Assert.Equal(2, view.Chips.Count);
        Assert.NotNull(view.TabView);
        Assert.Equal("select 2", view.TabView!.Editor.SqlText);
        // 연결이 끊기면 회색 점 (칩이 다시 만들어진다)
        var before = view.Chips[0];
        app.UpdateConnection(p.Id, c => c.State = ConnectionState.Disconnected);
        Pump();
        Assert.NotSame(before, view.Chips[0]);
        w.Close();
    });

    [Fact]
    public Task SwitchingTabs_KeepsTextAndSelection() => UiThread.Run(() =>
    {
        var (app, _, view, w) = Setup();
        var a = app.AddTab("SELECT * FROM a")!.Value;
        Pump();
        var editor = view.TabView!.Editor;
        editor.Select(7, 1);
        var b = app.AddTab("SELECT 2")!.Value;
        Pump();
        Assert.Equal("SELECT 2", editor.SqlText);
        editor.Document.Insert(editor.SqlText.Length, " -- b");
        Assert.Equal("SELECT 2 -- b", app.Tab(b)!.Text);
        app.SelectTab(a);
        Pump();
        Assert.Equal("SELECT * FROM a", editor.SqlText);
        Assert.Equal("*", editor.SelectedSql());
        app.SelectTab(b);
        Pump();
        Assert.Equal("SELECT 2 -- b", editor.SqlText);
        // 밖에서 탭 글을 바꾸면 (AI 가 만든 SQL 넣기 등) 편집기에 반영
        app.InsertIntoTab(b, "SELECT 3", "AI");
        Pump();
        Assert.Contains("-- AI\nSELECT 3;", editor.SqlText);
        w.Close();
    });

    [Fact]
    public Task CrlfFileTab_IsNormalized_WithoutDirtyMark() => UiThread.Run(() =>
    {
        var (app, _, view, w) = Setup();
        var id = app.AddTab("SELECT 1;\r\nSELECT 2;\r\n")!.Value;
        var tab = app.Tab(id)!;
        tab.FilePath = "/tmp/x.sql";
        tab.SavedText = tab.Text;
        app.AddTab("other");
        app.SelectTab(id);
        Pump();
        Assert.Equal("SELECT 1;\nSELECT 2;\n", tab.Text);
        Assert.False(tab.IsDirty);
        Assert.Equal(tab.Text, view.TabView!.Editor.SqlText);
        w.Close();
    });

    private sealed class SaveTo(string path) : GodDB.App.Services.NullDialogs
    {
        public override Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<GodDB.App.Services.FileFilter> filters) =>
            Task.FromResult<string?>(path);
    }

    [Fact]
    public Task CrlfFile_SavesBackWithCrlf_EditorKeepsLf() => UiThread.Run(async () =>
    {
        var (app, _, view, w) = Setup();
        var path = Path.Combine(TestSetup.Root, $"crlf-{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(path, "SELECT 1;\r\nSELECT 2;\r\n");
        try
        {
            app.OpenSqlFile(path);
            Pump();
            var tab = app.SelectedTab!;
            // 편집기 · 탭 글은 LF (Core 오프셋 기준), 파일 그대로라 '바뀜' 표시 없음
            Assert.Equal("SELECT 1;\nSELECT 2;\n", tab.Text);
            Assert.False(tab.IsDirty);
            Assert.Equal(tab.Text, view.TabView!.Editor.SqlText);
            // 고쳐서 저장하면 원래 줄바꿈(CRLF)으로 쓴다 (Mac 은 글 그대로 저장 → 원래 줄바꿈 유지)
            view.TabView.Editor.Document.Insert(view.TabView.Editor.SqlText.Length, "SELECT 3;\n");
            Assert.True(tab.IsDirty);
            await app.SaveSelectedTabAsync();
            Assert.Equal("SELECT 1;\r\nSELECT 2;\r\nSELECT 3;\r\n", await File.ReadAllTextAsync(path));
            Assert.False(tab.IsDirty);
            Assert.DoesNotContain('\r', tab.Text);
            // 다른 이름으로 저장해도 그 탭의 줄바꿈을 따른다
            var other = Path.Combine(TestSetup.Root, $"crlf-{Guid.NewGuid():N}.sql");
            app.Dialogs = new SaveTo(other);
            await app.SaveSelectedTabAsync(saveAs: true);
            Assert.Equal("SELECT 1;\r\nSELECT 2;\r\nSELECT 3;\r\n", await File.ReadAllTextAsync(other));
            File.Delete(other);

            // 새 탭(파일 없음)은 LF 로 저장
            app.AddTab("SELECT 4;\nSELECT 5;");
            var lf = Path.Combine(TestSetup.Root, $"lf-{Guid.NewGuid():N}.sql");
            app.Dialogs = new SaveTo(lf);
            await app.SaveSelectedTabAsync();
            Assert.Equal("SELECT 4;\nSELECT 5;", await File.ReadAllTextAsync(lf));
            File.Delete(lf);

            // LF 파일은 LF 그대로
            var lfFile = Path.Combine(TestSetup.Root, $"lf-{Guid.NewGuid():N}.sql");
            await File.WriteAllTextAsync(lfFile, "SELECT 6;\n");
            app.OpenSqlFile(lfFile);
            app.SelectedTab!.Text += "SELECT 7;\n";
            await app.SaveSelectedTabAsync();
            Assert.Equal("SELECT 6;\nSELECT 7;\n", await File.ReadAllTextAsync(lfFile));
            File.Delete(lfFile);
        }
        finally
        {
            File.Delete(path);
            w.Close();
        }
    });

    [Fact]
    public Task QueryToolbar_HasNoEnvironmentOrReadOnlyBadge_KeepsProductionBanner() => UiThread.Run(() =>
    {
        var (app, p, view, w) = Setup((app, p) =>
        {
            var i = app.Profiles.IndexOf(p);
            app.Profiles[i] = p with { ReadOnly = true, Environment = ConnectionEnvironment.Production };
        });
        app.AddTab("select 1");
        Pump();
        var tv = view.TabView!;
        // 접속 환경 · 읽기전용 표시는 창 위 막대(Mac ConnectionBadge)에만 — 쿼리 도구 막대에는 없다
        Assert.Empty(tv.Toolbar.GetVisualDescendants().OfType<EnvironmentBadge>());
        Assert.DoesNotContain(tv.Toolbar.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "읽기전용" || t.Text == "운영");
        // 운영 접속 빨간 띠는 Mac QueryTabView 에도 있다
        Assert.Contains(tv.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "운영 접속 · local");
        w.Close();
    });

    [Fact]
    public Task RunningTab_ShowsCircularSpinners() => UiThread.Run(() =>
    {
        var (app, _, view, w) = Setup();
        var id = app.AddTab("select sleep(1)")!.Value;
        Pump();
        var tab = app.Tab(id)!;
        tab.Running = true;
        tab.RunStarted = DateTime.Now;
        Pump();
        // 탭 칩: 작은 스피너 (Mac ProgressView().controlSize(.mini))
        var chipSpinner = Assert.Single(view.Chips[0].GetVisualDescendants().OfType<ActivitySpinner>());
        Assert.Equal(10, chipSpinner.Width);
        Assert.True(chipSpinner.IsSpinning);
        // 결과 영역: 동그란 스피너, 막대 없음
        Assert.True(view.TabView!.Results.Running.Spinner.IsSpinning);
        Assert.Empty(view.TabView.GetVisualDescendants().OfType<ProgressBar>());
        tab.Running = false;
        Pump();
        Assert.Empty(view.Chips[0].GetVisualDescendants().OfType<ActivitySpinner>());
        Assert.False(view.TabView.Results.Running.Spinner.IsSpinning);
        w.Close();
    });

    [Fact]
    public Task ResultPane_ShowsResultsTabsErrorAndPaging() => UiThread.Run(() =>
    {
        var (app, _, view, w) = Setup();
        var id = app.AddTab("select 1")!.Value;
        Pump();
        var tab = app.Tab(id)!;
        var pane = view.TabView!.Results;
        var select = new QueryResult(["id", "name"], [["1", "a"], ["2", null]], 0.005) { Sql = "SELECT id, name FROM t", Page = 1, PageSize = 2, HasMore = true };
        var update = new QueryResult([], [], 0.001, "1행 변경") { Sql = "UPDATE t SET name='x'" };
        tab.Results = [update, select];
        tab.ResultIndex = 1;
        tab.Result = select;
        Pump();
        Assert.True(pane.TabsBar.IsVisible);
        Assert.Equal(["1 UPDATE 1행 변경", "2 SELECT 2행"], pane.TabsBar.Titles);
        Assert.Equal(2, pane.Grid.Rows.Count);
        Assert.Contains("3–4행", pane.Status.Texts);
        Assert.Contains("2페이지 · 더 있음", pane.Status.Texts);
        // 편집 대상이 아니면 (테이블 정보 없음) "편집 불가"
        Assert.True(pane.EditBar.IsVisible);
        Assert.StartsWith("편집 불가: ", pane.EditBar.Message);
        // 실행 중
        tab.Running = true;
        tab.RunStarted = DateTime.Now.AddSeconds(-3);
        Pump();
        Assert.True(pane.Running.IsVisible);
        Assert.Equal("실행 중… 3초", pane.Running.Text);
        // 둘째 문장에서 멈춤: 오류와 앞 결과
        tab.Running = false;
        tab.Error = "2번째 문장에서 멈췄어요. (앞의 1개는 실행됨)\n\nx";
        Pump();
        Assert.False(pane.Running.IsVisible);
        Assert.True(pane.ErrorText.IsVisible);
        Assert.StartsWith("2번째 문장에서 멈췄어요", pane.ErrorText.Text);
        Assert.True(pane.Grid.IsVisible);
        w.Close();
    });

    [Fact]
    public Task OneRowResult_OpensDetail_EditStylingAndPanel() => UiThread.Run(() =>
    {
        var (app, p, view, w) = Setup((app, p) =>
            app.UpdateConnection(p.Id, c => c.Tables =
            [
                new TableMeta("tb_member", columns: [new ColumnMeta("id", "int", false, columnKey: "PRI"), new ColumnMeta("email", "varchar(50)", true)], indexes: [new IndexMeta("PRIMARY", true, ["id"], primary: true)]),
            ]));
        var id = app.AddTab("SELECT id, email FROM tb_member")!.Value;
        Pump();
        var tab = app.Tab(id)!;
        var pane = view.TabView!.Results;
        tab.Result = new QueryResult(["id", "email"], [["7", "line1\nline2"]], 0) { Sql = "SELECT id, email FROM tb_member" };
        Pump();
        // 1행이면 상세가 바로 열린다
        Assert.Equal([0], pane.Grid.SelectedIds);
        Assert.True(pane.Detail.IsVisible);
        Assert.Equal("행 편집", pane.Detail.Title);
        Assert.Equal("1번째 행", pane.Detail.NumberText);
        Assert.Contains("11자", pane.Detail.CountTexts);
        Assert.StartsWith("tb_member 편집 가능", pane.EditBar.Message);
        // 셀 고치기 → 주황 / 새 행 → 초록 (기본값) / 삭제 표시
        app.SetCell(id, 0, 1, "new@example.com");
        Pump();
        Assert.Equal(CellState.Modified, pane.Grid.Edits.State(0, 1));
        Assert.Equal("new@example.com", pane.Grid.Rows[0].Values[1]);
        var newId = app.AddRow(id)!.Value;
        Pump();
        Assert.Equal(2, pane.Grid.Rows.Count);
        Assert.Equal(CellState.InsertedDefault, pane.Grid.Edits.State(newId, 0));
        app.ToggleDelete(id, [0]);
        Pump();
        Assert.Equal(CellState.Deleted, pane.Grid.Edits.State(0, 1));
        // TSV 복사 (머리글 포함)
        pane.Grid.Select([0]);
        Assert.Equal("id\temail\n7\tnew@example.com\n", pane.Grid.CopyText(ExportFormat.Tsv).Replace("\r\n", "\n"));
        Assert.Equal("(7)", pane.Grid.InListText(0));
        // 되돌리기
        app.RevertEdits(id);
        Pump();
        Assert.Single(pane.Grid.Rows);
        Assert.Equal("line1\nline2", pane.Grid.Rows[0].Values[1]);
        // 빠른 필터
        pane.Filter.Text = "zzz";
        Pump();
        Assert.Empty(pane.Grid.Rows);
        pane.Filter.Text = "LINE2";
        Pump();
        Assert.Single(pane.Grid.Rows);
        w.Close();
    });

    [Fact]
    public Task HeaderSort_CallsAppState_AndShowsNativeArrow() => UiThread.Run(() =>
    {
        var (app, _, view, w) = Setup();
        var id = app.AddTab("show tables")!.Value;
        Pump();
        var tab = app.Tab(id)!;
        var pane = view.TabView!.Results;
        tab.Result = new QueryResult(["n", "m"], [["b", "1"], ["a", "2"], [null, "3"]], 0) { Sql = "SHOW TABLES" };
        Pump();
        var grid = pane.Grid;
        Avalonia.Controls.DataGridColumnHeader Header(int i) =>
            grid.Grid.GetVisualDescendants().OfType<Avalonia.Controls.DataGridColumnHeader>()
                .First(h => ReferenceEquals(h.Content, grid.Grid.Columns[i].Header));
        Assert.Null(grid.SortDirection(grid.Grid.Columns[0]));
        Assert.DoesNotContain(":sortascending", Header(0).Classes);
        // 연결(세션)이 없으면 받아 온 행을 앱에서 정렬한다 (SHOW 처럼 감쌀 수 없는 결과)
        grid.OnSort!("n", true);
        Pump();
        Assert.Equal([null, "a", "b"], grid.Rows.Select(r => r.Values[0]));
        // 머리글 글자는 이름 그대로, 방향은 표의 기본 정렬 화살표 (Mac Table 처럼)
        Assert.Equal("n", grid.Grid.Columns[0].Header?.ToString());
        Assert.Equal(System.ComponentModel.ListSortDirection.Ascending, grid.SortDirection(grid.Grid.Columns[0]));
        Assert.Null(grid.SortDirection(grid.Grid.Columns[1]));
        Assert.Contains(":sortascending", Header(0).Classes);
        Assert.DoesNotContain(":sortdescending", Header(0).Classes);
        Assert.DoesNotContain(":sortascending", Header(1).Classes);
        grid.OnSort!("n", false);
        Pump();
        Assert.Equal("b", grid.Rows[0].Values[0]);
        Assert.Equal("n", grid.Grid.Columns[0].Header?.ToString());
        Assert.Equal(System.ComponentModel.ListSortDirection.Descending, grid.SortDirection(grid.Grid.Columns[0]));
        Assert.Contains(":sortdescending", Header(0).Classes);
        Assert.DoesNotContain(":sortascending", Header(0).Classes);
        Assert.DoesNotContain(grid.Grid.Columns, c => (c.Header?.ToString() ?? "").Contains('↑') || (c.Header?.ToString() ?? "").Contains('↓'));
        w.Close();
    });

    [Fact]
    public Task ReadOnlyProfile_NoTransactionBar_AndEditBlocked() => UiThread.Run(() =>
    {
        var (app, p, view, w) = Setup((app, p) =>
        {
            var i = app.Profiles.IndexOf(p);
            app.Profiles[i] = p with { ReadOnly = true, Environment = ConnectionEnvironment.Production };
        });
        var id = app.AddTab("select 1")!.Value;
        Pump();
        var tv = view.TabView!;
        Assert.False(tv.Tx.IsVisible);
        app.Tab(id)!.Result = new QueryResult(["a"], [["1"]], 0) { Sql = "select a from t" };
        Pump();
        Assert.Contains("읽기전용", tv.Results.EditBar.Message);
        w.Close();
    });

    [Fact]
    public Task TransactionBar_ShowsOpenChanges() => UiThread.Run(() =>
    {
        var (app, p, view, w) = Setup((app, p) => app.UpdateConnection(p.Id, c => c.Tx = new GodDB.Core.Drivers.TransactionState(true, false, 0)));
        app.AddTab("select 1");
        Pump();
        var tx = view.TabView!.Tx;
        Assert.True(tx.IsVisible);
        Assert.True(tx.AutoCommitChecked);
        app.UpdateConnection(p.Id, c => c.Tx = new GodDB.Core.Drivers.TransactionState(false, true, 3));
        Pump();
        Assert.False(tx.AutoCommitChecked);
        Assert.Equal("⟲ 트랜잭션 · 변경 3건", tx.PillText);
        w.Close();
    });

    [Fact]
    public Task TableActionSheet_DropNeedsName_RenamePreviewsSql() => UiThread.Run(() =>
    {
        var (app, p, _, w) = Setup();
        var drop = new TableActionSheet(app, new TableActionRequest(TableActionKind.Drop, p.Id, "tb_member"));
        Assert.False(drop.RunButton.IsEnabled);
        Assert.StartsWith("DROP TABLE `tb_member`", drop.SqlPreview.Text);
        drop.Input.Text = "tb_membe";
        Assert.False(drop.CanRun);
        drop.Input.Text = "tb_member";
        Pump();
        Assert.True(drop.CanRun);
        Assert.True(drop.RunButton.IsEnabled);
        var rename = new TableActionSheet(app, new TableActionRequest(TableActionKind.Rename, p.Id, "tb_member"));
        Assert.False(rename.CanRun);   // 같은 이름
        rename.Input.Text = "tb_user";
        Pump();
        Assert.True(rename.CanRun);
        Assert.StartsWith("RENAME TABLE `tb_member` TO `tb_user`", rename.SqlPreview.Text);
        var apply = new EditApplySheet(app, new EditApplyRequest(Guid.NewGuid(), ["DELETE FROM t WHERE id = '1'"]));
        Assert.True(apply.ApplyButton.IsEnabled);
        w.Close();
    });

    [Fact]
    public Task MainWindow_UsesRealSheets() => UiThread.Run(() =>
    {
        Assert.Equal(typeof(EditApplySheet), SheetRegistry.Sheets[nameof(AppState.EditApply)]);
        Assert.Equal(typeof(TableActionSheet), SheetRegistry.Sheets[nameof(AppState.TableAction)]);
    });
}

/// 화면 확인에서 찾은 문제: Redis 영역이 SQL 접속 · 미접속에도 보이던 것.
/// SidebarView.BindRedis() 가 영역의 DataContext 를 null(또는 RedisWorkspace)로 직접 넣기 때문에, 영역 자신에 건
/// IsVisible="{Binding ShowRedis}" 는 값을 못 얻고 기본값(보임)으로 돌아간다. 보이기 조건은 바깥 틀에 걸어야 한다.
/// (예전 테스트는 void 로 선언돼 UiThread.Run 의 Task 를 버려서 실패가 드러나지 않았다 — Task 를 돌려준다)
public class SidebarRedisVisibilityTests
{
    [Fact]
    public Task RedisSectionHiddenWhenNotRedis() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        env.Store.Save([TestEnv.Profile("a"), TestEnv.Profile("r", GodDB.Core.DatabaseKind.Redis)]);
        var app = env.NewApp();
        var view = new GodDB.App.Views.SidebarView { DataContext = new GodDB.App.ViewModels.SidebarViewModel(app) };
        var window = new Avalonia.Controls.Window { Content = view, Width = 300, Height = 800 };
        window.Show();
        var section = view.FindControl<GodDB.App.Views.RedisSidebarSection>("RedisSection")!;
        var search = view.FindControl<GodDB.App.Views.RedisKeySearchBar>("RedisSearch")!;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        // 실제 앱과 같은 조건: BindRedis 가 영역의 DataContext 를 직접 null 로 넣었다
        Assert.True(section.IsSet(Avalonia.StyledElement.DataContextProperty));
        Assert.Null(section.DataContext);
        // 접속 안 됨: Redis 영역도 "키 불러오는 중…" 도 보이면 안 된다
        Assert.False(section.IsEffectivelyVisible);
        Assert.False(search.IsEffectivelyVisible);
        app.SelectedProfileId = app.Profiles[1].Id;   // Redis 접속을 골라도 연결 전이면 숨김
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(section.IsEffectivelyVisible);
        // SQL 접속: 스키마 트리만, Redis 영역은 숨김
        app.SelectedProfileId = app.Profiles[0].Id;
        TestEnv.FakeConnect(app, app.Profiles[0].Id);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.False(section.IsEffectivelyVisible);
        Assert.False(search.IsEffectivelyVisible);
        window.Close();
    });
}
