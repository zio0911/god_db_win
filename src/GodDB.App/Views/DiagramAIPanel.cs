using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 다이어그램 오른쪽 AI 패널: 관계 분석 + 채팅 + 대화 기록 (Mac DiagramAIPanel · AIChatTranscript · MarkdownBlocks).
/// 테이블 구조와 관계만 로컬 Ollama 에 보낸다 (데이터 행은 보내지 않음 — Core DiagramAI.SchemaText).
public sealed partial class DiagramAIPanel : Border
{
    public AppState App { get; }
    public DiagramDoc Doc { get; }
    public AiChat Chat { get; }

    public TextBox Input { get; }
    private readonly StackPanel _transcript = new() { Spacing = 10, Margin = new Thickness(10) };
    private readonly ScrollViewer _scroll;
    private readonly Button _newButton;
    private readonly Button _historyButton;
    private readonly Button _trashButton;
    private readonly Button _sendButton;
    private bool _rebuildQueued;

    public DiagramAIPanel(AppState app, DiagramDoc doc, AiChat chat)
    {
        App = app;
        Doc = doc;
        Chat = chat;
        this.Bind(BackgroundProperty, this.GetResourceObservable("PanelBackground"));

        _newButton = IconButton("＋💬", Tr("새 대화 (지금 대화는 기록에 남아요)"), Chat.StartNew);
        _historyButton = IconButton("🕘", Tr("이전 대화 (90일 보관)"), () => { });
        _historyButton.Flyout = new MenuFlyout();
        ((MenuFlyout)_historyButton.Flyout).Opening += (_, _) => FillHistory((MenuFlyout)_historyButton.Flyout!);
        _trashButton = IconButton("🗑", Tr("지금 대화 삭제"), () => Chat.Delete(Chat.ConversationId));

        var header = new DockPanel { Margin = new Thickness(10) };
        var buttons = Ui.HStack(2, _newButton, _historyButton, _trashButton);
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(Ui.HStack(6,
            new TextBlock { Text = Glyphs.Sparkles, Foreground = new SolidColorBrush(DiagramCanvas.Purple), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = Tr("AI 관계 분석"), Classes = { "headline" }, VerticalAlignment = VerticalAlignment.Center }));

        _scroll = new ScrollViewer { Content = _transcript, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        Input = new TextBox
        {
            PlaceholderText = Tr("질문을 입력하고 Enter"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 110,
        };
        // Enter 로 보내기, Shift+Enter 는 줄바꿈 (Mac TextField onSubmit)
        Input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
            {
                e.Handled = true;
                Send();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Input.TextChanged += (_, _) => UpdateButtons();
        _sendButton = IconButton("⬆", Tr("보내기"), () => { if (Chat.Running) Chat.Cancel(); else Send(); });
        _sendButton.FontSize = 16;
        _sendButton.VerticalAlignment = VerticalAlignment.Bottom;
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        inputRow.Children.Add(Input);
        Grid.SetColumn(_sendButton, 1);
        inputRow.Children.Add(_sendButton);
        var footer = Ui.Stack(6, inputRow,
            new TextBlock { Text = Tr("테이블 구조와 관계만 로컬 Ollama 에 보내요. 데이터 행은 보내지 않아요."), FontSize = 10, TextWrapping = TextWrapping.Wrap, Classes = { "secondary" } });
        footer.Margin = new Thickness(10);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var d1 = Divider();
        DockPanel.SetDock(d1, Dock.Top);
        root.Children.Add(d1);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var d2 = Divider();
        DockPanel.SetDock(d2, Dock.Bottom);
        root.Children.Add(d2);
        root.Children.Add(_scroll);
        Child = root;

        Chat.Changed += QueueRebuild;
        DetachedFromVisualTree += (_, _) => Chat.Changed -= QueueRebuild;
        Rebuild();
    }

    private static Border Divider()
    {
        var b = new Border { Height = 1 };
        b.Bind(Border.BackgroundProperty, b.GetResourceObservable("DividerBrush"));
        return b;
    }

    private static Button IconButton(string text, string tip, Action onClick)
    {
        var b = new Button { Content = text, Classes = { "hover" } };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    // MARK: - 보내기

    public void Ask(string question) =>
        App.Fire(() => Chat.AskDiagramAsync(question, Doc.Kind, Doc.Tables, Doc.Inferred, App.AiChatHooks()));

    public void Send()
    {
        var q = Input.Text ?? "";
        if (q.Trim().Length == 0 || Chat.Running) return;
        Input.Text = "";
        Ask(q);
        Input.Focus();
    }

    // MARK: - 대화 기록 메뉴

    private void FillHistory(MenuFlyout menu)
    {
        menu.Items.Clear();
        if (Chat.History.Count == 0)
            menu.Items.Add(new MenuItem { Header = Tr("저장된 대화가 없어요."), IsEnabled = false });
        foreach (var c in Chat.History)
        {
            var item = new MenuItem
            {
                Header = (c.Id == Chat.ConversationId ? "● " : "") + c.Updated.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                         + " · " + (c.Title.Length == 0 ? Tr("(제목 없음)") : c.Title),
            };
            var open = new MenuItem { Header = Tr("열기") };
            open.Click += (_, _) => Chat.Open(c);
            var delete = new MenuItem { Header = Tr("삭제") };
            delete.Click += (_, _) => Chat.Delete(c.Id);
            item.Items.Add(open);
            item.Items.Add(delete);
            menu.Items.Add(item);
        }
        if (Chat.History.Count > 0)
        {
            menu.Items.Add(new Separator());
            var all = new MenuItem { Header = Tr("이 다이어그램의 대화 모두 삭제") };
            all.Click += (_, _) => Chat.DeleteAll();
            menu.Items.Add(all);
        }
    }

    // MARK: - 대화 내용

    /// 답변이 흘러오는 동안 조각마다 다시 그리지 않도록 모아서 한 번에
    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        }, DispatcherPriority.Background);
    }

    private void UpdateButtons()
    {
        _newButton.IsEnabled = Chat.Messages.Count > 0;
        _trashButton.IsVisible = Chat.Messages.Count > 0;
        _trashButton.IsEnabled = !Chat.Running;
        _sendButton.Content = Chat.Running ? "■" : "⬆";
        ToolTip.SetTip(_sendButton, Chat.Running ? Tr("취소") : Tr("보내기"));
        _sendButton.IsEnabled = Chat.Running || (Input.Text ?? "").Trim().Length > 0;
    }

    /// 진행 표시 문장 (실행 중이 아니면 null)
    public string? ProgressText => Chat.Running ? Chat.ProgressText : null;

    public void Rebuild()
    {
        UpdateButtons();
        _transcript.Children.Clear();
        if (Chat.Messages.Count == 0) _transcript.Children.Add(Intro());
        var last = Chat.Messages.LastOrDefault();
        foreach (var m in Chat.Messages) _transcript.Children.Add(Bubble(m, streaming: Chat.Running && ReferenceEquals(m, last)));
        if (ProgressText is { } p)
            _transcript.Children.Add(Ui.HStack(6,
                new ProgressBar { IsIndeterminate = true, Width = 40, MinWidth = 40, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = p, FontSize = 11, Classes = { "secondary" } }));
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private Control Intro()
    {
        var s = Ui.Stack(8,
            new TextBlock { Text = Tr("다이어그램의 테이블 %@개 구조와 관계를 바탕으로 분석하고, 궁금한 점을 물어볼 수 있어요.", Doc.Tables.Count), TextWrapping = TextWrapping.Wrap, Classes = { "secondary" } });
        var start = new Button
        {
            Content = Glyphs.Sparkles + " " + Tr("관계 분석 시작"),
            Background = new SolidColorBrush(DiagramCanvas.Purple),
            Foreground = Brushes.White,
        };
        start.Click += (_, _) => Ask(DiagramAI.AnalysisQuestion(IsEnglish));
        s.Children.Add(start);
        s.Children.Add(new TextBlock { Text = Tr("예시 질문"), FontSize = 11, FontWeight = FontWeight.SemiBold, Classes = { "secondary" }, Margin = new Thickness(0, 4, 0, 0) });
        foreach (var q in AiChat.DiagramExampleQuestions)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = q, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                Classes = { "hover" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            b.Click += (_, _) => Ask(q);
            s.Children.Add(b);
        }
        return s;
    }

    private Control Bubble(AiChat.Message m, bool streaming)
    {
        var isUser = m.Role == "user";
        Control content = isUser || m.Failed
            ? new SelectableTextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, Foreground = m.Failed ? new SolidColorBrush(Color.Parse("#FF3B30")) : null }
            : Markdown(m.Text);
        var bubble = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(isUser ? Color.FromArgb(0x26, 0x0A, 0x84, 0xFF) : Color.FromArgb(0x12, 0xAF, 0x52, 0xDE)),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Child = content,
        };
        var stack = new StackPanel { Spacing = 4, HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch };
        stack.Children.Add(bubble);
        if (!isUser && m.Text.Length > 0 && !streaming)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            // Mac CopyButton: 누르면 1.5초 동안 초록 "복사됨" 으로 바뀌었다가 돌아온다
            var text = m.Text;
            var copy = new CopyButton { Title = Tr("복사"), FontSize = 11, Copier = t => App.Dialogs.SetClipboardTextAsync(t), TextProvider = () => text };
            actions.Children.Add(copy);
            if (AiText.FirstSql(m.Text) is { } sql)
            {
                var insert = new Button { Content = Tr("SQL 편집기에 넣기"), Classes = { "hover" }, FontSize = 11, IsEnabled = App.Tabs.Count > 0 };
                insert.Click += (_, _) => InsertSql(sql);
                actions.Children.Add(insert);
            }
            stack.Children.Add(actions);
        }
        return stack;
    }

    /// 답변의 SQL 을 지금(없으면 마지막) 쿼리 탭 끝에 넣는다
    public bool InsertSql(string sql)
    {
        if ((App.SelectedTab?.Id ?? App.Tabs.LastOrDefault()?.Id) is not { } tabId) return false;
        if (App.Mode != MainMode.Query) App.Mode = MainMode.Query;
        App.InsertIntoTab(tabId, sql, Tr("AI 관계 분석"));
        return true;
    }

    // MARK: - 간단한 마크다운 (제목 · 코드 블록 · 구분선 · 굵게 · `코드`)

    private static FontFamily Mono => Application.Current?.TryGetResource("MonoFont", null, out var f) == true && f is FontFamily ff
        ? ff : new FontFamily("Cascadia Mono, Consolas, Menlo, monospace");

    public static Control Markdown(string text)
    {
        var s = new StackPanel { Spacing = 6 };
        foreach (var block in AiText.Blocks(text))
        {
            switch (block)
            {
                case AiText.Block.Heading h:
                    s.Children.Add(InlineText(h.Text, h.Level <= 2 ? 16 : 14, FontWeight.SemiBold));
                    break;
                case AiText.Block.Code c:
                    var code = new Border
                    {
                        Padding = new Thickness(6),
                        CornerRadius = new CornerRadius(5),
                        Child = new SelectableTextBlock { Text = c.Text, FontFamily = Mono, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                    };
                    code.Bind(Border.BackgroundProperty, code.GetResourceObservable("CodeBackground"));
                    s.Children.Add(code);
                    break;
                case AiText.Block.Rule:
                    s.Children.Add(Divider());
                    break;
                case AiText.Block.Paragraph p:
                    s.Children.Add(InlineText(p.Text, 13, FontWeight.Normal));
                    break;
            }
        }
        return s;
    }

    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`)")]
    private static partial Regex InlineRegex();

    private static SelectableTextBlock InlineText(string text, double size, FontWeight weight)
    {
        var t = new SelectableTextBlock { FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Inlines = [] };
        foreach (var part in InlineRegex().Split(text))
        {
            if (part.Length == 0) continue;
            if (part.Length > 4 && part.StartsWith("**") && part.EndsWith("**"))
                t.Inlines!.Add(new Run(part[2..^2]) { FontWeight = FontWeight.Bold });
            else if (part.Length > 2 && part.StartsWith('`') && part.EndsWith('`'))
                t.Inlines!.Add(new Run(part[1..^1]) { FontFamily = Mono, FontSize = size - 1 });
            else
                t.Inlines!.Add(new Run(part));
        }
        return t;
    }
}
