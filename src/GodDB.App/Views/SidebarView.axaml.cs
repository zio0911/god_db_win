using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 사이드바: 접속 목록(그룹) · DB 선택 · 테이블 트리 · 뷰/프로시저/함수/트리거 · 테이블 검색 (Mac SidebarView)
public partial class SidebarView : UserControl
{
    private SidebarViewModel? Vm => DataContext as SidebarViewModel;
    private bool _syncingSelection;

    public SidebarView()
    {
        InitializeComponent();
        // DataGrip 에서 데이터 소스를 Ctrl+C 한 뒤 사이드바에서 Ctrl+V
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        // TreeViewItem 이 더블클릭을 펼치기·접기로 처리하고 Handled 로 표시하므로, 처리된 이벤트도 받아야 테이블 데이터를 연다
        TableTree.AddHandler(DoubleTappedEvent, OnTableDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is not { } vm) return;
        vm.App.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName is nameof(AppState.SelectedTables)) SyncTreeSelection();
            if (a.PropertyName is nameof(AppState.SelectedTables) or nameof(AppState.SelectedProfileId))
                ToolTip.SetTip(DiagramButton, vm.DiagramButtonTip);
            if (a.PropertyName is nameof(AppState.SelectedProfileId) or nameof(AppState.SelectedRedis)) BindRedis();
        };
        vm.App.ConnectionChanged += _ => BindRedis();
        ToolTip.SetTip(DiagramButton, vm.DiagramButtonTip);
        BindRedis();
        AttachTableDescriptions(vm.App);
    }

    /// Redis 사이드바 영역(키 트리 · 검색)은 Redis 화면 포팅의 뷰가 RedisWorkspace 를 DataContext 로 받는다
    private void BindRedis()
    {
        var ws = Vm?.App.SelectedRedis;
        RedisSection.DataContext = ws;
        RedisSearch.DataContext = ws;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.Key != Key.V || e.KeyModifiers != KeyModifiers.Control) return;
        // 검색 칸에 붙여넣는 중이면 그대로 둔다
        if (e.Source is TextBox) return;
        var text = await vm.App.Dialogs.GetClipboardTextAsync();
        if (text != null && DataGripImport.LooksLikeDataGrip(text))
        {
            e.Handled = true;
            await vm.App.PasteDataGripAsync(text);
        }
    }

    // MARK: - 접속 한 줄

    private static ProfileRowViewModel? RowOf(object? sender) => (sender as Control)?.DataContext as ProfileRowViewModel;

    private void OnProfileTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm || RowOf(sender) is not { } row) return;
        // 다른 접속은 끊지 않고 이 접속을 열거나 보기로 바꾼다.
        vm.App.Fire(() => vm.App.OpenAsync(row.Profile.Id));
    }

    private void OnProfileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm || RowOf(sender) is not { } row) return;
        // 더블클릭: 이 접속의 쿼리 탭으로 (없으면 새 탭)
        vm.App.Fire(() => vm.App.OpenQueryTabAsync(row.Profile.Id));
    }

    private void OnProfileContext(object? sender, ContextRequestedEventArgs e)
    {
        if (Vm is not { } vm || RowOf(sender) is not { } row || sender is not Control target) return;
        e.Handled = true;
        var app = vm.App;
        var p = row.Profile;
        var items = new List<Control>();
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var m = new MenuItem { Header = header, IsEnabled = enabled };
            m.Click += (_, _) => action();
            return m;
        }
        if (row.State.IsConnected)
        {
            items.Add(Item(Tr("새 쿼리 탭"), () => { app.SelectedProfileId = p.Id; app.AddTab(profileId: p.Id); }));
            items.Add(Item(Tr("다시 연결"), () => app.RequestReconnect(p.Id)));
            items.Add(Item(Tr("연결 끊기"), () => app.Fire(() => app.DisconnectAsync(p.Id))));
        }
        else
        {
            items.Add(Item(Tr("연결"), () => app.Fire(() => app.ConnectAsync(p.Id))));
        }
        items.Add(new Separator());
        items.Add(Item(Tr("편집…"), () => app.Edit(p)));
        items.Add(Item(Tr("복제"), () => app.Duplicate(p)));
        // 그룹으로 이동
        var groupMenu = new MenuItem { Header = Tr("그룹으로 이동") };
        var others = app.ProfileGroups.Where(g => g != p.Group).ToList();
        foreach (var g in others) groupMenu.Items.Add(Item(g, () => app.MoveProfile(p.Id, g)));
        if (others.Count > 0) groupMenu.Items.Add(new Separator());
        groupMenu.Items.Add(Item(Tr("새 그룹…"), () => app.GroupPrompt = new GroupPrompt(new GroupPromptAction.Move(p.Id))));
        if (p.Group.Length > 0) groupMenu.Items.Add(Item(Tr("그룹에서 빼기"), () => app.MoveProfile(p.Id, "")));
        items.Add(groupMenu);
        items.Add(ReorderMenu(m => app.ReorderProfile(p.Id, m)));
        items.Add(new Separator());
        var delete = Item(Tr("삭제…"), () => app.Fire(() => ConfirmDeleteAsync(app, p)));
        delete.Classes.Add("danger");
        items.Add(delete);
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(target);
    }

    public static async Task ConfirmDeleteAsync(AppState app, ConnectionProfile p)
    {
        var n = app.Tabs.Count(t => t.ProfileId == p.Id);
        var ok = await app.Dialogs.ConfirmAsync(Tr("'%@' 접속을 삭제할까요?", p.Name),
            n > 0 ? Tr("접속 정보와 저장된 비밀번호, 이 접속의 쿼리 탭 %@개가 함께 지워져요. 되돌릴 수 없어요.", n)
                  : Tr("접속 정보와 저장된 비밀번호가 지워져요. 되돌릴 수 없어요."),
            Tr("삭제"), Tr("취소"), destructive: true);
        if (ok) await app.DeleteProfileAsync(p);
    }

    private void OnGroupContext(object? sender, ContextRequestedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { Tag: string group } target) return;
        e.Handled = true;
        var rename = new MenuItem { Header = Tr("그룹 이름 바꾸기…") };
        rename.Click += (_, _) => vm.App.GroupPrompt = new GroupPrompt(new GroupPromptAction.Rename(group));
        var ungroup = new MenuItem { Header = Tr("그룹 풀기 (접속은 남아요)") };
        ungroup.Click += (_, _) => vm.App.RenameGroup(group, "");
        var reorder = ReorderMenu(m => vm.App.ReorderGroup(group, m));
        new ContextMenu { ItemsSource = new List<Control> { rename, ungroup, reorder } }.Open(target);
    }

    /// 우클릭 > 순서 바꾸기 (맨 위로 · 위로 · 아래로 · 맨 아래로)
    public static MenuItem ReorderMenu(Action<ConnectionListOrder.Move> move)
    {
        var menu = new MenuItem { Header = Tr("순서 바꾸기") };
        foreach (var (title, m) in new[] { (Tr("맨 위로"), ConnectionListOrder.Move.Top), (Tr("위로"), ConnectionListOrder.Move.Up),
                                           (Tr("아래로"), ConnectionListOrder.Move.Down), (Tr("맨 아래로"), ConnectionListOrder.Move.Bottom) })
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => move(m);
            menu.Items.Add(item);
        }
        return menu;
    }

    // MARK: - 접속 정렬 메뉴 · 찾기 칸

    /// "접속" 제목 오른쪽 정렬 메뉴: 정렬 방식 4가지 + 연결된 접속 먼저 (Mac ConnectionSortMenu)
    private void OnSortMenu(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control target) return;
        BuildSortMenu(vm.App).Open(target);
    }

    public static ContextMenu BuildSortMenu(AppState app)
    {
        var items = new List<Control>();
        foreach (var s in ConnectionSorts.All)
        {
            var item = new MenuItem
            {
                Header = s.Title(),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = app.ConnectionSort == s,
                GroupName = "connectionSort",
            };
            item.Click += (_, _) => app.ConnectionSort = s;
            items.Add(item);
        }
        items.Add(new Separator());
        var connected = new MenuItem { Header = Tr("연결된 접속 먼저"), ToggleType = MenuItemToggleType.CheckBox, IsChecked = app.ConnectedFirst };
        connected.Click += (_, _) => app.ConnectedFirst = !app.ConnectedFirst;
        items.Add(connected);
        return new ContextMenu { ItemsSource = items };
    }

    private void OnConnectionSearchKey(object? sender, KeyEventArgs e)
    {
        // Esc 로 지움 (Mac onExitCommand)
        if (e.Key == Key.Escape && Vm is { } vm && vm.ConnectionQuery.Length > 0)
        {
            vm.ConnectionQuery = "";
            e.Handled = true;
        }
    }

    // MARK: - 접속을 그룹 위로 끌어 놓기

    private PointerPressedEventArgs? _pressed;
    private Point _pressPoint;

    private void OnProfilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = e;
        _pressPoint = e.GetPosition(this);
    }

    private async void OnProfilePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed || RowOf(sender) is not { } row) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressed = null; return; }
        var d = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(d.X) + Math.Abs(d.Y) < 6) return;
        _pressed = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(row.Profile.Id.ToString()));
        await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move);
    }

    private void OnGroupPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = e;
        _pressPoint = e.GetPosition(this);
    }

    /// 그룹 이름을 끌어 다른 그룹 이름 위에 놓으면 그 앞으로 (Mac .draggable(groupDragPrefix + group))
    private async void OnGroupPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed || sender is not Control { Tag: string group }) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressed = null; return; }
        var d = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(d.X) + Math.Abs(d.Y) < 6) return;
        _pressed = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(ConnectionListKeys.GroupDragPrefix + group));
        await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move);
    }

    /// 놓을 곳: 그룹 머리글 또는 접속 줄
    private static Border? FindDropTarget(Visual v)
    {
        for (Visual? x = v; x != null; x = x.GetVisualParent())
            if (x is Border b && (b.Classes.Contains("groupHeader") || b.Classes.Contains("profileRow"))) return b;
        return null;
    }

    private static Border? FindHeader(Visual v)
    {
        for (Visual? x = v; x != null; x = x.GetVisualParent())
            if (x is Border b && b.Classes.Contains("groupHeader")) return b;
        return null;
    }

    private Border? _dropTarget;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var header = e.Source is Visual v ? FindDropTarget(v) : null;
        if (_dropTarget != header)
        {
            _dropTarget?.Classes.Remove("dropTarget");
            _dropTarget = header;
            _dropTarget?.Classes.Add("dropTarget");
        }
        e.DragEffects = header != null && e.DataTransfer.Contains(DataFormat.Text) ? DragDropEffects.Move : DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _dropTarget?.Classes.Remove("dropTarget");
        _dropTarget = null;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dropTarget?.Classes.Remove("dropTarget");
        _dropTarget = null;
        if (Vm is not { } vm || e.Source is not Visual v || e.DataTransfer.TryGetText() is not { } text) return;
        var target = FindDropTarget(v);
        if (target is { Tag: string group } && target.Classes.Contains("groupHeader"))
        {
            // 그룹을 다른 그룹 이름 위에 놓으면 그 앞으로, 접속을 놓으면 그 그룹으로
            foreach (var line in text.Split('\n'))
            {
                var s = line.Trim();
                if (s.StartsWith(ConnectionListKeys.GroupDragPrefix, StringComparison.Ordinal))
                    vm.App.ReorderGroup(s[ConnectionListKeys.GroupDragPrefix.Length..], group);
                else if (Guid.TryParse(s, out var id)) vm.App.MoveProfile(id, group);
            }
        }
        else if (target?.DataContext is ProfileRowViewModel row)
        {
            // 다른 접속을 이 줄에 놓으면 이 접속 바로 앞으로 (이 접속의 그룹으로) 옮긴다
            foreach (var line in text.Split('\n'))
                if (Guid.TryParse(line.Trim(), out var id) && id != row.Profile.Id) vm.App.ReorderProfile(id, row.Profile.Id);
        }
    }

    // MARK: - 테이블 트리

    private void OnTableSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || Vm is not { } vm) return;
        var names = TableTree.SelectedItems.OfType<TableNodeViewModel>().Select(n => n.Name).ToHashSet();
        vm.App.SelectedTables = names;
    }

    private void SyncTreeSelection()
    {
        if (Vm is not { } vm) return;
        var current = TableTree.SelectedItems.OfType<TableNodeViewModel>().Select(n => n.Name).ToHashSet();
        if (current.SetEquals(vm.App.SelectedTables)) return;
        _syncingSelection = true;
        try
        {
            TableTree.SelectedItems.Clear();
            foreach (var n in vm.TableNodes.Where(n => vm.App.SelectedTables.Contains(n.Name))) TableTree.SelectedItems.Add(n);
        }
        finally { _syncingSelection = false; }
    }

    private void OnTableDoubleTapped(object? sender, TappedEventArgs e)
    {
        // 한 번 클릭은 목록 선택(Ctrl/Shift 다중 선택)에 맡기고, 두 번 클릭만 테이블 데이터를 연다
        if (Vm is not { } vm || (e.Source as Control)?.DataContext is not TableNodeViewModel node) return;
        e.Handled = true;
        vm.App.OpenTableQuery(node.Name);
    }

    private void OnRoutineDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RoutineRowViewModel r) r.OpenDefinitionCommand.Execute(null);
    }
}
