using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.Services;
using GodDB.App.ViewModels;
using Node = GodDB.App.ViewModels.TableEditorViewModel.Node;

namespace GodDB.App.Views;

/// 테이블 수정 (ALTER) 창 (Mac TableEditorView — DataGrip Modify 와 비슷한 구성).
/// 왼쪽: 테이블 > columns / keys / indexes / foreign keys 트리 + 도구, 오른쪽: 고른 항목의 속성, 아래: ALTER 미리보기.
/// 트리 · 속성 칸은 항목이 바뀔 때마다 코드로 다시 만든다 (입력 중인 칸은 다시 만들지 않는다).
[SheetFor(nameof(AppState.AlteringTable))]
public sealed class TableEditorWindow : Window
{
    public TableEditorViewModel Vm { get; }

    private readonly StackPanel _tree = new() { Margin = new Thickness(4) };
    private readonly StackPanel _inspector = new() { Margin = new Thickness(16), Spacing = 14 };
    private readonly TextBlock _summary = Ui.Caption("");
    private readonly Button _remove, _duplicate, _up, _down;
    private readonly SelectableTextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly Border _previewBox;
    private readonly TextBlock _previewState = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly CopyButton _copy = new() { Title = Tr("복사") };
    private readonly SelectableTextBlock _execError = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _openTab, _execute;
    private TextBlock? _inspectorHeader;
    private bool _columnsOpen = true, _keysOpen = true, _indexesOpen = true, _fksOpen = true;

    public TableEditorWindow(AppState app, object value)
    {
        Vm = new TableEditorViewModel(app, (TableMeta)value);
        Title = Tr("테이블 수정 · %@", Vm.Table.Name);
        Width = 1040;
        Height = 740;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // 도구 (+ − 복제 ↑ ↓)
        var add = ToolButton(Glyphs.Plus, Tr("컬럼 / 인덱스 추가"), () => { });
        add.Flyout = new MenuFlyout
        {
            Items =
            {
                Item(Tr("컬럼 추가"), Vm.AddColumn),
                Item(Tr("인덱스 추가"), Vm.AddIndex),
            },
        };
        _remove = ToolButton("−", Tr("고른 컬럼 / 인덱스 삭제"), Vm.RemoveSelected);
        _duplicate = ToolButton("⧉", Tr("컬럼 복제"), Vm.DuplicateSelected);
        _up = ToolButton("↑", Tr("위로"), () => Vm.MoveSelected(-1));
        _down = ToolButton("↓", Tr("아래로"), () => Vm.MoveSelected(1));
        var toolbar = new DockPanel { Margin = new Thickness(8, 6) };
        var tools = Ui.HStack(4, add, _remove, _duplicate, _up, _down);
        DockPanel.SetDock(tools, Dock.Left);
        toolbar.Children.Add(tools);
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.TextWrapping = TextWrapping.NoWrap;
        toolbar.Children.Add(_summary);

        var left = new DockPanel();
        var tb = new Border { Child = toolbar, BorderBrush = Res("DividerBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(tb, Dock.Top);
        left.Children.Add(tb);
        left.Children.Add(new ScrollViewer { Content = _tree });

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("360,1,*") };
        top.Children.Add(left);
        var div = new Border { Classes = { "divider" } };
        Grid.SetColumn(div, 1);
        top.Children.Add(div);
        var right = new ScrollViewer { Content = _inspector };
        Grid.SetColumn(right, 2);
        top.Children.Add(right);

        // 미리보기
        _preview.Classes.Add("mono");
        _previewBox = new Border
        {
            Height = 150,
            CornerRadius = new CornerRadius(6),
            Background = Res("CodeBackground"),
            BorderBrush = Res("DividerBrush"),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = _preview },
        };
        var toggle = new ToggleButton { Classes = { "hover" }, IsChecked = true };
        void SetToggle() => toggle.Content = new TextBlock
        {
            Text = (toggle.IsChecked == true ? "▾ " : "▸ ") + Tr("미리보기"),
            Classes = { "headline" },
        };
        SetToggle();
        toggle.IsCheckedChanged += (_, _) =>
        {
            SetToggle();
            _previewBox.IsVisible = toggle.IsChecked == true;
        };
        toggle.Background = Brushes.Transparent;
        toggle.BorderThickness = new Thickness(0);
        _copy.TextProvider = () => Vm.SqlText ?? "";
        var previewHead = new DockPanel();
        var headRight = Ui.HStack(6, _copy, _previewState);
        DockPanel.SetDock(headRight, Dock.Right);
        previewHead.Children.Add(headRight);
        previewHead.Children.Add(toggle);
        _execError.Classes.Add("error");
        var previewSection = new Border
        {
            Padding = new Thickness(12),
            BorderBrush = Res("DividerBrush"),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Child = Ui.Stack(6, previewHead, _previewBox, _execError),
        };

        // 아래 버튼
        _openTab = Ui.Button(Tr("쿼리 탭으로 열기"), () => Vm.OpenInTab());
        _execute = Ui.Button(Tr("실행…"), () => _ = ConfirmAndExecuteAsync(), isDefault: true);
        var cancel = Ui.Button(Tr("취소"), () => { Vm.App.AlteringTable = null; Close(); }, isCancel: true);
        var footerLeft = new List<Control>();
        if (Vm.ReadOnly) footerLeft.Add(Ui.Text("🔒 " + Tr("읽기전용 접속이라 실행할 수 없어요. SQL을 복사해서 사용하세요."), "secondary"));
        var footer = new Border { Padding = new Thickness(12), Child = Ui.ButtonRow([cancel, _openTab, _execute], footerLeft) };

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        root.Children.Add(top);
        Grid.SetRow(previewSection, 1);
        root.Children.Add(previewSection);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;

        Vm.Changed += () =>
        {
            RebuildTree();
            UpdateState();
        };
        Vm.PropertyChanged += OnVmChanged;
        RebuildTree();
        RebuildInspector();
        UpdateState();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TableEditorViewModel.Selection):
                RebuildTree();
                RebuildInspector();
                UpdateState();
                break;
            case nameof(TableEditorViewModel.Executing):
            case nameof(TableEditorViewModel.ExecError):
                UpdateState();
                break;
        }
    }

    /// 버튼 상태 · 미리보기 · 변경 건수
    private void UpdateState()
    {
        _summary.Text = Vm.ChangeSummary;
        _remove.IsEnabled = Vm.CanRemove;
        _duplicate.IsEnabled = Vm.IsColumnSelected;
        _up.IsEnabled = Vm.CanMove(-1);
        _down.IsEnabled = Vm.CanMove(1);
        var (sql, err) = Vm.Preview;
        _preview.Text = Vm.PreviewText;
        if (sql != null) _preview.ClearValue(TextBlock.ForegroundProperty);
        else _preview.Foreground = Res(err != null ? "RedBrush" : "SecondaryText");
        _copy.IsVisible = sql != null;
        _previewState.Text = sql != null ? "✓" : err != null ? "⛔" : "";
        _previewState.Foreground = sql != null ? Res("GreenBrush") : Res("RedBrush");
        _execError.Text = Vm.ExecError;
        _execError.IsVisible = Vm.ExecError != null;
        _openTab.IsEnabled = sql != null;
        _execute.IsEnabled = Vm.CanExecute;
    }

    private async Task ConfirmAndExecuteAsync()
    {
        if (!Vm.CanExecute) return;
        var ok = await new WindowDialogs(this).ConfirmAsync(Tr("이 ALTER 문을 실행할까요?"), Vm.ConfirmMessage, Tr("실행"), Tr("취소"), destructive: true);
        if (ok) await Vm.ExecuteAsync();
    }

    // MARK: - 트리

    private void RebuildTree()
    {
        _tree.Children.Clear();
        var t = Vm.Table;

        var head = Ui.HStack(6, Glyph(Glyphs.Table, "BlueBrush"), new TextBlock { Text = t.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                             new TextBlock { Text = $"{Vm.Database} [{Vm.ConnectionName}]", Classes = { "caption" }, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (Vm.CommentChanged) head.Children.Add(Dot());
        _tree.Children.Add(Row(head, new Node.Table(), 0));

        // columns
        _tree.Children.Add(Folder("columns", Vm.Columns.Count, _columnsOpen, () => _columnsOpen = !_columnsOpen));
        if (_columnsOpen)
        {
            foreach (var c in Vm.Columns)
            {
                var pk = Vm.IsPK(c.Name);
                var fk = !pk && Vm.IsFK(c.Name);
                var row = new DockPanel();
                var mark = MarkView(Vm.ColumnMark(c));
                if (mark != null)
                {
                    DockPanel.SetDock(mark, Dock.Right);
                    row.Children.Add(mark);
                }
                var auto = new ColumnExtraParts(c.Extra, Vm.Kind).AutoIncrement ? " (auto increment)" : "";
                row.Children.Add(Ui.HStack(5,
                    pk ? Glyph(Glyphs.Key, "YellowBrush") : fk ? Glyph(Glyphs.Link, "BlueBrush") : Glyph("▭", "SecondaryText"),
                    new TextBlock { Text = c.Name.Length == 0 ? Tr("(이름 없음)") : c.Name, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = c.ColumnType + auto, Classes = { "caption" }, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center }));
                _tree.Children.Add(Row(row, new Node.Column(c.Id), 1));
            }
            foreach (var c in Vm.DeletedColumns)
            {
                var row = Ui.HStack(5, Glyph("▭", "RedBrush"),
                    new TextBlock { Text = c.Name, TextDecorations = TextDecorations.Strikethrough, Foreground = Res("RedBrush"), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = Tr("삭제 예정"), FontSize = 11, Foreground = Res("RedBrush"), VerticalAlignment = VerticalAlignment.Center });
                _tree.Children.Add(Row(row, new Node.DeletedColumn(c.Name), 1));
            }
        }

        // keys
        var p = Vm.PrimaryIndex;
        _tree.Children.Add(Folder("keys", p == null ? 0 : 1, _keysOpen, () => _keysOpen = !_keysOpen));
        if (_keysOpen && p is { } pi)
        {
            var e = Vm.Indexes[pi];
            _tree.Children.Add(Row(Ui.HStack(5, Glyph(Glyphs.Key, "YellowBrush"), new TextBlock { Text = e.Name, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = $"({string.Join(", ", e.Columns)})", Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center }), new Node.Index(e.Id), 1));
        }

        // indexes
        _tree.Children.Add(Folder("indexes", Vm.Indexes.Count, _indexesOpen, () => _indexesOpen = !_indexesOpen));
        if (_indexesOpen)
        {
            foreach (var e in Vm.Indexes)
            {
                var row = new DockPanel();
                var mark = MarkView(Vm.IndexMark(e));
                if (mark != null)
                {
                    DockPanel.SetDock(mark, Dock.Right);
                    row.Children.Add(mark);
                }
                row.Children.Add(Ui.HStack(5, Glyph("☰", "BlueBrush"), new TextBlock { Text = e.Name, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = $"({string.Join(", ", e.Columns)})" + (e.Unique ? " UNIQUE" : ""), Classes = { "caption" }, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center }));
                _tree.Children.Add(Row(row, new Node.Index(e.Id), 1));
            }
        }

        // foreign keys
        _tree.Children.Add(Folder("foreign keys", t.ForeignKeys.Count, _fksOpen, () => _fksOpen = !_fksOpen));
        if (_fksOpen)
            foreach (var fk in t.ForeignKeys)
                _tree.Children.Add(Row(Ui.HStack(5, Glyph(Glyphs.Link, "BlueBrush"), new TextBlock { Text = fk.Name, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = $"({string.Join(", ", fk.Columns)}) → {fk.ReferencedTable}", Classes = { "caption" }, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center }),
                    new Node.ForeignKey(fk.Name), 1));
    }

    private Control Folder(string title, int count, bool open, Action toggle)
    {
        var s = Ui.HStack(5, new TextBlock { Text = open ? "▾" : "▸", Width = 10, VerticalAlignment = VerticalAlignment.Center }, Glyph(Glyphs.Folder, "BlueBrush"),
                          new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        if (count > 0) s.Children.Add(new TextBlock { Text = count.ToString(), Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center });
        var b = new Border { Child = s, Padding = new Thickness(6, 3), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        b.PointerPressed += (_, _) =>
        {
            toggle();
            RebuildTree();
        };
        return b;
    }

    private Control Row(Control content, Node node, int level)
    {
        var selected = Equals(Vm.Selection, node);
        var b = new Border
        {
            Child = content,
            Padding = new Thickness(6 + level * 18, 3, 6, 3),
            CornerRadius = new CornerRadius(4),
            Background = selected ? Res("SelectionBackground") : Brushes.Transparent,
            Tag = node,
        };
        b.PointerPressed += (_, _) => Vm.Selection = node;
        return b;
    }

    private static Control? MarkView(TableEditorViewModel.Mark m) => m switch
    {
        TableEditorViewModel.Mark.New => new TextBlock { Text = Tr("새"), FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Res("GreenBrush"), VerticalAlignment = VerticalAlignment.Center },
        TableEditorViewModel.Mark.Changed => Dot(),
        _ => null,
    };

    private static Control Dot() => new Avalonia.Controls.Shapes.Ellipse
    {
        Width = 7, Height = 7, Fill = Res("OrangeBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0),
    };

    private static TextBlock Glyph(string text, string brush) => new()
    {
        Text = text, Foreground = Res(brush), Width = 16, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    // MARK: - 속성 (오른쪽)

    private void RebuildInspector()
    {
        _inspector.Children.Clear();
        _inspectorHeader = null;
        switch (Vm.Selection)
        {
            case Node.Column when Vm.SelectedColumn is { } c: ColumnForm(c); break;
            case Node.Index when Vm.SelectedIndex is { } e: IndexForm(e); break;
            case Node.ForeignKey f when Vm.Table.ForeignKeys.FirstOrDefault(x => x.Name == f.Name) is { } fk: FkForm(fk); break;
            case Node.DeletedColumn d:
                _inspector.Children.Add(new TextBlock { Text = "🗑 " + Tr("%@ 컬럼을 삭제할 예정이에요.", d.Name), Foreground = Res("RedBrush") });
                _inspector.Children.Add(Ui.Button(Tr("삭제 취소 (되살리기)"), () => Vm.Restore(d.Name)));
                break;
            default: TableForm(); break;
        }
    }

    private void Header(string icon, string title)
    {
        _inspectorHeader = new TextBlock { Text = title, FontSize = 17, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Center };
        _inspector.Children.Add(new Border
        {
            Background = new SolidColorBrush(Colors.Gray, 0.12),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Ui.HStack(6, new TextBlock { Text = icon, VerticalAlignment = VerticalAlignment.Center }, _inspectorHeader),
        });
    }

    /// 이름 칸 + 값 칸 두 열 표
    private Grid Form()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14, RowSpacing = 12 };
        _inspector.Children.Add(g);
        return g;
    }

    private static void AddRow(Grid g, string label, Control value)
    {
        var r = g.RowDefinitions.Count;
        g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var l = new TextBlock { Text = label, Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(l, r);
        g.Children.Add(l);
        Grid.SetRow(value, r);
        Grid.SetColumn(value, 1);
        g.Children.Add(value);
    }

    private static TextBox Box(string text, Action<string> changed, bool mono = false, string? placeholder = null, bool multiline = false)
    {
        var b = new TextBox { Text = text, PlaceholderText = placeholder, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (multiline) b.MaxHeight = 120;
        if (mono) b.Classes.Add("mono");
        // TextChanged 는 나중에(디스패처로) 오기도 해서, 만들 때 넣은 값 그대로면 무시한다
        // (안 그러면 다시 만든 속성 칸의 옛 상자가 이미 바뀐 값을 되돌린다)
        var last = text;
        b.TextChanged += (_, _) =>
        {
            var now = b.Text ?? "";
            if (now == last) return;
            last = now;
            changed(now);
        };
        return b;
    }

    private void TableForm()
    {
        var t = Vm.Table;
        Header(Glyphs.Table, t.Name);
        var g = Form();
        AddRow(g, Tr("이름"), new SelectableTextBlock { Text = t.Name, Classes = { "mono" } });
        AddRow(g, Tr("코멘트"), Box(Vm.Comment, s => Vm.Comment = s, placeholder: Tr("테이블 코멘트"), multiline: true));
        AddRow(g, Tr("요약"), Ui.Text(Tr("컬럼 %@ · 인덱스 %@ · 외래 키 %@", Vm.Columns.Count, Vm.Indexes.Count, t.ForeignKeys.Count), "secondary"));
        _inspector.Children.Add(Ui.Caption(Tr("테이블 이름 변경은 사이드바에서 테이블 우클릭 > 이름 바꾸기를 쓰세요.")));
    }

    private void ColumnForm(EditableColumn c)
    {
        var kind = Vm.Kind;
        Header("▭", c.Name.Length == 0 ? Tr("(이름 없음)") : c.Name);
        var g = Form();
        var renamed = Ui.Caption("");
        renamed.Foreground = Res("OrangeBrush");
        void UpdateRenamed()
        {
            renamed.IsVisible = c.OriginalName is { } o && o != c.Name;
            renamed.Text = "✎ " + Tr("원래 이름: %@ (이름 변경)", c.OriginalName ?? "");
        }
        AddRow(g, Tr("이름"), Box(c.Name, s =>
        {
            // Mac 과 같이 인덱스의 컬럼 이름은 따라 바꾸지 않는다 (미리보기에 검증 오류로 보인다)
            c.Name = s;
            if (_inspectorHeader != null) _inspectorHeader.Text = s.Length == 0 ? Tr("(이름 없음)") : s;
            UpdateRenamed();
            Vm.Touch();
        }, mono: true, placeholder: Tr("이름")));
        AddRow(g, Tr("코멘트"), Box(c.Comment, s => { c.Comment = s; Vm.Touch(); }, multiline: true));

        var typeBox = Box(c.ColumnType, s => { c.ColumnType = s; Vm.Touch(); }, mono: true, placeholder: Tr("타입"));
        var typeMenu = new Button { Content = "▾", Classes = { "hover" }, VerticalAlignment = VerticalAlignment.Center };
        var flyout = new MenuFlyout();
        foreach (var t in Vm.CommonTypes) flyout.Items.Add(Item(t, () => typeBox.Text = t));
        typeMenu.Flyout = flyout;
        var typeRow = new DockPanel();
        DockPanel.SetDock(typeMenu, Dock.Right);
        typeRow.Children.Add(typeMenu);
        typeRow.Children.Add(typeBox);
        AddRow(g, Tr("데이터 타입"), typeRow);

        var notNull = new CheckBox { Content = "Not Null", IsChecked = !c.Nullable };
        notNull.IsCheckedChanged += (_, _) => { c.Nullable = notNull.IsChecked != true; Vm.Touch(); };
        var auto = new CheckBox { Content = "Auto Increment", IsChecked = new ColumnExtraParts(c.Extra, kind).AutoIncrement };
        auto.IsCheckedChanged += (_, _) => Vm.EditExtra(c, p => p.AutoIncrement = auto.IsChecked == true);
        var pk = new CheckBox { Content = Tr("기본 키"), IsChecked = Vm.IsPK(c.Name) };
        pk.IsCheckedChanged += (_, _) =>
        {
            Vm.SetPK(c.Name, pk.IsChecked == true);
            notNull.IsChecked = !c.Nullable;
        };
        AddRow(g, "", Ui.HStack(24, notNull, auto, pk));

        AddRow(g, Tr("기본값"), Box(c.DefaultValue ?? "", s => { c.DefaultValue = s.Length == 0 ? null : s; Vm.Touch(); }, mono: true,
                                     placeholder: kind == DatabaseKind.Mysql ? Tr("없음") : Tr("없음 (SQL 식)")));
        if (kind == DatabaseKind.Mysql)
        {
            var parts = new ColumnExtraParts(c.Extra, kind);
            AddRow(g, "On Update", Box(parts.OnUpdate, s => Vm.EditExtra(c, p => p.OnUpdate = s), mono: true, placeholder: "CURRENT_TIMESTAMP"));
            AddRow(g, Tr("기타 Extra"), Box(parts.Rest, s => Vm.EditExtra(c, p => p.Rest = s), mono: true));
        }
        UpdateRenamed();
        _inspector.Children.Add(renamed);
    }

    private void IndexForm(EditableIndex e)
    {
        var isPrimary = Vm.IsSelectedPrimary;
        Header(isPrimary ? Glyphs.Key : "☰", e.Name);
        var g = Form();
        var name = Box(e.Name, s =>
        {
            e.Name = s;
            if (_inspectorHeader != null) _inspectorHeader.Text = s;
            Vm.Touch();
        }, mono: true, placeholder: Tr("이름"));
        name.IsEnabled = !isPrimary;
        AddRow(g, Tr("이름"), name);
        var unique = new CheckBox { Content = "UNIQUE", IsChecked = e.Unique, IsEnabled = !isPrimary };
        unique.IsCheckedChanged += (_, _) => { e.Unique = unique.IsChecked == true; Vm.Touch(); };
        AddRow(g, "", unique);

        var cols = new StackPanel { Spacing = 4 };
        for (var k = 0; k < e.Columns.Count; k++)
        {
            var at = k;
            var up = new Button { Content = "↑", Classes = { "borderless" }, IsEnabled = k > 0 };
            up.Click += (_, _) =>
            {
                (e.Columns[at], e.Columns[at - 1]) = (e.Columns[at - 1], e.Columns[at]);
                Vm.Touch();
                RebuildInspector();
            };
            var remove = new Button { Content = "⊖", Classes = { "borderless" } };
            remove.Click += (_, _) =>
            {
                e.Columns.RemoveAt(at);
                Vm.Touch();
                RebuildInspector();
            };
            var row = new DockPanel();
            var buttons = Ui.HStack(2, up, remove);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(Ui.HStack(6, new TextBlock { Text = $"{k + 1}.", Width = 22, TextAlignment = TextAlignment.Right, Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center },
                                       new TextBlock { Text = e.Columns[k], Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Center }));
            cols.Children.Add(row);
        }
        var addCol = new Button { Content = Tr("컬럼 추가") + " ▾", HorizontalAlignment = HorizontalAlignment.Left };
        var menu = new MenuFlyout();
        foreach (var c in Vm.Columns.Where(c => !e.Columns.Contains(c.Name)))
        {
            var n = c.Name;
            menu.Items.Add(Item(n, () =>
            {
                e.Columns.Add(n);
                Vm.Touch();
                RebuildInspector();
            }));
        }
        addCol.Flyout = menu;
        addCol.IsEnabled = menu.Items.Count > 0;
        cols.Children.Add(addCol);
        AddRow(g, Tr("컬럼"), cols);
        if (g.Children[^2] is TextBlock label) label.VerticalAlignment = VerticalAlignment.Top;
    }

    private void FkForm(ForeignKeyMeta fk)
    {
        Header(Glyphs.Link, fk.Name);
        var g = Form();
        AddRow(g, Tr("컬럼"), new TextBlock { Text = string.Join(", ", fk.Columns), Classes = { "mono" } });
        AddRow(g, Tr("참조"), new TextBlock { Text = $"{fk.ReferencedTable} ({string.Join(", ", fk.ReferencedColumns)})", Classes = { "mono" } });
        _inspector.Children.Add(Ui.Caption(Tr("외래 키는 여기서 보기만 할 수 있어요. 바꾸려면 쿼리 탭에서 ALTER 문을 실행하세요.")));
    }

    // MARK: - 도우미

    private static Button ToolButton(string glyph, string tip, Action onClick)
    {
        var b = new Button { Content = glyph, Classes = { "hover" }, MinWidth = 26 };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    private static MenuItem Item(string header, Action onClick)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => onClick();
        return m;
    }

    private static IBrush? Res(string key) =>
        Application.Current is { } a && a.TryFindResource(key, a.ActualThemeVariant, out var r) ? r as IBrush : null;
}
