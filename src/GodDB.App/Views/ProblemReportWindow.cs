using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 문제 보고: 미리 채운 내용을 확인·수정하고 GitHub 이슈 작성 화면을 연다 (Mac ProblemReportView)
[SheetFor(nameof(AppState.ShowProblemReport))]
public sealed class ProblemReportWindow : Window
{
    private static readonly string[] Dbs = ["MySQL", "PostgreSQL", "SQL Server", "Redis", "해당 없음"];

    public TextBox TitleBox { get; } = new() { Watermark = Tr("예: Redis 키 트리를 보다가 앱이 종료돼요") };
    public ComboBox DbBox { get; } = new() { ItemsSource = Dbs, SelectedIndex = Dbs.Length - 1 };
    public TextBox Steps { get; } = Editor(70);
    public TextBox Expected { get; } = Editor(50);
    public TextBox LogBox { get; } = Editor(150, mono: true);
    public CheckBox IncludeErrors { get; } = new() { Content = Tr("최근 오류"), IsChecked = true };
    public CheckBox IncludeCrashes { get; } = new() { IsChecked = true };
    private readonly AppState _app;
    private readonly List<ProblemReport.Crash> _crashes;

    private static TextBox Editor(double height, bool mono = false)
    {
        var t = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = height };
        if (mono) { t.Classes.Add("mono"); t.FontSize = 11; }
        return t;
    }

    public ProblemReportWindow(AppState app, object value)
    {
        _app = app;
        Title = Tr("문제 보고");
        Width = 660;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _crashes = ProblemReport.RecentCrashes();
        IncludeCrashes.Content = Tr("크래시 보고서 %@개", _crashes.Count);
        IncludeCrashes.IsEnabled = _crashes.Count > 0;
        IncludeErrors.IsCheckedChanged += (_, _) => Rebuild();
        IncludeCrashes.IsCheckedChanged += (_, _) => Rebuild();
        DbBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((s, _) => new TextBlock { Text = Tr(s) });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"), RowSpacing = 10, ColumnSpacing = 10 };
        void Row(int r, string label, Control c)
        {
            var t = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = r < 2 ? VerticalAlignment.Center : VerticalAlignment.Top, Margin = new Thickness(0, r < 2 ? 0 : 4, 0, 0) };
            Grid.SetRow(t, r);
            Grid.SetRow(c, r);
            Grid.SetColumn(c, 1);
            grid.Children.Add(t);
            grid.Children.Add(c);
        }
        Row(0, Tr("제목"), TitleBox);
        Row(1, Tr("DB 종류"), DbBox);
        Row(2, Tr("재현 방법"), Steps);
        Row(3, Tr("기대 / 실제"), Expected);
        Row(4, Tr("함께 보낼 기록"), Ui.Stack(6, Ui.HStack(16, IncludeErrors, IncludeCrashes), LogBox,
            Ui.Caption("🙈 " + Tr("호스트 · IP · 이메일 · 계정 · 홈 경로는 가렸어요. 공개 이슈로 올라가니 보내기 전에 한 번 더 확인해 주세요."))));

        var open = Ui.Button(Tr("GitHub 에서 열기"), OpenIssue, isDefault: true);
        ToolTip.SetTip(open, Tr("GitHub 버그 신고 화면을 미리 채워 브라우저로 열어요 (GitHub 로그인 필요)"));
        Content = new Border
        {
            Padding = new Thickness(20),
            Child = Ui.Stack(12,
                Ui.Text("🐞 " + Tr("문제 보고"), "title"),
                Ui.Text(ProblemReport.Environment(AppState.Version), "secondary"),
                grid,
                Ui.ButtonRow([open, Ui.Button(Tr("취소"), Close, isCancel: true)],
                             [Ui.Button(Tr("복사"), () => app.Fire(() => app.Dialogs.SetClipboardTextAsync(ReportText())))])),
        };
        Rebuild();
    }

    private string Db => DbBox.SelectedItem as string ?? "해당 없음";

    public string ReportText() =>
        $"## {TitleBox.Text}\n{ProblemReport.Environment(AppState.Version)}\n\n### 재현 방법\n{Steps.Text}\n\n### 기대 / 실제\n{Expected.Text}\n\n```\n{LogBox.Text}\n```";

    public void Rebuild()
    {
        var errors = IncludeErrors.IsChecked == true
            ? _app.Logs.Where(e => e.Level == LogLevel.Error).TakeLast(20)
                .Select(e => $"[{e.Source.RawValue()}] {e.Message}" + (e.Hint != null ? $" — {e.Hint}" : "")).ToList()
            : [];
        LogBox.Text = ProblemReport.Redact(ProblemReport.LogText(errors, IncludeCrashes.IsChecked == true ? _crashes : []));
    }

    private void OpenIssue()
    {
        var title = string.IsNullOrEmpty(TitleBox.Text) ? Tr("문제 보고") : TitleBox.Text;
        var url = ProblemReport.IssueUrl(title, AppState.Version, Db, Steps.Text ?? "", Expected.Text ?? "", LogBox.Text ?? "");
        _app.Dialogs.OpenExternal(url.AbsoluteUri);
        Close();
    }
}
