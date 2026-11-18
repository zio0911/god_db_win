using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.Services;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// SQL 추출 (Forward Engineering, Mac ModelSQLSheet): 대상 DB · DROP 먼저 · 복사 / .sql 저장 / 쿼리 탭으로 열기 (실행은 직접)
public sealed class ModelSqlWindow : Window
{
    private readonly AppState _app;
    private readonly DataModel _model;
    private readonly TextBox _text;
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _failHint;
    private readonly TextBlock _dropWarning;

    public DatabaseKind Kind { get; private set; }
    public bool DropFirst { get; private set; }
    /// 지금 보이는 SQL (실패하면 "-- 오류")
    public string SqlText { get; private set; } = "";
    public bool Failed { get; private set; }

    public ModelSqlWindow(AppState app, DataModel model)
    {
        _app = app;
        _model = model;
        Kind = model.Kind;
        Title = Tr("SQL 추출 · %@", model.Name);
        Width = 860;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var kinds = DataModel.TargetKinds;
        var picker = ModelUi.Segmented(kinds.Select(k => k.Title()).ToList(), Math.Max(0, kinds.ToList().IndexOf(Kind)),
                                       i => { Kind = kinds[i]; Refresh(); });
        var drop = ModelUi.Check(false, v => { DropFirst = v; Refresh(); }, Tr("DROP 먼저"));
        ToolTip.SetTip(drop, Tr("기존 테이블을 지우고 다시 만드는 스크립트 (데이터가 사라져요)"));
        var head = new DockPanel();
        var right = Ui.HStack(12, picker, drop);
        DockPanel.SetDock(right, Dock.Right);
        head.Children.Add(right);
        head.Children.Add(Ui.Headline("📄 " + Tr("SQL 추출 · %@", model.Name)));

        _text = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Classes = { "mono" },
            VerticalContentAlignment = VerticalAlignment.Top, Background = ModelUi.Brush("CodeBackground"),
        };
        _failHint = Ui.Caption(Tr("도구 막대의 검사 버튼에서 물리명이 빠진 곳을 확인하세요."));
        _dropWarning = new TextBlock { Text = "⚠ " + Tr("DROP 이 들어 있어요. 운영 DB 에서 실행하지 마세요."), Foreground = ModelUi.Brush("RedBrush"), VerticalAlignment = VerticalAlignment.Center };

        var bottom = new DockPanel();
        DockPanel.SetDock(_buttons, Dock.Right);
        bottom.Children.Add(_buttons);
        bottom.Children.Add(_dropWarning);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_failHint, Dock.Bottom);
        head.Margin = new Thickness(0, 0, 0, 10);
        bottom.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(head);
        root.Children.Add(bottom);
        root.Children.Add(_failHint);
        root.Children.Add(_text);
        Content = root;
        Refresh();
    }

    /// Mac: Result { try model.ddl(kind:dropFirst:) }
    public static (string Text, bool Ok) Build(DataModel model, DatabaseKind kind, bool dropFirst)
    {
        try { return (model.Ddl(kind, dropFirst), true); }
        catch (Exception e) { return ("-- " + e.Message, false); }
    }

    public void SetOptions(DatabaseKind kind, bool dropFirst)
    {
        Kind = kind;
        DropFirst = dropFirst;
        Refresh();
    }

    private void Refresh()
    {
        var (text, ok) = Build(_model, Kind, DropFirst);
        SqlText = text;
        Failed = !ok;
        _text.Text = text;
        _text.Foreground = ok ? null : ModelUi.Brush("RedBrush");
        if (ok) _text.ClearValue(TextBox.ForegroundProperty);
        _failHint.IsVisible = !ok;
        _dropWarning.IsVisible = DropFirst;
        _buttons.Children.Clear();
        _buttons.Children.Add(Ui.Button(Tr("닫기"), Close, isCancel: true));
        if (!ok) return;
        _buttons.Children.Add(new CopyButton { Title = Tr("복사"), TextProvider = () => SqlText });
        _buttons.Children.Add(Ui.Button(Tr(".sql 저장…"), () => _ = SaveAsync()));
        var open = Ui.Button(Tr("쿼리 탭으로 열기"), OpenInQueryTab, isDefault: true);
        open.IsEnabled = _app.State.IsConnected;
        ToolTip.SetTip(open, _app.State.IsConnected ? Tr("연결된 접속의 새 쿼리 탭에 넣어요. 실행은 직접 하세요.") : Tr("먼저 접속을 연결하세요."));
        _buttons.Children.Add(open);
    }

    public void OpenInQueryTab()
    {
        if (Failed) return;
        if (_app.AddTab(SqlText, $"DDL · {_model.Name}") != null) _app.Mode = MainMode.Query;
        Close();
    }

    private async Task SaveAsync()
    {
        var path = await _app.Dialogs.SaveFileAsync(Tr(".sql 저장…"), $"{_model.Name}.sql", [new FileFilter("SQL", ["*.sql"])]);
        if (path == null) return;
        try { await File.WriteAllTextAsync(path, SqlText); }
        catch (Exception e) { await _app.Dialogs.AlertAsync(Tr(".sql 저장…"), e.Message); }
    }
}

/// DB와 비교 (Synchronize, Mac ModelCompareSheet): 모델 → 연결된 DB 차이와 DB 를 모델에 맞추는 SQL. 실행하지 않고 쿼리 탭에 넣기만.
public sealed class ModelCompareWindow : Window
{
    private readonly AppState _app;
    private readonly DataModel _model;
    public SchemaDiffReport? Report { get; }
    public string? Error { get; }

    public ModelCompareWindow(AppState app, DataModel model)
    {
        _app = app;
        _model = model;
        Title = Tr("DB와 비교");
        Width = 820;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        try { Report = model.Compare(app.Tables, app.Dialect); }
        catch (Exception e) { Error = e.Message; }

        var top = Ui.Stack(10,
            Ui.Headline("⇄ " + Tr("모델 → %@ / %@ 비교", app.SelectedProfile?.Name ?? "", app.CurrentDatabase)),
            Ui.Caption(Tr("모델을 기준으로 DB 에 없는 것, 다른 것, DB 에만 있는 것을 보여주고 DB 를 모델에 맞추는 SQL 을 만들어요.")));
        if (model.Kind != app.SelectedProfile?.Kind)
            top.Children.Add(new TextBlock
            {
                Text = "⚠ " + Tr("모델 대상 DB(%@)와 접속 DB(%@)가 달라서 타입 차이가 많이 보일 수 있어요.", model.Kind.Title(), app.SelectedProfile?.Kind.Title() ?? ""),
                Foreground = ModelUi.Brush("OrangeBrush"), TextWrapping = TextWrapping.Wrap,
            });
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var close = Ui.Button(Tr("닫기"), Close, isCancel: true);
        if (Report is not { } report)
        {
            var bottomRow = Ui.ButtonRow([close]);
            DockPanel.SetDock(bottomRow, Dock.Bottom);
            root.Children.Add(bottomRow);
            root.Children.Add(Ui.Text(Error ?? "", "error"));
            Content = root;
            return;
        }
        var counts = Ui.HStack(14,
            new TextBlock { Text = "⊕ " + Tr("DB 에 없음 %@", report.MissingCount), Foreground = ModelUi.Brush("GreenBrush") },
            new TextBlock { Text = "✎ " + Tr("다름 %@", report.DifferentCount), Foreground = ModelUi.Brush("OrangeBrush") },
            new TextBlock { Text = "⊖ " + Tr("DB 에만 있음 %@", report.ExtraCount), Classes = { "secondary" } },
            new TextBlock { Text = Tr("일치 테이블 %@", report.MatchedTables), Classes = { "secondary" } });
        counts.Margin = new Thickness(0, 10, 0, 6);
        DockPanel.SetDock(counts, Dock.Top);
        root.Children.Add(counts);

        var script = report.FixScript;
        var copy = new CopyButton { Title = Tr("복사"), TextProvider = () => script };
        var open = Ui.Button(Tr("쿼리 탭으로 열기"), OpenInQueryTab, isDefault: true);
        open.IsEnabled = script.Length > 0;
        var buttons = Ui.ButtonRow([close, copy, open]);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var sql = new TextBox
        {
            Text = script.Length == 0 ? Tr("-- 차이가 없어요.") : script, IsReadOnly = true, AcceptsReturn = true, Height = 160,
            Classes = { "mono" }, FontSize = 11, VerticalContentAlignment = VerticalAlignment.Top, Background = ModelUi.Brush("CodeBackground"),
        };
        DockPanel.SetDock(sql, Dock.Bottom);
        root.Children.Add(sql);
        var sqlTitle = new TextBlock { Text = Tr("DB 를 모델에 맞추는 SQL (DB 에만 있는 것은 지우지 않아요)"), FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 4) };
        DockPanel.SetDock(sqlTitle, Dock.Bottom);
        root.Children.Add(sqlTitle);
        var list = new StackPanel { Spacing = 6, Margin = new Thickness(8) };
        foreach (var item in report.Items)
        {
            var line = new DockPanel();
            var kind = Ui.Caption(SchemaDiffItem.RawValue(item.Kind));
            kind.TextWrapping = TextWrapping.NoWrap;
            DockPanel.SetDock(kind, Dock.Right);
            line.Children.Add(kind);
            line.Children.Add(new TextBlock { Text = item.Table + (item.Object is { } o ? $".{o}" : ""), Classes = { "mono" } });
            list.Children.Add(Ui.Stack(2, line, Ui.Caption(item.Detail)));
        }
        root.Children.Add(new Border
        {
            BorderBrush = ModelUi.Brush("DividerBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), MinHeight = 200,
            Child = new ScrollViewer { Content = list },
        });
        Content = root;
    }

    public void OpenInQueryTab()
    {
        if (Report is not { } r || r.FixScript.Length == 0) return;
        if (_app.AddTab(r.FixScript, $"SYNC · {_model.Name}") != null) _app.Mode = MainMode.Query;
        Close();
    }
}

/// 리버스 엔지니어링 (Mac ReverseEngineerSheet): 연결된 DB 의 테이블을 골라 모델로
[SheetFor(nameof(AppState.ShowReverseEngineer))]
public sealed class ReverseEngineerWindow : Window
{
    private readonly AppState _app;
    private readonly StackPanel _list = new() { Spacing = 2, Margin = new Thickness(6) };
    private readonly TextBlock _count = new() { Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center };
    private Button? _create;

    public HashSet<string> Picked { get; } = [];
    public TextBox NameBox { get; } = new();
    public TextBox FilterBox { get; } = new();
    public CheckBox InferredBox { get; }
    public DataModel? Created { get; private set; }

    public ReverseEngineerWindow(AppState app, object value)
    {
        _app = app;
        Title = Tr("리버스 엔지니어링");
        Width = 620;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        InferredBox = new CheckBox { IsChecked = true, Content = Tr("FK 제약이 없어도 컬럼 이름으로 관계 추정 (FK 제약 없는 논리 관계로 들어가요)") };
        var root = new DockPanel { Margin = new Thickness(16) };
        var title = Ui.Headline("↶ " + Tr("리버스 엔지니어링"));
        title.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);
        if (!app.CanReverseEngineer)
        {
            var row = Ui.ButtonRow([Ui.Button(Tr("닫기"), Close, isCancel: true)]);
            DockPanel.SetDock(row, Dock.Bottom);
            root.Children.Add(row);
            root.Children.Add(Ui.Text(Tr("SQL DB 접속을 연결하고 스키마를 불러온 뒤 다시 열어 주세요."), "secondary"));
            Content = root;
            return;
        }
        // Mac onAppear: 이름 기본값, 사이드바에서 고른 테이블(없으면 처음 200개)
        NameBox.Text = $"{app.CurrentDatabase} " + Tr("모델");
        NameBox.PlaceholderText = Tr("모델 이름");
        foreach (var n in app.SelectedTables.Count == 0 ? app.Tables.Take(200).Select(t => t.Name) : app.SelectedTables) Picked.Add(n);
        FilterBox.PlaceholderText = Tr("테이블 검색");
        FilterBox.OnText(_ => RebuildList());

        var top = Ui.Stack(10,
            Ui.Caption(Tr("%@ / %@ 의 테이블 구조를 모델로 가져와요. 코멘트 첫 줄은 논리명, 나머지는 설명이 되고, FK 는 관계가 돼요.",
                          app.SelectedProfile?.Name ?? "", app.CurrentDatabase)),
            NameBox);
        var all = Ui.Button(Tr("모두"), () => { foreach (var t in Shown) Picked.Add(t.Name); RebuildList(); });
        var none = Ui.Button(Tr("해제"), () => { Picked.Clear(); RebuildList(); });
        var filterRow = new DockPanel();
        var fr = Ui.HStack(6, all, none);
        fr.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(fr, Dock.Right);
        filterRow.Children.Add(fr);
        filterRow.Children.Add(FilterBox);
        top.Children.Add(filterRow);
        top.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        _create = Ui.Button(Tr("모델 만들기"), Create, isDefault: true);
        var bottom = Ui.ButtonRow([Ui.Button(Tr("취소"), Close, isCancel: true), _create], [_count]);
        bottom.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        InferredBox.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(InferredBox, Dock.Bottom);
        root.Children.Add(InferredBox);
        root.Children.Add(new Border
        {
            BorderBrush = ModelUi.Brush("DividerBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Child = new ScrollViewer { Content = _list },
        });
        Content = root;
        RebuildList();
    }

    public IReadOnlyList<TableMeta> Shown
    {
        get
        {
            var f = FilterBox.Text ?? "";
            return f.Length == 0 ? _app.Tables
                : _app.Tables.Where(t => t.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase)
                                         || t.Comment.Contains(f, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }
    }

    /// 코멘트 첫 줄 (너무 길면 자름) — Mac DiagramView.logicalName
    public static string? LogicalName(string comment)
    {
        var first = comment.Split('\n', '\r').FirstOrDefault()?.Trim() ?? "";
        if (first.Length == 0) return null;
        return first.Length > 40 ? first[..40] + "…" : first;
    }

    private void RebuildList()
    {
        _list.Children.Clear();
        foreach (var t in Shown)
        {
            var name = t.Name;
            var line = new DockPanel { LastChildFill = true };
            var cols = Ui.Caption(Tr("컬럼 %@", t.Columns.Count));
            cols.TextWrapping = TextWrapping.NoWrap;
            DockPanel.SetDock(cols, Dock.Right);
            line.Children.Add(cols);
            var label = Ui.HStack(8, new TextBlock { Text = name, Classes = { "mono" } });
            if (LogicalName(t.Comment) is { } l) label.Children.Add(new TextBlock { Text = l, Classes = { "secondary" } });
            line.Children.Add(label);
            var box = new CheckBox { IsChecked = Picked.Contains(name), Content = line, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true) Picked.Add(name); else Picked.Remove(name);
                UpdateCount();
            };
            _list.Children.Add(box);
        }
        UpdateCount();
    }

    private void UpdateCount()
    {
        _count.Text = Tr("%@개 선택", Picked.Count);
        if (_create != null) _create.IsEnabled = Picked.Count > 0;
    }

    public void Create()
    {
        if (Picked.Count == 0) return;
        var m = DataModel.ReverseSelected(_app.Tables, Picked, NameBox.Text ?? "", _app.CurrentDatabase,
                                          _app.SelectedProfile?.Kind ?? DatabaseKind.Mysql, InferredBox.IsChecked == true);
        _app.Modeling.Add(m);
        Created = m;
        _app.Mode = MainMode.Modeling;
        _app.Log(LogLevel.Info, LogSource.Schema, Tr("리버스 엔지니어링 · %@ · 테이블 %@개 · 관계 %@개", m.Name, m.Tables.Count, m.Relations.Count));
        Close();
        // 창을 닫으면 메인 창이 요청을 지운다. 띄우지 않은 창(테스트)이면 여기서 지운다 → 모델링 화면이 목록을 다시 읽는다
        if (_app.ShowReverseEngineer) _app.ShowReverseEngineer = false;
    }
}
