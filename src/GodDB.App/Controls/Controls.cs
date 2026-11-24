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
        // StyleKeyOverride 가 ScrollViewer 라 형식 선택자(c|AdaptiveBar)가 맞지 않는다 → 클래스로 고른다
        Classes.Add("adaptiveBar");
    }

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    /// 글자를 다 보일 때 필요한 폭 (마지막으로 잰 값)
    private double _fullWidth;

    public bool IsCompact => PseudoClasses.Contains(":compact");

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Content is Layoutable child && !double.IsInfinity(availableSize.Width))
        {
            // 상태를 바꿀 때만 :compact 를 건드린다 (재기 위해 껐다 켜면 글자 표시가 바뀌어 배치가 끝없이 다시 돈다)
            if (!IsCompact)
            {
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                _fullWidth = child.DesiredSize.Width;
                if (_fullWidth > availableSize.Width)
                {
                    PseudoClasses.Set(":compact", true);
                    child.InvalidateMeasure();
                }
            }
            else if (_fullWidth <= availableSize.Width)
            {
                PseudoClasses.Set(":compact", false);
                child.InvalidateMeasure();
            }
        }
        return base.MeasureOverride(availableSize);
    }
}

/// DB 종류 아이콘: 브랜드 로고(MySQL · PostgreSQL · Redis, Simple Icons)를 브랜드 색으로. PostgreSQL 은 파랗게 채운 코끼리(PostgresIcon), SQL Server 는 자체 그림(MSSQLIcon). (Mac KindBadge)
public class KindBadge : Border
{
    public static readonly StyledProperty<DatabaseKind> KindProperty =
        AvaloniaProperty.Register<KindBadge, DatabaseKind>(nameof(Kind));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<KindBadge, double>(nameof(Size), 16);

    /// PostgreSQL 접속 중 Amazon Redshift (보라 원통 아이콘)
    public static readonly StyledProperty<bool> RedshiftProperty =
        AvaloniaProperty.Register<KindBadge, bool>(nameof(Redshift));

    public DatabaseKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool Redshift { get => GetValue(RedshiftProperty); set => SetValue(RedshiftProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    private readonly Avalonia.Controls.Shapes.Path _logo = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    /// 그리는 로고 모양 (테스트용). PostgreSQL · SQL Server 는 여러 겹 그림(Layers)이라 null
    public Geometry? Logo => Child == _logo ? _logo.Data : null;
    public IBrush? LogoBrush => Child == _logo ? _logo.Fill : null;
    /// 여러 겹 그림 (PostgreSQL · SQL Server, 테스트용)
    public Canvas? Layers => Child as Canvas;

    public KindBadge()
    {
        VerticalAlignment = VerticalAlignment.Center;
        Background = Brushes.Transparent;
        Child = _logo;
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == SizeProperty || change.Property == RedshiftProperty) Refresh();
    }

    public static Color ColorOf(DatabaseKind kind) => BrandLogo.ColorOf(kind);

    private void Refresh()
    {
        Width = Height = Size;
        var redshift = Redshift && Kind == DatabaseKind.Postgres;
        if (redshift) Child = RedshiftIcon.Create(Size);
        else if (Kind == DatabaseKind.Postgres) Child = BrandLogo.PostgresIcon(Size);
        else if (Kind == DatabaseKind.Mssql) Child = BrandLogo.MssqlIcon(Size);
        else
        {
            _logo.Width = _logo.Height = Size;
            _logo.Data = BrandLogo.Path(Kind);
            _logo.Fill = new SolidColorBrush(ColorOf(Kind));
            Child = _logo;
        }
        var title = redshift ? "Redshift" : Kind.Title();
        ToolTip.SetTip(this, title);
        Avalonia.Automation.AutomationProperties.SetName(this, title);
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
