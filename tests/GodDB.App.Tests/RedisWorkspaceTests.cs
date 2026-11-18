using Avalonia.Controls;
using Avalonia.Threading;
using GodDB.App.Services;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Audit;
using GodDB.Core.Platform;
using GodDB.Core.Redis;

namespace GodDB.App.Tests;

/// 확인창에 정해 둔 답을 하고 물어본 것을 기록한다
public sealed class AnswerDialogs(bool answer) : NullDialogs
{
    public bool Answer { get; set; } = answer;
    public List<string> Asked { get; } = [];
    public override Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel, bool destructive = false)
    {
        Asked.Add(title + "\n" + message);
        return Task.FromResult(Answer);
    }
}

/// Redis 화면 상태 · 화면 (연결 없이)
public class RedisWorkspaceTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    internal static RedisWorkspace Offline(AppState? app = null, bool readOnly = false) =>
        new(Guid.NewGuid(), new RedisSession(new ConnectionProfile { Kind = DatabaseKind.Redis, Name = "r-off", Host = "127.0.0.1", Port = 1, ReadOnly = readOnly }), app);

    [Fact]
    public void Hint_ForCursorLine()
    {
        var text = "// 주석\nHSET user:1 a 1\nzr";
        Assert.Null(RedisWorkspace.HintFor(text, 3));
        Assert.Equal("HSET key field value [field value …]", RedisWorkspace.HintFor(text, 12));
        Assert.StartsWith("ZRANGE", RedisWorkspace.HintFor(text, text.Length));
        Assert.Null(RedisWorkspace.HintFor("", 0));
        Assert.Null(RedisWorkspace.HintFor("qqqq", 2));
    }

    [Fact]
    public void TextToRun_SelectionOrAll()
    {
        Assert.Equal("a\nb", RedisWorkspace.TextToRun("a\nb", 1, 1));
        Assert.Equal("b", RedisWorkspace.TextToRun("a\nb", 3, 2));
    }

    [Fact]
    public void PrettyJson_SortsKeys_AndRejectsNonJson()
    {
        var s = RedisWorkspace.PrettyJson("{\"b\":1,\"a\":{\"z\":[1,\"한글\"],\"y\":null}}");
        Assert.NotNull(s);
        Assert.True(s!.IndexOf("\"a\"", StringComparison.Ordinal) < s.IndexOf("\"b\"", StringComparison.Ordinal));
        Assert.True(s.IndexOf("\"y\"", StringComparison.Ordinal) < s.IndexOf("\"z\"", StringComparison.Ordinal));
        Assert.Contains("한글", s);
        Assert.Contains("\n", s);
        Assert.Null(RedisWorkspace.PrettyJson("hello"));
        Assert.Null(RedisWorkspace.PrettyJson("{broken"));
    }

    [Fact]
    public void ParsePairs_AndStreamTime()
    {
        Assert.Equal([new RedisPair("a", "1"), new RedisPair("b", "hello world")], RedisWorkspace.ParsePairs("a=1 \"b=hello world\""));
        Assert.Null(RedisWorkspace.ParsePairs("a"));
        Assert.Null(RedisWorkspace.ParsePairs("=1"));
        Assert.Null(RedisWorkspace.ParsePairs(""));
        Assert.Matches(@"^\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}$", RedisWorkspace.StreamTime("1700000000000-0"));
        Assert.Equal("", RedisWorkspace.StreamTime("x-0"));
        Assert.Equal("St", RedisWorkspace.TypeLetter("set"));
        Assert.Equal("X", RedisWorkspace.TypeLetter("stream"));
    }

    [Fact]
    public Task TreeAndList_Rows_Prefs() => UiThread.Run(() =>
    {
        Prefs.Set(RedisWorkspace.TreeModeKey, true);
        Prefs.Set(RedisWorkspace.DelimiterKey, ":");
        var ws = Offline();
        ws.Keys = ["user:1001:name", "user:1001:mail", "user:2", "session:abc", "plain"];
        // 트리: 폴더 먼저 (session, user), 그다음 키
        Assert.Equal(["session", "user", "plain"], ws.Rows.Select(r => r.Label));
        var user = ws.Rows.Single(r => r.Label == "user");
        Assert.True(user.IsFolder);
        Assert.Equal(3, user.Count);
        ws.Toggle(user);
        Assert.Equal(["session", "user", "1001", "2", "plain"], ws.Rows.Select(r => r.Label));
        ws.ExpandAll();
        Assert.Equal(8, ws.Rows.Count);
        ws.CollapseAll();
        Assert.Equal(3, ws.Rows.Count);
        // 목록
        ws.TreeMode = false;
        Assert.Equal(5, ws.Rows.Count);
        Assert.False(Prefs.Get(RedisWorkspace.TreeModeKey, true));
        ws.TreeMode = true;
        ws.Delimiter = "/";
        Assert.Equal("/", Prefs.GetString(RedisWorkspace.DelimiterKey));
        Assert.Equal(5, ws.Rows.Count);   // "/" 가 없으니 모두 키
        ws.Delimiter = ":";
        Assert.Equal("5", ws.KeyCountText);
        Assert.True(ws.ShowAllCount);
        ws.NextCursor = "17";
        Assert.Equal("5+", ws.KeyCountText);
        Assert.True(ws.HasMoreKeys);

        // 메타가 있으면 타입 배지 · TTL
        ws.SelectedKey = "plain";
        ws.Meta["plain"] = new RedisKeyMetaInfo("hash", 30_000, DateTime.Now);
        ws.RebuildRows();
        var plain = ws.Rows.Single(r => r.Key == "plain");
        Assert.True(plain.Selected);
        Assert.Equal("H", plain.TypeLetter);
        Assert.True(plain.TtlSoon);
        Assert.StartsWith("⏱ ", plain.TtlText);
        Assert.InRange(ws.RemainingTtl("plain")!.Value, 25_000L, 30_000L);
    });

    [Fact]
    public Task AutoRefreshSeconds_ClampsAndPersists_Workbench_PerConnection() => UiThread.Run(() =>
    {
        var ws = Offline();
        ws.SetAutoRefreshSeconds(0);
        Assert.Equal(1, ws.AutoRefreshSeconds);
        ws.SetAutoRefreshSeconds(99999);
        Assert.Equal(3600, ws.AutoRefreshSeconds);
        Assert.Equal(3600, Prefs.Get(RedisWorkspace.AutoRefreshSecondsKey, 0));
        ws.SetAutoRefreshSeconds(5);
        Assert.Equal(RedisWorkspace.DefaultWorkbench, ws.Workbench);
        ws.Workbench = "PING";
        ws.InsertIntoWorkbench("GET a");
        Assert.Equal("PING\nGET a\n", ws.Workbench);
        var again = new RedisWorkspace(ws.ProfileId, ws.Session, null);
        Assert.Equal("PING\nGET a\n", again.Workbench);
        Assert.Equal(RedisWorkspace.DefaultWorkbench, Offline().Workbench);
    });

    [Fact]
    public Task Dangerous_Script_AsksAndStopsWhenDeclined() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var dialogs = new AnswerDialogs(false);
        app.Dialogs = dialogs;
        var ws = Offline(app);
        await ws.RunScriptAsync("// 주석\nPING\nFLUSHDB");
        Assert.Single(dialogs.Asked);
        Assert.Contains("FLUSHDB", dialogs.Asked[0]);
        Assert.Empty(ws.Console);
        Assert.Null(ws.PendingScript);
        // 따옴표가 안 닫히면 바로 오류 줄
        await ws.RunConsoleAsync("SET \"a");
        Assert.True(Assert.Single(ws.Console).IsError);
    });

    [Fact]
    public Task Views_ConstructAndLayOut_AllTabs_AllValueTypes() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var ws = Offline(app);
        ws.TreeMode = true;
        ws.Delimiter = ":";
        ws.Keys = ["user:1", "user:2", "queue"];
        var view = new RedisWorkspaceView { DataContext = ws };
        var side = new RedisSidebarSection { DataContext = ws };
        var search = new RedisKeySearchBar { DataContext = ws };
        var w = new Window { Width = 1100, Height = 700, Content = new DockPanel { Children = { new StackPanel { Width = 280, Children = { side, search } }, view } } };
        DockPanel.SetDock((Control)((DockPanel)w.Content).Children[0], Dock.Left);
        w.Show();
        Pump();
        Assert.True(view.Bounds.Width > 0);
        Assert.IsType<RedisKeyView>(view.CurrentView);
        Assert.Equal(2, side.FindControl<ItemsControl>("RowsList")!.ItemCount);

        // 키 값 타입마다 그려 본다
        ws.SelectedKey = "user:1";
        ws.KeyInfo = new RedisKeyInfo("user:1", "string", 45_000, 13, 72, "embstr");
        RedisValue[] values =
        [
            new RedisValue.Str("{\"a\":1}"),
            new RedisValue.Hash([new("f", "v"), new("g", "w")]),
            new RedisValue.List(["a", "b"], 900),
            new RedisValue.Set(["x"]),
            new RedisValue.Zset([new("kim", "10")], 1),
            new RedisValue.Stream([new RedisStreamEntry("1700000000000-0", [new("k", "v")])]),
            new RedisValue.Other("ReJSON-RL"),
        ];
        foreach (var v in values)
        {
            ws.Value = v;
            Pump();
            Assert.True(view.CurrentView!.Bounds.Height > 0);
        }
        ws.Value = new RedisValue.Str("abc");
        Pump();
        var kv = (RedisKeyView)view.CurrentView!;
        Assert.NotNull(kv.DraftBox);
        Assert.False(kv.SaveButton!.IsEnabled);
        kv.DraftBox!.Text = "abcd";
        Pump();
        Assert.True(kv.SaveButton.IsEnabled);
        // 같은 값을 다시 읽어도(자동 새로고침) 편집 중인 글 · 칸은 그대로
        var box = kv.DraftBox;
        ws.Value = new RedisValue.Str("abc");
        Pump();
        Assert.Same(box, kv.DraftBox);
        Assert.Equal("abcd", kv.DraftBox!.Text);
        ws.ValueTruncatedTotal = 5_000_000;
        Pump();
        Assert.False(((RedisKeyView)view.CurrentView!).SaveButton!.IsEnabled);
        ws.ValueError = "키가 없어요.";
        Pump();

        foreach (var tab in Enum.GetValues<RedisTab>())
        {
            ws.Tab = tab;
            Pump();
            Assert.NotNull(view.CurrentView);
            Assert.True(view.CurrentView!.Bounds.Width > 0, tab.ToString());
        }
        // Workbench: 실행 버튼 · 안내
        ws.Tab = RedisTab.Console;
        Pump();
        var console = (RedisConsoleView)view.CurrentView!;
        console.Editor.Text = "HGET k";
        console.Editor.CaretIndex = 3;
        Pump();
        Assert.Equal("HGET k", ws.Workbench);
        Assert.True(console.RunButton.IsEnabled);
        console.Editor.Text = "// 주석만";
        Pump();
        Assert.False(console.RunButton.IsEnabled);
        ws.InsertIntoWorkbench("PING");
        Assert.Equal("// 주석만\nPING\n", console.Editor.Text);
        ws.Console.Add(new RedisConsoleEntry("PING", "PONG", false, 3, 0));
        Pump();

        // Pub/Sub 거르기
        ws.Tab = RedisTab.PubSub;
        Pump();
        var pubsub = (RedisPubSubView)view.CurrentView!;
        ws.Messages.Add(new RedisPubSubMessage(DateTime.Now, "message", "news", null, "배포 완료"));
        ws.Messages.Add(new RedisPubSubMessage(DateTime.Now, "pmessage", "evt:login", "evt:*", "{}"));
        pubsub.RefreshMessages();
        Assert.Equal(2, pubsub.Shown.Count);
        Assert.Equal("evt:login", pubsub.Shown[0].Channel);   // 최신이 위
        pubsub.FilterBox.Text = "배포";
        Pump();
        Assert.Single(pubsub.Shown);

        // 서버 정보 찾기
        ws.Info = [new RedisInfoSection("Server", [new("redis_version", "7.2.0"), new("os", "Linux")]), new RedisInfoSection("Clients", [new("connected_clients", "3")])];
        ws.Tab = RedisTab.Info;
        Pump();
        var info = (RedisInfoView)view.CurrentView!;
        Assert.Equal(3, info.VisibleRows);
        info.FilterBox.Text = "client";
        Pump();
        Assert.Equal(1, info.VisibleRows);

        // 새 키 창
        ws.ShowNewKey = true;
        Pump();
        var nk = Assert.IsType<RedisNewKeyWindow>(view.NewKeyWindow);
        Assert.False(nk.CreateButton.IsEnabled);
        nk.KeyBox.Text = "k";
        Pump();
        Assert.True(nk.CreateButton.IsEnabled);
        nk.SelectType("hash");
        Pump();
        Assert.False(nk.CreateButton.IsEnabled);   // hash 는 필드가 있어야
        nk.FieldBox.Text = "f";
        Pump();
        Assert.True(nk.CreateButton.IsEnabled);
        nk.Close();
        Pump();
        Assert.False(ws.ShowNewKey);
        w.Close();
    });

    [Fact]
    public Task ReadOnly_DisablesWriteControls() => UiThread.Run(() =>
    {
        var ws = Offline(readOnly: true);
        var side = new RedisSidebarSection { DataContext = ws };
        var w = new Window { Content = side };
        w.Show();
        Pump();
        Assert.False(side.FindControl<Button>("NewKeyButton")!.IsEnabled);
        Assert.False(ws.CanWrite);
        w.Close();
    });
}
