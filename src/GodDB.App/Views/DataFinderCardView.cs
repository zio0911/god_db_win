using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// AI 데이터 찾기 답 카드 (Mac DataFinderCardView). 어시스턴트 대화에서 답 말풍선 대신 그린다.
public static class DataFinderCardView
{
    private static readonly Color Purple = Color.FromRgb(0xAF, 0x52, 0xDE);
    private static readonly Color Green = Color.FromRgb(0x34, 0xC7, 0x59);
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x95, 0x00);
    private static readonly Color Gray = Color.FromRgb(0x8E, 0x8E, 0x93);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x3B, 0x30);

    public static Control Build(AppState app, Guid messageId, DataFinderCard card, bool running)
    {
        var s = new StackPanel { Spacing = 6 };
        s.Children.Add(new TextBlock { Text = "🔎 " + card.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (card.ModelProblem is { } m)
            ModelProblem(s, app, card, m, running);
        else if (card.Current is { } a)
            Answer(s, app, messageId, card, a, running);
        else
        {
            s.Children.Add(Ui.Text(Tr("다른 후보가 없어요"), "secondary"));
            s.Children.Add(RefindButton(app, card, running));
        }
        if (card.ModelProblem == null && card.WithoutAI)
            s.Children.Add(new TextBlock { Text = "⚠ " + AppState.WithoutAIText(card), FontSize = 11, Foreground = new SolidColorBrush(Orange), TextWrapping = TextWrapping.Wrap });
        if (card.Others.Count > 0)
        {
            var others = string.Join(" · ", card.Others.Select(o => o.SampleStatus is { } st ? $"{o.Location} ({st})" : o.Location));
            var t = Ui.Caption(Tr("다른 후보: %@", others));
            t.TextWrapping = TextWrapping.Wrap;
            t.MaxLines = 3;
            s.Children.Add(t);
        }
        if (Chips(card) is { } chips) s.Children.Add(chips);
        return new Border
        {
            Name = "FinderCard",
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Purple, 0.07),
            BorderBrush = new SolidColorBrush(Purple, 0.25),
            BorderThickness = new Thickness(1),
            Child = s,
        };
    }

    private static void Answer(StackPanel s, AppState app, Guid messageId, DataFinderCard card, DataFinderAnswer a, bool running)
    {
        var head = new WrapPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new SelectableTextBlock { Text = a.Location, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        if (a.Confidence is { } c)
            head.Children.Add(Badge(Tr("신뢰도 %@", c.Label()), c == DataFinder.Confidence.High ? Green : c == DataFinder.Confidence.Medium ? Orange : Gray));
        head.Children.Add(Badge(a.Confirmed ? Tr("확인됨") : Tr("AI · 미확인"), a.Confirmed ? Green : Purple));
        s.Children.Add(head);
        if (a.Samples is { Count: > 0 } samples)
        {
            var t = new SelectableTextBlock { Text = Tr("값 예: %@", string.Join(", ", samples.Take(10))), Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
            s.Children.Add(t);
        }
        if (a.SampleStatus is { } status)
        {
            var t = Ui.Caption(status);
            t.FontSize = 10;
            s.Children.Add(t);
        }
        else if (a.Samples is { Count: 0 })
            s.Children.Add(Ui.Caption(Tr("샘플 없음")));
        if (a.Explain.Length > 0) s.Children.Add(new SelectableTextBlock { Text = a.Explain, TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Small(Tr("데이터 보기"), () => app.FinderViewData(card), "FinderViewData"));
        buttons.Children.Add(Small(Tr("SQL 넣기"), () => app.FinderInsertSql(card), "FinderInsertSql"));
        var yes = Small("👍 " + Tr("맞아요"), () => app.FinderConfirm(messageId), "FinderConfirm");
        yes.IsEnabled = !a.Confirmed;
        buttons.Children.Add(yes);
        buttons.Children.Add(Small("👎 " + Tr("아니에요"), () => app.FinderReject(messageId), "FinderReject"));
        if (card.FromNotes) buttons.Children.Add(RefindButton(app, card, running));
        s.Children.Add(buttons);
    }

    private static void ModelProblem(StackPanel s, AppState app, DataFinderCard card, FinderModelProblem m, bool running)
    {
        s.Children.Add(new SelectableTextBlock { Text = "⛔ " + m.Message, Foreground = new SolidColorBrush(Red), TextWrapping = TextWrapping.Wrap });
        if (m.Alternatives.Count > 0)
        {
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var alt in m.Alternatives)
            {
                var b = Small("⇄ " + Tr("%@ 로 바꾸기", alt), () => app.FinderSwitchModel(alt, card), "FinderSwitch");
                b.IsEnabled = !running;
                buttons.Children.Add(b);
            }
            s.Children.Add(buttons);
        }
        if (m.Checking)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new GodDB.App.Controls.ActivitySpinner { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Ui.Caption(Tr("설치된 다른 모델을 확인하는 중…")));
            s.Children.Add(row);
        }
        else if (m.Alternatives.Count == 0)
        {
            s.Children.Add(new SelectableTextBlock
            {
                Text = Tr("답하는 다른 모델이 없어요. 터미널에서 ollama pull %@ 로 받을 수 있어요.", AISettings.DefaultRecommendedModel),
                Classes = { "caption" }, TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private static Button RefindButton(AppState app, DataFinderCard card, bool running)
    {
        var b = Small("⟳ " + Tr("다시 찾기"), () => app.FindData(card.Question, skipNotes: true), "FinderRefind");
        b.IsEnabled = !running;
        return b;
    }

    private static Control? Chips(DataFinderCard card)
    {
        var items = new List<string>();
        if (card.FromNotes) items.Add("📄 " + Tr("테이블 설명에서 찾음 (AI 호출 없음)"));
        else if (card.TablesSent is { } n)
        {
            items.Add("☰ " + Tr("테이블 목록 %@개", n));
            items.Add("🔒 " + Tr("후보 컬럼 샘플 %@개 (로컬)", card.SamplesSent ?? 0));
        }
        if (items.Count == 0) return null;
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items)
            row.Children.Add(new Border
            {
                Background = new SolidColorBrush(Purple, 0.08),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 2),
                Margin = new Thickness(0, 0, 4, 2),
                Child = new TextBlock { Text = i, FontSize = 10 },
            });
        return row;
    }

    private static Border Badge(string text, Color color) => new()
    {
        Background = new SolidColorBrush(color, 0.12),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 1),
        Margin = new Thickness(0, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(color) },
    };

    private static Button Small(string text, Action click, string name)
    {
        var b = new Button { Content = text, Classes = { "hover" }, FontSize = 11, Name = name };
        b.Click += (_, _) => click();
        return b;
    }
}
