using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 사이드바 Redis 영역: DB 선택 · 트리/목록 · 자동 새로고침 · 키 트리. DataContext = RedisWorkspace (없으면 null) — Mac RedisSidebarSection
/// 키마다 타입·TTL 은 화면에 보이는 줄만 묶어서 조회한다 (Mac onAppear → wantMeta).
public partial class RedisSidebarSection : UserControl
{
    private readonly DispatcherTimer _ttlTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private RedisWorkspace? _bound;
    private ScrollViewer? _scroll;
    private bool _visibleCheckQueued;

    public RedisSidebarSection()
    {
        InitializeComponent();
        NewKeyButton.Click += (_, _) => { if (Ws is { } ws) ws.ShowNewKey = true; };
        TreeButton.Click += (_, _) => { if (Ws is { } ws) { ws.TreeMode = true; TreeButton.IsChecked = true; } };
        ListButton.Click += (_, _) => { if (Ws is { } ws) { ws.TreeMode = false; ListButton.IsChecked = true; } };
        RefreshButton.Click += (_, _) => { if (Ws is { } ws) ws.Fire(() => ws.RefreshAllAsync()); };
        AutoButton.Click += (_, _) => { if (Ws is { } ws) { ws.SetAutoRefresh(!ws.AutoRefresh); AutoButton.IsChecked = ws.AutoRefresh; } };
        SecondsBox.LostFocus += (_, _) => ApplySeconds();
        SecondsBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplySeconds(); e.Handled = true; } };
        MoreButton.Click += (_, _) => { if (Ws is { } ws) ws.Fire(() => ws.LoadMoreKeysAsync()); };
        DelimiterButton.Click += (_, _) => ShowDelimiterMenu();
        RowsList.AddHandler(TappedEvent, OnRowTapped);
        RowsList.AddHandler(ContextRequestedEvent, OnRowContext);
        _ttlTimer.Tick += (_, _) => Ws?.TickTtl();
        DataContextChanged += (_, _) => Bind();
        AttachedToVisualTree += (_, _) =>
        {
            _scroll = this.FindAncestorOfType<ScrollViewer>();
            if (_scroll != null) _scroll.ScrollChanged += (_, _) => QueueVisibleCheck();
            _ttlTimer.Start();
            QueueVisibleCheck();
        };
        DetachedFromVisualTree += (_, _) => _ttlTimer.Stop();
        LayoutUpdated += (_, _) => QueueVisibleCheck();
    }

    public RedisWorkspace? Ws => DataContext as RedisWorkspace;

    private void Bind()
    {
        if (_bound != null) _bound.PropertyChanged -= OnWsChanged;
        _bound = Ws;
        if (_bound != null) _bound.PropertyChanged += OnWsChanged;
        SecondsBox.Text = _bound?.AutoRefreshSeconds.ToString(CultureInfo.InvariantCulture) ?? "";
        Dispatcher.UIThread.Post(SyncDbBox);
        QueueVisibleCheck();
    }

    private void OnWsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RedisWorkspace.Rows):
                QueueVisibleCheck();
                break;
            case nameof(RedisWorkspace.Databases) or nameof(RedisWorkspace.SelectedDatabase) or nameof(RedisWorkspace.Db):
                // 목록(ItemsSource)이 바뀌면 콤보 상자가 선택을 지우고, 그때 바인딩이 같은 값을 이미 읽어 둔 상태라
                // 뒤이은 SelectedDatabase 알림을 무시해 칸이 비어 보였다. 바인딩을 끊지 않고 현재 DB 를 다시 맞춘다.
                Dispatcher.UIThread.Post(SyncDbBox);
                break;
            case nameof(RedisWorkspace.AutoRefreshSeconds):
                if (!SecondsBox.IsFocused) SecondsBox.Text = Ws?.AutoRefreshSeconds.ToString(CultureInfo.InvariantCulture);
                break;
        }
    }

    private void SyncDbBox()
    {
        var want = Ws?.SelectedDatabase;
        if (!Equals(DbBox.SelectedItem, want)) DbBox.SetCurrentValue(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, want);
    }

    private void ApplySeconds()
    {
        if (Ws is not { } ws) return;
        if (int.TryParse(SecondsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s != ws.AutoRefreshSeconds)
            ws.SetAutoRefreshSeconds(s);
        SecondsBox.Text = ws.AutoRefreshSeconds.ToString(CultureInfo.InvariantCulture);
    }

    // MARK: - 보이는 키만 타입·TTL 조회

    private void QueueVisibleCheck()
    {
        if (_visibleCheckQueued) return;
        _visibleCheckQueued = true;
        Dispatcher.UIThread.Post(() => { _visibleCheckQueued = false; RequestVisibleMeta(); }, DispatcherPriority.Background);
    }

    /// 화면(스크롤 영역) 안에 들어온 키 줄의 메타를 요청한다
    public void RequestVisibleMeta()
    {
        if (Ws is not { } ws || !IsEffectivelyVisible) return;
        Visual? host = _scroll ?? (Visual?)TopLevel.GetTopLevel(this);
        if (host == null) return;
        var height = host.Bounds.Height;
        for (var i = 0; i < ws.Rows.Count; i++)
        {
            var row = ws.Rows[i];
            if (row.Key == null || ws.Meta.ContainsKey(row.Key)) continue;
            if (RowsList.ContainerFromIndex(i) is not { } c) continue;
            if (c.TranslatePoint(new Point(0, 0), host) is not { } p) continue;
            if (p.Y + c.Bounds.Height < 0) continue;
            if (p.Y > height) break;
            ws.WantMeta(row.Key);
        }
    }

    // MARK: - 줄 누르기 · 우클릭

    private static RedisKeyRowItem? RowFrom(RoutedEventArgs e) =>
        (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<RedisKeyRowItem>().FirstOrDefault();

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (Ws is not { } ws || RowFrom(e) is not { } row) return;
        if (row.IsFolder) ws.Toggle(row);
        else if (row.Key is { } key) ws.Fire(() => ws.OpenAsync(key));
    }

    private void OnRowContext(object? sender, ContextRequestedEventArgs e)
    {
        if (Ws is not { } ws || RowFrom(e) is not { } row || e.Source is not Control target) return;
        e.Handled = true;
        var items = new List<Control>();
        if (row.Folder is { } node)
        {
            var pattern = RedisKeyTree.Pattern(node.Path, ws.Delimiter);
            items.Add(Item(row.Expanded ? Tr("접기") : Tr("펼치기"), () => ws.Toggle(row)));
            items.Add(Item(Tr("이 폴더만 검색 (%@)", pattern), () => ws.Fire(() => ws.SearchFolderAsync(node.Path))));
            items.Add(Item(Tr("접두어 복사"), () => Copy(ws, node.Path + ws.Delimiter)));
        }
        else if (row.Key is { } key)
        {
            items.Add(Item(Tr("열기"), () => ws.Fire(() => ws.OpenAsync(key))));
            items.Add(Item(Tr("이름 복사"), () => Copy(ws, key)));
            items.Add(new Separator());
            var del = Item(Tr("삭제"), () => ws.Fire(() => ws.DeleteKeyAsync(key)));
            del.IsEnabled = !ws.ReadOnly;
            items.Add(del);
        }
        new ContextMenu { ItemsSource = items }.Open(target);
    }

    private static MenuItem Item(string header, Action action)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => action();
        return m;
    }

    private static void Copy(RedisWorkspace ws, string text) => ws.Fire(() => ws.App?.Dialogs.SetClipboardTextAsync(text) ?? Task.CompletedTask);

    /// 구분자 고르기 · 모두 펼치기/접기
    private void ShowDelimiterMenu()
    {
        if (Ws is not { } ws) return;
        var items = new List<Control>();
        foreach (var d in RedisWorkspace.Delimiters)
        {
            var m = Item($"  {d}  ", () => ws.Delimiter = d);
            m.ToggleType = MenuItemToggleType.Radio;
            m.IsChecked = ws.Delimiter == d;
            items.Add(m);
        }
        items.Add(new Separator());
        items.Add(Item(Tr("모두 펼치기"), ws.ExpandAll));
        items.Add(Item(Tr("모두 접기"), ws.CollapseAll));
        new ContextMenu { ItemsSource = items }.Open(DelimiterButton);
    }
}
