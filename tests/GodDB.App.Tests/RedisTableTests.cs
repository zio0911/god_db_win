using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core.Redis;

namespace GodDB.App.Tests;

/// Redis 표 (Mac SwiftUI Table 대응): 칸 너비 · 정렬 없음 · 선택 · 새로고침에도 선택/행 유지
public class RedisTableTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static (RedisWorkspace Ws, RedisKeyView View, Window W) KeyView()
    {
        var ws = RedisWorkspaceTests.Offline(new TestEnv().NewApp());
        var view = new RedisKeyView(ws);
        var w = new Window { Width = 1000, Height = 700, Content = view };
        w.Show();
        ws.SelectedKey = "h";
        ws.KeyInfo = new RedisKeyInfo("h", "hash", -1, 3, null, null);
        return (ws, view, w);
    }

    private static void AssertMacTable(RedisTable t)
    {
        Assert.False(t.Grid.CanUserSortColumns);
        Assert.True(t.Grid.CanUserResizeColumns);
        Assert.All(t.Grid.Columns, c => Assert.False(c.CanUserSort));
    }

    private static void AssertWidth(DataGridColumn c, double min, double ideal, double max)
    {
        Assert.Equal(min, c.MinWidth);
        Assert.Equal(max, c.MaxWidth);
        Assert.Equal(ideal, c.Width.Value);
        Assert.Equal(DataGridLengthUnitType.Pixel, c.Width.UnitType);
        Assert.Equal(!min.Equals(max), c.CanUserResize);
    }

    [Fact]
    public Task KeyTables_HaveMacColumnWidths_AndNoSorting() => UiThread.Run(() =>
    {
        var (ws, view, w) = KeyView();
        ws.Value = new RedisValue.Hash([new("a", "1"), new("b", "2")]);
        Pump();
        var t = view.KvTableView!;
        AssertMacTable(t);
        AssertWidth(t.Grid.Columns[0], 120, 220, 600);
        Assert.Equal(DataGridLengthUnitType.Star, t.Grid.Columns[1].Width.UnitType);   // 값 칸은 남은 자리
        Assert.True(t.Grid.Columns[1].CanUserResize);
        Assert.Equal(3, t.Columns[1].MaxLines);
        Assert.True(t.Columns[0].Selectable && t.Columns[1].Selectable);
        Assert.Equal(DataGridSelectionMode.Extended, t.Grid.SelectionMode);

        // list / set: 앞 칸 70 … 140
        ws.KeyInfo = new RedisKeyInfo("h", "list", -1, 2, null, null);
        ws.Value = new RedisValue.List(["x", "y"], 2);
        Pump();
        AssertWidth(view.KvTableView!.Grid.Columns[0], 70, 70, 140);
        ws.Value = new RedisValue.Set(["x"]);
        Pump();
        AssertWidth(view.KvTableView!.Grid.Columns[0], 70, 70, 140);
        w.Close();
    });

    [Fact]
    public Task StreamTables_HaveMacColumnWidths() => UiThread.Run(() =>
    {
        var ws = RedisWorkspaceTests.Offline(new TestEnv().NewApp());
        var streams = new RedisStreamsView(ws);
        var pubsub = new RedisPubSubView(ws);
        var w = new Window { Width = 1100, Height = 700, Content = new StackPanel { Children = { streams, pubsub } } };
        w.Show();
        Pump();

        var e = streams.Entries.Table;
        AssertMacTable(e);
        AssertWidth(e.Grid.Columns[0], 150, 190, 260);
        AssertWidth(e.Grid.Columns[1], 120, 150, 200);
        Assert.Equal(2, e.Columns[2].MaxLines);
        Assert.True(e.Columns[0].Mono);

        var g = streams.GroupTable;
        AssertMacTable(g);
        Assert.Equal(DataGridSelectionMode.Single, g.Grid.SelectionMode);
        Assert.Equal(DataGridLengthUnitType.Star, g.Grid.Columns[0].Width.UnitType);
        for (var i = 1; i <= 3; i++) AssertWidth(g.Grid.Columns[i], 50, 50, 50);   // .width(50) 고정
        Assert.True(g.Columns[4].Mono && g.Columns[4].Caption);
        var pendingOrange = new RedisStreamGroup("g1", 1, 2, "0-1", 0);
        Assert.Equal(Avalonia.Media.Color.Parse("#FF9500"), ((Avalonia.Media.ISolidColorBrush)g.Columns[2].Brush!(pendingOrange)!).Color);
        Assert.True(g.Columns[2].Secondary!(pendingOrange with { Pending = 0 }));

        var c = streams.ConsumerTable;
        AssertWidth(c.Grid.Columns[1], 50, 50, 50);
        AssertWidth(c.Grid.Columns[2], 80, 80, 80);

        var p = streams.PendingTable;
        Assert.Equal(DataGridSelectionMode.Extended, p.Grid.SelectionMode);
        AssertWidth(p.Grid.Columns[2], 80, 80, 80);
        AssertWidth(p.Grid.Columns[3], 60, 60, 60);
        Assert.Equal(Avalonia.Media.Color.Parse("#FF3B30"), ((Avalonia.Media.ISolidColorBrush)p.Columns[3].Brush!(new RedisPendingEntry("1-0", "c", 0, 4))!).Color);
        Assert.Null(p.Columns[3].Brush!(new RedisPendingEntry("1-0", "c", 0, 3)));

        var m = pubsub.MessageTable;
        AssertMacTable(m);
        AssertWidth(m.Grid.Columns[0], 90, 100, 130);
        AssertWidth(m.Grid.Columns[1], 100, 160, 300);
        Assert.Equal(3, m.Columns[2].MaxLines);
        w.Close();
    });

    [Fact]
    public Task PubSubMessages_AreNotSelectable_AndKeepRowsOnLiveUpdate() => UiThread.Run(() =>
    {
        var ws = RedisWorkspaceTests.Offline(new TestEnv().NewApp());
        var pubsub = new RedisPubSubView(ws);
        var w = new Window { Width = 1000, Height = 600, Content = pubsub };
        w.Show();
        ws.Messages.Add(new RedisPubSubMessage(DateTime.Now, "message", "a", null, "1"));
        ws.Messages.Add(new RedisPubSubMessage(DateTime.Now, "message", "a", null, "2"));
        pubsub.RefreshMessages();
        Pump();
        var t = pubsub.MessageTable;
        var oldest = t.Rows[1];
        t.Grid.SelectedItem = t.Rows[0];   // Mac Table(shown) 는 선택이 없다
        Pump();
        Assert.Null(t.Grid.SelectedItem);
        ws.Messages.Add(new RedisPubSubMessage(DateTime.Now, "message", "a", null, "3"));
        pubsub.RefreshMessages();
        Assert.Equal(3, t.Count);
        Assert.Equal("3", ((RedisPubSubMessage)t.Rows[0]).Payload);
        Assert.Same(oldest, t.Rows[2]);   // 기존 행은 그대로 (위에 끼워 넣음)
        w.Close();
    });

    [Fact]
    public Task AutoRefresh_KeepsSelection_Table_AndInputs() => UiThread.Run(() =>
    {
        var (ws, view, w) = KeyView();
        ws.Value = new RedisValue.Hash([new("a", "1"), new("b", "2"), new("c", "3")]);
        Pump();
        var t = view.KvTableView!;
        t.Select([t.Rows[1], t.Rows[2]]);
        Pump();
        var unchanged = t.Rows[0];

        // 자동 새로고침: 같은 키 · 같은 타입 — 값 하나만 바뀜
        ws.Value = new RedisValue.Hash([new("a", "1"), new("b", "20"), new("c", "3")]);
        Pump();
        Assert.Same(t, view.KvTableView);                 // 표를 다시 만들지 않는다 (스크롤 유지)
        Assert.Same(unchanged, t.Rows[0]);                // 안 바뀐 행은 그대로
        Assert.Equal("20", ((RedisKeyView.KV)t.Rows[1]).B);
        Assert.Equal([1, 2], t.SelectedAs<RedisKeyView.KV>().Select(r => r.Index));   // Mac: 행 번호로 선택 유지

        // 행이 줄면 남은 번호만
        ws.Value = new RedisValue.Hash([new("a", "1"), new("b", "20")]);
        Pump();
        Assert.Same(t, view.KvTableView);
        Assert.Equal([1], t.SelectedAs<RedisKeyView.KV>().Select(r => r.Index));

        // 다른 키로 바꾸면 처음부터 (Mac resetInputs: selection = [])
        ws.SelectedKey = "h2";
        ws.Value = new RedisValue.Hash([new("a", "1"), new("b", "2")]);
        Pump();
        Assert.NotSame(t, view.KvTableView);
        Assert.Empty(view.KvTableView!.Selected);
        w.Close();
    });

    [Fact]
    public Task AutoRefresh_DoesNotOverwriteTypedInput() => UiThread.Run(() =>
    {
        var (ws, view, w) = KeyView();
        ws.Value = new RedisValue.Hash([new("a", "1"), new("b", "2")]);
        Pump();
        var t = view.KvTableView!;
        t.Select([t.Rows[0]]);
        Pump();
        var boxes = view.GetVisualDescendants().OfType<TextBox>().ToList();
        var field = boxes.First(b => b.PlaceholderText == "필드");
        Assert.Equal("a", field.Text);   // 하나 고르면 입력 칸에 채운다 (Mac onChange(of: selection))
        field.Text = "typed";
        ws.Value = new RedisValue.Hash([new("a", "9"), new("b", "2")]);
        Pump();
        Assert.Same(field, view.GetVisualDescendants().OfType<TextBox>().First(b => b.PlaceholderText == "필드"));
        Assert.Equal("typed", field.Text);
        w.Close();
    });

    [Fact]
    public Task StreamEntries_KeepSelectionById() => UiThread.Run(() =>
    {
        var ws = RedisWorkspaceTests.Offline(new TestEnv().NewApp());
        var table = new StreamEntriesTable(ws, null);
        var w = new Window { Width = 900, Height = 500, Content = table };
        w.Show();
        table.SetEntries([new("2-0", [new("a", "1")]), new("1-0", [new("a", "0")])]);
        Pump();
        table.Table.Select([table.Table.Rows[1]]);
        // 다시 읽기: 새 레코드, 위에 하나 더
        table.SetEntries([new("3-0", [new("a", "2")]), new("2-0", [new("a", "1")]), new("1-0", [new("a", "0")])]);
        Pump();
        Assert.Equal(["1-0"], table.Selected.Select(e => e.Id));
        w.Close();
    });

    [Fact]
    public Task ClickOnSelectableCell_SelectsRow() => UiThread.Run(() =>
    {
        var (ws, view, w) = KeyView();
        ws.Value = new RedisValue.Hash([new("a", "1"), new("b", "2")]);
        Pump();
        w.UpdateLayout();
        Pump();
        var t = view.KvTableView!;
        var cell = t.GetVisualDescendants().OfType<RedisTable.CellText>().First(c => c.Text == "b");
        var pt = cell.TranslatePoint(new Point(2, cell.Bounds.Height / 2), w)!.Value;
        w.MouseDown(pt, MouseButton.Left);
        w.MouseUp(pt, MouseButton.Left);
        Pump();
        Assert.Equal([1], t.SelectedAs<RedisKeyView.KV>().Select(r => r.Index));
        w.Close();
    });
}
