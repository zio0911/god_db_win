using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GodDB.App.Infrastructure;

namespace GodDB.App.Controls;

/// 도구 막대: 자리가 넉넉하면 그대로, 모자라면 아이콘만(:compact — Classes="label" 인 글자를 숨김),
/// 그래도 모자라면 가로 스크롤. (Mac AdaptiveBar)
public class AdaptiveBar : ScrollViewer
{
    public AdaptiveBar()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Content is Layoutable child && !double.IsInfinity(availableSize.Width))
        {
            PseudoClasses.Set(":compact", false);
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            var full = child.DesiredSize.Width;
            if (full > availableSize.Width)
            {
                PseudoClasses.Set(":compact", true);
                child.InvalidateMeasure();
            }
        }
        return base.MeasureOverride(availableSize);
    }
}

/// DB 종류 작은 표시 (MY / PG / MSSQL / RD)
public class KindBadge : Border
{
    public static readonly StyledProperty<DatabaseKind> KindProperty =
        AvaloniaProperty.Register<KindBadge, DatabaseKind>(nameof(Kind));

    public DatabaseKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    private readonly TextBlock _text = new() { FontSize = 9, FontWeight = FontWeight.SemiBold };

    public KindBadge()
    {
        Classes.Add("badge");
        Child = _text;
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty) Refresh();
    }

    public static Color ColorOf(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Mysql => Color.Parse("#FF9500"),
        DatabaseKind.Postgres => Color.Parse("#0A84FF"),
        DatabaseKind.Mssql => Color.Parse("#AF52DE"),
        _ => Color.Parse("#FF3B30"),
    };

    private void Refresh()
    {
        var c = ColorOf(Kind);
        _text.Text = Kind.Badge();
        _text.Foreground = new SolidColorBrush(c);
        Background = new SolidColorBrush(c, 0.12);
        ToolTip.SetTip(this, Kind.Title());
    }
}

/// 접속 환경 표시 (개발 · 스테이징 · 운영). 표시 안 함이면 보이지 않는다. (Mac EnvironmentBadge)
public class EnvironmentBadge : Border
{
    public static readonly StyledProperty<ConnectionEnvironment> EnvironmentProperty =
        AvaloniaProperty.Register<EnvironmentBadge, ConnectionEnvironment>(nameof(Environment));

    public ConnectionEnvironment Environment { get => GetValue(EnvironmentProperty); set => SetValue(EnvironmentProperty, value); }

    private readonly TextBlock _text = new() { FontSize = 10, FontWeight = FontWeight.SemiBold };

    public EnvironmentBadge()
    {
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(4, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Child = _text;
        Refresh();
        L10n.LanguageChanged += () => Dispatcher.UIThread.Post(Refresh);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EnvironmentProperty) Refresh();
    }

    public static Color ColorOf(ConnectionEnvironment e) => e switch
    {
        ConnectionEnvironment.Development => Color.Parse("#34C759"),
        ConnectionEnvironment.Staging => Color.Parse("#FF9500"),
        ConnectionEnvironment.Production => Color.Parse("#FF3B30"),
        _ => Color.Parse("#8A8A8E"),
    };

    private void Refresh()
    {
        IsVisible = Environment != ConnectionEnvironment.None;
        var c = ColorOf(Environment);
        _text.Text = Environment.Title();
        _text.Foreground = new SolidColorBrush(c);
        Background = new SolidColorBrush(c, 0.15);
    }
}

/// 누르면 클립보드에 복사하고 잠깐 "복사됨 ✓" 으로 바뀌는 버튼 (Mac CopyButton)
public class CopyButton : Button
{
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<CopyButton, string>(nameof(Title), "");

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// 복사할 글 (누를 때 부른다)
    public Func<string>? TextProvider { get; set; }
    public string? Text { get; set; }
    /// 클립보드에 넣는 방법 (없으면 이 창의 클립보드)
    public Func<string, Task>? Copier { get; set; }

    protected override Type StyleKeyOverride => typeof(Button);

    public CopyButton()
    {
        Classes.Add("hover");
        Content = Title;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty) Content = Title;
    }

    protected override async void OnClick()
    {
        base.OnClick();
        var text = TextProvider?.Invoke() ?? Text ?? "";
        if (Copier != null) await Copier(text);
        else if (TopLevel.GetTopLevel(this)?.Clipboard is { } c) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(c, text);
        Content = "✓ " + Tr("복사됨");
        Foreground = Brushes.Green;
        await Task.Delay(1500);
        Content = Title;
        ClearValue(ForegroundProperty);
    }
}
