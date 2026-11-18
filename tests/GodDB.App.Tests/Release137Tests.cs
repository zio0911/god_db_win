using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Profiles;
using GodDB.Core.Schema;

namespace GodDB.App.Tests;

/// Mac 1.3.6 · 1.3.7 변경 (#94 좁은 창, #100 경계 크기 기억 · 커서 문장 실행 · 그룹 줄 선택, #102 로고 · 비교 접속 환경, #108 SQL Server · PostgreSQL 아이콘, #104 위험한 명령 확인 설정)
public class LayoutPersistenceTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static void Clean()
    {
        foreach (var k in Layout.All) Prefs.Remove(k);
    }

    /// Mac ResizeHandle.swift 의 Layout 키 · 처음 크기와 같다
    [Fact]
    public void KeysAndDefaultsMatchMac()
    {
        Assert.Equal(new Dictionary<string, double>
        {
            ["GodDB.layout.queryEditorHeight"] = 220,
            ["GodDB.layout.rowDetailWidth"] = 380,
            ["GodDB.layout.logHeight"] = 220,
            ["GodDB.layout.logDetailWidth"] = 380,
            ["GodDB.layout.assistantWidth"] = 400,
            ["GodDB.layout.diagramAIWidth"] = 380,
            ["GodDB.layout.modelListWidth"] = 220,
            ["GodDB.layout.modelEditorHeight"] = 280,
            ["GodDB.layout.redisWorkbenchHeight"] = 160,
        }, Layout.Defaults.ToDictionary());
    }

    /// 편집기 높이는 저장한 값으로 열리고 (모든 탭 · 다음 실행), 보기 > 레이아웃 초기화로 처음 크기로
    [Fact]
    public Task QueryEditorHeight_Remembered_AndReset() => UiThread.Run(() =>
    {
        Clean();
        var app = new TestEnv().NewApp();
        Prefs.Set(Layout.QueryEditorHeight, 300.0);
        try
        {
            var view = new QueryTabView(app);
            var w = new Window { Width = 900, Height = 700, Content = view };
            w.Show();
            Pump();
            Assert.Equal(300, view.EditorHeight);
            // 다른 화면(새로 연 탭 화면)도 같은 높이
            Assert.Equal(300, new QueryTabView(app).EditorHeight);

            Layout.Reset(app);
            Pump();
            Assert.Equal(220, view.EditorHeight);
            Assert.False(Prefs.Contains(Layout.QueryEditorHeight));
            Assert.Equal("화면 레이아웃을 처음 크기로 되돌렸어요", app.Logs[^1].Message);
            w.Close();
        }
        finally { Clean(); }
    });

    /// 화면에 없던 동안 초기화돼도 다시 붙으면 처음 크기
    [Fact]
    public Task ResetWhileDetached_AppliesOnAttach() => UiThread.Run(() =>
    {
        Clean();
        var app = new TestEnv().NewApp();
        Prefs.Set(Layout.QueryEditorHeight, 333.0);
        try
        {
            var view = new QueryTabView(app);
            Assert.Equal(333, view.EditorHeight);
            Layout.Reset(app);
            var w = new Window { Width = 900, Height = 700, Content = view };
            w.Show();
            Pump();
            Assert.Equal(220, view.EditorHeight);
            w.Close();
        }
        finally { Clean(); }
    });

    /// 경계선 끌기가 끝나면 그 크기를 저장한다
    [Fact]
    public Task DragCompleted_SavesSize() => UiThread.Run(() =>
    {
        Clean();
        try
        {
            var grid = new Grid { RowDefinitions = new RowDefinitions("250,Auto,*") };
            var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Height = 4 };
            Grid.SetRow(splitter, 1);
            grid.Children.Add(new Border());
            grid.Children.Add(splitter);
            var w = new Window { Width = 400, Height = 600, Content = grid };
            w.Show();
            Pump();
            Layout.SaveOnDrag(splitter, grid.RowDefinitions[0], Layout.QueryEditorHeight);
            splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Source = splitter });
            Assert.Equal(250, Prefs.Get(Layout.QueryEditorHeight, 0.0));
            Assert.Equal(250, Layout.Get(Layout.QueryEditorHeight));
            w.Close();
        }
        finally { Clean(); }
    });

    /// AI 어시스턴트 폭은 바꾸면 저장되고, 초기화하면 400
    [Fact]
    public Task AssistantWidth_SavedAndReset() => UiThread.Run(() =>
    {
        Clean();
        var app = new TestEnv().NewApp();
        try
        {
            app.Assistant.Width = 520;
            Assert.Equal(520, Layout.Get(Layout.AssistantWidth));
            Assert.Equal(520, new TestEnv().NewApp().Assistant.Width);   // 다음 실행
            Layout.Reset(app);
            Assert.Equal(400, app.Assistant.Width);
        }
        finally { Clean(); }
    });

    /// 행 상세 · 로그 상세 · Redis Workbench 도 저장한 크기로 연다
    [Fact]
    public Task OtherBoundaries_UseSavedSizes() => UiThread.Run(() =>
    {
        Clean();
        var app = new TestEnv().NewApp();
        Prefs.Set(Layout.LogDetailWidth, 444.0);
        try
        {
            var log = new LogPanelView { DataContext = app };
            var body = log.FindControl<Grid>("Body")!;
            Assert.Equal(444, body.ColumnDefinitions[2].Width.Value);
            var w = new Window { Width = 1000, Height = 400, Content = log };
            w.Show();
            Layout.Reset(app);
            Assert.Equal(380, body.ColumnDefinitions[2].Width.Value);
            w.Close();
        }
        finally { Clean(); }
    });

    /// 보기 메뉴에 레이아웃 초기화
    [Fact]
    public Task ViewMenu_HasResetLayout() => UiThread.Run(() =>
    {
        var w = new MainWindow(new TestEnv().NewApp());
        var menu = w.FindControl<Menu>("MainMenu")!;
        var view = ((IEnumerable<MenuItem>)menu.ItemsSource!).First(m => m.Header?.ToString() == "보기");
        Assert.Contains(view.Items.OfType<MenuItem>(), m => m.Header?.ToString() == "레이아웃 초기화");
    });
}

public class QueryToolbarTests
{
    /// 실행 / 전체 실행 버튼 · 도움말 (Mac #98), 페이지 크기 선택 최소 폭 72 (Mac #94)
    [Fact]
    public Task RunAndRunAllButtons_Tooltips_LimitWidth() => UiThread.Run(() =>
    {
        var view = new QueryTabView(new TestEnv().NewApp());
        Assert.Equal("선택 영역만 실행, 선택이 없으면 커서가 있는 문장(강조된 문장)만 실행 (Ctrl+Enter)", ToolTip.GetTip(view.RunButton));
        Assert.Equal("편집기의 모든 문장을 위에서부터 실행 (Ctrl+Alt+Enter)", ToolTip.GetTip(view.RunAllButton));
        Assert.Contains(((Avalonia.Controls.StackPanel)view.RunAllButton.Content!).Children.OfType<Avalonia.Controls.TextBlock>(), t => t.Text == "전체 실행");
        Assert.Equal(72, view.LimitPicker.MinWidth);
    });

    /// 좁은 창: "페이지" 이름표만 숨기고 선택한 값은 보인다 (Mac #94)
    [Fact]
    public Task NarrowToolbar_HidesPageLabelOnly() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var p = TestEnv.Profile("a");
        env.Store.Save([p]);
        var app = env.NewApp();
        var view = new QueryTabView(app);
        var w = new Window { Width = 420, Height = 500, Content = view };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var bar = Assert.IsType<AdaptiveBar>(view.Toolbar);
        Assert.True(bar.IsCompact);
        var page = view.Toolbar.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "페이지");
        Assert.False(page.IsEffectivelyVisible);
        Assert.True(view.LimitPicker.IsEffectivelyVisible);
        Assert.True(view.LimitPicker.Bounds.Width >= 72);
        // 넓히면 다시 이름표가 보인다
        w.Width = 4000;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.False(bar.IsCompact);
        Assert.True(page.IsEffectivelyVisible);
        w.Close();
    });

    /// 결과 아래 줄 글자는 좁아도 한 줄 (Mac #94)
    [Fact]
    public Task ResultStatusBar_NeverWraps() => UiThread.Run(() =>
    {
        var bar = new ResultStatusBar();
        var result = new QueryResult(["id"], [["1"]], 0.012) { PageSize = 1, Page = 0, HasMore = true };
        bar.Update(result, 0, false, null, false);
        var w = new Window { Width = 200, Height = 100, Content = bar };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        foreach (var t in bar.GetVisualDescendants().OfType<TextBlock>().Where(t => !string.IsNullOrEmpty(t.Text)))
        {
            Assert.Equal(TextWrapping.NoWrap, t.TextWrapping);
            Assert.True(t.Bounds.Height < 30, $"'{t.Text}' 높이 {t.Bounds.Height}");
        }
        w.Close();
    });
}

public class BrandLogoTests
{
    /// 모든 DB 종류에 로고가 있다: MySQL · Redis 는 브랜드 색 한 겹, PostgreSQL · SQL Server 는 여러 겹 그림 (Mac #101, #108)
    [Fact]
    public Task EveryKind_HasLogoInBrandColor() => UiThread.Run(() =>
    {
        var colors = new Dictionary<DatabaseKind, string>
        {
            [DatabaseKind.Mysql] = "#FF4479A1", [DatabaseKind.Postgres] = "#FF336791",
            [DatabaseKind.Mssql] = "#FFFFB03B", [DatabaseKind.Redis] = "#FFFF4438",
        };
        foreach (var kind in Enum.GetValues<DatabaseKind>())
        {
            var badge = new KindBadge { Kind = kind };
            Assert.Equal(Color.Parse(colors[kind]), KindBadge.ColorOf(kind));
            if (kind is DatabaseKind.Mysql or DatabaseKind.Redis)
            {
                Assert.NotNull(badge.Logo);
                Assert.True(badge.Logo!.Bounds.Width > 1 && badge.Logo.Bounds.Height > 1, kind.ToString());
                Assert.Equal(Color.Parse(colors[kind]), ((ISolidColorBrush)badge.LogoBrush!).Color);
            }
            else
            {
                Assert.Null(badge.Logo);
                Assert.NotNull(badge.Layers);
                Assert.Equal(16, badge.Layers!.Width);
            }
            Assert.Equal(kind.Title(), ToolTip.GetTip(badge));
            Assert.Equal(16, badge.Width);
        }
    });

    private static T Layer<T>(KindBadge b, string name) where T : Control =>
        Assert.IsType<T>(b.Layers!.Children.Single(c => c.Name == name));

    private static Color FillOf(Avalonia.Controls.Shapes.Shape s) => ((ISolidColorBrush)s.Fill!).Color;

    /// PostgreSQL: 파랗게 채운 코끼리(바깥 윤곽) + 검은 테두리 + 흰 선 (Mac PostgresIcon, #108)
    [Fact]
    public Task Postgres_BlueFilledElephant_WhiteLines_BlackOutline() => UiThread.Run(() =>
    {
        foreach (var size in new[] { 16.0, 24.0 })
        {
            var badge = new KindBadge { Kind = DatabaseKind.Postgres, Size = size };
            var names = badge.Layers!.Children.Select(c => c.Name).ToList();
            Assert.Equal(["Fill", "Outline", "Lines"], names);   // 아래에서 위로 쌓는 순서
            var fill = Layer<Avalonia.Controls.Shapes.Path>(badge, "Fill");
            Assert.Equal(Color.Parse("#336791"), FillOf(fill));
            var outline = Layer<Avalonia.Controls.Shapes.Path>(badge, "Outline");
            Assert.Null(outline.Fill);
            Assert.Equal(Colors.Black, ((ISolidColorBrush)outline.Stroke!).Color);
            Assert.Equal(0.8, outline.StrokeThickness);
            var lines = Layer<Avalonia.Controls.Shapes.Path>(badge, "Lines");
            Assert.Equal(Colors.White, FillOf(lines));
            Assert.Equal(FillRule.NonZero, ((PathGeometry)lines.Data!).FillRule);
            // 세 겹 모두 같은 기준(전체 경로 범위)으로 size 칸에 맞춘다
            var m = ((MatrixTransform)lines.Data.Transform!).Matrix;
            Assert.Equal(m, ((MatrixTransform)fill.Data!.Transform!).Matrix);
            Assert.Equal(m, ((MatrixTransform)outline.Data!.Transform!).Matrix);
            var full = PathGeometry.Parse(BrandLogo.PostgreSql).Bounds;
            Assert.Equal(size / Math.Max(full.Width, full.Height), m.M11, 6);
        }
    });

    /// 코끼리 바깥 윤곽은 가장 큰 조각 하나 (Mac postgresSilhouette)
    [Fact]
    public Task Postgres_Silhouette_IsLargestFigure() => UiThread.Run(() =>
    {
        var full = PathGeometry.Parse(BrandLogo.PostgreSql);
        var sil = BrandLogo.PostgresSilhouette;
        var f = Assert.Single(sil.Figures!);
        var area = (Rect r) => r.Width * r.Height;
        var max = full.Figures!.Max(x => area(BrandLogo.PointBounds(x)));
        Assert.Equal(max, area(BrandLogo.PointBounds(f)));
        Assert.True(full.Figures!.Count > 1);
        Assert.True(BrandLogo.PointBounds(f).Width > 20);   // 코끼리 전체 폭 (24 칸)
    });

    /// SQL Server: 주황 DB 원통 + 렌치 · 드라이버 (Mac MSSQLIcon 과 같은 비율 · 색, #108)
    [Fact]
    public Task Mssql_OrangeCylinder_WithWrenchAndScrewdriver() => UiThread.Run(() =>
    {
        foreach (var size in new[] { 16.0, 24.0 })
        {
            var badge = new KindBadge { Kind = DatabaseKind.Mssql, Size = size };
            Assert.Equal(
                ["CylinderBottom", "CylinderBody", "CylinderShine", "CylinderTop", "CylinderTopFace",
                 "WrenchHalo", "ScrewdriverHalo", "Wrench", "Screwdriver"],
                badge.Layers!.Children.Select(c => c.Name).ToList());
            double w = size * 0.66, h = size * 0.78, cap = h * 0.3;
            var orange = Color.FromRgb(255, 176, 59);

            var bottom = Layer<Avalonia.Controls.Shapes.Ellipse>(badge, "CylinderBottom");
            Assert.Equal((w, cap, h - cap), (bottom.Width, bottom.Height, Canvas.GetTop(bottom)));
            Assert.Equal(orange, FillOf(bottom));
            var body = Layer<Avalonia.Controls.Shapes.Rectangle>(badge, "CylinderBody");
            Assert.Equal((w, h - cap, cap / 2), (body.Width, body.Height, Canvas.GetTop(body)));
            Assert.Equal(orange, FillOf(body));
            var shine = Layer<Avalonia.Controls.Shapes.Rectangle>(badge, "CylinderShine");
            Assert.Equal(w * 0.16, shine.Width, 6);
            Assert.Equal(w * 0.66, Canvas.GetLeft(shine), 6);
            Assert.Equal(Color.FromRgb(255, 214, 140), FillOf(shine));
            var top = Layer<Avalonia.Controls.Shapes.Ellipse>(badge, "CylinderTop");
            Assert.Equal((w, cap, 0.0), (top.Width, top.Height, Canvas.GetTop(top)));
            var face = Layer<Avalonia.Controls.Shapes.Ellipse>(badge, "CylinderTopFace");
            Assert.Equal(w * 0.8, face.Width, 6);
            Assert.Equal(cap * 0.62, face.Height, 6);
            Assert.Equal(Color.FromRgb(214, 214, 219), FillOf(face));

            // 도구: 흰 테두리 위에 짙은 회색, 원통 오른쪽 아래 (0.44 ~ 1.0 칸)
            foreach (var n in new[] { "Wrench", "Screwdriver" })
            {
                var tool = Layer<Avalonia.Controls.Shapes.Path>(badge, n);
                Assert.Equal(Color.FromRgb(61, 61, 66), FillOf(tool));
                var halo = Layer<Avalonia.Controls.Shapes.Path>(badge, n + "Halo");
                Assert.Equal(Colors.White, ((ISolidColorBrush)halo.Stroke!).Color);
                var m = ((MatrixTransform)tool.Data!.Transform!).Matrix;
                var c = new Point(12, 12).Transform(m);
                Assert.Equal(size * 0.72, c.X, 6);
                Assert.Equal(size * 0.72, c.Y, 6);
            }
            // 렌치 머리(왼쪽 끝)는 왼쪽 위, 드라이버 손잡이(왼쪽 끝)는 왼쪽 아래 — X 자로 겹친다
            var wm = ((MatrixTransform)Layer<Avalonia.Controls.Shapes.Path>(badge, "Wrench").Data!.Transform!).Matrix;
            var dm = ((MatrixTransform)Layer<Avalonia.Controls.Shapes.Path>(badge, "Screwdriver").Data!.Transform!).Matrix;
            var wHead = new Point(2, 12).Transform(wm);
            var dHandle = new Point(2, 12).Transform(dm);
            Assert.True(wHead.X < size * 0.72 && wHead.Y < size * 0.72);
            Assert.True(dHandle.X < size * 0.72 && dHandle.Y > size * 0.72);
            // 모든 겹이 size 칸 안(도구 테두리 조금 빼고)에 들어온다
            foreach (var p in new[] { new Point(23, 12).Transform(wm), new Point(23, 12).Transform(dm) })
                Assert.True(p.X <= size && p.Y <= size && p.X >= 0 && p.Y >= 0, p.ToString());
        }
    });

    /// 도구 경로는 해석되고, 렌치는 한 조각 · 드라이버는 세 조각
    [Fact]
    public Task Mssql_ToolPaths_Parse() => UiThread.Run(() =>
    {
        Assert.Single(PathGeometry.Parse(BrandLogo.MssqlWrench).Figures!);
        Assert.Equal(3, PathGeometry.Parse(BrandLogo.MssqlScrewdriver).Figures!.Count);
    });

    /// MySQL 은 작은 크기에서 글자가 뭉개져 돌고래만 (Mac keepSubpaths maxY <= 13.6)
    [Fact]
    public Task MySql_KeepsDolphinOnly() => UiThread.Run(() =>
    {
        var full = PathGeometry.Parse(BrandLogo.MySql);
        Assert.True(full.Bounds.Bottom > 18);
        var dolphin = Assert.IsType<PathGeometry>(BrandLogo.Path(DatabaseKind.Mysql));
        Assert.InRange(dolphin.Figures!.Count, 2, full.Figures!.Count - 1);
        foreach (var f in dolphin.Figures) Assert.True(BrandLogo.PointBounds(f).Bottom <= 13.6);
        Assert.True(dolphin.Bounds.Width > 5);
    });

    /// Mac SVGPathTests: Avalonia 경로 해석기가 같은 명령을 해석한다
    [Fact]
    public Task SvgPath_BasicCommands() => UiThread.Run(() =>
    {
        Assert.Equal(new Rect(2, 2, 20, 20), PathGeometry.Parse("M2 2h20v20H2z").Bounds);
    });

    [Fact]
    public Task SvgPath_CompactNumbersRelativeAndArc() => UiThread.Run(() =>
    {
        // ".5.5" 같은 붙은 숫자, 상대 좌표, 플래그가 붙은 호 ("a1 1 0 011 1")
        var g = PathGeometry.Parse("M0 0l.5.5a1 1 0 011 1c-.5 0-1-.5-1-1z");
        var f = Assert.Single(g.Figures!);
        Assert.Equal(new Point(.5, .5), Assert.IsType<LineSegment>(f.Segments![0]).Point);
        var arc = Assert.IsType<ArcSegment>(f.Segments[1]);
        Assert.Equal(new Point(1.5, 1.5), arc.Point);
        Assert.False(arc.IsLargeArc);
        Assert.Equal(SweepDirection.Clockwise, arc.SweepDirection);
        var b = BrandLogo.PointBounds(f);
        Assert.True(b.X >= -0.01 && b.Right <= 2.6 && b.Y >= -0.01 && b.Bottom <= 2.6, b.ToString());
    });

    [Fact]
    public Task SvgPath_FullCircleArc_Parsed() => UiThread.Run(() =>
    {
        // 화면 없는 테스트 그리기 엔진은 호의 실제 범위를 재지 않아서, 해석 결과(큰 호 · 방향 · 끝점)를 본다
        var f = Assert.Single(PathGeometry.Parse("M12 2a10 10 0 1 0 0.0001 0z").Figures!);
        var arc = Assert.IsType<ArcSegment>(f.Segments![0]);
        Assert.True(arc.IsLargeArc);
        Assert.Equal(SweepDirection.CounterClockwise, arc.SweepDirection);
        Assert.Equal(new Size(10, 10), arc.Size);
        Assert.Equal(12.0001, arc.Point.X, 4);
    });

    /// 라이선스 목록에 Simple Icons (CC0) 와 상표 안내
    [Fact]
    public void Licenses_IncludeSimpleIcons()
    {
        var s = Assert.Single(Licenses.ThirdParty, l => l.Name == "Simple Icons");
        Assert.Equal("CC0-1.0", s.License);
        Assert.Equal("16.38.0", s.Version);
        Assert.Contains("상표", s.Note);
        var vm = new SettingsViewModel(null) { SelectedLicense = "Simple Icons" };
        Assert.False(vm.HasLicenseText);   // 전체 문구 대신 링크 안내
    }

    /// 사이드바 접속 줄: [상태] [로고] 이름 [환경], 고른 접속 이름은 굵게
    [Fact]
    public Task SidebarRow_ShowsLogoBeforeName() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var a = TestEnv.Profile("pg-a", DatabaseKind.Postgres);
        env.Store.Save([a]);
        var app = env.NewApp();
        app.SelectedProfileId = a.Id;
        var view = new SidebarView { DataContext = new SidebarViewModel(app) };
        var w = new Window { Content = view, Width = 300, Height = 600 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var row = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("profileRow"));
        var badge = row.GetVisualDescendants().OfType<KindBadge>().Single();
        Assert.Equal(DatabaseKind.Postgres, badge.Kind);
        Assert.Equal(1, Grid.GetColumn(badge));
        var name = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "pg-a");
        Assert.Equal(FontWeight.SemiBold, name.FontWeight);
        Assert.DoesNotContain(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text is "PG" or "🛢");
        w.Close();
    });

    /// Mac #99 (그룹 안 접속을 누르면 그룹 전체가 선택돼 보이던 문제): Windows 사이드바는 그룹 줄이 선택 대상이 아니다.
    [Fact]
    public Task GroupMemberSelected_GroupRowNotSelected() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var a = TestEnv.Profile("m1") with { Group = "UI 그룹" };
        var b = TestEnv.Profile("m2") with { Group = "UI 그룹" };
        env.Store.Save([a, b]);
        var app = env.NewApp();
        var view = new SidebarView { DataContext = new SidebarViewModel(app) };
        var w = new Window { Content = view, Width = 300, Height = 600 };
        w.Show();
        app.SelectedProfileId = a.Id;
        Dispatcher.UIThread.RunJobs();
        var rows = view.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("profileRow") && x.DataContext is ProfileRowViewModel).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.Classes.Contains("selected"));
        Assert.Equal(a.Id, ((ProfileRowViewModel)rows.Single(r => r.Classes.Contains("selected")).DataContext!).Profile.Id);
        var header = view.GetVisualDescendants().OfType<Border>().Single(x => x.Classes.Contains("groupHeader"));
        Assert.DoesNotContain("selected", header.Classes);
        Assert.DoesNotContain(header.GetVisualAncestors().OfType<Border>(), x => x.Classes.Contains("selected"));
        w.Close();
    });
}

public class CompareChoiceTests
{
    /// 비교 접속 목록: [로고] 이름 · 환경, 그룹별로 묶고 그룹 이름 줄은 고를 수 없다 (Mac #101)
    [Fact]
    public Task Choices_ShowEnvironment_GroupedByGroup() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var prod = TestEnv.Profile("회원 DB") with { Environment = ConnectionEnvironment.Production };
        var dev = TestEnv.Profile("dev", DatabaseKind.Postgres) with { Environment = ConnectionEnvironment.Development, Group = "팀" };
        var plain = TestEnv.Profile("plain");
        var redis = TestEnv.Profile("cache", DatabaseKind.Redis);
        env.Store.Save([prod, dev, plain, redis]);
        var app = env.NewApp();
        var vm = new CompareViewModel(app);
        Assert.Equal(["선택", "회원 DB · 운영", "plain", "팀", "dev · 개발"], vm.Choices.Select(c => c.Label));
        var header = vm.Choices[3];
        Assert.True(header.IsHeader);
        Assert.Null(header.Id);
        Assert.Equal(DatabaseKind.Postgres, vm.Choices[4].Kind);
        // 그룹 이름 줄을 고르면 "선택" 으로
        vm.Source = header;
        Assert.Null(vm.Source!.Id);
        Assert.False(vm.Source.IsHeader);
        vm.Select(dev.Id, prod.Id);
        Assert.Equal("dev · 개발 · 팀", vm.Source!.Tip);
        Assert.Equal(ConnectionEnvironment.Production, vm.Target!.Environment);
    });

    /// 고른 접속 옆에 사이드바와 같은 색 환경 표시
    [Fact]
    public Task SelectedConnection_ShowsEnvironmentBadge() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var prod = TestEnv.Profile("p") with { Environment = ConnectionEnvironment.Production };
        var dev = TestEnv.Profile("d") with { Environment = ConnectionEnvironment.Development };
        env.Store.Save([prod, dev]);
        var app = env.NewApp();
        var view = new CompareView { DataContext = app };
        var w = new Window { Content = view, Width = 900, Height = 600 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var src = view.FindControl<EnvironmentBadge>("SourceEnv")!;
        Assert.False(src.IsVisible);
        view.Vm!.Select(prod.Id, dev.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ConnectionEnvironment.Production, src.Environment);
        Assert.True(src.IsVisible);
        Assert.Equal(ConnectionEnvironment.Development, view.FindControl<EnvironmentBadge>("TargetEnv")!.Environment);
        // 고른 항목도 로고 + 이름 · 환경
        var picker = view.FindControl<ComboBox>("SourcePicker")!;
        Assert.Contains(picker.GetVisualDescendants().OfType<KindBadge>(), k => k.Kind == DatabaseKind.Mysql);
        w.Close();
    });
}

/// 테이블 아이콘: Mac "tablecells" 처럼 파란 2열 × 3행 격자 (글자 ▦ 를 쓰지 않음)
public class TableCellsIconTests
{
    [Fact]
    public Task SidebarAndQuickOpenUseGridIcon() => UiThread.Run(() =>
    {
        var icon = new GodDB.App.Controls.TableCellsIcon();
        Assert.Equal(16, icon.Width);
        var blue = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(icon.Foreground);
        Assert.True(blue.Color.B > 200 && blue.Color.R < 60);   // 강조 파랑
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VERSION"))) dir = dir.Parent;
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src/GodDB.App/Views/SidebarView.axaml"));
        Assert.Contains("TableCellsIcon", xaml);
        Assert.DoesNotContain("Text=\"▦\"", xaml);
    });
}

/// 도구 막대 아이콘: 글자 기호(이모지처럼 그려짐) 대신 벡터 아이콘 + 글자, 좁으면 글자만 숨김
public class ToolbarSymbolTests
{
    [Fact]
    public Task ToolbarButtonsUseVectorSymbols() => UiThread.Run(() =>
    {
        foreach (var s in Enum.GetValues<GodDB.App.Controls.Symbol>())
        {
            var b = GodDB.App.Views.Q.B(s, "x", () => { });
            var panel = Assert.IsType<Avalonia.Controls.StackPanel>(b.Content);
            Assert.IsType<GodDB.App.Controls.SymbolIcon>(panel.Children[0]);
            Assert.Contains("label", ((Avalonia.Controls.TextBlock)panel.Children[1]).Classes);
        }
        var src = File.ReadAllText(FindRepoFile("src/GodDB.App/Views/QueryTabView.cs"));
        foreach (var emoji in new[] { "🕘", "⏩", "⋔ " }) Assert.DoesNotContain(emoji, src);
    });

    [Fact]
    public void TableTreeReceivesHandledDoubleTaps()
    {
        // TreeViewItem 이 더블클릭을 Handled 로 표시하므로 handledEventsToo 로 받아야 테이블 데이터가 열린다
        var src = File.ReadAllText(FindRepoFile("src/GodDB.App/Views/SidebarView.axaml.cs"));
        Assert.Contains("TableTree.AddHandler(DoubleTappedEvent, OnTableDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true)", src);
    }

    internal static string FindRepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VERSION"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }
}
