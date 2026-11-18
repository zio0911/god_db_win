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

/// Mac 1.3.6 · 1.3.7 변경 (#94 좁은 창, #100 경계 크기 기억 · 커서 문장 실행 · 그룹 줄 선택, #102 로고 · 비교 접속 환경, #104 위험한 명령 확인 설정)
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
        Assert.Contains("전체 실행", view.RunAllButton.Content?.ToString());
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
    /// 모든 DB 종류에 로고가 있고 브랜드 색으로 칠한다 (Mac #101)
    [Fact]
    public Task EveryKind_HasLogoInBrandColor() => UiThread.Run(() =>
    {
        var colors = new Dictionary<DatabaseKind, string>
        {
            [DatabaseKind.Mysql] = "#FF4479A1", [DatabaseKind.Postgres] = "#FF4169E1",
            [DatabaseKind.Mssql] = "#FFCC2927", [DatabaseKind.Redis] = "#FFFF4438",
        };
        foreach (var kind in Enum.GetValues<DatabaseKind>())
        {
            var badge = new KindBadge { Kind = kind };
            Assert.NotNull(badge.Logo);
            Assert.True(badge.Logo!.Bounds.Width > 1 && badge.Logo.Bounds.Height > 1, kind.ToString());
            Assert.Equal(Color.Parse(colors[kind]), ((ISolidColorBrush)badge.LogoBrush!).Color);
            Assert.Equal(kind.Title(), ToolTip.GetTip(badge));
            Assert.Equal(16, badge.Width);
        }
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
