using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 모델링 편집기 공용 조각
internal static class ModelUi
{
    /// Mac .pickerStyle(.segmented) 대신: 하나만 눌리는 토글 버튼 줄
    public static StackPanel Segmented(IReadOnlyList<string> titles, int selected, Action<int> onChange)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        var buttons = new List<ToggleButton>();
        for (int i = 0; i < titles.Count; i++)
        {
            int index = i;
            var b = new ToggleButton
            {
                Content = titles[i], IsChecked = i == selected, Padding = new Thickness(10, 3), MinWidth = 0,
                CornerRadius = new CornerRadius(i == 0 ? 5 : 0, i == titles.Count - 1 ? 5 : 0, i == titles.Count - 1 ? 5 : 0, i == 0 ? 5 : 0),
                FontSize = 12,
            };
            b.Click += (_, _) =>
            {
                foreach (var x in buttons) x.IsChecked = ReferenceEquals(x, b);
                onChange(index);
            };
            buttons.Add(b);
            panel.Children.Add(b);
        }
        return panel;
    }

    public static TextBox Box(string text, string placeholder, double width = double.NaN, bool mono = false)
    {
        var t = new TextBox { Text = text, PlaceholderText = placeholder, Width = width, VerticalContentAlignment = VerticalAlignment.Center };
        if (mono) t.Classes.Add("mono");
        return t;
    }

    /// 입력이 바뀔 때마다 (처음 넣은 값으로는 부르지 않음)
    public static TextBox OnText(this TextBox box, Action<string> changed)
    {
        // TextChanged 는 나중에(디스패처로) 오므로 Text 속성 변경을 바로 받는다
        box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) changed(box.Text ?? ""); };
        return box;
    }

    public static Button IconButton(string glyph, string tip, Action onClick, params string[] classes)
    {
        var b = new Button { Content = glyph, Classes = { "hover" } };
        foreach (var c in classes) b.Classes.Add(c);
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static CheckBox Check(bool value, Action<bool> changed, string? content = null)
    {
        var c = new CheckBox { IsChecked = value, Content = content, MinWidth = 0, Padding = new Thickness(content == null ? 0 : 6, 0, 0, 0) };
        c.IsCheckedChanged += (_, _) => changed(c.IsChecked == true);
        return c;
    }

    public static Ellipse ColorDot(string? color, double size = 12) =>
        new() { Width = size, Height = size, Fill = new SolidColorBrush(ModelCanvas.CardColor(color)) };

    public static Border Divider() => new() { Height = 1, Background = Brush("DividerBrush") };

    public static IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var v) == true && v is IBrush b
            ? b : Brushes.Gray;
}

/// 다이어그램 아래 테이블 편집기 (Mac ModelTableEditor): 이름 · 컬럼 / 인덱스 / 관계 / 설명 탭 · 카드 색 · 삭제
public sealed class ModelTableEditor : DockPanel
{
    private readonly ModelingViewModel _vm;
    private readonly Guid _tableId;
    /// 컬럼 칸 너비 (Mac columnsGrid 의 frame 들)
    private const string ColumnLayout = "40,140,140,150,26,26,26,26,110,*,16,26";

    public ModelTableEditor(ModelingViewModel vm, Guid tableId)
    {
        _vm = vm;
        _tableId = tableId;
        Background = ModelUi.Brush("PanelBackground");
        Build();
    }

    private ModelTable? Table => _vm.Model?.Table(_tableId);

    private void Build()
    {
        if (_vm.Model is not { } m || Table is not { } t) return;
        var header = new DockPanel { Margin = new Thickness(12, 8) };
        var right = Ui.HStack(4,
            ColorMenu(t),
            ModelUi.IconButton("🗑", Tr("테이블 삭제"), () => _ = _vm.DeleteSelectionAsync(), "danger"),
            ModelUi.IconButton(Glyphs.Close, Tr("닫기"), () => _vm.Selection = null));
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        var logical = ModelUi.Box(t.LogicalName, Tr("논리명"), 180).OnText(s =>
        {
            if (Table is { } x && x.LogicalName != s) { x.LogicalName = s; _vm.Changed(); }
        });
        ToolTip.SetTip(logical, Tr("논리명 (업무 이름, 예: 회원)"));
        var physical = ModelUi.Box(t.PhysicalName, Tr("물리명"), 180, mono: true).OnText(s =>
        {
            if (Table is { } x && x.PhysicalName != s) { x.PhysicalName = s; _vm.Changed(); }
        });
        ToolTip.SetTip(physical, Tr("물리명 (실제 테이블 이름, 예: tb_member)"));
        int relCount = m.RelationsOf(t.Id).Count;
        var tabs = ModelUi.Segmented(
            [Tr("컬럼 %@", t.Columns.Count), Tr("인덱스 %@", t.Indexes.Count), Tr("관계 %@", relCount), Tr("설명")],
            _vm.EditorTab, i => { _vm.EditorTab = i; Rebuild(); });
        header.Children.Add(Ui.HStack(10,
            new GodDB.App.Controls.TableCellsIcon { Foreground = new SolidColorBrush(ModelCanvas.Accent), VerticalAlignment = VerticalAlignment.Center },
            logical, physical, tabs));
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);
        var div = ModelUi.Divider();
        DockPanel.SetDock(div, Dock.Top);
        Children.Add(div);
        Children.Add(_vm.EditorTab switch
        {
            0 => ColumnsGrid(m, t),
            1 => IndexList(t),
            2 => RelationList(m, t),
            _ => CommentBox(t),
        });
    }

    private void Rebuild()
    {
        Children.Clear();
        Build();
    }

    private Control ColorMenu(ModelTable t)
    {
        var flyout = new MenuFlyout();
        foreach (var c in ModelDiagram.CardColors)
        {
            var item = new MenuItem { Header = c, Icon = ModelUi.ColorDot(c == "blue" ? null : c, 10) };
            var color = c;
            item.Click += (_, _) => { _vm.SetCardColor(_tableId, color); Rebuild(); };
            flyout.Items.Add(item);
        }
        var b = new Button { Content = ModelUi.ColorDot(t.Color), Classes = { "hover" }, Flyout = flyout };
        ToolTip.SetTip(b, Tr("카드 색"));
        return b;
    }

    // MARK: 컬럼

    private Control ColumnsGrid(DataModel m, ModelTable t)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnLayout), ColumnSpacing = 6, Margin = new Thickness(12, 4) };
        string[] titles = ["", Tr("논리명"), Tr("물리명"), Tr("타입"), "PK", "NN", "AI", "UQ", Tr("기본값"), Tr("설명")];
        for (int i = 0; i < titles.Length; i++)
        {
            var tb = Ui.Caption(titles[i]);
            tb.TextWrapping = TextWrapping.NoWrap;
            if (i is >= 4 and <= 7) tb.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(tb, i);
            head.Children.Add(tb);
        }
        var add = new Button { Content = Glyphs.Plus + " " + Tr("컬럼 추가"), Classes = { "hover" }, FontSize = 11 };
        add.Click += (_, _) => _vm.AddColumn(_tableId);
        var headRow = new DockPanel();
        DockPanel.SetDock(add, Dock.Right);
        headRow.Children.Add(add);
        headRow.Children.Add(head);

        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(12, 4) };
        for (int i = 0; i < t.Columns.Count; i++) rows.Children.Add(ColumnRow(m, t, t.Columns[i], i));
        var panel = new DockPanel();
        DockPanel.SetDock(headRow, Dock.Top);
        panel.Children.Add(headRow);
        var div = ModelUi.Divider();
        DockPanel.SetDock(div, Dock.Top);
        panel.Children.Add(div);
        panel.Children.Add(new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return panel;
    }

    private Control ColumnRow(DataModel m, ModelTable t, ModelColumn c, int index)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnLayout), ColumnSpacing = 6, Tag = c.Id };
        void Put(Control ctl, int col)
        {
            Grid.SetColumn(ctl, col);
            g.Children.Add(ctl);
        }
        var up = new Button { Content = "▲", Classes = { "borderless" }, FontSize = 9, IsEnabled = index > 0 };
        up.Click += (_, _) => _vm.MoveColumn(_tableId, index, -1);
        var down = new Button { Content = "▼", Classes = { "borderless" }, FontSize = 9, IsEnabled = index < t.Columns.Count - 1 };
        down.Click += (_, _) => _vm.MoveColumn(_tableId, index, 1);
        Put(Ui.HStack(0, up, down), 0);

        Put(ModelUi.Box(c.LogicalName, Tr("논리명")).OnText(s => { if (c.LogicalName != s) { c.LogicalName = s; _vm.Changed(); } }), 1);
        var missing = new TextBlock
        {
            Text = "!", Foreground = ModelUi.Brush("OrangeBrush"), FontWeight = FontWeight.Bold, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            IsVisible = c.PhysicalName.Length == 0,
        };
        var physical = ModelUi.Box(c.PhysicalName, Tr("물리명"), mono: true).OnText(s =>
        {
            missing.IsVisible = s.Length == 0;
            if (c.PhysicalName != s) { c.PhysicalName = s; _vm.Changed(); }
        });
        Put(new Panel { Children = { physical, missing } }, 2);

        var type = ModelUi.Box(c.DataType, Tr("타입"), mono: true).OnText(s => { if (c.DataType != s) { c.DataType = s; _vm.Changed(); } });
        var typeMenu = new MenuFlyout();
        foreach (var name in DataModel.CommonTypes(m.Kind))
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => type.Text = name;
            typeMenu.Items.Add(item);
        }
        var typeButton = new Button { Content = "▾", Classes = { "borderless" }, Flyout = typeMenu };
        DockPanel.SetDock(typeButton, Dock.Right);
        Put(new DockPanel { Children = { typeButton, type } }, 3);

        bool syncing = false;
        CheckBox nn = null!, uq = null!;
        var pk = ModelUi.Check(c.PrimaryKey, v =>
        {
            if (syncing) return;
            c.PrimaryKey = v;
            syncing = true;
            nn.IsChecked = c.NotNull || c.PrimaryKey;
            nn.IsEnabled = !c.PrimaryKey;
            uq.IsEnabled = !c.PrimaryKey;
            syncing = false;
            _vm.Changed();
        });
        nn = ModelUi.Check(c.NotNull || c.PrimaryKey, v => { if (!syncing) { c.NotNull = v; _vm.Changed(); } });
        nn.IsEnabled = !c.PrimaryKey;
        var ai = ModelUi.Check(c.AutoIncrement, v => { c.AutoIncrement = v; _vm.Changed(); });
        uq = ModelUi.Check(c.Unique, v => { c.Unique = v; _vm.Changed(); });
        uq.IsEnabled = !c.PrimaryKey;
        int col = 4;
        foreach (var cb in new[] { pk, nn, ai, uq })
        {
            cb.HorizontalAlignment = HorizontalAlignment.Center;
            Put(cb, col++);
        }
        Put(ModelUi.Box(c.DefaultValue, Tr("없음"), mono: true).OnText(s => { if (c.DefaultValue != s) { c.DefaultValue = s; _vm.Changed(); } }), 8);
        Put(ModelUi.Box(c.Comment, "").OnText(s => { if (c.Comment != s) { c.Comment = s; _vm.Changed(); } }), 9);
        var link = new TextBlock
        {
            Text = Glyphs.Link, FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
            Opacity = m.IsForeignKeyColumn(t.Id, c.Id) ? 1 : 0,
        };
        ToolTip.SetTip(link, Tr("관계로 생긴 FK 컬럼"));
        Put(link, 10);
        var remove = new Button { Content = "⊖", Classes = { "borderless", "danger" } };
        ToolTip.SetTip(remove, Tr("컬럼 삭제"));
        var id = c.Id;
        remove.Click += (_, _) => _vm.RemoveColumn(_tableId, id);
        Put(remove, 11);
        return g;
    }

    // MARK: 인덱스

    private Control IndexList(ModelTable t)
    {
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(12) };
        var add = new Button { Content = Glyphs.Plus + " " + Tr("인덱스 추가"), Classes = { "hover" } };
        add.Click += (_, _) => _vm.AddIndex(_tableId);
        var top = new DockPanel();
        DockPanel.SetDock(add, Dock.Right);
        top.Children.Add(add);
        top.Children.Add(Ui.Caption(Tr("PK 와 UQ 표시 컬럼은 자동으로 만들어져요. 여러 컬럼 인덱스를 여기서 추가하세요.")));
        stack.Children.Add(top);
        foreach (var idx in t.Indexes)
        {
            var name = ModelUi.Box(idx.Name, Tr("이름"), 200, mono: true).OnText(s => { if (idx.Name != s) { idx.Name = s; _vm.Changed(); } });
            var unique = ModelUi.Check(idx.Unique, v => { idx.Unique = v; _vm.Changed(); }, "UNIQUE");
            // 고른 순서대로 컬럼
            var menu = new MenuFlyout();
            foreach (var c in t.Columns)
            {
                int k = idx.Columns.IndexOf(c.Id);
                var item = new MenuItem
                {
                    Header = (k >= 0 ? $"{k + 1}. " : "") + c.DisplayName + (c.PhysicalName.Length == 0 ? "" : $" ({c.PhysicalName})"),
                };
                var cid = c.Id;
                var iid = idx.Id;
                item.Click += (_, _) => _vm.ToggleIndexColumn(_tableId, iid, cid);
                menu.Items.Add(item);
            }
            var label = idx.Columns.Count == 0 ? Tr("컬럼 고르기")
                : string.Join(", ", idx.Columns.Select(id => t.Columns.FirstOrDefault(c => c.Id == id)?.DisplayName).OfType<string>());
            var pick = new Button { Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis }, MaxWidth = 360, Flyout = menu };
            var indexId = idx.Id;
            var remove = new Button { Content = "⊖", Classes = { "borderless", "danger" } };
            remove.Click += (_, _) => _vm.RemoveIndex(_tableId, indexId);
            var row = new DockPanel();
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(Ui.HStack(8, name, unique, pick));
            stack.Children.Add(row);
        }
        return new ScrollViewer { Content = stack };
    }

    // MARK: 관계

    private Control RelationList(DataModel m, ModelTable t)
    {
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(12) };
        var rels = m.RelationsOf(t.Id);
        if (rels.Count == 0)
            stack.Children.Add(Ui.Text(Tr("관계가 없어요. 위 도구 막대의 1:N / 1:1 도구로 부모 → 자식 순서로 테이블을 누르세요."), "secondary"));
        foreach (var r in rels)
        {
            var row = new DockPanel();
            var chevron = new TextBlock { Text = "›", Foreground = ModelUi.Brush("TertiaryText") };
            DockPanel.SetDock(chevron, Dock.Right);
            row.Children.Add(chevron);
            row.Children.Add(Ui.HStack(8,
                new TextBlock { Text = r.Parent == t.Id ? "→" : "←" },
                new TextBlock { Text = $"{m.Table(r.Parent)?.DisplayName ?? "?"} → {m.Table(r.Child)?.DisplayName ?? "?"}" },
                new TextBlock { Text = ModelDiagram.RelationKindText(r), Classes = { "secondary" } }));
            var b = new Button { Content = row, Classes = { "hover" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var rid = r.Id;
            b.Click += (_, _) => _vm.Selection = new ModelSelection.Relation(rid);
            stack.Children.Add(b);
        }
        return new ScrollViewer { Content = stack };
    }

    // MARK: 설명

    private Control CommentBox(ModelTable t)
    {
        var box = new TextBox { Text = t.Comment, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top };
        box.OnText(s => { if (Table is { } x && x.Comment != s) { x.Comment = s; _vm.Changed(); } });
        var caption = Ui.Caption(Tr("테이블 설명 (DB 코멘트에 논리명 다음 줄로 들어가요)"));
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(caption, Dock.Top);
        caption.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(caption);
        panel.Children.Add(box);
        return panel;
    }
}

/// 관계 편집기 (Mac ModelRelationEditor): 카디널리티 · 식별 · 선택 · FK 이름 · 제약 · ON DELETE / UPDATE · 컬럼 연결
public sealed class ModelRelationEditor : Border
{
    private readonly ModelingViewModel _vm;
    private readonly Guid _relationId;

    public ModelRelationEditor(ModelingViewModel vm, Guid relationId)
    {
        _vm = vm;
        _relationId = relationId;
        Background = ModelUi.Brush("PanelBackground");
        Padding = new Thickness(12);
        Build();
    }

    private void Build()
    {
        if (_vm.Model is not { } m || m.Relations.FirstOrDefault(x => x.Id == _relationId) is not { } r
            || m.Table(r.Parent) is not { } parent || m.Table(r.Child) is not { } child) return;
        void Set(Action<ModelRelation> change, bool rebuild = true) => _vm.UpdateRelation(_relationId, change, rebuild);

        var stack = new StackPanel { Spacing = 10 };
        var head = new DockPanel();
        var drop = ModelUi.Check(_vm.DropColumns, v => _vm.DropColumns = v, Tr("FK 컬럼도 삭제"));
        var delete = new Button { Content = Tr("관계 삭제"), Classes = { "danger" } };
        delete.Click += (_, _) => _vm.DeleteRelation(_relationId);
        var right = Ui.HStack(8, drop, delete, ModelUi.IconButton(Glyphs.Close, Tr("닫기"), () => _vm.Selection = null));
        DockPanel.SetDock(right, Dock.Right);
        head.Children.Add(right);
        head.Children.Add(Ui.HStack(8,
            new TextBlock { Text = "⑂", Foreground = new SolidColorBrush(ModelCanvas.Accent), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = Tr("관계"), Classes = { "headline" }, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = parent.DisplayName, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = "→", VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = child.DisplayName, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }));
        stack.Children.Add(head);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 12, RowSpacing = 8 };
        void Put(Control c, int row, int col)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, col);
            c.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(c);
        }
        Put(new TextBlock { Text = Tr("카디널리티") }, 0, 0);
        var cards = new[] { ModelCardinality.OneToMany, ModelCardinality.OneToOne };
        Put(ModelUi.Segmented(cards.Select(c => c.Title()).ToList(), Array.IndexOf(cards, r.Cardinality), i => Set(x => x.Cardinality = cards[i])), 0, 1);
        Put(ModelUi.Check(r.Identifying, v => Set(x => x.Identifying = v), Tr("식별 관계 (FK 가 자식 PK 에 포함)")), 0, 2);
        var optional = ModelUi.Check(r.Optional, v => Set(x => x.Optional = v), Tr("선택 (자식 FK NULL 허용)"));
        optional.IsEnabled = !r.Identifying;
        Put(optional, 0, 3);

        Put(new TextBlock { Text = Tr("FK 이름") }, 1, 0);
        var fkName = ModelUi.Box(r.Name, $"fk_{child.PhysicalName}_{parent.PhysicalName}", 260, mono: true)
            .OnText(s => { if (s != r.Name) Set(x => x.Name = s, rebuild: false); });
        Put(fkName, 1, 1);
        var constraint = ModelUi.Check(r.CreateConstraint, v => Set(x => x.CreateConstraint = v), Tr("DB 에 FK 제약 만들기"));
        ToolTip.SetTip(constraint, Tr("끄면 다이어그램에만 보이는 논리 관계가 돼요 (운영 DB 처럼 FK 를 안 거는 경우)"));
        Grid.SetColumnSpan(constraint, 2);
        Put(constraint, 1, 2);

        var actions = Enum.GetValues<ModelFkAction>();
        ComboBox ActionBox(ModelFkAction value, Action<ModelFkAction> set)
        {
            var box = new ComboBox { ItemsSource = actions.Select(a => a.RawValue()).ToList(), SelectedIndex = Array.IndexOf(actions, value) };
            box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) set(actions[box.SelectedIndex]); };
            return box;
        }
        Put(new TextBlock { Text = "ON DELETE" }, 2, 0);
        Put(ActionBox(r.OnDelete, a => Set(x => x.OnDelete = a, rebuild: false)), 2, 1);
        Put(new TextBlock { Text = "ON UPDATE" }, 2, 2);
        Put(ActionBox(r.OnUpdate, a => Set(x => x.OnUpdate = a, rebuild: false)), 2, 3);
        stack.Children.Add(grid);
        stack.Children.Add(ModelUi.Divider());
        stack.Children.Add(new TextBlock { Text = Tr("컬럼 연결"), FontSize = 11, FontWeight = FontWeight.SemiBold, Classes = { "secondary" } });
        foreach (var p in r.Pairs)
        {
            var cc = child.Columns.FirstOrDefault(c => c.Id == p.Child);
            var pc = parent.Columns.FirstOrDefault(c => c.Id == p.Parent);
            stack.Children.Add(Ui.HStack(8,
                new TextBlock { Text = $"{child.DisplayName}.{cc?.DisplayName ?? "?"}" },
                new TextBlock { Text = cc?.PhysicalName ?? "", Classes = { "mono", "secondary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "→" },
                new TextBlock { Text = $"{parent.DisplayName}.{pc?.DisplayName ?? "?"}" },
                new TextBlock { Text = pc?.PhysicalName ?? "", Classes = { "mono", "secondary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }));
        }
        Child = new ScrollViewer { Content = stack };
    }
}
