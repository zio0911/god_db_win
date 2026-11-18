using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Infrastructure;
using GodDB.App.Services;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// CSV / TSV 가져오기: 파일을 미리 보고 컬럼을 맞춘 뒤 한 트랜잭션으로 INSERT (실패하면 모두 되돌림). Mac CSVImportView.swift
[SheetFor(nameof(AppState.ImportingTable))]
public sealed class CsvImportWindow : Window
{
    private const double CellWidth = 150;
    private static readonly (string Label, char Value)[] Delimiters = [("쉼표 ,", ','), ("탭", '\t'), ("세미콜론 ;", ';')];

    private readonly AppState _app;
    public CsvImportDocument Doc { get; }
    private bool _working;
    private bool _updating;

    private readonly Button _pick;
    private readonly TextBlock _path = new() { Classes = { "caption", "mono" }, TextTrimming = TextTrimming.CharacterEllipsis };
    public ComboBox DelimiterBox { get; }
    public CheckBox HeaderBox { get; }
    public CheckBox EmptyAsNullBox { get; }
    private readonly TextBlock _summary = Ui.Text("", "secondary");
    /// 미리 보기 표 (첫 줄: 열마다 넣을 컬럼 고르기, 머리글, 앞 20행)
    public Grid PreviewGrid { get; } = new();
    private readonly Control _loadedPanel;
    private readonly Control _emptyPanel;
    private readonly SelectableTextBlock _error = new() { Classes = { "error" }, TextWrapping = TextWrapping.Wrap, MaxLines = 4 };
    private readonly GodDB.App.Controls.ActivitySpinner _progress = new() { Width = 16, Height = 16, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    public Button ImportButton { get; }

    public CsvImportWindow(AppState app, object value)
    {
        _app = app;
        Doc = new CsvImportDocument((TableMeta)value);
        Title = Doc.Title;
        Width = 760;
        Height = 560;
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _pick = Ui.Button(Tr("파일 고르기…"), () => _app.Fire(PickFileAsync));
        var head = new DockPanel();
        DockPanel.SetDock(_pick, Dock.Right);
        head.Children.Add(_pick);
        head.Children.Add(new TextBlock { Text = Doc.Title, Classes = { "headline" }, VerticalAlignment = VerticalAlignment.Center });

        DelimiterBox = new ComboBox { ItemsSource = Delimiters.Select(d => Tr(d.Label)).ToList(), SelectedIndex = 0 };
        DelimiterBox.SelectionChanged += (_, _) =>
        {
            if (_updating || DelimiterBox.SelectedIndex < 0) return;
            Doc.SetDelimiter(Delimiters[DelimiterBox.SelectedIndex].Value);
            Refresh();
        };
        HeaderBox = new CheckBox { Content = Tr("첫 줄은 머리글"), IsChecked = Doc.HasHeader };
        HeaderBox.IsCheckedChanged += (_, _) => { if (_updating) return; Doc.SetHasHeader(HeaderBox.IsChecked == true); Refresh(); };
        EmptyAsNullBox = new CheckBox { Content = Tr("빈 칸은 NULL"), IsChecked = Doc.EmptyAsNull };
        EmptyAsNullBox.IsCheckedChanged += (_, _) => { if (_updating) return; Doc.EmptyAsNull = EmptyAsNullBox.IsChecked == true; Refresh(); };
        var options = Ui.HStack(16,
            Ui.HStack(6, new TextBlock { Text = Tr("구분자"), VerticalAlignment = VerticalAlignment.Center }, DelimiterBox),
            HeaderBox, EmptyAsNullBox);

        var previewBorder = TuningSheet.CodeBorder(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = PreviewGrid,
        });
        var loaded = new DockPanel();
        var loadedTop = Ui.Stack(8, _path, options, _summary);
        DockPanel.SetDock(loadedTop, Dock.Top);
        loaded.Children.Add(loadedTop);
        previewBorder.Margin = new Thickness(0, 8, 0, 0);
        loaded.Children.Add(previewBorder);
        _loadedPanel = loaded;

        _emptyPanel = new StackPanel
        {
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 480,
            Children =
            {
                new TextBlock { Text = Glyphs.Table, FontSize = 36, HorizontalAlignment = HorizontalAlignment.Center, Classes = { "secondary" } },
                new TextBlock { Text = Tr("가져올 CSV / TSV 파일을 고르세요"), Classes = { "headline" }, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock
                {
                    Text = Tr("머리글이 컬럼 이름과 같으면 자동으로 맞춰요. 모든 행을 한 트랜잭션으로 넣고, 하나라도 실패하면 모두 되돌려요."),
                    Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                },
            },
        };

        ImportButton = Ui.Button("", () => _app.Fire(RunImportAsync), isDefault: true);
        var bottom = Ui.Stack(8, _error,
            Ui.ButtonRow([Ui.Button(Tr("취소"), Close, isCancel: true), ImportButton], [_progress]));

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        head.Margin = new Thickness(0, 0, 0, 12);
        bottom.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(head);
        root.Children.Add(bottom);
        root.Children.Add(new Panel { Children = { _loadedPanel, _emptyPanel } });
        Content = root;
        Refresh();
    }

    private async Task PickFileAsync()
    {
        var files = await _app.Dialogs.OpenFilesAsync(Tr("파일 고르기…"),
            [new FileFilter("CSV / TSV", ["*.csv", "*.tsv", "*.txt"]), new FileFilter(Tr("텍스트"), ["*.*"])]);
        if (files.Count == 0) return;
        LoadFile(files[0]);
    }

    /// 파일을 읽어 구분자를 고르고 컬럼을 자동으로 맞춘다
    public void LoadFile(string path)
    {
        try
        {
            Doc.Load(path);
            _error.Text = "";
        }
        catch (Exception e)
        {
            _error.Text = ErrorText.Describe(e).Message;
        }
        Refresh();
    }

    /// 글을 직접 넣는다 (테스트)
    public void LoadText(string text, string path = "test.csv")
    {
        Doc.LoadText(text, path);
        _error.Text = "";
        Refresh();
    }

    public void Refresh()
    {
        _updating = true;
        try
        {
            var loaded = Doc.FilePath != null;
            _pick.Content = loaded ? Tr("다른 파일…") : Tr("파일 고르기…");
            _loadedPanel.IsVisible = loaded;
            _emptyPanel.IsVisible = !loaded;
            _path.Text = Doc.FilePath ?? "";
            ToolTip.SetTip(_path, Doc.FilePath);
            DelimiterBox.SelectedIndex = Array.FindIndex(Delimiters, d => d.Value == Doc.Delimiter);
            HeaderBox.IsChecked = Doc.HasHeader;
            EmptyAsNullBox.IsChecked = Doc.EmptyAsNull;
            _summary.Text = Doc.Summary;
            _error.IsVisible = !string.IsNullOrEmpty(_error.Text);
            BuildPreview();
            UpdateButtons();
        }
        finally { _updating = false; }
    }

    private void UpdateButtons()
    {
        ImportButton.Content = Doc.ImportButtonTitle;
        ImportButton.IsEnabled = Doc.CanImport && !_working;
        _progress.IsVisible = _working;
    }

    private void BuildPreview()
    {
        var g = PreviewGrid;
        g.Children.Clear();
        g.ColumnDefinitions.Clear();
        g.RowDefinitions.Clear();
        g.Margin = new Thickness(8);
        g.ColumnSpacing = 10;
        g.RowSpacing = 4;
        var cols = Doc.CsvColumns;
        if (cols == 0) return;
        for (var i = 0; i < cols; i++) g.ColumnDefinitions.Add(new ColumnDefinition(CellWidth, GridUnitType.Pixel));
        var row = 0;
        void AddRow() => g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        void Put(Control c, int r, int col) { Grid.SetRow(c, r); Grid.SetColumn(c, col); g.Children.Add(c); }

        // 열마다 넣을 테이블 컬럼 (건너뛰기 = null)
        AddRow();
        var choices = new List<string> { Tr("건너뛰기") };
        choices.AddRange(Doc.Table.Columns.Select(c => c.Name));
        for (var i = 0; i < cols; i++)
        {
            var col = i;
            var current = col < Doc.Mapping.Count ? Doc.Mapping[col] : null;
            var box = new ComboBox
            {
                ItemsSource = choices,
                SelectedIndex = current == null ? 0 : Math.Max(0, choices.IndexOf(current)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = col,
            };
            box.SelectionChanged += (_, _) =>
            {
                if (_updating) return;
                Doc.SetMapping(col, box.SelectedIndex <= 0 ? null : choices[box.SelectedIndex]);
                UpdateButtons();
            };
            Put(box, row, col);
        }
        row++;

        if (Doc.Header is { } header)
        {
            AddRow();
            for (var i = 0; i < cols; i++)
                Put(new TextBlock { Text = i < header.Count ? header[i] : "", FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis }, row, i);
            row++;
            AddRow();
            var line = new Border { Classes = { "divider" }, Height = 1 };
            Grid.SetColumnSpan(line, cols);
            Put(line, row, 0);
            row++;
        }

        foreach (var data in Doc.PreviewRows)
        {
            AddRow();
            for (var i = 0; i < cols; i++)
            {
                var v = i < data.Count ? data[i] : "";
                var t = new TextBlock { Text = Doc.PreviewText(v), Classes = { "mono" }, TextTrimming = TextTrimming.CharacterEllipsis };
                if (v.Length == 0) t.Classes.Add("secondary");
                Put(t, row, i);
            }
            row++;
        }
    }

    /// 미리 보기의 열 매핑 칸 (테스트)
    public IReadOnlyList<ComboBox> MappingBoxes => PreviewGrid.Children.OfType<ComboBox>().ToList();

    public async Task RunImportAsync()
    {
        if (!Doc.CanImport || _working) return;
        _working = true;
        _error.Text = "";
        Refresh();
        var statements = Doc.Statements(_app.Dialect);
        try
        {
            await _app.ImportRowsAsync(Doc.Table.Name, statements, Doc.DataRows.Count);
            Close();
        }
        catch (Exception e)
        {
            _error.Text = ErrorText.Describe(e).Message;
        }
        finally
        {
            _working = false;
            Refresh();
        }
    }
}
