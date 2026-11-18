using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 쿼리 튜닝 결과 창 (Mac TuningView.swift TuningSheet). 개선 SQL 과 추천 인덱스는 절대 자동으로 실행하지 않는다
/// — 복사 / 편집기에 넣기 / 새 탭으로만.
[SheetFor(nameof(AppState.TuningJob))]
public sealed class TuningSheet : Window
{
    private readonly AppState _app;
    public TuningJob Job { get; }
    /// 가운데 내용 (작업이 바뀔 때마다 다시 그린다)
    public StackPanel Body { get; } = new() { Spacing = 14, Margin = new Thickness(16) };
    private readonly TextBlock _elapsed = Ui.Text("", "secondary");
    private readonly StackPanel _footerButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Expander _promptBox;
    private readonly SelectableTextBlock _promptText = new() { Classes = { "mono" }, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private bool _renderQueued;

    public TuningSheet(AppState app, object value)
    {
        _app = app;
        Job = (TuningJob)value;
        Title = Tr("쿼리 튜닝");
        Width = 820;
        Height = 720;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var header = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(_elapsed, Dock.Right);
        header.Children.Add(_elapsed);
        header.Children.Add(Ui.HStack(8,
            new TextBlock { Text = Glyphs.Sparkles, Foreground = new SolidColorBrush(Color.Parse("#AF52DE")) },
            new TextBlock { Text = Tr("쿼리 튜닝"), Classes = { "headline" } },
            Ui.Text($"{Job.ConnectionName} · {(Job.Model.Length == 0 ? Tr("자동 모델") : Job.Model)}", "secondary")));

        _promptBox = new Expander
        {
            Header = Tr("Ollama 에 보낸 정보 보기"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = CodeBorder(_promptText),
        };

        var footer = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(_footerButtons, Dock.Right);
        footer.Children.Add(_footerButtons);
        footer.Children.Add(new TextBlock { Text = Tr("개선 SQL 과 인덱스는 자동으로 실행하지 않아요."), Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center });

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(Divider(Dock.Top));
        root.Children.Add(footer);
        root.Children.Add(Divider(Dock.Bottom));
        root.Children.Add(new ScrollViewer { Content = Body });
        Content = root;

        Job.Changed += OnJobChanged;
        Closed += (_, _) =>
        {
            Job.Changed -= OnJobChanged;
            // 창을 닫으면 진행 중인 튜닝도 멈춘다 (Mac [닫기]: cancelTuning + tuningJob = nil)
            if (Job.IsRunning) Job.Cancel();
        };
        Render();
    }

    private static Border Divider(Dock dock)
    {
        var b = new Border { Classes = { "divider" }, Height = 1 };
        DockPanel.SetDock(b, dock);
        return b;
    }

    /// TuningJob.Changed 는 스트리밍 중 아무 스레드에서나 자주 온다 → UI 스레드에서 한 번에 모아 그린다
    private void OnJobChanged()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.UIThread.Post(() => { _renderQueued = false; Render(); }, DispatcherPriority.Background);
    }

    public void Render()
    {
        var job = Job;
        _elapsed.Text = job.Finished is { } f ? Tr("%@초", (int)(f - job.Started).TotalSeconds) : "";
        Body.Children.Clear();
        Body.Children.Add(CodeBox(Tr("원래 쿼리"), job.Sql, actions: false));
        if (job.Result is { } result && job.Phase != TuningPhase.Writing) AddResult(result);
        else AddProgress();
        _promptText.Text = job.Prompt.Length == 0 ? Tr("아직 모으는 중이에요.") : job.Prompt;
        Body.Children.Add(_promptBox);

        _footerButtons.Children.Clear();
        if (job.IsRunning) _footerButtons.Children.Add(Ui.Button(Tr("취소"), _app.CancelTuning));
        else _footerButtons.Children.Add(Ui.Button(Tr("다시 튜닝"), () => _app.StartTuning(job.Sql)));
        _footerButtons.Children.Add(Ui.Button(Tr("닫기"), CloseSheet, isCancel: true));
    }

    private void CloseSheet()
    {
        _app.CancelTuning();
        _app.TuningJob = null;
        Close();
    }

    private void AddProgress()
    {
        var job = Job;
        if (job.Phase == TuningPhase.Failed)
            Body.Children.Add(new SelectableTextBlock { Text = "⛔ " + job.PhaseText, Classes = { "error" }, TextWrapping = TextWrapping.Wrap });
        else
            Body.Children.Add(Ui.HStack(8,
                new ProgressBar { IsIndeterminate = true, Width = 60, MinWidth = 60, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = job.PhaseText }));
        if (job.Streamed.Length > 0)
            Body.Children.Add(CodeBorder(new SelectableTextBlock { Text = job.Streamed, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap }));
    }

    private void AddResult(TuningResult r)
    {
        var job = Job;
        if (job.Phase == TuningPhase.Failed)
            Body.Children.Add(new SelectableTextBlock { Text = "⛔ " + job.PhaseText, Classes = { "error" }, TextWrapping = TextWrapping.Wrap });

        Body.Children.Add(new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#AF52DE"), 0.07),
            Child = Ui.Stack(6, Ui.Headline(Tr("요약")),
                new SelectableTextBlock { Text = r.Summary.Length == 0 ? Tr("(요약 없음)") : r.Summary, TextWrapping = TextWrapping.Wrap }),
        });

        Body.Children.Add(PlanCompare());

        if (r.ImprovedSql is { } sql) Body.Children.Add(CodeBox(Tr("개선 SQL"), sql, actions: true));
        if (r.IndexSql is { } idx)
        {
            Body.Children.Add(CodeBox(Tr("추천 인덱스"), idx, actions: r.HasIndexes));
            if (r.HasIndexes) Body.Children.Add(Ui.Caption(Tr("인덱스는 쓰기 성능과 저장 공간에도 영향을 줘요. 운영 DB 에는 검토 후 만드세요.")));
        }
        if (r.Explanation.Length > 0)
            Body.Children.Add(Ui.Stack(6, Ui.Headline(Tr("설명")),
                new SelectableTextBlock { Text = r.Explanation, TextWrapping = TextWrapping.Wrap }));
    }

    /// 실행 계획 주의사항 (전 → 후)
    private Control PlanCompare()
    {
        var job = Job;
        var before = job.Context?.Warnings ?? [];
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        row.Children.Add(WarningList(Tr("원래 쿼리"), before));
        Control? after = null;
        if (job.AfterWarnings is { } aw) after = WarningList(Tr("개선 SQL"), aw);
        else if (job.AfterError is { } e)
            after = Ui.Stack(2, new TextBlock { Text = Tr("개선 SQL"), FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = Tr("실행 계획을 확인할 수 없어요: %@", e), Classes = { "caption", "error" }, TextWrapping = TextWrapping.Wrap });
        if (after != null) { Grid.SetColumn(after, 1); row.Children.Add(after); }
        return Ui.Stack(6, Ui.Headline(Tr("실행 계획 주의사항 (전 → 후)")), row,
            Ui.Caption(Tr("추천 인덱스는 아직 만들지 않았으므로, 인덱스 효과는 '후' 계획에 반영되지 않아요.")));
    }

    private static Control WarningList(string title, IReadOnlyList<PlanAnalyzer.Warning> list)
    {
        var s = Ui.Stack(3, new TextBlock
        {
            Text = title + " · " + TuningJob.WarningSummary(list),
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(list.Count == 0 ? "#34C759" : "#FF9500")),
        });
        foreach (var w in list) s.Children.Add(Ui.Caption($"[{w.Table}] {w.Message}"));
        return s;
    }

    internal static Border CodeBorder(Control child)
    {
        var b = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(6), Child = child };
        b.Bind(Border.BackgroundProperty, b.GetResourceObservable("CodeBackground"));
        return b;
    }

    private Control CodeBox(string title, string sql, bool actions)
    {
        var buttons = Ui.HStack(4, new CopyButton { Title = "⧉ " + Tr("복사"), Text = sql });
        if (actions)
        {
            var insert = new Button { Content = "↧ " + Tr("편집기에 넣기"), Classes = { "hover" } };
            insert.Click += (_, _) => _app.InsertIntoTab(Job.TabId, sql, title);
            var newTab = new Button { Content = Glyphs.Plus + " " + Tr("새 탭으로"), Classes = { "hover" } };
            newTab.Click += (_, _) =>
            {
                if (_app.Tab(Job.TabId) is { } tab) _app.AddTab(sql + ";\n", title, tab.ProfileId);
            };
            buttons.Children.Add(insert);
            buttons.Children.Add(newTab);
        }
        var head = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        head.Children.Add(new TextBlock { Text = title, Classes = { "headline" }, VerticalAlignment = VerticalAlignment.Center });
        return Ui.Stack(6, head, CodeBorder(new SelectableTextBlock { Text = sql, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap }));
    }
}
