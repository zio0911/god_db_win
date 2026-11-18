using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 결과 내보내기 설정 창: 형식 → 범위 → 형식별 옵션 → [저장 위치 선택…] (Mac ResultExportUI.swift ExportSheet)
[SheetFor(nameof(AppState.ExportRequest))]
public sealed class ExportSheet : Window
{
    private readonly AppState _app;
    private readonly TabExportRequest _request;
    /// 지금 고른 값 (마지막 형식 기억은 [저장 위치 선택…] 때 AppState.ContinueExportAsync 가 한다)
    public ExportChoice Choice { get; }

    private readonly Dictionary<ExportFormat, SheetCard> _formatCards = [];
    private readonly SheetCard _allCard;
    private readonly SheetCard _visibleCard;
    private readonly TextBlock _unstable;
    /// 형식별 옵션 칸 (BOM 체크박스 / 대상 테이블 / 안내)
    public StackPanel OptionsPanel { get; } = new() { Spacing = 8 };
    public CheckBox BomBox { get; }
    public TextBox TableNameBox { get; }
    public Button ContinueButton { get; }

    public ExportSheet(AppState app, object value)
    {
        _app = app;
        _request = (TabExportRequest)value;
        var r = _request.Request;
        Choice = ExportChoice.Initial(r);
        Title = Tr("결과 내보내기");
        Width = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // 형식: 두 칸씩 6장
        var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2 };
        foreach (var f in ExportFormats.All)
        {
            var card = new SheetCard(FormatIcon(f), f.Title(), f.Note()) { Margin = new Thickness(0, 0, 8, 8) };
            card.Click += (_, _) => SetFormat(f);
            _formatCards[f] = card;
            grid.Children.Add(card);
        }
        grid.Margin = new Thickness(0, 0, -8, -8);

        // 범위
        _allCard = new SheetCard("⧉", Tr("쿼리 전체 데이터"), r.FetchAllNote) { IsEnabled = r.CanFetchAll };
        _allCard.Click += (_, _) => SetFetchAll(true);
        _visibleCard = new SheetCard("▭", Tr("지금 보이는 행만"), r.VisibleNote);
        _visibleCard.Click += (_, _) => SetFetchAll(false);
        _unstable = new TextBlock
        {
            Text = "⚠ " + ExportRequest.UnstableWarning,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#FF9500")),
        };

        BomBox = new CheckBox { Content = Tr("엑셀 호환 (UTF-8 BOM, 한글 깨짐 방지)"), IsChecked = Choice.ExcelBom };
        BomBox.IsCheckedChanged += (_, _) => Choice.ExcelBom = BomBox.IsChecked == true;
        TableNameBox = new TextBox { Text = Choice.TableName, MinWidth = 260 };
        TableNameBox.PropertyChanged += (_, e) => { if (e.Property != TextBox.TextProperty) return; Choice.TableName = TableNameBox.Text ?? ""; UpdateButtons(); };

        ContinueButton = Ui.Button(Tr("저장 위치 선택…"), Continue, isDefault: true);
        var cancel = Ui.Button(Tr("취소"), () => { _app.ExportRequest = null; Close(); }, isCancel: true);

        Content = new Border
        {
            Padding = new Thickness(22),
            Child = Ui.Stack(18,
                Ui.Stack(2, Ui.Headline("⇩ " + Tr("결과 내보내기")), Ui.Text(r.Subtitle, "secondary")),
                Section(Tr("형식"), grid),
                Section(Tr("범위"), Ui.Stack(8, _allCard, _visibleCard), _unstable),
                OptionsPanel,
                Ui.ButtonRow([cancel, ContinueButton])),
        };
        Refresh();
    }

    internal const string TableIcon = "[tablecells]", TableFillIcon = "[tablecells.fill]";

    private static string FormatIcon(ExportFormat f) => f switch
    {
        ExportFormat.Csv => "≡",
        ExportFormat.Tsv => TableIcon,
        ExportFormat.Xlsx => TableFillIcon,
        ExportFormat.Json => "{ }",
        ExportFormat.Sql => "</>",
        _ => "¶",
    };

    private static StackPanel Section(string title, params Control[] content)
    {
        var s = Ui.Stack(8, Ui.Headline(title));
        foreach (var c in content) s.Children.Add(c);
        return s;
    }

    public void SetFormat(ExportFormat f)
    {
        Choice.Format = f;
        Refresh();
    }

    public void SetFetchAll(bool on)
    {
        if (on && !_request.Request.CanFetchAll) return;
        Choice.FetchAll = on;
        Refresh();
    }

    /// 경고가 보이는지 (전체 + 정렬 기준 없음)
    public bool UnstableWarningVisible => _unstable.IsVisible;

    private void Refresh()
    {
        foreach (var (f, card) in _formatCards) card.Selected = Choice.Format == f;
        _allCard.Selected = Choice.FetchAll;
        _visibleCard.Selected = !Choice.FetchAll;
        _unstable.IsVisible = Choice.FetchAll && !_request.Request.Stable;
        BuildOptions();
        UpdateButtons();
    }

    private void BuildOptions()
    {
        OptionsPanel.Children.Clear();
        var f = Choice.Format;
        switch (f)
        {
            case ExportFormat.Csv or ExportFormat.Tsv:
                OptionsPanel.Children.Add(Ui.Headline(Tr("옵션")));
                OptionsPanel.Children.Add(BomBox);
                break;
            case ExportFormat.Sql:
                OptionsPanel.Children.Add(Ui.Headline(Tr("옵션")));
                var label = new TextBlock { Text = Tr("대상 테이블"), VerticalAlignment = VerticalAlignment.Center };
                OptionsPanel.Children.Add(Ui.HStack(8, label, TableNameBox));
                OptionsPanel.Children.Add(Ui.Text(f.Hint()!, "secondary"));
                break;
            default:
                if (f.Hint() is { } hint) OptionsPanel.Children.Add(Ui.Text(hint, "secondary"));
                break;
        }
    }

    private void UpdateButtons() => ContinueButton.IsEnabled = Choice.CanContinue;

    /// [저장 위치 선택…]: 이 창이 닫힌 뒤 저장 창(기본 이름 = ExportRequest.FileName)을 띄우고 내보내기를 시작한다
    public void Continue()
    {
        if (!Choice.CanContinue) return;
        var request = _request;
        var choice = Choice with { };
        Close();
        _app.Fire(() => _app.ContinueExportAsync(request, choice));
    }
}

/// 고를 수 있는 카드 한 칸 (선택 표시 + 아이콘 + 제목 + 설명). Mac ExportSheet.card
public sealed class SheetCard : Button
{
    private readonly TextBlock _radio = new() { Width = 16, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _icon;
    private readonly GodDB.App.Controls.TableCellsIcon? _tableIcon;
    private bool _selected;

    protected override Type StyleKeyOverride => typeof(Button);

    public SheetCard(string icon, string title, string note)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(10, 8);
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        _icon = new TextBlock { Text = icon, Width = 26, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        if (icon is ExportSheet.TableIcon or ExportSheet.TableFillIcon)
        {
            _icon.Text = "";
            _tableIcon = new GodDB.App.Controls.TableCellsIcon { Width = 18, Height = 18, Filled = icon == ExportSheet.TableFillIcon, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center };
            _icon.Inlines = [new Avalonia.Controls.Documents.InlineUIContainer(_tableIcon)];
        }
        var texts = Ui.Stack(1,
            new TextBlock { Text = title, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = note, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6 };
        Grid.SetColumn(_icon, 1);
        Grid.SetColumn(texts, 2);
        g.Children.Add(_radio);
        g.Children.Add(_icon);
        g.Children.Add(texts);
        Content = g;
        Selected = false;
    }

    public string TitleText => ((TextBlock)((StackPanel)((Grid)Content!).Children[2]).Children[0]).Text ?? "";

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            var accent = Color.Parse("#0A84FF");
            _radio.Text = value ? "◉" : "○";
            _radio.Foreground = value ? new SolidColorBrush(accent) : null;
            _icon.Foreground = value ? new SolidColorBrush(accent) : null;
            if (_tableIcon != null) _tableIcon.Foreground = value ? new SolidColorBrush(accent) : Brushes.Gray;
            Background = value ? new SolidColorBrush(accent, 0.10) : Brushes.Transparent;
            BorderBrush = value ? new SolidColorBrush(accent) : new SolidColorBrush(Colors.Gray, 0.25);
            BorderThickness = new Thickness(value ? 1.5 : 1);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsEnabledProperty) Opacity = IsEnabled ? 1 : 0.45;
    }
}
