using Avalonia.Controls;
using Avalonia.Threading;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Audit;
using GodDB.Core.Redis;

namespace GodDB.App.Tests;

/// Docker 테스트 Redis (scripts/test-dbs.sh: 127.0.0.1:36379 비밀번호 goddbtest, Cluster 37000, Sentinel 36381 mymaster) 로
/// Redis 화면 상태를 확인한다. GODDB_REDIS_PORT 가 없으면 건너뛴다.
/// 다른 테스트가 같은 Redis 를 함께 쓰므로 키 이름에는 매번 다른 접두어를 붙이고 끝나면 지운다.
public class RedisWorkspaceIntegrationTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static string Password => Environment.GetEnvironmentVariable("GODDB_IT_PASSWORD") ?? "goddbtest";

    private static int Port(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(v)) Assert.Skip($"{name} 없음 — scripts/test-dbs.sh test 로 실행");
        return int.Parse(v!);
    }

    private static (AppState App, ConnectionProfile Profile, AnswerDialogs Dialogs) Setup(bool readOnly = false, RedisMode mode = RedisMode.Standalone,
                                                                                     string portVar = "GODDB_REDIS_PORT", string db = "7")
    {
        var port = Port(portVar);
        var app = new TestEnv().NewApp();
        var dialogs = new AnswerDialogs(true);
        app.Dialogs = dialogs;
        var p = new ConnectionProfile
        {
            Kind = DatabaseKind.Redis, Name = "it-redis-ui", Host = "127.0.0.1", Port = port, Username = "", Database = db,
            UseTLS = false, SavePassword = false, ReadOnly = readOnly, RedisMode = mode, SentinelMaster = "mymaster",
        };
        app.Profiles.Add(p);
        app.SessionPasswords[p.Id] = Password;
        return (app, p, dialogs);
    }

    private static async Task<RedisWorkspace> Connect(AppState app, ConnectionProfile p)
    {
        await app.ConnectAsync(p.Id);
        Assert.True(app.ConnectionState(p.Id).IsConnected, app.LastLog?.Message);
        return app.RedisWorkspaces[p.Id];
    }

    private static async Task Cleanup(RedisSession s, string prefix)
    {
        var keys = await s.ScanKeysAsync(prefix + "*", limit: 100_000);
        foreach (var k in keys.Keys) await s.CommandAsync(["DEL", k]);
    }

    private static async Task Until(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (!cond() && DateTime.Now < end)
        {
            await Task.Delay(30);
            Pump();
        }
        Assert.True(cond());
    }

    [Fact]
    public Task Keys_Tree_Meta_Values_Writes() => UiThread.Run(async () =>
    {
        var (app, p, dialogs) = Setup();
        var prefix = "itui" + Guid.NewGuid().ToString("N")[..8];
        var ws = await Connect(app, p);
        var s = ws.Session;
        try
        {
            Assert.IsType<RedisWorkspace>(app.SelectedRedis ?? ws);
            Assert.Equal(7, ws.Db);
            Assert.Contains(ws.Databases, d => d.Index == 7);
            await s.CommandAsync(["HSET", $"{prefix}:user:1001", "name", "hong", "grade", "VIP"]);
            await s.CommandAsync(["SET", $"{prefix}:session:abc", "{\"userId\":1001}", "EX", "3600"]);
            await s.CommandAsync(["RPUSH", $"{prefix}:queue", "m1", "m2", "m3"]);
            await s.CommandAsync(["SADD", $"{prefix}:tags", "redis", "swift"]);
            await s.CommandAsync(["ZADD", $"{prefix}:rank", "980", "kim", "1200", "lee"]);
            await s.CommandAsync(["XADD", $"{prefix}:orders", "*", "id", "1"]);

            // 패턴 검색 + 타입 거르기
            ws.Pattern = prefix + ":*";
            ws.TreeMode = true;
            ws.Delimiter = ":";
            await ws.ReloadKeysAsync();
            Assert.Equal(6, ws.Keys.Count);
            Assert.Null(ws.NextCursor);
            Assert.Equal(prefix, Assert.Single(ws.Rows).Label);   // 접두어 폴더 하나
            Assert.Contains(app.Logs, l => l.Message.Contains("키 6개"));
            ws.TypeFilter = "hash";
            await ws.ReloadKeysAsync();
            Assert.Equal([$"{prefix}:user:1001"], ws.Keys);
            ws.TypeFilter = "";
            // 이 폴더만 검색 → 펼쳐 둔다
            await ws.SearchFolderAsync($"{prefix}:user");
            Assert.Equal($"{prefix}:user:*", ws.Pattern);
            Assert.Equal([$"{prefix}:user:1001"], ws.Keys);
            Assert.Contains(ws.Rows, r => r.Key == $"{prefix}:user:1001");
            ws.Pattern = prefix + ":*";
            await ws.ReloadKeysAsync();

            // 보이는 키만 메타 조회
            ws.TreeMode = false;
            ws.WantMeta($"{prefix}:session:abc");
            ws.WantMeta($"{prefix}:queue");
            await ws.MetaTask;
            Assert.Equal(2, ws.Meta.Count);
            var session = ws.Rows.Single(r => r.Key == $"{prefix}:session:abc");
            Assert.Equal("S", session.TypeLetter);
            Assert.Matches("^⏱ (1h|59m)$", session.TtlText);
            Assert.Equal("L", ws.Rows.Single(r => r.Key == $"{prefix}:queue").TypeLetter);
            Assert.False(ws.Rows.Single(r => r.Key == $"{prefix}:tags").HasMeta);

            // 값 열기: 타입마다
            await ws.OpenAsync($"{prefix}:user:1001");
            var h = Assert.IsType<RedisValue.Hash>(ws.Value);
            Assert.Equal(["grade", "name"], h.Items.Select(x => x.Key));
            Assert.Equal("hash", ws.KeyInfo!.Type);
            Assert.Equal(2, ws.KeyInfo.Length);
            Assert.Null(await ws.ApplyAsync(["HSET", $"{prefix}:user:1001", "point", "10"], "HSET"));
            Assert.Equal(3, Assert.IsType<RedisValue.Hash>(ws.Value).Items.Count);
            Assert.Null(await ws.ApplyAsync(["HDEL", $"{prefix}:user:1001", "point", "grade"], "HDEL"));
            Assert.Single(Assert.IsType<RedisValue.Hash>(ws.Value).Items);
            Assert.Contains(app.Logs, l => l.Audit?.Action == AuditAction.Delete && l.Audit.Statement.StartsWith("HDEL", StringComparison.Ordinal));

            await ws.OpenAsync($"{prefix}:session:abc");
            Assert.Equal("{\"userId\":1001}", Assert.IsType<RedisValue.Str>(ws.Value).Value);
            Assert.NotNull(RedisWorkspace.PrettyJson("{\"userId\":1001}"));
            // SET … KEEPTTL 은 만료를 유지
            Assert.Null(await ws.ApplyAsync(["SET", $"{prefix}:session:abc", "x", "KEEPTTL"], "SET"));
            Assert.True(ws.KeyInfo!.TtlMillis > 3_000_000);
            Assert.Equal("x", Assert.IsType<RedisValue.Str>(ws.Value).Value);

            await ws.OpenAsync($"{prefix}:queue");
            Assert.Equal(["m1", "m2", "m3"], Assert.IsType<RedisValue.List>(ws.Value).Items);
            await ws.ApplyAsync(["LPUSH", $"{prefix}:queue", "m0"], "LPUSH");
            await ws.ApplyAsync(["LSET", $"{prefix}:queue", "1", "M1"], "LSET");
            await ws.ApplyAsync(["LREM", $"{prefix}:queue", "1", "m3"], "LREM");
            Assert.Equal(["m0", "M1", "m2"], Assert.IsType<RedisValue.List>(ws.Value).Items);
            // 500개씩 더 보기
            await s.CommandAsync(["RPUSH", $"{prefix}:queue", .. Enumerable.Range(0, 600).Select(i => $"x{i}")]);
            ws.ListPageSize = 500;
            await ws.ReloadValueAsync();
            var l = Assert.IsType<RedisValue.List>(ws.Value);
            Assert.Equal(500, l.Items.Count);
            Assert.Equal(603, l.Total);
            await ws.LoadMoreListAsync();
            Assert.Equal(603, Assert.IsType<RedisValue.List>(ws.Value).Items.Count);

            await ws.OpenAsync($"{prefix}:tags");
            await ws.ApplyAsync(["SADD", $"{prefix}:tags", "avalonia"], "SADD");
            await ws.ApplyAsync(["SREM", $"{prefix}:tags", "swift"], "SREM");
            Assert.Equal(["avalonia", "redis"], Assert.IsType<RedisValue.Set>(ws.Value).Items);

            await ws.OpenAsync($"{prefix}:rank");
            Assert.Equal(["lee", "kim"], Assert.IsType<RedisValue.Zset>(ws.Value).Items.Select(x => x.Key));   // 점수 높은 순
            await ws.ApplyAsync(["ZADD", $"{prefix}:rank", "2000", "park"], "ZADD");
            await ws.ApplyAsync(["ZREM", $"{prefix}:rank", "kim"], "ZREM");
            Assert.Equal(["park", "lee"], Assert.IsType<RedisValue.Zset>(ws.Value).Items.Select(x => x.Key));

            await ws.OpenAsync($"{prefix}:orders");
            Assert.Single(Assert.IsType<RedisValue.Stream>(ws.Value).Entries);

            // TTL · 이름 변경 · 삭제
            Assert.Null(await ws.SetTtlAsync($"{prefix}:orders", 100));
            Assert.InRange(ws.KeyInfo!.TtlMillis, 1, 100_000);
            Assert.Null(await ws.SetTtlAsync($"{prefix}:orders", null));
            Assert.Equal(-1, ws.KeyInfo!.TtlMillis);
            Assert.Null(await ws.RenameKeyAsync($"{prefix}:orders", $"{prefix}:orders2"));
            Assert.Equal($"{prefix}:orders2", ws.SelectedKey);
            Assert.Contains($"{prefix}:orders2", ws.Keys);
            Assert.DoesNotContain($"{prefix}:orders", ws.Keys);
            await ws.DeleteKeyAsync($"{prefix}:orders2");
            Assert.Null(ws.SelectedKey);
            Assert.DoesNotContain($"{prefix}:orders2", ws.Keys);
            Assert.Equal(0, (await s.CommandAsync(["EXISTS", $"{prefix}:orders2"])).AsInt);

            // 새 키 (만료 지정)
            Assert.Null(await ws.CreateAsync("zset", $"{prefix}:new", "5", "m", 60));
            Assert.Equal($"{prefix}:new", ws.SelectedKey);
            Assert.InRange(ws.KeyInfo!.TtlMillis, 1, 60_000);
            Assert.Contains($"{prefix}:new", ws.Keys);
            // 없는 키
            ws.SelectedKey = $"{prefix}:nope";
            await ws.ReloadValueAsync();
            Assert.Equal("키가 없어요. (지워졌거나 만료됨)", ws.ValueError);

            // 새로고침: 다른 곳에서 만든 키가 보이고 선택은 유지
            await ws.OpenAsync($"{prefix}:rank");
            await s.CommandAsync(["SET", $"{prefix}:later", "1"]);
            await ws.RefreshAllAsync();
            Assert.Contains($"{prefix}:later", ws.Keys);
            Assert.Equal($"{prefix}:rank", ws.SelectedKey);

            // 화면: 사이드바 · 키 보기가 실제 데이터로 그려진다 (보이는 줄 메타 조회)
            ws.TreeMode = false;
            var side = new RedisSidebarSection { DataContext = ws };
            var view = new RedisWorkspaceView { DataContext = ws };
            var w = new Window { Width = 1100, Height = 700, Content = new Grid { ColumnDefinitions = new("280,*"), Children = { side, view } } };
            Grid.SetColumn(view, 1);
            w.Show();
            Pump();
            ws.Meta.Clear();
            side.RequestVisibleMeta();
            await ws.MetaTask;
            Assert.Contains(ws.Rows, r => r.HasMeta);
            Assert.IsType<RedisKeyView>(view.CurrentView);
            w.Close();
        }
        finally
        {
            await Cleanup(s, prefix);
            await app.DisconnectRedisAsync(p.Id);
        }
    });

    [Fact]
    public Task Workbench_Console_SelectDb_Dangerous() => UiThread.Run(async () =>
    {
        var (app, p, dialogs) = Setup();
        var prefix = "itui" + Guid.NewGuid().ToString("N")[..8];
        var ws = await Connect(app, p);
        try
        {
            await ws.RunScriptAsync($"// 주석은 건너뜀\n# 이것도\nSET {prefix}:a \"hello world\"\nGET {prefix}:a\nHGETALL {prefix}:none\nNOPE");
            Assert.Equal(4, ws.Console.Count);
            Assert.Equal("OK", ws.Console[0].Output);
            Assert.Equal("\"hello world\"", ws.Console[1].Output);
            Assert.Equal("(empty array)", ws.Console[2].Output);
            Assert.True(ws.Console[3].IsError);
            Assert.Equal(7, ws.Console[0].Db);
            Assert.Contains($"GET {prefix}:a", ws.History);
            Assert.Contains(app.Logs, l => l.Audit?.Result.StartsWith("Workbench", StringComparison.Ordinal) == true);

            // 위험한 명령: 아니오 → 실행 안 함, 예 → 실행
            dialogs.Answer = false;
            await ws.RunScriptAsync($"KEYS {prefix}:*");
            Assert.Equal(4, ws.Console.Count);
            dialogs.Answer = true;
            await ws.RunScriptAsync($"KEYS {prefix}:*");
            Assert.Equal(5, ws.Console.Count);
            Assert.Contains($"{prefix}:a", ws.Console[^1].Output);
            Assert.Equal(2, dialogs.Asked.Count);

            // 열린 키를 바꾸면 값 다시 읽기
            await ws.OpenAsync($"{prefix}:a");
            await ws.RunConsoleAsync($"APPEND {prefix}:a !");
            Assert.Equal("hello world!", Assert.IsType<RedisValue.Str>(ws.Value).Value);

            // SELECT 하면 DB 표시도 바뀐다
            await ws.RunConsoleAsync("SELECT 8");
            Assert.Equal(8, ws.Db);
            Assert.Equal("it-redis-ui · db8", ws.HeaderText);
            await ws.SelectDbAsync(7);
            Assert.Equal(7, ws.Db);
            Assert.Null(ws.SelectedKey);
            Assert.Equal(7, ws.Session.Db);
            // DB 선택 칸
            ws.SelectedDatabase = ws.Databases.First(d => d.Index == 9);
            await Until(() => ws.Db == 9);
            ws.SelectedDatabase = ws.Databases.First(d => d.Index == 7);
            await Until(() => ws.Db == 7);

            // 화면에서 Ctrl+Enter 실행
            var view = new RedisConsoleView(ws);
            var w = new Window { Width = 900, Height = 600, Content = view };
            w.Show();
            Pump();
            view.Editor.Text = $"PING\nGET {prefix}:a";
            Pump();
            view.Editor.SelectionStart = 0;
            view.Editor.SelectionEnd = 4;
            Pump();
            Assert.Equal("PING", view.TextToRun);
            var before = ws.Console.Count;
            view.Run();
            await Until(() => ws.Console.Count == before + 1);
            Assert.Equal("PONG", ws.Console[^1].Output);
            w.Close();
        }
        finally
        {
            await Cleanup(ws.Session, prefix);
            await app.DisconnectRedisAsync(p.Id);
        }
    });

    [Fact]
    public Task PubSub_SubscribePublishReceive() => UiThread.Run(async () =>
    {
        var (app, p, _) = Setup();
        var ch = "itui" + Guid.NewGuid().ToString("N")[..8];
        var ws = await Connect(app, p);
        try
        {
            await ws.SubscribeAsync($"{ch}:news, {ch}:evt:*");
            Assert.Equal([$"{ch}:news"], ws.Channels);
            Assert.Equal([$"{ch}:evt:*"], ws.Patterns);
            Assert.NotNull(ws.Subscriber);
            await Until(() => ws.Messages.Count(m => m.Kind.EndsWith("subscribe", StringComparison.Ordinal)) >= 2);
            Assert.Null(await ws.PublishAsync($"{ch}:news", "배포 완료"));
            Assert.Null(await ws.PublishAsync($"{ch}:evt:login", "{}"));
            await Until(() => ws.Messages.Count(m => m.Kind.EndsWith("message", StringComparison.Ordinal)) == 2);
            var pm = ws.Messages.Single(m => m.Kind == "pmessage");
            Assert.Equal($"{ch}:evt:*", pm.Pattern);
            await ws.LoadActiveChannelsAsync();
            Assert.Contains(ws.ActiveChannels, c => c.Channel == $"{ch}:news" && c.Subscribers >= 1);
            // 일시 정지 중엔 메시지를 쌓지 않는다
            ws.PubsubPaused = true;
            await ws.PublishAsync($"{ch}:news", "skip");
            await Task.Delay(300);
            Pump();
            Assert.DoesNotContain(ws.Messages, m => m.Payload == "skip");
            ws.PubsubPaused = false;
            ws.Unsubscribe($"{ch}:news");
            Assert.Empty(ws.Channels);
            await ws.UnsubscribeAllAsync();
            Assert.Null(ws.Subscriber);
            Assert.Empty(ws.Patterns);
        }
        finally
        {
            await app.DisconnectRedisAsync(p.Id);
        }
    });

    [Fact]
    public Task Streams_Groups_Pending_Ack_Tail() => UiThread.Run(async () =>
    {
        var (app, p, _) = Setup();
        var prefix = "itui" + Guid.NewGuid().ToString("N")[..8];
        var ws = await Connect(app, p);
        var s = ws.Session;
        var key = $"{prefix}:orders";
        try
        {
            for (var i = 1; i <= 6; i++) await s.CommandAsync(["XADD", key, "*", "order_id", $"{5000 + i}", "status", i % 2 == 0 ? "paid" : "new"]);
            await s.CommandAsync(["XGROUP", "CREATE", key, "billing", "0"]);
            await s.CommandAsync(["XREADGROUP", "GROUP", "billing", "worker-1", "COUNT", "3", "STREAMS", key, ">"]);

            await ws.LoadStreamKeysAsync();
            Assert.Contains(key, ws.StreamKeys);
            await ws.OpenStreamAsync(key);
            Assert.Equal(RedisTab.Streams, ws.Tab);
            Assert.Equal(6, ws.StreamEntries.Count);
            Assert.Equal("5006", ws.StreamEntries[0].Fields[0].Value);   // 최신순
            Assert.Contains(ws.StreamInfo, x => x.Key == "length" && x.Value == "6");
            Assert.Equal("billing", ws.Group);
            Assert.Equal(3, Assert.Single(ws.Groups).Pending);
            Assert.Equal("worker-1", Assert.Single(ws.Consumers).Name);
            Assert.Equal(3, ws.Pending.Count);

            // XADD / XDEL / XACK / 그룹 만들기·SETID·DESTROY
            Assert.Null(await ws.StreamWriteAsync(["XADD", key, "*", "a", "1"], "XADD"));
            Assert.Equal(7, ws.StreamEntries.Count);
            Assert.Null(await ws.StreamWriteAsync(["XDEL", key, ws.StreamEntries[0].Id], "XDEL"));
            Assert.Equal(6, ws.StreamEntries.Count);
            Assert.Null(await ws.StreamWriteAsync(["XACK", key, "billing", ws.Pending[0].Id, ws.Pending[1].Id], "XACK"));
            Assert.Single(ws.Pending);
            Assert.Null(await ws.StreamWriteAsync(["XGROUP", "CREATE", key, "audit", "$"], "XGROUP CREATE"));
            Assert.Equal(2, ws.Groups.Count);
            Assert.Null(await ws.StreamWriteAsync(["XGROUP", "SETID", key, "audit", "0"], "XGROUP SETID"));
            Assert.Null(await ws.StreamWriteAsync(["XGROUP", "DESTROY", key, "audit"], "XGROUP DESTROY"));
            Assert.Single(ws.Groups);

            // 실시간 보기 (별도 연결)
            ws.ToggleTail();
            Assert.True(ws.Tailing);
            await Task.Delay(400);
            await s.CommandAsync(["XADD", key, "*", "live", "yes"]);
            await Until(() => ws.StreamEntries.Count == 7 && ws.StreamEntries[0].Fields[0].Key == "live");
            ws.ToggleTail();
            Assert.False(ws.Tailing);

            // 이전 항목 더 보기 (200개씩)
            for (var i = 0; i < 250; i++) await s.CommandAsync(["XADD", key, "*", "n", $"{i}"]);
            await ws.ReloadStreamAsync();
            Assert.Equal(200, ws.StreamEntries.Count);
            Assert.True(ws.StreamHasMore);
            await ws.LoadOlderStreamAsync();
            Assert.Equal(257, ws.StreamEntries.Count);
            Assert.False(ws.StreamHasMore);

            // 화면
            var view = new RedisStreamsView(ws);
            var w = new Window { Width = 1100, Height = 700, Content = view };
            w.Show();
            Pump();
            Assert.Equal(257, view.Entries.Table.Count);
            Assert.Equal(1, view.GroupTable.Count);
            w.Close();
        }
        finally
        {
            ws.StopTail();
            await Cleanup(s, prefix);
            await app.DisconnectRedisAsync(p.Id);
        }
    });

    [Fact]
    public Task Info_AutoRefresh_StopsOnDisconnect() => UiThread.Run(async () =>
    {
        var (app, p, _) = Setup();
        var ws = await Connect(app, p);
        await ws.LoadInfoAsync();
        Assert.NotNull(ws.InfoValue("redis_version"));
        Assert.Contains(ws.Info, sct => sct.Name == "Server");
        ws.SetAutoRefreshSeconds(1);
        ws.SetAutoRefresh(true);
        Assert.True(ws.AutoRefresh);
        // 접속을 끊으면 자동 새로고침도 멈춘다
        await app.DisconnectRedisAsync(p.Id);
        Assert.False(ws.AutoRefresh);
        // 서버에 닿지 않으면(연결 끊김) 조용한 새로고침이 자동 새로고침을 끈다
        var offline = RedisWorkspaceTests.Offline(app);
        offline.SetAutoRefresh(true);
        await offline.RefreshAllAsync(quiet: true);
        Assert.False(offline.AutoRefresh);
        ws.SetAutoRefreshSeconds(5);
        Assert.False(app.RedisWorkspaces.ContainsKey(p.Id));
    });

    [Fact]
    public Task ReadOnly_BlocksWrites() => UiThread.Run(async () =>
    {
        var (app, p, _) = Setup(readOnly: true);
        var ws = await Connect(app, p);
        try
        {
            Assert.True(ws.ReadOnly);
            var e = await ws.WriteAsync(["SET", "itui:ro", "1"], "SET");
            Assert.NotNull(e);
            Assert.NotNull(await ws.PublishAsync("itui:ro", "x"));
            await ws.RunConsoleAsync("SET itui:ro 1");
            Assert.True(ws.Console[^1].IsError);
            await ws.RunConsoleAsync("PING");
            Assert.Equal("PONG", ws.Console[^1].Output);
            Assert.Equal(0, (await ws.Session.CommandAsync(["EXISTS", "itui:ro"])).AsInt);
        }
        finally
        {
            await app.DisconnectRedisAsync(p.Id);
        }
    });

    [Fact]
    public Task Cluster_TopologyBadge_NodeInfo_Scan() => UiThread.Run(async () =>
    {
        var (app, p, _) = Setup(mode: RedisMode.Cluster, portVar: "GODDB_REDIS_CLUSTER_PORT", db: "");
        var prefix = "itui" + Guid.NewGuid().ToString("N")[..8];
        var ws = await Connect(app, p);
        try
        {
            Assert.True(ws.HasTopology);
            Assert.StartsWith("Cluster", ws.TopologyText);
            Assert.Equal(3, ws.Nodes.Count);
            Assert.True(ws.MultipleNodes);
            Assert.Equal([0], ws.Databases.Select(d => d.Index));
            foreach (var i in Enumerable.Range(0, 30)) await ws.Session.CommandAsync(["SET", $"{prefix}:{i}", "v"]);
            ws.Pattern = prefix + ":*";
            await ws.ReloadKeysAsync();
            Assert.Equal(30, ws.Keys.Count);
            ws.TreeMode = false;
            foreach (var k in ws.Keys) ws.WantMeta(k);
            await ws.MetaTask;
            Assert.All(ws.Rows, r => Assert.Equal("S", r.TypeLetter));
            // 노드별 INFO
            ws.InfoNode = ws.Nodes[1];
            await ws.LoadInfoAsync();
            Assert.Equal(ws.Nodes[1].Split(':')[1], ws.InfoValue("tcp_port"));
            // SELECT 는 안내
            await ws.RunConsoleAsync("SELECT 1");
            Assert.True(ws.Console[^1].IsError);
        }
        finally
        {
            foreach (var i in Enumerable.Range(0, 30)) await ws.Session.CommandAsync(["DEL", $"{prefix}:{i}"]);
            await app.DisconnectRedisAsync(p.Id);
        }
    });

    [Fact]
    public Task Sentinel_ConnectsToMaster() => UiThread.Run(async () =>
    {
        var (app, p, _) = Setup(mode: RedisMode.Sentinel, portVar: "GODDB_REDIS_SENTINEL_PORT", db: "0");
        var ws = await Connect(app, p);
        try
        {
            Assert.Contains("mymaster", ws.TopologyText);
            await ws.RunConsoleAsync("PING");
            Assert.Equal("PONG", ws.Console[^1].Output);
        }
        finally
        {
            await app.DisconnectRedisAsync(p.Id);
        }
    });
}
