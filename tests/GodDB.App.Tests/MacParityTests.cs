using Avalonia.Controls;
using Avalonia.Threading;
using GodDB.App.Controls;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core.Modeling;
using GodDB.Core.Schema;

namespace GodDB.App.Tests;

/// Mac 과 같게 맞춘 동작: 상대 시간, 비교 화면 쿼리 탭 열기, 다이어그램 · 모델링 애니메이션
public class MacParityTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    // Mac RelativeDateTimeFormatter(.named) 와 같은 결과
    [Theory]
    [InlineData(0, "지금")]
    [InlineData(5, "5초 전")]
    [InlineData(1, "1초 전")]
    [InlineData(60, "1분 전")]
    [InlineData(3 * 60 + 20, "3분 전")]
    [InlineData(3600, "1시간 전")]
    [InlineData(5 * 3600 + 59 * 60, "5시간 전")]
    [InlineData(26 * 3600, "어제")]
    [InlineData(2 * 86400 + 3600, "2일 전")]
    [InlineData(6 * 86400, "6일 전")]
    [InlineData(7 * 86400, "지난주")]
    [InlineData(15 * 86400, "2주 전")]
    public void RelativeTime_MatchesMacNamedStyle(int secondsAgo, string expected)
    {
        var now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Local);
        Assert.Equal(expected, ModelRow.Relative(now.AddSeconds(-secondsAgo), now));
    }

    [Fact]
    public void RelativeTime_MonthsYearsAndFuture()
    {
        var now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Local);
        Assert.Equal("지난달", ModelRow.Relative(new DateTime(2026, 5, 10, 12, 0, 0), now));
        Assert.Equal("3개월 전", ModelRow.Relative(new DateTime(2026, 3, 1), now));
        Assert.Equal("작년", ModelRow.Relative(new DateTime(2025, 6, 1), now));
        Assert.Equal("2년 전", ModelRow.Relative(new DateTime(2024, 1, 1), now));
        // 달력 차이로 센다: 어제 23시 → 오늘 1시 = 2시간 전 (날짜가 바뀌어도 '어제' 가 아님)
        Assert.Equal("2시간 전", ModelRow.Relative(new DateTime(2026, 6, 14, 23, 0, 0), new DateTime(2026, 6, 15, 1, 0, 0)));
        Assert.Equal("1분 후", ModelRow.Relative(now.AddSeconds(90), now));
        Assert.Equal("내일", ModelRow.Relative(now.AddHours(25), now));
        // UTC 로 저장된 시각도 같은 결과
        Assert.Equal("3분 전", ModelRow.Relative(now.AddMinutes(-3).ToUniversalTime(), now));
    }

    [Fact]
    public Task Compare_OpenInTab_WithoutConnection_StaysInCompare() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        app.Mode = MainMode.Compare;
        var vm = new CompareViewModel(app)
        {
            Report = new SchemaDiffReport([new SchemaDiffItem(SchemaDiffKind.MissingTable, "tb_member", null, "없음", "CREATE TABLE tb_member (id int);")], 0),
        };
        Assert.True(vm.OpenInTabCommand.CanExecute(null));
        vm.OpenInTabCommand.Execute(null);
        // Mac app.addTab: 연결된 접속이 없으면 안내만 하고 화면은 그대로
        Assert.Equal(MainMode.Compare, app.Mode);
        Assert.Empty(app.Tabs);
        Assert.Contains(app.Logs, l => l.Message.StartsWith("먼저 왼쪽에서 접속을 연결해 주세요."));
    });

    [Fact]
    public Task ModelCanvas_AutoLayout_Animates() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var vm = new ModelingViewModel(app);
        vm.NewModel();
        var m = vm.Model!;
        m.Tables[0].X = 900;
        m.Tables[0].Y = 700;
        var canvas = new ModelCanvas(vm);
        var w = new Window { Width = 800, Height = 600, Content = new ScrollViewer { Content = canvas } };
        w.Show();
        Pump();
        var t = m.Tables[0];
        vm.AutoLayout();
        Assert.True(canvas.LayoutTween.IsRunning);
        // 처음엔 이전 자리에서 출발
        Assert.Equal(new Avalonia.Point(900, 700), canvas.ShownCardOrigin(t));
        await Task.Delay(120);
        Pump();
        var mid = canvas.ShownCardOrigin(t);
        Assert.True(mid.X < 900 && mid.X > t.X, $"중간 {mid} / 끝 {t.X}");
        await Task.Delay(400);
        Pump();
        Assert.False(canvas.LayoutTween.IsRunning);
        Assert.Equal(new Avalonia.Point(t.X, t.Y), canvas.ShownCardOrigin(t));
        w.Close();
    });

    [Fact]
    public Task Modeling_HasNoFitButtonOrSampleEntry() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var view = new ModelingView { DataContext = app };
        var w = new Window { Width = 1100, Height = 700, Content = view };
        w.Show();
        Pump();
        bool Has(string text) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Button>()
            .Any(b => b.Content as string == text || (ToolTip.GetTip(b) as string) == text);
        Assert.False(Has("예시 모델 (쇼핑몰)"));
        view.Vm!.NewModel();
        Pump();
        Assert.False(Has("화면에 맞추기"));
        Assert.True(Has("확대"));
        w.Close();
    });
}
