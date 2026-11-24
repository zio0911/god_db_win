using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GodDB.App.Controls;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Results;
using GodDB.Core.Schema;

namespace GodDB.App.Tests;

/// Mac 1.4.0 결과 표 · 행 상세: 칸 그 자리 편집, 외래 키 따라가기, JSON 보기, 사이드바 선택 색 · 머리글 여백
public class Release140ResultTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static (AppState App, ConnectionProfile Profile, QueryWorkspaceView View, Window Window) Setup(IReadOnlyList<TableMeta> tables)
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var p = TestEnv.Profile("local");
        app.Profiles.Add(p);
        TestEnv.FakeConnect(app, p.Id);
        app.SelectedProfileId = p.Id;
        app.UpdateConnection(p.Id, c => c.Tables = [.. tables]);
        var view = new QueryWorkspaceView { DataContext = app };
        var w = new Window { Width = 1200, Height = 800, Content = view };
        w.Show();
        Pump();
        return (app, p, view, w);
    }

    private static ColumnMeta Col(string name, string type, bool pk = false) => new(name, type, nullable: !pk, columnKey: pk ? "PRI" : "");

    private static TableMeta Table(string name, ColumnMeta[] cols, string[] pk, ForeignKeyMeta[]? fks = null) =>
        new(name, columns: cols, indexes: [new IndexMeta("PRIMARY", true, pk)], foreignKeys: fks ?? []);

    private static readonly TableMeta Member = Table("tb_member",
        [Col("id", "int", pk: true), Col("email", "varchar(50)"), Col("status", "char(1)")], ["id"]);

    private static ResultPane Show(AppState app, QueryWorkspaceView view, string sql, string[] columns, string?[][] rows, out Guid tabId)
    {
        tabId = app.AddTab(sql)!.Value;
        Pump();
        app.Tab(tabId)!.Result = new QueryResult(columns, rows.Select(r => (IReadOnlyList<string?>)r), 0) { Sql = sql };
        Pump();
        view.UpdateLayout();
        Pump();
        return view.TabView!.Results;
    }

    /// 표 안 칸 (행 번호 · 컬럼 번호)의 창 좌표
    private static Point CellPoint(ResultGrid grid, Window w, int rowIndex, int column)
    {
        var row = grid.Grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.DataContext is GridRow g && g.Id == grid.Rows[rowIndex].Id);
        var cell = row.GetVisualDescendants().OfType<DataGridCell>().ElementAt(column);
        return cell.TranslatePoint(new Point(8, cell.Bounds.Height / 2), w)!.Value;
    }

    private static List<MenuItem> OpenMenu(ResultGrid grid)
    {
        var menu = grid.Grid.ContextMenu!;
        // 우클릭과 같은 경로 (ContextRequested → 메뉴 Opening 에서 항목을 채운다)
        grid.Grid.RaiseEvent(new ContextRequestedEventArgs());
        Pump();
        var items = menu.Items.OfType<MenuItem>().ToList();
        menu.Close();
        return items;
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void Key(Control target, Key key, KeyModifiers mods = KeyModifiers.None) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, Source = target });

    // MARK: - 칸 그 자리 편집

    [Fact]
    public Task InlineEdit_DoubleClickEnterEscTab() => UiThread.Run(() =>
    {
        var (app, _, view, w) = Setup([Member]);
        var pane = Show(app, view, "SELECT * FROM tb_member", ["id", "email", "status"],
                        [["1", "a@x.com", "A"], ["2", "two\nlines", "B"], ["3", null, "C"]], out var id);
        var grid = pane.Grid;
        Assert.Equal("tb_member 편집 가능 · 칸을 더블클릭해 바로 고치거나, 행을 고르면 오른쪽에서 고쳐요", pane.EditBar.Message);
        Assert.Equal([0, 1, 2], grid.EditableColumns.Order());
        // 더블클릭 → 그 칸이 입력란 (지금 값)
        var pt = CellPoint(grid, w, 0, 1);
        w.MouseDown(pt, MouseButton.Left);
        w.MouseUp(pt, MouseButton.Left);
        w.MouseDown(pt, MouseButton.Left);
        w.MouseUp(pt, MouseButton.Left);
        Pump();
        Assert.Equal((0, 1), grid.EditingCell);
        Assert.Equal([0], grid.SelectedIds);
        var box = Assert.IsType<TextBox>(grid.ActiveEditor);
        Assert.Equal("a@x.com", box.Text);
        Assert.Equal("칸 편집", Avalonia.Automation.AutomationProperties.GetName(box));
        // Enter: 넣고 끝 (오른쪽 패널에서 고친 것과 같은 변경)
        box.Text = "b@x.com";
        Key(box, Avalonia.Input.Key.Enter);
        Pump();
        Assert.Null(grid.EditingCell);
        Assert.Null(grid.ActiveEditor);
        Assert.Equal("b@x.com", app.Tab(id)!.Edits.Updates[0][1]);
        Assert.Equal(CellState.Modified, grid.Edits.State(0, 1));
        // Esc: 넣지 않고 끝
        Assert.True(grid.BeginEditing(0, 2));
        Pump();
        grid.ActiveEditor!.Text = "Z";
        Key(grid.ActiveEditor!, Avalonia.Input.Key.Escape);
        Pump();
        Assert.Null(grid.EditingCell);
        Assert.False(app.Tab(id)!.Edits.Updates[0].ContainsKey(2));
        // Tab: 넣고 같은 행 다음 칸 / Shift+Tab: 앞 칸, 바꾸지 않았으면 변경으로 남기지 않는다
        Assert.True(grid.BeginEditing(0, 0));
        Pump();
        Key(grid.ActiveEditor!, Avalonia.Input.Key.Tab);
        Pump();
        Assert.Equal((0, 1), grid.EditingCell);
        Assert.Equal("b@x.com", grid.ActiveEditor!.Text);
        Key(grid.ActiveEditor!, Avalonia.Input.Key.Tab);
        Pump();
        Assert.Equal((0, 2), grid.EditingCell);
        grid.ActiveEditor!.Text = "X";
        Key(grid.ActiveEditor!, Avalonia.Input.Key.Tab, KeyModifiers.Shift);
        Pump();
        Assert.Equal((0, 1), grid.EditingCell);
        Assert.Equal("X", app.Tab(id)!.Edits.Updates[0][2]);
        Assert.False(app.Tab(id)!.Edits.Updates[0].ContainsKey(0));
        // 마지막 칸에서 Tab 은 끝
        Assert.True(grid.BeginEditing(0, 2));
        Pump();
        Key(grid.ActiveEditor!, Avalonia.Input.Key.Tab);
        Pump();
        Assert.Null(grid.EditingCell);
        // NULL 칸을 열고 아무것도 안 쓰면 NULL 그대로, 비우면 빈 문자열
        Assert.True(grid.BeginEditing(2, 1));
        Pump();
        Assert.Equal("", grid.ActiveEditor!.Text);
        Key(grid.ActiveEditor!, Avalonia.Input.Key.Enter);
        Pump();
        Assert.False(app.Tab(id)!.Edits.Updates.ContainsKey(2));
        Assert.True(grid.BeginEditing(2, 2));
        Pump();
        grid.ActiveEditor!.Text = "";
        Key(grid.ActiveEditor!, Avalonia.Input.Key.Enter);
        Pump();
        Assert.Equal("", app.Tab(id)!.Edits.Updates[2][2]);
        // 여러 줄 값 · 삭제 표시한 행은 그 자리에서 고치지 않는다 (패널에서)
        Assert.False(grid.CanEditInline(1, 1));
        Assert.True(grid.CanEditInline(1, 2));
        app.ToggleDelete(id, [1]);
        Pump();
        Assert.False(grid.BeginEditing(1, 2));
        // 새 행도 같은 방법으로
        var newId = app.AddRow(id)!.Value;
        Pump();
        Assert.True(grid.BeginEditing(newId, 1));
        Pump();
        grid.ActiveEditor!.Text = "new@x.com";
        Key(grid.ActiveEditor!, Avalonia.Input.Key.Enter);
        Pump();
        Assert.Equal("new@x.com", app.Tab(id)!.Edits.Inserts[0][1]);
        w.Close();
    });

    [Fact]
    public Task InlineEdit_EndsBeforeSortPageAndButtons_AndNotForReadOnlyOrExpressions() => UiThread.Run(() =>
    {
        var (app, p, view, w) = Setup([Member]);
        var pane = Show(app, view, "SELECT * FROM tb_member", ["id", "email", "status"], [["1", "a", "A"], ["2", "b", "B"]], out var id);
        var grid = pane.Grid;
        // 편집 막대 버튼(되돌리기 · 적용 · 행 추가 등) 전에 고치던 값을 먼저 넣는다
        Assert.True(grid.BeginEditing(0, 1));
        Pump();
        grid.ActiveEditor!.Text = "typed";
        pane.EditBar.BeforeAction!();
        Pump();
        Assert.Null(grid.EditingCell);
        Assert.Equal("typed", app.Tab(id)!.Edits.Updates[0][1]);
        // 헤더 정렬 전에도
        Assert.True(grid.BeginEditing(1, 1));
        Pump();
        grid.ActiveEditor!.Text = "sorted";
        grid.OnSort!("id", true);
        Pump();
        Assert.Equal("sorted", app.Tab(id)!.Edits.Updates[1][1]);
        app.ResolveDiscardPrompt(false);
        app.RevertEdits(id);
        Pump();
        // 식 · 다른 이름 별칭 열은 고치지 않는다 (#194)
        var pane2 = Show(app, view, "SELECT id, UPPER(email) AS email, status AS st FROM tb_member", ["id", "email", "st"], [["1", "A", "A"]], out _);
        Assert.Equal([0], pane2.Grid.EditableColumns);
        Assert.False(pane2.Grid.BeginEditing(0, 1));
        Assert.False(pane2.Grid.BeginEditing(0, 2));
        w.Close();
    });

    [Fact]
    public Task SetNullFromContextMenu() => UiThread.Run(() =>
    {
        var (app, _, view, w) = Setup([Member]);
        var pane = Show(app, view, "SELECT * FROM tb_member", ["id", "email", "status"], [["1", "a", "A"], ["2", "b", "B"], ["3", "c", "C"]], out var id);
        var grid = pane.Grid;
        grid.Select([0, 2]);
        app.ToggleDelete(id, [2]);
        Pump();
        grid.Select([0, 2]);
        Pump();
        // 고른 행 안의 칸을 우클릭 → NULL 로 설정 (컬럼) — 삭제 표시한 행은 빼고
        w.MouseDown(CellPoint(grid, w, 0, 1), MouseButton.Right);
        w.MouseUp(CellPoint(grid, w, 0, 1), MouseButton.Right);
        Pump();
        Assert.Equal([0, 2], grid.SelectedIds);
        var item = OpenMenu(grid).Single(m => m.Header as string == "NULL 로 설정 (email)");
        Assert.True(item.IsEnabled);
        Click(item);
        Pump();
        Assert.Null(app.Tab(id)!.Edits.Updates[0][1]);
        Assert.True(app.Tab(id)!.Edits.Updates[0].ContainsKey(1));
        Assert.False(app.Tab(id)!.Edits.Updates.ContainsKey(2));
        w.Close();
    });

    // MARK: - 외래 키 따라가기

    private static readonly TableMeta Customers = Table("ui_customers", [Col("id", "int", pk: true), Col("name", "varchar(50)")], ["id"]);
    private static readonly TableMeta Orders = Table("ui_orders", [Col("id", "int", pk: true), Col("customer_id", "int"), Col("code", "varchar(20)")],
        ["id"], [new ForeignKeyMeta("fk_ui_orders_customer", "ui_orders", ["customer_id"], "ui_customers", ["id"])]);
    private static readonly TableMeta MemberNo = Table("tb_member", [Col("member_no", "bigint", pk: true), Col("name", "varchar(50)")], ["member_no"]);
    private static readonly TableMeta Point = Table("tb_point", [Col("id", "int", pk: true), Col("member_no", "bigint")], ["id"]);

    [Fact]
    public Task ForeignKey_HeaderArrowMenuCtrlClickAndDetail() => UiThread.Run(() =>
    {
        var (app, _, view, w) = Setup([Customers, Orders, MemberNo, Point]);
        var pane = Show(app, view, "SELECT * FROM ui_orders", ["id", "customer_id", "code"], [["1", "10", "A"], ["2", null, "B"]], out var ordersTab);
        var grid = pane.Grid;
        // 머리글: 외래 키 컬럼 옆 ↗ (진짜 FK)
        Assert.Equal(false, grid.HeaderArrow(1));
        Assert.Equal("customer_id ↗", grid.HeaderText(1));
        Assert.Null(grid.HeaderArrow(0));
        Assert.Equal("customer_id", grid.Grid.Columns[1].Header?.ToString());
        // 우클릭 메뉴: 참조 따라가기 (값이 NULL 이면 비활성)
        grid.Select([1]);
        Pump();
        var disabled = OpenMenu(grid).Single(m => m.Header as string == "참조 따라가기 → ui_customers (customer_id)");
        Assert.False(disabled.IsEnabled);
        grid.Select([0]);
        Pump();
        var follow = OpenMenu(grid).Single(m => m.Header as string == "참조 따라가기 → ui_customers (customer_id)");
        Assert.True(follow.IsEnabled);
        // 행 상세: 외래 키 칸 이름 옆 ↗
        Assert.True(pane.Detail.IsVisible);
        var arrow = Assert.Single(pane.Detail.FkButtons);
        Assert.True(arrow.IsEnabled);
        Assert.Equal("참조 따라가기 → ui_customers (customer_id)", ToolTip.GetTip(arrow));
        Assert.Empty(pane.Detail.ReferencingButtons);   // ui_orders 를 참조하는 테이블 없음
        // Ctrl+클릭: 외래 키가 아닌 칸은 그대로 (새 탭 없음)
        var before = app.Tabs.Count;
        w.MouseDown(CellPoint(grid, w, 0, 2), MouseButton.Left, RawInputModifiers.Control);
        w.MouseUp(CellPoint(grid, w, 0, 2), MouseButton.Left, RawInputModifiers.Control);
        Pump();
        Assert.Equal(before, app.Tabs.Count);
        // 외래 키 칸 Ctrl+클릭 → 새 탭 ↗ ui_customers 에서 조회
        w.MouseDown(CellPoint(grid, w, 0, 1), MouseButton.Left, RawInputModifiers.Control);
        w.MouseUp(CellPoint(grid, w, 0, 1), MouseButton.Left, RawInputModifiers.Control);
        Pump();
        Assert.Equal(before + 1, app.Tabs.Count);
        var opened = app.Tabs[^1];
        Assert.Equal("↗ ui_customers", opened.CustomTitle);
        Assert.Equal("SELECT * FROM `ui_customers` WHERE `id` = 10", opened.Text);
        // 메뉴로도 같은 탭
        app.SelectTab(ordersTab);
        Pump();
        Click(follow);
        Pump();
        Assert.Equal(before + 2, app.Tabs.Count);
        Assert.Equal("↗ ui_customers", app.Tabs[^1].CustomTitle);
        w.Close();
    });

    [Fact]
    public Task ForeignKey_ReferencingRowsAndInferred() => UiThread.Run(() =>
    {
        var (app, p, view, w) = Setup([Customers, Orders, MemberNo, Point]);
        var pane = Show(app, view, "SELECT * FROM ui_customers", ["id", "name"], [["10", "kim"]], out _);
        // 이 행을 참조하는 행 ▸ ui_orders (customer_id)
        var sub = OpenMenu(pane.Grid).Single(m => m.Header as string == "이 행을 참조하는 행");
        var item = sub.Items.OfType<MenuItem>().Single();
        Assert.Equal("ui_orders (customer_id)", item.Header);
        // 행 상세 맨 아래: 참조하는 테이블
        var b = Assert.Single(pane.Detail.ReferencingButtons);
        Assert.Equal("↙ ui_orders (customer_id)", b.Content);
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Equal("↙ ui_orders", app.Tabs[^1].CustomTitle);
        Assert.Equal("SELECT * FROM `ui_orders` WHERE `customer_id` = 10", app.Tabs[^1].Text);
        // 추정 관계: 머리글 흐린 ↗, 메뉴에 · 추정, 행 상세 ↗ 흐리게
        var pane2 = Show(app, view, "SELECT id, member_no FROM tb_point", ["id", "member_no"], [["1", "1002"]], out _);
        Assert.Equal(true, pane2.Grid.HeaderArrow(1));
        Assert.Contains(OpenMenu(pane2.Grid), m => m.Header as string == "참조 따라가기 → tb_member (member_no) · 추정");
        Assert.Equal(0.45, Assert.Single(pane2.Detail.FkButtons).Opacity);
        // 조인 · 집계 결과에는 나오지 않는다
        var pane3 = Show(app, view, "SELECT o.* FROM ui_orders o JOIN ui_customers c ON c.id = o.customer_id",
                         ["id", "customer_id", "code"], [["1", "10", "A"]], out _);
        Assert.Null(pane3.Grid.Fk);
        Assert.Null(pane3.Grid.HeaderArrow(1));
        // 읽기전용 접속에서도 따라가기는 된다 (편집은 안 됨)
        app.Profiles[0] = p with { ReadOnly = true };
        var pane4 = Show(app, view, "SELECT * FROM ui_orders", ["id", "customer_id", "code"], [["1", "10", "A"]], out _);
        Assert.NotNull(pane4.Grid.Fk);
        Assert.False(pane4.Grid.Editable);
        w.Close();
    });

    // MARK: - JSON 보기

    private static readonly TableMeta Doc = Table("tb_doc", [Col("id", "int", pk: true), Col("meta", "json"), Col("note", "text")], ["id"]);

    [Fact]
    public Task JsonView_RawPrettyTree_CopyAndWarning() => UiThread.Run(() =>
    {
        var (app, _, view, w) = Setup([Doc]);
        JSONViewModes.Current = JSONViewMode.Tree;
        const string json = """{"a":{"b":[1,2]},"s":"x"}""";
        var pane = Show(app, view, "SELECT * FROM tb_doc", ["id", "meta", "note"], [["1", json, "plain"]], out var id);
        var copied = new List<string>();
        pane.Detail.Copier = t => { copied.Add(t); return Task.CompletedTask; };
        var field = pane.Detail.JsonViews[1];
        Assert.False(pane.Detail.JsonViews.ContainsKey(2));   // 원래 값이 JSON 아님
        Assert.Equal(["원문", "정렬", "트리"], field.ModeButtons.Select(b => b.Content as string));
        // 트리: 처음엔 2단계까지
        var tree = field.Tree!;
        Assert.Equal(["$.a", "$.a.b", "$.s"], tree.Lines.Select(l => l.PathText));
        tree.Toggle(tree.Lines[1]);
        Assert.Equal(["$.a", "$.a.b", "$.a.b[0]", "$.a.b[1]", "$.s"], tree.Lines.Select(l => l.PathText));
        // 우클릭 > 값 복사 · 경로 복사
        var menu = tree.Lines[1].View.ContextMenu!;
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["값 복사", "경로 복사"], items.Select(m => m.Header as string));
        Click(items[0]);
        Click(items[1]);
        Click(tree.Lines[^1].View.ContextMenu!.Items.OfType<MenuItem>().First());
        Pump();
        Assert.Equal(["[\n  1,\n  2\n]", "$.a.b", "x"], copied);
        // 정렬 · 원문 (마지막 선택 기억)
        field.SetMode(JSONViewMode.Pretty);
        Pump();
        Assert.Equal(JSONViewMode.Pretty, JSONViewModes.Current);
        Assert.Equal(JSONView.Pretty(JSONView.Parse(json)!), field.PrettyText);
        field.SetMode(JSONViewMode.Raw);
        Pump();
        Assert.Contains(field.GetVisualDescendants().OfType<TextBox>(), t => t.Text == json);   // 원문 = 편집 칸
        // JSON 이 깨지면 칸 아래와 적용 확인 창에 경고 (저장은 가능)
        Assert.Empty(pane.Detail.JsonWarnings);
        app.SetCell(id, 0, 1, "{broken");
        Pump();
        Assert.Equal([1], pane.Detail.JsonWarnings);
        // (RequestApplyEdits 는 세션이 있어야 해서, 같은 계산으로 요청을 만든다)
        var tab = app.Tab(id)!;
        var target = app.EditTarget(tab)!.Value.Target!;
        var invalid = JSONView.InvalidEdits(tab.Edits, target, [.. tab.Result!.Columns.Select(c => c.Name)], tab.Result.Rows);
        Assert.Equal(["meta"], invalid);
        var request = new EditApplyRequest(id, ["UPDATE `tb_doc` SET `meta` = '{broken' WHERE `id` = '1';"]) { InvalidJsonColumns = invalid };
        var sheet = new EditApplySheet(app, request);
        Assert.Equal("⚠ JSON 형식이 아니에요: meta", sheet.InvalidJsonWarning?.Text);
        Assert.True(sheet.ApplyButton.IsEnabled);
        JSONViewModes.Current = JSONViewMode.Tree;
        w.Close();
    });

    [Fact]
    public Task JsonView_ReadOnlyResultAndPlainValues() => UiThread.Run(() =>
    {
        var (app, _, view, w) = Setup([]);
        JSONViewModes.Current = JSONViewMode.Tree;
        // 편집할 수 없는 결과에서도 JSON 칸은 보기 전환, 그 밖은 그대로 글자
        var pane = Show(app, view, "SELECT 1", ["j", "t", "n"], [["[1, {\"k\": null}]", "{not json", "42"]], out _);
        var field = Assert.Single(pane.Detail.JsonViews).Value;
        Assert.Equal(3, field.ModeButtons.Count);
        Assert.Equal(["$[0]", "$[1]", "$[1].k"], field.Tree!.Lines.Select(l => l.PathText));
        w.Close();
    });

    // MARK: - 사이드바

    [Fact]
    public Task Sidebar_LightSelectionAndHeaderClearOfScrollbar() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var a = TestEnv.Profile("m1");
        env.Store.Save([a]);
        var app = env.NewApp();
        app.SelectedProfileId = a.Id;
        TestEnv.FakeConnect(app, a.Id);
        app.UpdateConnection(a.Id, c => c.Tables = [new TableMeta("tb_a"), new TableMeta("tb_b")]);
        var view = new SidebarView { DataContext = new SidebarViewModel(app) };
        var w = new Window { Content = view, Width = 300, Height = 600 };
        w.Show();
        Pump();
        var item = view.GetVisualDescendants().OfType<TreeViewItem>().First();
        item.IsSelected = true;
        Pump();
        var root = item.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_LayoutRoot");
        var bg = Assert.IsAssignableFrom<ISolidColorBrush>(root.Background);
        Assert.True(bg.Color.A < 0x40, $"selection {bg.Color}");   // 옅은 강조색 (기본은 불투명 진한 파랑)
        Assert.True(view.TryFindResource("SelectionBackground", view.ActualThemeVariant, out var sel));
        Assert.Equal(((ISolidColorBrush)sel!).Color, bg.Color);
        var header = view.FindControl<Grid>("SchemaHeader")!;
        Assert.Equal(14, header.Margin.Right);
        w.Close();
    });
}
