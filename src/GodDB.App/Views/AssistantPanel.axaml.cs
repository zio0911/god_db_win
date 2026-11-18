using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 메인 창 오른쪽 AI 어시스턴트 패널 (Mac AssistantPanel + AIChatTranscript). DataContext = AppState.
/// 스트리밍 중에는 마지막 답 말풍선만 다시 그린다.
public partial class AssistantPanel : UserControl
{
    public AssistantPanelViewModel? Vm { get; private set; }

    /// 마지막으로 그린 대화 모양 (대화 id, 메시지 수, 답하는 중) — 같으면 마지막 말풍선만 바꾼다
    private (Guid, int, bool)? _drawn;
    private StackPanel? _lastBubbleBody;
    private bool _scrollQueued;

    public AssistantPanel()
    {
        InitializeComponent();
        HistoryButton.Content = new GodDB.App.Controls.SymbolIcon(GodDB.App.Controls.Symbol.History);
        // 언어를 바꾸면 코드로 그린 부분(보낼 정보 · 자주 쓰는 질문 · 안내 · 대화)을 새 언어로
        GodDB.Core.L10n.LanguageChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext == null) return;
            try { DrawContext(); DrawChips(); UpdateHint(); UpdateInput(); DrawTranscript(); } catch { }
        });
        InputBox.AcceptsReturn = true;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AppState app && Vm?.App != app) Attach(app);
        };
        // Enter = 보내기, Shift+Enter = 줄바꿈
        InputBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                Vm?.Send();
            }
        }, RoutingStrategies.Tunnel);
        InputBox.TextChanged += (_, _) =>
        {
            if (Vm == null) return;
            Vm.Input = InputBox.Text ?? "";
        };
        SendButton.Click += (_, _) => Vm?.Send();
        StopButton.Click += (_, _) => Vm?.Cancel();
        NewChat.Click += (_, _) => Vm?.StartNew();
        HistoryButton.Click += (_, _) => ShowHistory();
        QuickMenu.Click += (_, _) => ShowQuickMenu();
        ClosePanel.Click += (_, _) => { if (Vm != null) Vm.App.Assistant.Visible = false; };
        IncludeSql.IsCheckedChanged += (_, _) => { if (Vm != null) Vm.App.Assistant.IncludeSql = IncludeSql.IsChecked == true; };
        HintFix.Click += async (_, _) => { if (Vm != null) await Vm.FixOllamaAsync(); };
        PropertyChanged += (_, e) =>
        {
            // 보일 때: 모델 선택처럼 알림이 없는 범위 변화를 다시 보고, Ollama 상태를 확인한다
            if (e.Property == IsVisibleProperty && IsVisible && Vm != null)
            {
                Vm.Refresh();
                _ = Vm.CheckOllamaAsync();
            }
        };
    }

    private void Attach(AppState app)
    {
        Vm = new AssistantPanelViewModel(app);
        Vm.ChatChanged += DrawTranscript;
        Vm.ContextChanged += DrawContext;
        Vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AssistantPanelViewModel.Input):
                    if ((InputBox.Text ?? "") != Vm.Input) InputBox.Text = Vm.Input;
                    UpdateInput();
                    DrawChips();
                    break;
                case nameof(AssistantPanelViewModel.OllamaHint):
                case nameof(AssistantPanelViewModel.OllamaFix):
                    UpdateHint();
                    break;
                case nameof(AssistantPanelViewModel.Title):
                    ScopeTitle.Text = Vm.Title;
                    break;
            }
        };
        DrawContext();
        DrawTranscript();
        UpdateHint();
    }

    // MARK: - 머리 · 문맥

    private void DrawContext()
    {
        if (Vm == null) return;
        ScopeTitle.Text = Vm.Title;
        IncludeSql.IsVisible = Vm.IsSql;
        IncludeSql.IsChecked = Vm.App.Assistant.IncludeSql;
        DrawChips();
        // 범위가 같아도 빠른 질문(모델링 / SQL)은 바뀔 수 있다
        if (Vm.Chat.Messages.Count == 0) { _drawn = null; DrawTranscript(); }
    }

    private void DrawChips()
    {
        if (Vm == null) return;
        Chips.Children.Clear();
        foreach (var (icon, text) in Vm.Chips())
        {
            Chips.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xAF, 0x52, 0xDE), 0.08),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = $"{icon}  {text}", FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis },
            });
        }
    }

    private void UpdateHint()
    {
        if (Vm == null) return;
        HintRow.IsVisible = Vm.OllamaHint != null;
        HintText.Text = Vm.OllamaHint;
        HintFix.IsVisible = Vm.OllamaFix != AssistantPanelViewModel.OllamaAction.None;
        HintFix.Content = Vm.OllamaFix == AssistantPanelViewModel.OllamaAction.Start ? Tr("Ollama 실행") : Tr("Ollama 내려받기 (ollama.com)");
    }

    private void UpdateInput()
    {
        if (Vm == null) return;
        var running = Vm.Chat.Running;
        SendButton.IsVisible = !running;
        StopButton.IsVisible = running;
        SendButton.IsEnabled = Vm.CanSend;
        QuickMenu.IsEnabled = !running;
        NewChat.IsEnabled = Vm.Chat.Messages.Count > 0;
    }

    private void ShowHistory()
    {
        if (Vm == null) return;
        var chat = Vm.Chat;
        var items = new List<Control>();
        if (chat.History.Count == 0) items.Add(new MenuItem { Header = Tr("저장된 대화가 없어요."), IsEnabled = false });
        foreach (var c in chat.History)
        {
            var title = (c.Id == chat.ConversationId ? "● " : "") + c.Updated.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                        + " · " + (c.Title.Length == 0 ? Tr("(제목 없음)") : c.Title);
            var open = new MenuItem { Header = Tr("열기") };
            open.Click += (_, _) => Vm.Open(c);
            var delete = new MenuItem { Header = Tr("삭제"), Classes = { "danger" } };
            delete.Click += (_, _) => Vm.Delete(c.Id);
            items.Add(new MenuItem { Header = title, ItemsSource = new List<Control> { open, delete } });
        }
        if (chat.History.Count > 0)
        {
            items.Add(new Separator());
            var all = new MenuItem { Header = Tr("이 범위의 대화 모두 삭제…") };
            all.Click += async (_, _) =>
            {
                var n = chat.History.Count;
                if (await Vm.App.Dialogs.ConfirmAsync(Tr("이 범위의 대화를 모두 삭제할까요?"), Tr("지난 대화 %@개가 지워져요. 되돌릴 수 없어요.", n),
                                                      Tr("삭제"), Tr("취소"), destructive: true))
                    Vm.DeleteAll();
            };
            items.Add(all);
        }
        new ContextMenu { ItemsSource = items }.Open(HistoryButton);
    }

    private void ShowQuickMenu()
    {
        if (Vm == null) return;
        var items = new List<Control>();
        foreach (var q in Vm.QuickActions)
        {
            var m = new MenuItem { Header = q };
            m.Click += (_, _) => Vm.AskQuick(q);
            items.Add(m);
        }
        new ContextMenu { ItemsSource = items }.Open(QuickMenu);
    }

    // MARK: - 대화

    private void DrawTranscript()
    {
        if (Vm == null) return;
        UpdateInput();
        var chat = Vm.Chat;
        var shape = (chat.ConversationId, chat.Messages.Count, chat.Running);
        if (_drawn == shape && _lastBubbleBody != null && chat.Messages.LastOrDefault() is { } last)
        {
            // 스트리밍: 마지막 답만 다시 그린다
            FillBody(_lastBubbleBody, last);
            UpdateProgress();
            QueueScroll();
            return;
        }
        _drawn = shape;
        _lastBubbleBody = null;
        Transcript.Children.Clear();
        if (chat.Messages.Count == 0) Transcript.Children.Add(Intro());
        foreach (var m in chat.Messages) Transcript.Children.Add(Bubble(m));
        if (chat.Running)
        {
            var progress = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Name = "Progress" };
            progress.Children.Add(new GodDB.App.Controls.ActivitySpinner { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
            progress.Children.Add(Ui.Caption(chat.ProgressText));
            Transcript.Children.Add(progress);
        }
        QueueScroll();
    }

    private void UpdateProgress()
    {
        if (Vm == null) return;
        if (Transcript.Children.LastOrDefault() is StackPanel { Name: "Progress" } p && p.Children.LastOrDefault() is TextBlock t)
            t.Text = Vm.Chat.ProgressText;
    }

    private void QueueScroll()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            TranscriptScroll.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private Control Intro()
    {
        var s = new StackPanel { Spacing = 8 };
        s.Children.Add(Ui.Text(Vm!.IntroText, "secondary"));
        var head = Ui.Caption(Tr("자주 쓰는 질문"));
        head.FontWeight = FontWeight.SemiBold;
        head.Margin = new Thickness(0, 4, 0, 0);
        s.Children.Add(head);
        foreach (var q in Vm.QuickActions)
        {
            var b = new Button
            {
                Classes = { "hover" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new TextBlock { Text = "⚡ " + q, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                IsEnabled = !Vm.Chat.Running,
            };
            b.Click += (_, _) => Vm.AskQuick(q);
            s.Children.Add(b);
        }
        return s;
    }

    private Control Bubble(AiChat.Message m)
    {
        var isUser = m.Role == "user";
        var body = new StackPanel { Spacing = 6 };
        FillBody(body, m);
        var outer = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
        };
        outer.Children.Add(new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            Background = isUser ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF), 0.15) : new SolidColorBrush(Color.FromRgb(0xAF, 0x52, 0xDE), 0.07),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Child = body,
        });
        if (ReferenceEquals(m, Vm!.Chat.Messages.LastOrDefault())) _lastBubbleBody = body;
        return outer;
    }

    /// 말풍선 내용 + (끝난 답이면) 아래 버튼 줄
    private void FillBody(StackPanel body, AiChat.Message m)
    {
        body.Children.Clear();
        if (m.Role == "user" || m.Failed)
        {
            var t = new SelectableTextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap };
            if (m.Failed) t.Classes.Add("error");
            body.Children.Add(t);
        }
        else
        {
            foreach (var block in AiText.Blocks(m.Text)) body.Children.Add(Block(block));
        }
        if (m.Role != "user" && Vm!.IsFinished(m)) body.Children.Add(Actions(m));
    }

    private static Control Block(AiText.Block b) => b switch
    {
        AiText.Block.Heading h => new SelectableTextBlock { Text = h.Text, FontWeight = FontWeight.SemiBold, FontSize = h.Level <= 1 ? 15 : h.Level == 2 ? 14 : 13, TextWrapping = TextWrapping.Wrap },
        AiText.Block.Code c => new Border
        {
            Background = Res("CodeBackground"),
            BorderBrush = Res("DividerBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6),
            Child = new SelectableTextBlock { Text = c.Text, Classes = { "mono" }, FontSize = 12, TextWrapping = TextWrapping.Wrap },
        },
        AiText.Block.Rule => new Border { Height = 1, Classes = { "divider" } },
        AiText.Block.Paragraph p => new SelectableTextBlock { Text = p.Text.Replace("**", ""), TextWrapping = TextWrapping.Wrap },
        _ => new TextBlock(),
    };

    private Control Actions(AiChat.Message m)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        var copy = new CopyButton { Title = Tr("복사"), Text = m.Text, FontSize = 11 };
        row.Children.Add(copy);
        if (AssistantPanelViewModel.SqlOf(m) != null)
        {
            var insert = new Button { Content = Tr("SQL 편집기에 넣기"), Classes = { "hover" }, FontSize = 11, IsEnabled = Vm!.App.Tabs.Count > 0 };
            insert.Click += (_, _) => Vm.InsertSql(m);
            row.Children.Add(insert);
        }
        if (Vm!.CanApplyPhysicalNames(m))
        {
            var apply = new Button { Content = "🪄 " + Tr("물리명 적용 (빈 칸만)"), Classes = { "hover" }, FontSize = 11 };
            apply.Click += (_, _) => Vm.ApplyPhysicalNames(m);
            row.Children.Add(apply);
        }
        return row;
    }

    private static IBrush? Res(string key) =>
        Application.Current is { } a && a.TryFindResource(key, a.ActualThemeVariant, out var r) ? r as IBrush : null;
}
