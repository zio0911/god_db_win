using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Infrastructure;
using GodDB.App.Services;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 테이블 설명 (AI 참고) 창 (Mac 1.4.3 TableDescriptionsView.swift, #178 · #185 · #189):
/// JSON 가져오기 · 내보내기 · 지우기, AI 에이전트로 채우기 (빈 양식에 할 일이 들어 있다), 왼쪽 목록 + 오른쪽 읽기 전용 상세.
[SheetFor(nameof(AppState.TableDescriptionsRequest))]
public sealed class TableDescriptionsWindow : Window
{
    private static readonly Color Purple = Color.FromRgb(0xAF, 0x52, 0xDE);
    private static readonly Color Green = Color.FromRgb(0x34, 0xC7, 0x59);
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x95, 0x00);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x3B, 0x30);
    private static readonly Color Gray = Color.FromRgb(0x8E, 0x8E, 0x93);
    private static readonly IReadOnlyList<FileFilter> JsonFilter = [new("JSON", ["*.json"])];

    private readonly AppState _app;
    public TableDescriptionsRequest Request { get; }
    private readonly StackPanel _status = new() { Spacing = 6 };
    public Button ExportButton { get; }
    public Button ClearButton { get; }
    public Button SkeletonButton { get; }
    public Button CopyRequestButton { get; }
    public TextBox SearchBox { get; }
    public ListBox List { get; }
    private readonly ContentControl _detail = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _emptyList = new() { Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    /// 가져오기 실패 이유 (빨간 줄)
    public string? Problem { get; private set; }
    private string? _selected;
    private bool _filling;

    public TableDescriptionsWindow(AppState app, object value)
    {
        _app = app;
        Request = (TableDescriptionsRequest)value;
        Title = Tr("테이블 설명 (AI 참고)");
        Width = 880;
        Height = 700;
        MinWidth = 720;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var header = new StackPanel { Spacing = 4 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleRow.Children.Add(new TextBlock { Text = "📄", Foreground = new SolidColorBrush(Purple), VerticalAlignment = VerticalAlignment.Center });
        titleRow.Children.Add(Ui.Headline(Tr("테이블 설명 (AI 참고)")));
        titleRow.Children.Add(new TextBlock { Text = $"· {Profile?.Name ?? ""} / {Request.Database}", Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        header.Children.Add(titleRow);
        header.Children.Add(new TextBlock { Text = Tr("AI 어시스턴트 · 데이터 찾기가 질문에 맞는 테이블의 설명 · 코드 값 · 규칙을 함께 봐요 (이 PC 의 Ollama 에만)."), Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap });

        var import = Ui.Button("⬇ " + Tr("JSON 가져오기…"), () => _app.Fire(ImportAsync));
        ExportButton = Ui.Button("⬆ " + Tr("내보내기…"), () => _app.Fire(ExportAsync));
        ClearButton = Ui.Button("🗑 " + Tr("지우기"), () => _app.Fire(ConfirmClearAsync));
        ClearButton.Classes.Add("danger");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { import, ExportButton, ClearButton } };

        SkeletonButton = Ui.Button(Tr("빈 양식 내려받기…"), () => _app.Fire(SaveSkeletonAsync));
        CopyRequestButton = Ui.Button("📋 " + Tr("요청문 샘플 복사"), () => _app.Fire(CopyRequestAsync));
        var agent = AgentSection();

        SearchBox = new TextBox { PlaceholderText = Tr("테이블 · 설명 찾기") };
        SearchBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) FillList(); };
        List = new ListBox { ItemTemplate = new FuncDataTemplate<TableDescriptionDoc.Table>((t, _) => t == null ? new TextBlock() : Row(t)) };
        List.SelectionChanged += (_, _) =>
        {
            if (_filling) return;
            if (List.SelectedItem is TableDescriptionDoc.Table t) _selected = t.Name;
            ShowDetail();
        };
        var left = new DockPanel { Width = 220 };
        DockPanel.SetDock(SearchBox, Dock.Top);
        SearchBox.Margin = new Thickness(0, 0, 0, 6);
        left.Children.Add(SearchBox);
        left.Children.Add(new Border
        {
            BorderBrush = Res("DividerBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Child = new Panel { Children = { List, _emptyList } },
        });
        var bottomPane = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*") };
        bottomPane.Children.Add(left);
        Grid.SetColumn(_detail, 2);
        bottomPane.Children.Add(_detail);

        var close = Ui.Button(Tr("닫기"), Close, isCancel: true);
        var bottom = Ui.ButtonRow([close]);

        var root = new DockPanel { Margin = new Thickness(16) };
        foreach (var (c, gap) in new (Control, double)[] { (header, 12), (_status, 12), (actions, 12), (Divider(), 12), (agent, 12), (Divider(), 12) })
        {
            c.Margin = new Thickness(0, 0, 0, gap);
            DockPanel.SetDock(c, Dock.Top);
            root.Children.Add(c);
        }
        DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(bottom);
        root.Children.Add(bottomPane);
        Content = root;
        Refresh();
    }

    private ConnectionProfile? Profile => _app.Profiles.FirstOrDefault(p => p.Id == Request.ProfileId);

    public TableDescriptionEntry? Entry => _app.TableDescriptions.Entry(Request.ProfileId, Request.Database);

    /// 지금 보고 있는 DB 의 구조 (맞춰 보기 · 빈 양식에 쓴다)
    private IReadOnlyList<TableMeta>? LiveTables =>
        _app.SelectedProfileId == Request.ProfileId && _app.State.IsConnected && _app.CurrentDatabase == Request.Database ? _app.Tables : null;

    private TableDescriptionDoc.CheckResult? LiveCheck => Entry?.Doc is { } d && LiveTables is { } live ? d.Check(live) : null;

    private static Border Divider() => new() { Height = 1, Classes = { "divider" } };

    private static IBrush? Res(string key) =>
        Application.Current is { } a && a.TryFindResource(key, a.ActualThemeVariant, out var r) ? r as IBrush : null;

    private Control AgentSection()
    {
        var s = new StackPanel { Spacing = 6 };
        var title = Ui.Text(Tr("AI 에이전트로 채우기"));
        title.FontWeight = FontWeight.SemiBold;
        s.Children.Add(title);
        s.Children.Add(new TextBlock
        {
            Text = Tr("빈 양식을 Claude · Codex 같은 AI 에이전트에게 소스 저장소와 함께 주면, 소스와 테이블 구조를 분석해 채워 줘요. 채운 설명이 있으면 AI 어시스턴트가 질문을 더 정확히 이해해요."),
            Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap,
        });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 6, ColumnSpacing = 8 };
        void AddRow(int row, string n, string text, Control? button)
        {
            var num = new TextBlock { Text = n, Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(num, row);
            Grid.SetRow(t, row);
            Grid.SetColumn(t, 1);
            grid.Children.Add(num);
            grid.Children.Add(t);
            if (button == null) return;
            Grid.SetRow(button, row);
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }
        AddRow(0, "1", Tr("빈 양식 내려받기 — 지금 테이블 · 컬럼 이름과 코멘트가 들어 있어요 (데이터 값은 없어요)"), SkeletonButton);
        AddRow(1, "2", Tr("요청문 샘플을 복사해 AI 에이전트에게 양식 · 소스 저장소와 함께 주기 (같은 내용이 양식 안 instructions 에도 있어요)"), CopyRequestButton);
        AddRow(2, "3", Tr("채워 온 JSON 가져오기 (양식이 어긋나면 어디가 틀렸는지 알려 줘요)"), null);
        s.Children.Add(grid);
        return s;
    }

    // MARK: - 그리기

    public void Refresh()
    {
        _status.Children.Clear();
        if (Problem != null) _status.Children.Add(Note("⛔ " + Problem, Red));
        if (Entry is { } e)
        {
            _status.Children.Add(Note("✓ " + EntryLine(e), Green));
            if (LiveCheck is { } c && (c.MissingTables.Count > 0 || c.UnknownColumns.Count > 0 || c.UndescribedTables.Count > 0))
                _status.Children.Add(Note("⚠ " + CheckText(c), Orange));
        }
        else
        {
            _status.Children.Add(Note("ⓘ " + Tr("아직 테이블 설명이 없어요. 아래 순서대로 AI 에이전트에게 맡겨 채운 JSON 을 가져오세요."), Gray));
        }
        ExportButton.IsEnabled = Entry != null;
        ClearButton.IsEnabled = Entry != null;
        SkeletonButton.IsEnabled = LiveTables != null;
        ToolTip.SetTip(SkeletonButton, LiveTables == null ? Tr("이 DB 에 연결해 있어야 해요") : null);
        FillList();
    }

    /// 상태 줄들 (테스트용)
    public IEnumerable<string> StatusLines => _status.Children.OfType<Border>().Select(b => (b.Child as SelectableTextBlock)?.Text ?? "");

    private static Border Note(string text, Color color) => new()
    {
        Background = new SolidColorBrush(color, 0.08),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8),
        Child = new SelectableTextBlock { Text = text, Foreground = new SolidColorBrush(color), TextWrapping = TextWrapping.Wrap },
    };

    private static string EntryLine(TableDescriptionEntry e)
    {
        var d = e.Doc;
        var line = Tr("테이블 %@개 · 컬럼 %@개 · 코드 값 %@개", d.Tables.Count, d.ColumnCount, d.ValueCount);
        line += " · " + e.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        if (e.FileName.Length > 0) line += $" ({e.FileName})";
        return line;
    }

    private static string CheckText(TableDescriptionDoc.CheckResult c)
    {
        var parts = new List<string>();
        if (c.MissingTables.Count > 0)
            parts.Add(Tr("DB 에 없는 테이블 %@개 (%@)", c.MissingTables.Count, string.Join(", ", c.MissingTables.Take(3))));
        if (c.UnknownColumns.Count > 0)
            parts.Add(Tr("DB 에 없는 컬럼 %@개 (%@)", c.UnknownColumns.Count, string.Join(", ", c.UnknownColumns.Take(3))));
        if (c.UndescribedTables.Count > 0)
            parts.Add(Tr("설명 없는 테이블 %@개", c.UndescribedTables.Count));
        return string.Join(" · ", parts);
    }

    public List<TableDescriptionDoc.Table> Filtered
    {
        get
        {
            var all = Entry?.Doc.Tables ?? [];
            var q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
            if (q.Length == 0) return all;
            return all.Where(t =>
                new[] { t.Name, t.Title ?? "", t.Description ?? "" }.Concat(t.Terms ?? [])
                    .Concat((t.Columns ?? []).SelectMany(c => new[] { c.Name, c.Title ?? "" }))
                    .Any(x => x.ToLowerInvariant().Contains(q, StringComparison.Ordinal))).ToList();
        }
    }

    private HashSet<string> Missing => (LiveCheck?.MissingTables ?? []).Select(t => t.ToLowerInvariant()).ToHashSet();

    private void FillList()
    {
        var tables = Filtered;
        _filling = true;
        List.ItemsSource = tables;
        var current = tables.FirstOrDefault(t => t.Name == _selected) ?? tables.FirstOrDefault();
        List.SelectedItem = current;
        _filling = false;
        _emptyList.Text = Entry != null && tables.Count == 0 ? Tr("맞는 테이블이 없어요") : "";
        _emptyList.IsVisible = _emptyList.Text.Length > 0;
        ShowDetail();
    }

    private Control Row(TableDescriptionDoc.Table t)
    {
        var s = new StackPanel { Spacing = 1 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        top.Children.Add(new TextBlock { Text = t.Name, Classes = { "mono" }, TextTrimming = TextTrimming.CharacterEllipsis });
        if (Missing.Contains(t.Name.ToLowerInvariant()))
        {
            var warn = new TextBlock { Text = "⚠", Foreground = new SolidColorBrush(Orange) };
            ToolTip.SetTip(warn, Tr("지금 DB 에 없는 테이블"));
            top.Children.Add(warn);
        }
        s.Children.Add(top);
        var sub = string.Join(" · ", new[] { t.Title ?? "", Tr("컬럼 %@", t.Columns?.Count ?? 0) }.Where(x => x.Length > 0));
        var cap = Ui.Caption(sub);
        cap.TextTrimming = TextTrimming.CharacterEllipsis;
        s.Children.Add(cap);
        return s;
    }

    /// 오른쪽 상세 (지금 고른 테이블)
    public TableDescriptionDoc.Table? Shown { get; private set; }

    private void ShowDetail()
    {
        var current = List.SelectedItem as TableDescriptionDoc.Table;
        Shown = current;
        if (current == null)
        {
            _detail.Content = new TextBlock
            {
                Text = Entry == null ? Tr("테이블 설명을 가져오면 여기에서 내용을 볼 수 있어요") : Tr("왼쪽에서 테이블을 고르세요"),
                Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            return;
        }
        var unknown = (LiveCheck?.UnknownColumns ?? []).Select(x => x.ToLowerInvariant()).ToHashSet();
        _detail.Content = Detail(current, Missing.Contains(current.Name.ToLowerInvariant()), unknown);
    }

    /// 테이블 설명 하나를 읽기 전용으로 (#189): title · 설명 · 찾는 말 · 규칙 · 컬럼 표 (코드 값 · 관계)
    private static Control Detail(TableDescriptionDoc.Table table, bool missing, HashSet<string> unknownColumns)
    {
        var s = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 6, 0) };
        var head = new WrapPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new SelectableTextBlock { Text = table.Name, Classes = { "mono" }, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
        if (!string.IsNullOrEmpty(table.Title)) head.Children.Add(new TextBlock { Text = table.Title, FontSize = 16 });
        s.Children.Add(head);
        if (missing) s.Children.Add(new TextBlock { Text = "⚠ " + Tr("지금 DB 에 없는 테이블이에요"), Foreground = new SolidColorBrush(Orange) });
        if (!string.IsNullOrEmpty(table.Description))
            s.Children.Add(new SelectableTextBlock { Text = table.Description, Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap });
        if (table.Terms is { Count: > 0 } terms) s.Children.Add(LabeledRow(Tr("찾는 말"), Chips(terms, Purple)));
        if (table.Notes is { Count: > 0 } notes)
        {
            var list = new StackPanel { Spacing = 2 };
            foreach (var n in notes) list.Children.Add(new SelectableTextBlock { Text = "• " + n, TextWrapping = TextWrapping.Wrap });
            s.Children.Add(LabeledRow(Tr("규칙"), list));
        }
        var cols = table.Columns ?? [];
        if (cols.Count > 0)
        {
            var t = Ui.Text(Tr("컬럼 %@", cols.Count));
            t.FontWeight = FontWeight.SemiBold;
            t.Margin = new Thickness(0, 4, 0, 0);
            s.Children.Add(t);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,110,*"), ColumnSpacing = 12, RowSpacing = 6 };
            var row = 0;
            void Add(Control c, int col)
            {
                Grid.SetRow(c, row);
                Grid.SetColumn(c, col);
                grid.Children.Add(c);
            }
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Add(Ui.Caption(Tr("컬럼")), 0);
            Add(Ui.Caption(Tr("이름")), 1);
            Add(Ui.Caption(Tr("설명 · 코드 값 · 관계")), 2);
            foreach (var c in cols)
            {
                row++;
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var isUnknown = unknownColumns.Contains($"{table.Name}.{c.Name}".ToLowerInvariant());
                var name = new StackPanel { Spacing = 3 };
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
                nameRow.Children.Add(new SelectableTextBlock { Text = c.Name, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap });
                if (isUnknown)
                {
                    var w = new TextBlock { Text = "⚠", Foreground = new SolidColorBrush(Orange) };
                    ToolTip.SetTip(w, Tr("지금 DB 에 없는 컬럼"));
                    nameRow.Children.Add(w);
                }
                name.Children.Add(nameRow);
                if (c.Terms is { Count: > 0 } ct) name.Children.Add(Chips(ct, Purple));
                Add(name, 0);
                Add(new TextBlock { Text = c.Title ?? "", TextWrapping = TextWrapping.Wrap }, 1);
                var info = new StackPanel { Spacing = 4 };
                if (!string.IsNullOrEmpty(c.Description)) info.Children.Add(new SelectableTextBlock { Text = c.Description, TextWrapping = TextWrapping.Wrap });
                if (c.Values is { Count: > 0 } v)
                    info.Children.Add(Chips(v.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} = {kv.Value}").ToList(), Gray));
                if (!string.IsNullOrEmpty(c.References)) info.Children.Add(new SelectableTextBlock { Text = "→ " + c.References, Classes = { "mono" } });
                if (isUnknown) info.Children.Add(new TextBlock { Text = Tr("지금 DB 에 없는 컬럼"), FontSize = 11, Foreground = new SolidColorBrush(Orange) });
                Add(info, 2);
            }
            s.Children.Add(grid);
        }
        if (cols.Count == 0 && string.IsNullOrEmpty(table.Description) && (table.Terms ?? []).Count == 0)
            s.Children.Add(Ui.Text(Tr("이 테이블은 이름만 있어요"), "secondary"));
        return new ScrollViewer { Content = s, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private static Control LabeledRow(string title, Control content)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("48,*"), ColumnSpacing = 8 };
        g.Children.Add(Ui.Caption(title));
        Grid.SetColumn(content, 1);
        g.Children.Add(content);
        return g;
    }

    private static WrapPanel Chips(IReadOnlyList<string> items, Color color)
    {
        var w = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items)
            w.Children.Add(new Border
            {
                Background = new SolidColorBrush(color, 0.14),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 2),
                Margin = new Thickness(0, 0, 4, 4),
                Child = new TextBlock { Text = item, FontSize = 11 },
            });
        return w;
    }

    // MARK: - 동작

    public async Task ImportAsync()
    {
        var files = await _app.Dialogs.OpenFilesAsync(Tr("테이블 설명 JSON 파일을 고르세요."), JsonFilter);
        if (files.FirstOrDefault() is not { } path) return;
        await ImportFileAsync(path);
    }

    /// 파일 하나 가져오기 (JSON 의 database 가 지금 DB 와 다르면 넣을지 묻는다)
    public async Task ImportFileAsync(string path)
    {
        var fileName = Path.GetFileName(path);
        TableDescriptionDoc doc;
        try
        {
            doc = TableDescriptionDoc.Parse(await File.ReadAllBytesAsync(path));
        }
        catch (Exception e)
        {
            Problem = e is TableDescriptionDoc.ParseException ? e.Message : ErrorText.Describe(e).Message;
            _app.Log(LogLevel.Warning, LogSource.Schema, Tr("테이블 설명을 가져오지 못했어요: %@", fileName), detail: Problem);
            Refresh();
            return;
        }
        Problem = null;
        if (doc.Database is { Length: > 0 } db && db.ToLowerInvariant() != Request.Database.ToLowerInvariant())
        {
            if (!await _app.Dialogs.ConfirmAsync(Tr("JSON 은 ‘%@’ DB 용이에요. ‘%@’ 에 넣을까요?", db, Request.Database), "", Tr("넣기"), Tr("취소")))
            {
                Refresh();
                return;
            }
        }
        Apply(doc, fileName);
    }

    private void Apply(TableDescriptionDoc doc, string fileName)
    {
        doc.Database = Request.Database;
        doc.Instructions = null;
        _app.TableDescriptions.Set(new TableDescriptionEntry(Request.ProfileId, Request.Database, doc, fileName: fileName));
        _app.SaveTableDescriptions();
        _app.Log(LogLevel.Success, LogSource.Schema, Tr("테이블 설명을 가져왔어요: %@ · 테이블 %@개", $"{Profile?.Name ?? ""}/{Request.Database}", doc.Tables.Count));
        Refresh();
    }

    public async Task ExportAsync()
    {
        if (Entry?.Doc is not { } doc) return;
        var path = await _app.Dialogs.SaveFileAsync(Tr("내보내기…"), $"{Request.Database}-table-descriptions.json", JsonFilter);
        if (path == null) return;
        await WriteAsync(path, doc.Encoded());
    }

    public async Task SaveSkeletonAsync()
    {
        if (LiveTables is not { } live) return;
        // 이미 설명이 있으면 그 위에 새 테이블 · 컬럼만 더한 양식 + AI 에이전트가 할 일(instructions)
        var doc = TableDescriptionDoc.Form(live, Request.Database, Profile?.Kind ?? DatabaseKind.Mysql, IsEnglish, Entry?.Doc);
        var path = await _app.Dialogs.SaveFileAsync(Tr("빈 양식 내려받기…"), $"{Request.Database}-table-descriptions-form.json", JsonFilter);
        if (path == null) return;
        await WriteAsync(path, doc.Encoded());
    }

    private async Task WriteAsync(string path, byte[] data)
    {
        try { await File.WriteAllBytesAsync(path, data); }
        catch (Exception e) { _app.LogError(e, LogSource.App, Tr("%@ 파일을 저장하지 못했어요", Path.GetFileName(path))); }
    }

    public async Task CopyRequestAsync()
    {
        await _app.Dialogs.SetClipboardTextAsync(TableDescriptionDoc.RequestPrompt(Request.Database, Profile?.Kind ?? DatabaseKind.Mysql, IsEnglish));
        CopyRequestButton.Content = "✓ " + Tr("복사했어요");
        Avalonia.Threading.DispatcherTimer.RunOnce(() => CopyRequestButton.Content = "📋 " + Tr("요청문 샘플 복사"), TimeSpan.FromSeconds(2));
    }

    public async Task ConfirmClearAsync()
    {
        if (Entry == null) return;
        if (!await _app.Dialogs.ConfirmAsync(Tr("이 DB 의 테이블 설명을 지울까요?"),
                Tr("AI 어시스턴트 · 데이터 찾기가 더는 참고하지 않아요. 되돌릴 수 없으니 필요하면 먼저 내보내세요."), Tr("지우기"), Tr("취소"), destructive: true))
            return;
        _app.TableDescriptions.Remove(Request.ProfileId, Request.Database);
        _app.SaveTableDescriptions();
        _app.Log(LogLevel.Info, LogSource.Schema, Tr("테이블 설명을 지웠어요: %@", $"{Profile?.Name ?? ""}/{Request.Database}"));
        Refresh();
    }
}
