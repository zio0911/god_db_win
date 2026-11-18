using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 모델링 화면 (Mac ModelingView · ModelEditorView). DataContext = AppState (메인 창이 넣는다).
/// 목록은 XAML, 도구 막대 · 다이어그램 · 아래 편집기는 여기서 만든다. 상태와 동작은 <see cref="ModelingViewModel"/>.
public partial class ModelingView : UserControl
{
    public ModelingViewModel? Vm { get; private set; }
    public ModelCanvas? Canvas { get; private set; }

    private Control? _editorView;
    private Control? _emptyView;
    private Grid? _canvasGrid;
    private ContentControl? _selectionHost;
    private GridSplitter? _editorSplitter;
    private double _editorHeight = 280;
    private ScrollViewer? _canvasScroll;
    private TextBox? _nameBox;
    private ComboBox? _kindBox;
    private Button? _undoButton, _issueButton, _compareButton;
    private TextBlock? _issueText, _zoomText, _hintText;
    private Border? _hint;
    private readonly List<(ModelTool Tool, Button Button)> _toolButtons = [];
    private bool _syncing;

    public ModelingView()
    {
        InitializeComponent();
        var col = Root.ColumnDefinitions[0];
        col.MinWidth = 170;
        col.MaxWidth = 360;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm == null && DataContext is AppState app) Attach(new ModelingViewModel(app));
    }

    private void Attach(ModelingViewModel vm)
    {
        Vm = vm;
        Root.DataContext = vm;
        vm.EditorRebuild += Sync;
        vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ModelingViewModel.Tool): SyncTools(); break;
                case nameof(ModelingViewModel.ToolHint): SyncHint(); break;
                case nameof(ModelingViewModel.Zoom): if (_zoomText != null) _zoomText.Text = vm.ZoomText; break;
                case nameof(ModelingViewModel.IssueBadge): SyncIssues(); break;
                case nameof(ModelingViewModel.CanUndo): if (_undoButton != null) _undoButton.IsEnabled = vm.CanUndo; break;
                case nameof(ModelingViewModel.CanCompare): if (_compareButton != null) _compareButton.IsEnabled = vm.CanCompare; break;
            }
        };
        Sync();
    }

    /// 모델이 바뀌었거나 구조가 바뀌었을 때: 안내 / 편집기 전환, 도구 막대 값 맞추기, 아래 편집기 다시 만들기
    private void Sync()
    {
        if (Vm is not { } vm) return;
        if (!vm.HasModel)
        {
            EditorHost.Content = _emptyView ??= BuildEmpty(vm);
            return;
        }
        EditorHost.Content = _editorView ??= BuildEditor(vm);
        _syncing = true;
        if (_nameBox != null && _nameBox.Text != vm.Model!.Name) _nameBox.Text = vm.Model.Name;
        if (_kindBox != null) _kindBox.SelectedIndex = DataModel.TargetKinds.ToList().IndexOf(vm.Model!.Kind);
        _syncing = false;
        SyncTools();
        SyncHint();
        SyncIssues();
        if (_undoButton != null) _undoButton.IsEnabled = vm.CanUndo;
        if (_compareButton != null) _compareButton.IsEnabled = vm.CanCompare;
        if (_zoomText != null) _zoomText.Text = vm.ZoomText;
        SyncSelectionEditor();
    }

    // MARK: - 모델이 없을 때

    private Control BuildEmpty(ModelingViewModel vm)
    {
        var reverse = Ui.Button(Tr("현재 DB 에서 리버스 엔지니어링…"), vm.ReverseEngineer);
        reverse.IsEnabled = vm.CanReverseEngineer;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ModelingViewModel.CanReverseEngineer)) reverse.IsEnabled = vm.CanReverseEngineer; };
        var title = new TextBlock { Text = "⧉ " + Tr("모델이 없어요"), Classes = { "title" }, HorizontalAlignment = HorizontalAlignment.Center };
        var desc = Ui.Text(Tr("논리명으로 테이블을 설계하고 관계를 이어 다이어그램으로 보고, 물리명을 붙여 SQL 로 뽑을 수 있어요."), "secondary");
        desc.TextAlignment = TextAlignment.Center;
        desc.MaxWidth = 420;
        var buttons = Ui.HStack(8, Ui.Button(Tr("새 모델"), vm.NewModel, accent: true), reverse);
        buttons.HorizontalAlignment = HorizontalAlignment.Center;
        var s = Ui.Stack(14, title, desc, buttons);
        s.HorizontalAlignment = HorizontalAlignment.Center;
        s.VerticalAlignment = VerticalAlignment.Center;
        return s;
    }

    // MARK: - 편집기 (도구 막대 + 다이어그램 + 아래 편집기)

    private Control BuildEditor(ModelingViewModel vm)
    {
        var root = new DockPanel();
        // 자리가 모자라면(AI 패널을 연 경우 등) 도구 막대를 가로로 스크롤한다
        var bar = new AdaptiveBar { Content = BuildToolbar(vm) };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        var div = ModelUi.Divider();
        DockPanel.SetDock(div, Dock.Top);
        root.Children.Add(div);

        Canvas = new ModelCanvas(vm);
        _canvasScroll = new ScrollViewer
        {
            Content = Canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = ModelUi.Brush("BarBackground"),
        };
        // 다이어그램 위: 도구 안내(위 가운데), 이름 표시 · 확대(오른쪽 아래)
        _hintText = new TextBlock { FontSize = 13 };
        _hint = new Border
        {
            Child = _hintText, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 10, 0, 0),
            Background = new SolidColorBrush(Colors.Gray, 0.18), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        var modes = new[] { ModelNameMode.Logical, ModelNameMode.Physical, ModelNameMode.Both };
        var modePicker = ModelUi.Segmented(modes.Select(ModelingViewModel.NameModeTitle).ToList(), Array.IndexOf(modes, vm.NameMode), i => vm.NameMode = modes[i]);
        ToolTip.SetTip(modePicker, Tr("다이어그램에 논리명 / 물리명 / 둘 다"));
        _zoomText = new TextBlock { Text = vm.ZoomText, Width = 40, TextAlignment = TextAlignment.Center, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var zoom = new Border
        {
            Background = ModelUi.Brush("PanelBackground"), CornerRadius = new CornerRadius(6), Padding = new Thickness(4, 2),
            Child = Ui.HStack(2,
                ModelUi.IconButton("−", Tr("축소"), vm.ZoomOut),
                _zoomText,
                ModelUi.IconButton("+", Tr("확대"), vm.ZoomIn)),
        };
        var overlay = Ui.HStack(6, modePicker, zoom);
        overlay.HorizontalAlignment = HorizontalAlignment.Right;
        overlay.VerticalAlignment = VerticalAlignment.Bottom;
        overlay.Margin = new Thickness(10, 10, 24, 24);
        var canvasArea = new Panel { Children = { _canvasScroll, _hint, overlay } };

        _canvasGrid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,0") };
        _canvasGrid.Children.Add(canvasArea);
        _editorSplitter = new GridSplitter { Height = 4, ResizeDirection = GridResizeDirection.Rows, Background = ModelUi.Brush("DividerBrush"), IsVisible = false };
        Grid.SetRow(_editorSplitter, 1);
        _canvasGrid.Children.Add(_editorSplitter);
        _selectionHost = new ContentControl();
        Grid.SetRow(_selectionHost, 2);
        _canvasGrid.Children.Add(_selectionHost);
        root.Children.Add(_canvasGrid);
        return root;
    }

    private Control BuildToolbar(ModelingViewModel vm)
    {
        _nameBox = new TextBox
        {
            PlaceholderText = Tr("모델 이름"), MinWidth = 90, MaxWidth = 180, FontSize = 14, FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center,
        };
        _nameBox.OnText(s => { if (!_syncing) vm.SetModelName(s); });
        _kindBox = new ComboBox { ItemsSource = DataModel.TargetKinds.Select(k => k.Title()).ToList() };
        ToolTip.SetTip(_kindBox, Tr("SQL 을 뽑을 대상 DB"));
        _kindBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _kindBox.SelectedIndex >= 0) vm.SetKind(DataModel.TargetKinds[_kindBox.SelectedIndex]);
        };
        _toolButtons.Clear();
        Button ToolButton(ModelTool t, string icon, string help, string? label = null)
        {
            var b = new Button
            {
                Content = Ui.HStack(3, new TextBlock { Text = icon }, new TextBlock { Text = label ?? "", FontSize = 11, FontWeight = FontWeight.SemiBold, IsVisible = label != null }),
                Classes = { "hover" },
            };
            ToolTip.SetTip(b, help);
            b.Click += (_, _) => vm.PressTool(t);
            _toolButtons.Add((t, b));
            return b;
        }
        Border Sep() => new() { Width = 1, Height = 16, Background = ModelUi.Brush("DividerBrush"), Margin = new Thickness(2, 0) };

        _undoButton = ModelUi.IconButton("↶", Tr("되돌리기 (테이블·관계 추가/삭제, 이동)"), vm.Undo);
        var left = Ui.HStack(8, _nameBox, _kindBox, Sep(),
            ToolButton(ModelTool.SelectTool, "↖", Tr("선택 / 이동")),
            ToolButton(ModelTool.AddTableTool, "▦⁺", Tr("테이블 추가")),
            ToolButton(new ModelTool.Relation(ModelCardinality.OneToMany, false), "⑂", Tr("1:N 비식별 관계"), "1:N"),
            ToolButton(new ModelTool.Relation(ModelCardinality.OneToMany, true), "⑂", Tr("1:N 식별 관계 (자식 PK 에 포함)"), "1:N ●"),
            ToolButton(new ModelTool.Relation(ModelCardinality.OneToOne, false), "↔", Tr("1:1 관계"), "1:1"),
            Sep(),
            _undoButton,
            ModelUi.IconButton("▤", Tr("자동 정렬"), vm.AutoLayout));
        foreach (var c in left.Children) c.VerticalAlignment = VerticalAlignment.Center;

        _issueText = new TextBlock { FontWeight = FontWeight.SemiBold };
        _issueButton = new Button { Content = _issueText, Classes = { "hover" } };
        ToolTip.SetTip(_issueButton, Tr("모델 검사 (물리명·타입·기본 키)"));
        var issueFlyout = new Flyout { Placement = PlacementMode.Bottom };
        issueFlyout.Opening += (_, _) => issueFlyout.Content = IssueList(vm, issueFlyout);
        _issueButton.Flyout = issueFlyout;
        var png = ModelUi.IconButton("🖼", Tr("PNG 저장"), () => _ = ExportPngAsync());
        _compareButton = Ui.Button("⇄ " + Tr("DB와 비교"), () => OpenDialog(m => new ModelCompareWindow(vm.App, m)));
        _compareButton.Classes.Add("hover");
        ToolTip.SetTip(_compareButton, Tr("연결된 DB 와 비교해서 모델에 맞추는 ALTER SQL 을 만들어요 (Synchronize)"));
        var sql = Ui.Button("📄 " + Tr("SQL 추출"), () => OpenDialog(m => new ModelSqlWindow(vm.App, m)), accent: true);
        ToolTip.SetTip(sql, Tr("CREATE TABLE · FK SQL 로 뽑기 (Forward Engineering)"));
        var right = Ui.HStack(8, _issueButton, png, _compareButton, sql);
        foreach (var c in right.Children) c.VerticalAlignment = VerticalAlignment.Center;

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 8), MinHeight = 32 };
        Grid.SetColumn(right, 2);
        right.Margin = new Thickness(8, 0, 0, 0);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    private Control IssueList(ModelingViewModel vm, Flyout flyout)
    {
        var issues = vm.Issues;
        var stack = new StackPanel { Spacing = 6, Width = 420 };
        stack.Children.Add(Ui.Headline(Tr("모델 검사")));
        if (issues.Count == 0)
            stack.Children.Add(new TextBlock { Text = "✓ " + Tr("문제가 없어요. SQL 로 뽑을 수 있어요."), Foreground = ModelUi.Brush("GreenBrush") });
        var list = new StackPanel { Spacing = 4 };
        foreach (var i in issues)
        {
            bool error = i.Level == ModelIssueLevel.Error;
            var b = new Button
            {
                Content = new TextBlock { Text = (error ? "⛔ " : "⚠ ") + i.Message, TextWrapping = TextWrapping.Wrap },
                Foreground = ModelUi.Brush(error ? "RedBrush" : "OrangeBrush"), Classes = { "hover" },
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            var table = i.Table;
            b.Click += (_, _) =>
            {
                if (table is { } t) vm.Selection = new ModelSelection.Table(t);
                flyout.Hide();
            };
            list.Children.Add(b);
        }
        stack.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320 });
        return stack;
    }

    private void SyncTools()
    {
        if (Vm is not { } vm) return;
        foreach (var (t, b) in _toolButtons)
            b.Background = t == vm.Tool ? new SolidColorBrush(ModelCanvas.Accent, 0.22) : Brushes.Transparent;
    }

    private void SyncHint()
    {
        if (Vm is not { } vm || _hint == null || _hintText == null) return;
        _hintText.Text = vm.ToolHint ?? "";
        _hint.IsVisible = vm.ToolHint != null;
    }

    private void SyncIssues()
    {
        if (Vm is not { } vm || _issueText == null) return;
        var issues = vm.Issues;
        int errors = issues.Count(i => i.Level == ModelIssueLevel.Error);
        _issueText.Text = (errors > 0 ? "⛔ " : issues.Count == 0 ? "✓ " : "⚠ ") + vm.IssueBadge;
        _issueText.Foreground = ModelUi.Brush(errors > 0 ? "RedBrush" : issues.Count == 0 ? "GreenBrush" : "OrangeBrush");
    }

    /// 아래 편집기: 고른 테이블 / 관계 (없으면 숨김, 높이는 끌어서 조절)
    private void SyncSelectionEditor()
    {
        if (Vm is not { } vm || _canvasGrid == null || _selectionHost == null || _editorSplitter == null) return;
        Control? editor = vm.Selection switch
        {
            ModelSelection.Table t when vm.Model?.Table(t.Id) != null => new ModelTableEditor(vm, t.Id),
            ModelSelection.Relation r when vm.Model?.Relations.Any(x => x.Id == r.Id) == true => new ModelRelationEditor(vm, r.Id),
            _ => null,
        };
        var row = _canvasGrid.RowDefinitions[2];
        if (_selectionHost.Content != null && row.Height.IsAbsolute && row.Height.Value > 0) _editorHeight = row.Height.Value;
        _selectionHost.Content = editor;
        _editorSplitter.IsVisible = editor != null;
        row.MinHeight = editor != null ? 140 : 0;
        row.Height = new GridLength(editor != null ? _editorHeight : 0);
    }

    // MARK: - 창

    private void OpenDialog(Func<DataModel, Window> make)
    {
        if (Vm?.Model is not { } m) return;
        // Mac 시트처럼 지금 모델 값으로 연다
        var w = make(m.Clone());
        if (TopLevel.GetTopLevel(this) is Window owner) _ = w.ShowDialog(owner);
        else w.Show();
    }

    private async Task ExportPngAsync()
    {
        if (Vm is not { } vm || vm.Model is not { } m) return;
        byte[] png;
        try { png = ModelCanvas.RenderPng(m, vm.NameMode, ModelUi.Brush("PanelBackground")); }
        catch (Exception e)
        {
            vm.App.LogError(e, LogSource.Schema, Tr("PNG 저장"));
            return;
        }
        await vm.SavePngAsync(png);
    }

    // MARK: - 목록 (XAML 이벤트)

    private static ModelRow? RowOf(object? sender) => (sender as Control)?.DataContext as ModelRow;

    private void OnNewModel(object? sender, RoutedEventArgs e) => Vm?.NewModel();
    private void OnReverse(object? sender, RoutedEventArgs e) => Vm?.ReverseEngineer();
    private void OnImport(object? sender, RoutedEventArgs e) { if (Vm != null) _ = Vm.ImportAsync(); }

    private void OnRename(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) Vm?.StartRename(r); }
    private void OnDuplicate(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) Vm?.Duplicate(r.Id); }
    private void OnExport(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r && Vm != null) _ = Vm.ExportAsync(r.Id); }
    private void OnDelete(object? sender, RoutedEventArgs e) { if (RowOf(sender) is { } r && Vm != null) _ = Vm.DeleteAsync(r.Id); }

    private void OnRenameKey(object? sender, KeyEventArgs e)
    {
        if (RowOf(sender) is not { } r || Vm == null) return;
        if (e.Key == Key.Enter) { Vm.CommitRename(r); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm.CancelRename(r); e.Handled = true; }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { IsRenaming: true } r) Vm?.CommitRename(r);
    }

    private void OnRenameBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
            box.PropertyChanged += (_, a) =>
            {
                if (a.Property == IsVisibleProperty && box.IsVisible)
                {
                    box.Focus();
                    box.SelectAll();
                }
            };
    }
}
