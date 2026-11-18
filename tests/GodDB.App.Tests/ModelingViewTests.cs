using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using GodDB.App.Controls;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Modeling;
using GodDB.Core.Schema;

namespace GodDB.App.Tests;

/// 모델링 화면 (headless, 창을 실제로 띄우지 않음)
public class ModelingViewTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static (AppState App, ModelingView View, Window Host) Open(AppState app)
    {
        var view = new ModelingView { DataContext = app };
        var host = new Window { Content = view, Width = 1200, Height = 800 };
        host.Show();
        Pump();
        return (app, view, host);
    }

    private static DataModel TwoTables(out Guid parent, out Guid child)
    {
        var m = new DataModel(name: "관계 테스트", kind: DatabaseKind.Mysql);
        parent = m.AddTable(logicalName: "회원");
        child = m.AddTable(logicalName: "주문");
        m.Table(parent)!.PhysicalName = "tb_member";
        m.Table(parent)!.Columns[0].PhysicalName = "id";
        m.Table(child)!.PhysicalName = "tb_order";
        m.Table(child)!.Columns[0].PhysicalName = "id";
        return m;
    }

    [Fact]
    public Task View_ConstructsWithSampleModel_AndShowsEditors() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var sample = ModelingSample.Model();
        app.Modeling.UseForPreview([sample]);
        var (_, view, host) = Open(app);
        var vm = view.Vm!;
        Assert.True(vm.HasModel);
        Assert.Single(vm.Rows);
        Assert.Equal("쇼핑몰 모델", vm.Rows[0].Name);
        Assert.NotNull(view.Canvas);
        var (w, h) = ModelDiagram.Size(sample);
        Assert.InRange(view.Canvas!.DesiredSize.Width, w - 2, w + 2);
        Assert.InRange(view.Canvas.DesiredSize.Height, h - 2, h + 2);
        // 가입일시 물리명이 비어 있어 검사 오류
        Assert.True(vm.HasErrors);

        // 카드 고르기 → 테이블 편집기, 탭 모두 그려 보기
        var member = sample.Tables[0];
        vm.TapTable(member.Id);
        Pump();
        var editor = view.GetLogicalDescendants().OfType<ModelTableEditor>().Single();
        Assert.NotNull(editor);
        for (var i = 0; i < 4; i++)
        {
            vm.EditorTab = i;
            vm.Changed(structure: true);
            Pump();
        }
        // 관계 고르기 → 관계 편집기
        vm.SelectRelation(sample.Relations[0].Id);
        Pump();
        Assert.Single(view.GetLogicalDescendants().OfType<ModelRelationEditor>());
        Assert.DoesNotContain(view.GetLogicalDescendants(), x => x is ModelTableEditor);

        // 확대 · 이름 표시 (설정에 저장)
        vm.ZoomIn();
        Pump();
        Assert.Equal(1.1, vm.Zoom, 3);
        Assert.InRange(view.Canvas.DesiredSize.Width, w * 1.1 - 2, w * 1.1 + 2);
        vm.NameMode = ModelNameMode.Both;
        Assert.Equal("both", Prefs.GetString(ModelingViewModel.NameModeKey));
        Assert.Equal(ModelNameMode.Both, ModelingViewModel.LoadNameMode());
        vm.NameMode = ModelNameMode.Logical;
        Pump();
        host.Close();
    });

    [Fact]
    public Task View_WithoutModels_ShowsEmptyState_AndCreatesModel() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var (_, view, host) = Open(app);
        var vm = view.Vm!;
        Assert.False(vm.HasModel);
        Assert.Null(view.Canvas);
        vm.NewModel();
        Pump();
        Assert.True(vm.HasModel);
        Assert.NotNull(view.Canvas);
        Assert.Equal(DatabaseKind.Mysql, vm.Model!.Kind);
        Assert.Single(vm.Model.Tables);
        vm.NewModel();
        vm.Duplicate(vm.Model!.Id);
        Assert.Equal(3, vm.Rows.Count);
        Assert.EndsWith(" 복사본", vm.Rows[0].Name);
        // 이름 바꾸기 (앞뒤 공백 제거)
        vm.StartRename(vm.Rows[0]);
        vm.Rows[0].RenameText = "  새 이름  ";
        vm.CommitRename(vm.Rows[0]);
        Assert.Equal("새 이름", vm.Model!.Name);
        Assert.Equal("새 이름", vm.Rows[0].Name);
        host.Close();
    });

    [Fact]
    public Task RelationTool_CreatesRelation_WithFkColumnCopy_AndUndo() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var m = TwoTables(out var parent, out var child);
        app.Modeling.UseForPreview([m]);
        var vm = new ModelingViewModel(app);
        vm.PressTool(new ModelTool.Relation(ModelCardinality.OneToMany, false));
        Assert.Contains("먼저 부모", vm.ToolHint);
        vm.TapTable(parent);
        Assert.Equal(parent, vm.PendingParent);
        Assert.Contains("회원", vm.ToolHint);
        vm.TapTable(child);
        var model = vm.Model!;
        var r = Assert.Single(model.Relations);
        Assert.Equal(parent, r.Parent);
        Assert.Equal(child, r.Child);
        Assert.False(r.Identifying);
        Assert.Equal(new ModelSelection.Relation(r.Id), vm.Selection);
        Assert.IsType<ModelTool.Select>(vm.Tool);
        Assert.Null(vm.PendingParent);
        // 부모 기본 키가 FK 컬럼으로 복사 (id → member_id)
        var fk = model.Table(child)!.Columns.Single(c => c.Id == r.Pairs[0].Child);
        Assert.Equal("member_id", fk.PhysicalName);
        Assert.Equal("회원 ID", fk.LogicalName);
        Assert.True(vm.CanUndo);

        // 관계 속성 바꾸기 → FK 컬럼도 맞춘다
        vm.UpdateRelation(r.Id, x => x.Identifying = true);
        Assert.True(vm.Model!.Table(child)!.Columns.Single(c => c.Id == fk.Id).PrimaryKey);

        // 되돌리기 → 관계 없음, 고른 관계는 닫힘
        vm.Undo();
        Assert.Empty(vm.Model!.Relations);
        Assert.Null(vm.Selection);

        // 같은 도구를 다시 누르면 선택 도구
        var tool = new ModelTool.Relation(ModelCardinality.OneToOne, false);
        vm.PressTool(tool);
        vm.PressTool(tool);
        Assert.IsType<ModelTool.Select>(vm.Tool);

        // 테이블 추가 도구: 빈 곳을 누르면 카드 머리 가운데가 누른 곳
        vm.PressTool(ModelTool.AddTableTool);
        vm.TapBackground(500, 400);
        Assert.Equal(3, vm.Model!.Tables.Count);
        var added = vm.Model.Tables[^1];
        Assert.Equal(500 - ModelDiagram.CardWidth / 2, added.X);
        Assert.Equal(380, added.Y);
        Assert.Equal(new ModelSelection.Table(added.Id), vm.Selection);

        // 카드 끌기 (선택 도구, 정수 반올림)
        vm.DragTable(added.Id, 10.4, -5.6);
        vm.EndDrag(added.Id);
        Assert.Equal(500 - ModelDiagram.CardWidth / 2 + 10, vm.Model.Tables[^1].X);
        Assert.Equal(374, vm.Model.Tables[^1].Y);
        vm.Undo();
        Assert.Equal(380, vm.Model!.Tables[^1].Y);

        // 지우기 (확인 없이 바로 부르는 경로)
        vm.Delete(new ModelSelection.Table(vm.Model.Tables[^1].Id));
        Assert.Equal(2, vm.Model!.Tables.Count);
    });

    [Fact]
    public Task EditorEdits_TriggerAutosave() => UiThread.Run(async () =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        app.Modeling.SaveDelay = TimeSpan.FromMilliseconds(30);
        app.Modeling.LoadIfNeeded();
        var m = TwoTables(out var parent, out _);
        app.Modeling.Add(m);
        var (_, view, host) = Open(app);
        var vm = view.Vm!;
        vm.Selection = new ModelSelection.Table(parent);
        Pump();
        var editor = view.GetLogicalDescendants().OfType<ModelTableEditor>().Single();
        var logical = editor.GetLogicalDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "논리명");
        logical.Text = "고객";
        Assert.Equal("고객", vm.Model!.Table(parent)!.LogicalName);
        var physical = editor.GetLogicalDescendants().OfType<TextBox>().Where(t => t.PlaceholderText == "물리명").Skip(1).First();
        physical.Text = "member_no";   // 첫 컬럼 물리명

        DataModel? saved = null;
        for (var i = 0; i < 100 && saved?.Table(parent)?.LogicalName != "고객"; i++)
        {
            await Task.Delay(20);
            Pump();
            saved = app.Modeling.Store.List().FirstOrDefault(x => x.Id == m.Id);
        }
        Assert.Equal("고객", saved!.Table(parent)!.LogicalName);
        Assert.Equal("member_no", saved.Table(parent)!.Columns[0].PhysicalName);

        // 컬럼 추가 · 타입 메뉴의 자주 쓰는 타입
        vm.AddColumn(parent);
        Assert.Equal(2, vm.Model!.Table(parent)!.Columns.Count);
        Assert.Contains("bigint", DataModel.CommonTypes(vm.Model.Kind));
        host.Close();
    });

    [Fact]
    public Task SqlDialog_TextEqualsCoreDdl() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var sample = ModelingSample.Model();
        // 물리명이 빠진 컬럼이 있으면 오류 (SQL 추출을 막음)
        var failed = new ModelSqlWindow(app, sample);
        Assert.True(failed.Failed);
        Assert.StartsWith("-- ", failed.SqlText);

        sample.Tables[0].Columns.Single(c => c.LogicalName == "가입일시").PhysicalName = "joined_at";
        Assert.DoesNotContain(sample.Issues(), i => i.Level == ModelIssueLevel.Error);
        var w = new ModelSqlWindow(app, sample);
        Assert.False(w.Failed);
        Assert.Equal(sample.Ddl(DatabaseKind.Mysql, dropFirst: false), w.SqlText);
        w.SetOptions(DatabaseKind.Postgres, true);
        Assert.Equal(sample.Ddl(DatabaseKind.Postgres, dropFirst: true), w.SqlText);
        w.SetOptions(DatabaseKind.Mssql, false);
        Assert.Equal(sample.Ddl(DatabaseKind.Mssql, dropFirst: false), w.SqlText);

        // 쿼리 탭으로 열기 (실행은 하지 않음)
        var p = TestEnv.Profile("p");
        app.Profiles.Add(p);
        TestEnv.FakeConnect(app, p.Id);
        app.SelectedProfileId = p.Id;
        app.Mode = MainMode.Modeling;
        var w2 = new ModelSqlWindow(app, sample);
        w2.OpenInQueryTab();
        var tab = Assert.Single(app.Tabs);
        Assert.Equal(w2.SqlText, tab.Text);
        Assert.Equal(MainMode.Query, app.Mode);
    });

    private static List<TableMeta> Fixture() =>
    [
        new TableMeta("member", "회원\n가입한 사용자",
            [new ColumnMeta("member_id", "bigint", false, columnKey: "PRI", position: 1), new ColumnMeta("name", "varchar(50)", false, comment: "이름", position: 2)],
            [new IndexMeta("PRIMARY", true, ["member_id"], primary: true)]),
        new TableMeta("orders", "주문",
            [new ColumnMeta("order_id", "bigint", false, columnKey: "PRI", position: 1), new ColumnMeta("member_id", "bigint", false, position: 2)],
            [new IndexMeta("PRIMARY", true, ["order_id"], primary: true)]),
        new TableMeta("audit_log", "",
            [new ColumnMeta("log_id", "bigint", false, columnKey: "PRI", position: 1)],
            [new IndexMeta("PRIMARY", true, ["log_id"], primary: true)]),
    ];

    [Fact]
    public Task ReverseEngineerSheet_CreatesModelFromFixtureTables() => UiThread.Run(() =>
    {
        Assert.Equal(typeof(ReverseEngineerWindow), SheetRegistry.Sheets[nameof(AppState.ShowReverseEngineer)]);
        var app = new TestEnv().NewApp();
        var vm = new ModelingViewModel(app);
        Assert.False(vm.CanReverseEngineer);
        // 연결 안 됨 → 안내만
        var notReady = new ReverseEngineerWindow(app, true);
        Assert.Empty(notReady.Picked);

        var p = TestEnv.Profile("p");
        app.Profiles.Add(p);
        TestEnv.FakeConnect(app, p.Id);
        app.UpdateConnection(p.Id, c => c.Tables = Fixture());
        app.SelectedProfileId = p.Id;
        Assert.True(vm.CanReverseEngineer);
        Assert.True(vm.CanCompare);

        app.ShowReverseEngineer = true;
        var w = new ReverseEngineerWindow(app, true);
        Assert.Equal(3, w.Picked.Count);   // 고른 테이블이 없으면 모두
        Assert.Equal("shop 모델", w.NameBox.Text);
        Assert.Equal("회원", ReverseEngineerWindow.LogicalName(Fixture()[0].Comment));
        w.FilterBox.Text = "mem";
        Assert.Single(w.Shown);
        w.FilterBox.Text = "";
        w.Picked.Remove("audit_log");
        w.InferredBox.IsChecked = true;
        w.Create();

        var m = w.Created!;
        Assert.Equal("shop 모델", m.Name);
        Assert.Equal(2, m.Tables.Count);
        var member = m.Tables.Single(t => t.PhysicalName == "member");
        Assert.Equal("회원", member.LogicalName);
        Assert.Equal("가입한 사용자", member.Comment);
        // FK 제약이 없어도 컬럼 이름으로 추정한 관계 (논리 관계)
        var r = Assert.Single(m.Relations);
        Assert.False(r.CreateConstraint);
        Assert.Equal(member.Id, r.Parent);
        Assert.Equal(MainMode.Modeling, app.Mode);
        Assert.False(app.ShowReverseEngineer);
        // 모델링 화면 목록이 새 모델을 보여 준다
        Assert.Equal(m.Id, vm.Model!.Id);
        Assert.Contains(vm.Rows, x => x.Id == m.Id);

        // DB와 비교: 모델(member, orders) vs DB(fixture 3개) → 차이 없음이면 고칠 SQL 없음
        var cmp = new ModelCompareWindow(app, m);
        Assert.Null(cmp.Error);
        Assert.NotNull(cmp.Report);
        Assert.Equal(1, cmp.Report!.ExtraCount);   // audit_log 는 DB 에만
        // 모델에 컬럼을 더하면 ALTER SQL 이 생기고 쿼리 탭으로 열린다 (실행하지 않음)
        m.Tables[0].Columns.Add(new ModelColumn(logicalName: "메모", physicalName: "memo", dataType: "varchar(100)"));
        var cmp2 = new ModelCompareWindow(app, m);
        Assert.Contains("memo", cmp2.Report!.FixScript);
        cmp2.OpenInQueryTab();
        Assert.Equal(cmp2.Report.FixScript, Assert.Single(app.Tabs).Text);
        Assert.Equal(MainMode.Query, app.Mode);
    });

    [Fact]
    public Task PngExport_DoesNotThrow() => UiThread.Run(() =>
    {
        var bytes = ModelCanvas.RenderPng(ModelingSample.Model(), ModelNameMode.Both, Avalonia.Media.Brushes.White);
        // headless(Skia 없음) 렌더러는 실제 그림을 만들지 않으므로 예외 없이 끝나는지만 본다
        Assert.NotNull(bytes);
    });
}
