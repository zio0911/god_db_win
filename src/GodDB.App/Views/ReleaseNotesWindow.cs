using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 새로운 기능 (버전별 변경 내용) — Mac ReleaseNotesView. 새 버전을 처음 켰으면 since 이후 버전을 강조한다.
[WindowFor(WindowKinds.ReleaseNotes)]
public sealed class ReleaseNotesWindow : Window
{
    public ReleaseNotesWindow(AppState app, object? argument)
    {
        var since = argument as string;
        var version = AppState.Version;
        Title = Tr("새로운 기능");
        Width = 640;
        Height = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ask = Ui.Button(Tr("AI 에게 물어보기"), () =>
        {
            Close();
            app.Assistant.Visible = true;
            app.AskAssistant(ReleaseNotes.AskQuestion(since, version));
        });
        ToolTip.SetTip(ask, Tr("AI 어시스턴트가 변경 이력을 보고 버전별 차이를 설명해요"));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(Ui.Text("✨ " + (since == null ? Tr("새로운 기능") : Tr("%@ 로 업데이트됐어요", version)), "title"));
        Grid.SetColumn(ask, 1);
        header.Children.Add(ask);

        var list = new StackPanel { Spacing = 12 };
        foreach (var e in AppState.ReleaseNotesEntries)
        {
            var isNew = since != null && ReleaseNotes.Compare(e.Version, since) > 0;
            var head = Ui.HStack(6, Ui.Headline(e.Version));
            if (e.Version == version)
                head.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#260A84FF")), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1),
                    Child = new TextBlock { Text = Tr("지금 버전"), FontSize = 11 },
                });
            else if (isNew)
                head.Children.Add(new TextBlock { Text = "NEW", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#FF9500")) });
            var block = Ui.Stack(6, head);
            foreach (var line in e.Lines)
                block.Children.Add(new SelectableTextBlock
                {
                    Text = (line.StartsWith("- ") ? "• " + line[2..] : line).Replace("**", "").Replace("`", ""),
                    TextWrapping = TextWrapping.Wrap,
                });
            list.Children.Add(new Border
            {
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(8),
                Background = isNew || e.Version == version ? new SolidColorBrush(Color.Parse("#0F0A84FF")) : Brushes.Transparent,
                Child = block,
            });
        }
        if (list.Children.Count == 0) list.Children.Add(Ui.Text(Tr("변경 이력이 없어요."), "secondary"));

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        var close = Ui.ButtonRow([Ui.Button(Tr("닫기"), Close, isDefault: true, isCancel: true)]);
        DockPanel.SetDock(close, Dock.Bottom);
        dock.Children.Add(close);
        dock.Children.Add(new ScrollViewer { Content = list, Margin = new Thickness(0, 12) });
        Content = new Border { Padding = new Thickness(20), Child = dock };
    }
}
