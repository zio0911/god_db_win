using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.Services;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// ER 다이어그램 창 (Mac DiagramWindow · DiagramView). 여러 개를 동시에 띄울 수 있고, 연 순간의 스키마를 그린다.
[WindowFor(WindowKinds.Diagram)]
public sealed class DiagramWindow : Window
{
    public AppState App { get; }
    public DiagramViewModel? Vm { get; }
    public DiagramCanvas? Canvas { get; }
    public DiagramAIPanel? AiPanel { get; }
    /// 저장 위치 고르기 (테스트에서 바꾼다)
    public IAppDialogs Dialogs { get; set; }

    private readonly ScrollViewer? _scroll;
    private readonly LayoutTransformControl? _zoomHost;
    private readonly ColumnDefinition? _aiColumn;
    private readonly GridSplitter? _splitter;
    private readonly Button? _aiButton;
    private bool _didFit;
    private double _aiWidth = 380;

    public DiagramWindow(AppState app, object? argument)
    {
        App = app;
        Dialogs = new WindowDialogs(this);
        Width = 1180;
        Height = 760;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (argument is not Guid id || !app.Diagrams.TryGetValue(id, out var doc))
        {
            Title = Tr("ER 다이어그램");
            Content = Ui.Stack(8,
                new TextBlock { Text = "⛓", FontSize = 36, HorizontalAlignment = HorizontalAlignment.Center, Classes = { "secondary" } },
                new TextBlock { Text = Tr("다이어그램을 찾을 수 없어요"), Classes = { "title" }, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = Tr("사이드바에서 테이블을 우클릭하고 '다이어그램 보기'를 눌러 주세요."), Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Center });
            ((Control)Content).VerticalAlignment = VerticalAlignment.Center;
            return;
        }

        Title = Tr("ER 다이어그램 · %@", doc.Title);
        var vm = Vm = new DiagramViewModel(doc);
        Canvas = new DiagramCanvas(vm);
        _zoomHost = new LayoutTransformControl { Child = Canvas, LayoutTransform = new ScaleTransform(vm.Zoom, vm.Zoom) };
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _zoomHost,
        };
        _scroll.Bind(BackgroundProperty, _scroll.GetResourceObservable("PanelBackground"));
        // 처음 열 때 전체가 한 화면에 보이게 (관계 없는 테이블이 아래로 밀려 안 보이는 일 방지)
        _scroll.SizeChanged += (_, e) =>
        {
            if (_didFit || e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
            _didFit = true;
            vm.Fit(e.NewSize);
        };
        // Ctrl + 휠: 확대/축소 (Mac 은 핀치) — 스크롤보다 먼저 받는다
        _scroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
            if (e.Delta.Y > 0) vm.ZoomIn(); else if (e.Delta.Y < 0) vm.ZoomOut();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        vm.PropertyChanged += OnVmChanged;

        var chat = new AiChat(doc.ChatScope.Length == 0 ? doc.Title : doc.ChatScope);
        AiPanel = new DiagramAIPanel(app, doc, chat);
        _aiButton = ToolButton(Glyphs.Sparkles + " " + Tr("AI 분석"), Tr("로컬 Ollama 로 관계를 분석하고 질문할 수 있어요"), ToggleAi);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,0") };
        _aiColumn = body.ColumnDefinitions[2];
        body.Children.Add(_scroll);
        _splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Width = 4 };
        _splitter.Bind(BackgroundProperty, _splitter.GetResourceObservable("DividerBrush"));
        Grid.SetColumn(_splitter, 1);
        body.Children.Add(_splitter);
        Grid.SetColumn(AiPanel, 2);
        body.Children.Add(AiPanel);

        var toolbar = new AdaptiveBar { Content = BuildToolbar(vm, doc) };
        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        var divider = new Border { Height = 1 };
        divider.Bind(Border.BackgroundProperty, divider.GetResourceObservable("DividerBrush"));
        DockPanel.SetDock(divider, Dock.Top);
        root.Children.Add(divider);
        root.Children.Add(body);
        Content = root;

        // 이전 대화가 있으면 AI 패널을 열어 둔다
        SetAiVisible(chat.Messages.Count > 0);
        Closed += (_, _) => chat.Cancel();
    }

    public bool AiVisible { get; private set; }

    private void ToggleAi() => SetAiVisible(!AiVisible);

    public void SetAiVisible(bool on)
    {
        if (_aiColumn == null || AiPanel == null || _splitter == null) return;
        if (AiVisible && _aiColumn.Width.IsAbsolute && _aiColumn.Width.Value > 0) _aiWidth = _aiColumn.Width.Value;
        AiVisible = on;
        AiPanel.IsVisible = on;
        _splitter.IsVisible = on;
        _aiColumn.MinWidth = on ? 300 : 0;
        _aiColumn.MaxWidth = on ? 700 : 0;
        _aiColumn.Width = new GridLength(on ? _aiWidth : 0);
        if (_aiButton != null) _aiButton.Foreground = on ? new SolidColorBrush(DiagramCanvas.Purple) : null;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiagramViewModel.Zoom) && _zoomHost != null && Vm != null)
            _zoomHost.LayoutTransform = new ScaleTransform(Vm.Zoom, Vm.Zoom);
    }

    private static Button ToolButton(string text, string? tip, Action onClick)
    {
        var b = new Button { Content = text, Classes = { "hover" } };
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Border Separator()
    {
        var s = new Border { Width = 1, Height = 16, Margin = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center };
        s.Bind(Border.BackgroundProperty, s.GetResourceObservable("DividerBrush"));
        return s;
    }

    private Control BuildToolbar(DiagramViewModel vm, DiagramDoc doc)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(12, 8) };
        bar.Children.Add(new TextBlock { Text = "⛓", Foreground = new SolidColorBrush(DiagramCanvas.Accent), VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = doc.Title, Classes = { "headline" }, MaxWidth = 360, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(title, doc.Title);
        bar.Children.Add(title);
        bar.Children.Add(new TextBlock { Text = vm.CountText, Classes = { "secondary", "label" }, VerticalAlignment = VerticalAlignment.Center });

        // 이름 표시 (Mac 세그먼트 Picker)
        var seg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        ToolTip.SetTip(seg, Tr("물리명: 실제 테이블·컬럼 이름 / 논리명: 코멘트 (없으면 물리명)"));
        var modeButtons = new List<(DiagramNameMode Mode, ToggleButton Button)>();
        foreach (var mode in Enum.GetValues<DiagramNameMode>())
        {
            var tb = new ToggleButton { Content = mode.Title(), IsChecked = vm.NameMode == mode, Padding = new Thickness(10, 3), CornerRadius = new CornerRadius(0) };
            tb.Click += (_, _) => vm.NameMode = mode;
            modeButtons.Add((mode, tb));
            seg.Children.Add(tb);
        }
        modeButtons[0].Button.CornerRadius = new CornerRadius(5, 0, 0, 5);
        modeButtons[^1].Button.CornerRadius = new CornerRadius(0, 5, 5, 0);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiagramViewModel.NameMode))
                foreach (var (m, b) in modeButtons) b.IsChecked = vm.NameMode == m;
        };
        bar.Children.Add(seg);

        var inferred = new CheckBox { Content = vm.InferredText, IsChecked = vm.ShowInferred, IsEnabled = doc.Inferred.Count > 0, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(inferred, Tr("외래 키 제약이 없어도, 컬럼 이름이 다른 테이블의 기본 키 이름과 같으면 관계로 보고 점선으로 그려요"));
        inferred.IsCheckedChanged += (_, _) => vm.ShowInferred = inferred.IsChecked == true;
        bar.Children.Add(inferred);
        bar.Children.Add(Separator());

        bar.Children.Add(ToolButton("－", Tr("축소"), vm.ZoomOut));
        var zoomText = new TextBlock { Text = vm.ZoomText, Width = 44, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFeatures = FontFeatureCollection.Parse("tnum") };
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DiagramViewModel.ZoomText)) zoomText.Text = vm.ZoomText; };
        bar.Children.Add(zoomText);
        bar.Children.Add(ToolButton("＋", Tr("확대"), vm.ZoomIn));
        bar.Children.Add(ToolButton("100%", null, () => vm.Zoom = 1));
        bar.Children.Add(ToolButton("⤢ " + Tr("화면에 맞추기"), Tr("모든 테이블이 한 화면에 보이게 확대/축소"), FitToWindow));
        bar.Children.Add(Separator());
        bar.Children.Add(ToolButton("▦ " + Tr("자동 정렬"), null, vm.Relayout));
        bar.Children.Add(ToolButton("⤓ " + Tr("PNG 저장"), null, () => App.Fire(ExportPngAsync)));
        bar.Children.Add(Separator());
        bar.Children.Add(_aiButton!);
        return bar;
    }

    public void FitToWindow()
    {
        if (_scroll != null && Vm != null) Vm.Fit(_scroll.Bounds.Size);
    }

    /// PNG 저장 (2배 해상도). 저장한 경로, 취소면 null.
    public async Task<string?> ExportPngAsync()
    {
        if (Canvas == null || Vm == null) return null;
        var png = Canvas.RenderPng(2);
        var path = await Dialogs.SaveFileAsync(Tr("PNG 저장"), $"ERD_{Vm.Doc.Focus ?? "schema"}.png", [new FileFilter("PNG", ["*.png"])]);
        if (path == null) return null;
        // Mac SaveFile.write: 성공은 상태창, 실패는 상태창 + 알림
        var name = Path.GetFileName(path);
        try
        {
            await File.WriteAllBytesAsync(path, png);
            App.Log(LogLevel.Success, LogSource.App, Tr("%@ 에 저장했어요", name));
        }
        catch (Exception e)
        {
            App.LogError(e, LogSource.App, Tr("%@ 파일을 저장하지 못했어요", name));
            await Dialogs.AlertAsync(Tr("%@ 파일을 저장하지 못했어요", name), e.Message);
            return null;
        }
        return path;
    }
}
