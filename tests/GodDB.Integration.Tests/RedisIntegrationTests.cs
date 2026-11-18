#pragma warning disable xUnit1051 // 통합 테스트는 자체 시간 제한을 쓴다
using System.Text;
using GodDB.Core;
using GodDB.Core.Redis;

namespace GodDB.Integration.Tests;

file static class Env
{
    public static string? Get(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
    public static string Password => Get("GODDB_IT_PASSWORD") ?? "goddbtest";

    public static int Port(string name)
    {
        var v = Get(name);
        if (v == null) Assert.Skip($"{name} 가 없어 건너뜀 (./scripts/test-dbs.sh up)");
        return int.Parse(v!);
    }

    public static async Task<T> First<T>(System.Threading.Channels.ChannelReader<T> reader, Func<T, bool> match, int seconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        await foreach (var m in reader.ReadAllAsync(cts.Token)) if (match(m)) return m;
        throw new InvalidOperationException("stream ended");
    }
}

/// 실제 Redis (./scripts/test-dbs.sh up → 127.0.0.1:36379, 비밀번호 goddbtest)
[Collection("redis")]
public class RedisIntegrationTests
{
    private static ConnectionProfile Profile(bool readOnly = false) => new()
    {
        Kind = DatabaseKind.Redis, Name = "redis-it", Host = "127.0.0.1", Port = Env.Port("GODDB_REDIS_PORT"),
        Username = "", Database = "3", UseTLS = false, ReadOnly = readOnly,
    };

    private static Task<RedisSession> Session(bool readOnly = false) => RedisSession.ConnectAsync(Profile(readOnly), Env.Password);

    [Fact]
    public async Task WrongPasswordFails()
    {
        await Assert.ThrowsAsync<RedisServerException>(() => RedisSession.ConnectAsync(Profile(), "nope"));
    }

    [Fact]
    public async Task TypesScanAndInfo()
    {
        await using var s = await Session();
        await s.CommandAsync(["FLUSHDB"]);
        await s.CommandAsync(["SET", "user:1:name", "홍길동"]);
        await s.CommandAsync(["EXPIRE", "user:1:name", "100"]);
        await s.CommandAsync(["HSET", "user:1", "name", "kim", "age", "30"]);
        await s.CommandAsync(["RPUSH", "queue", "a", "b", "c"]);
        await s.CommandAsync(["SADD", "tags", "x", "y"]);
        await s.CommandAsync(["ZADD", "rank", "10", "a", "20", "b"]);
        for (int i = 0; i < 1200; i++) await s.CommandAsync(["SET", $"bulk:{i}", $"{i}"]);

        Assert.Equal(3, s.Db);
        Assert.Equal(1205, await s.DbSizeAsync());
        var all = await s.ScanKeysAsync("user:*");
        Assert.Equal(new HashSet<string> { "user:1:name", "user:1" }, all.Keys.ToHashSet());
        var page = await s.ScanKeysAsync("bulk:*", limit: 500);
        Assert.True(page.Keys.Count >= 500);
        Assert.NotNull(page.Next);
        var hashes = await s.ScanKeysAsync("*", type: "hash");
        Assert.Equal(["user:1"], hashes.Keys);

        var info = await s.KeyInfoAsync("user:1:name");
        Assert.Equal("string", info.Type);
        Assert.True(info.TtlMillis > 90_000);
        Assert.Equal(Encoding.UTF8.GetByteCount("홍길동"), info.Length);
        Assert.Equal("홍길동", await s.StringAsync("user:1:name"));
        Assert.Equal(["age=30", "name=kim"], (await s.HashAsync("user:1")).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        Assert.Equal(["b", "c"], await s.ListAsync("queue", start: 1, count: 5));
        Assert.Equal(new HashSet<string> { "x", "y" }, (await s.SetAsync("tags")).ToHashSet());
        Assert.Equal(["b", "a"], (await s.ZsetAsync("rank", start: 0, count: 10, reverse: true)).Select(p => p.Key));
        Assert.False((await s.KeyInfoAsync("missing")).Exists);

        var dbs = await s.DatabasesAsync();
        Assert.Equal(1205, dbs.First(d => d.Index == 3).Keys);
        var sections = await s.InfoAsync();
        Assert.Contains(sections, x => x.Name == "Server");

        // 콘솔: 오류도 값으로
        var err = await s.ConsoleAsync(["HGET", "queue", "x"]);
        Assert.Equal(RespKind.Error, err.Kind);
        Assert.StartsWith("WRONGTYPE", err.Text);
        var e = await Assert.ThrowsAsync<RedisCommandException>(() => s.ConsoleAsync(["SUBSCRIBE", "c"]));
        Assert.Equal(RedisCommandErrorKind.UseSubscribeTab, e.Kind);

        // Windows 판에서 더한 검사: 1MB 넘는 문자열은 앞부분만, 키 메타 파이프라인
        await s.CommandAsync(["SET", "big", new string('x', 1_048_576 + 10)]);
        var big = await s.StringPrefixAsync("big");
        Assert.True(big.Truncated);
        Assert.Equal(1_048_576 + 10, big.Total);
        Assert.Equal(1_048_576, big.Value.Length);
        var meta = await s.KeyMetaAsync(["user:1", "queue", "nope"]);
        Assert.Equal("hash", meta["user:1"].Type);
        Assert.Equal(-1, meta["queue"].TtlMillis);
        Assert.Equal("none", meta["nope"].Type);
        await s.CommandAsync(["DEL", "big"]);
    }

    [Fact]
    public async Task ReadOnlyBlocksWrites()
    {
        await using var s = await Session(readOnly: true);
        Assert.Equal(RedisCommandErrorKind.ReadOnly, (await Assert.ThrowsAsync<RedisCommandException>(() => s.ConsoleAsync(["SET", "k", "v"]))).Kind);
        Assert.Equal(RedisCommandErrorKind.ReadOnly, (await Assert.ThrowsAsync<RedisCommandException>(() => s.CommandAsync(["DEL", "k"]))).Kind);
        _ = await s.ConsoleAsync(["GET", "k"]);
    }

    [Fact]
    public async Task StreamsAndGroups()
    {
        await using var s = await Session();
        await s.CommandAsync(["DEL", "orders"]);
        for (int i = 1; i <= 5; i++) await s.CommandAsync(["XADD", "orders", "*", "order_id", $"{i}", "status", "new"]);
        await s.CommandAsync(["XGROUP", "CREATE", "orders", "billing", "0"]);
        // consumer c1 가 2건 읽고 1건만 ACK
        var read = await s.CommandAsync(["XREADGROUP", "GROUP", "billing", "c1", "COUNT", "2", "STREAMS", "orders", ">"]);
        var firstID = read.AsArray?.FirstOrDefault()?.AsArray?[1].AsArray?.FirstOrDefault()?.AsArray?.FirstOrDefault()?.AsString;
        Assert.NotNull(firstID);
        await s.CommandAsync(["XACK", "orders", "billing", firstID!]);

        var latest = await s.StreamLatestAsync("orders", count: 3);
        Assert.Equal(3, latest.Count);
        Assert.Equal(new RedisPair("order_id", "5"), latest.First().Fields.First());
        var older = await s.StreamLatestAsync("orders", count: 10, before: latest.Last().Id);
        Assert.Equal(2, older.Count);
        Assert.Equal(5, (await s.StreamRangeAsync("orders", start: "", end: "", count: 100)).Count);

        var groups = await s.StreamGroupsAsync("orders");
        Assert.Equal(["billing"], groups.Select(g => g.Name));
        Assert.Equal(1, groups.First().Pending);
        var consumers = await s.StreamConsumersAsync("orders", group: "billing");
        Assert.Equal(["c1"], consumers.Select(c => c.Name));
        var pending = await s.StreamPendingAsync("orders", group: "billing");
        Assert.True(pending.Count == 1 && pending.First().Consumer == "c1");
        var info = await s.StreamInfoAsync("orders");
        Assert.Contains(info, p => p.Key == "length" && p.Value == "5");

        // 실시간 보기: 새 항목이 들어오면 받는다
        await using var tail = await s.StreamTailAsync("orders");
        var got = Env.First(tail.Entries, _ => true);
        await Task.Delay(300);
        await s.CommandAsync(["XADD", "orders", "*", "order_id", "99"]);
        Assert.Equal("99", (await got).Fields.First().Value);
    }

    [Fact]
    public async Task PubSub()
    {
        await using var s = await Session();
        await using var sub = await s.SubscriberAsync();
        sub.Subscribe(["news"]);
        sub.PSubscribe(["evt:*"]);
        var collected = Task.Run(async () =>
        {
            var output = new List<RedisPubSubMessage>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var m in sub.Messages.ReadAllAsync(cts.Token))
            {
                output.Add(m);
                if (output.Count(x => x.Kind != "subscribe" && x.Kind != "psubscribe") == 2) break;
            }
            return output;
        });
        await Task.Delay(300);
        var channels = await s.ActiveChannelsAsync();
        Assert.Contains(channels, c => c.Channel == "news" && c.Subscribers == 1);
        Assert.Equal(1, await s.PublishAsync("news", "안녕"));
        Assert.Equal(1, await s.PublishAsync("evt:login", "u1"));
        var msgs = await collected;
        Assert.Contains(msgs, m => m.Kind == "message" && m.Channel == "news" && m.Payload == "안녕");
        Assert.Contains(msgs, m => m.Kind == "pmessage" && m.Pattern == "evt:*" && m.Channel == "evt:login");
    }
}

/// 실제 Redis Cluster (./scripts/test-dbs.sh up → 127.0.0.1:37000~37002) / Sentinel (36381 → mymaster)
[Collection("redis")]
public class RedisClusterIntegrationTests
{
    private static ConnectionProfile Profile(RedisMode mode, int port) => new()
    {
        Kind = DatabaseKind.Redis, Name = "it", Host = "127.0.0.1", Port = port, Username = "", Database = "", UseTLS = false,
        RedisMode = mode, SentinelMaster = "mymaster",
    };

    [Fact]
    public async Task ClusterRoutesScansAndFollowsMoved()
    {
        var port = Env.Port("GODDB_REDIS_CLUSTER_PORT");
        // "단일"로 설정해도 자동으로 Cluster 를 알아챈다
        await using var s = await RedisSession.ConnectAsync(Profile(RedisMode.Standalone, port), Env.Password);
        Assert.True(s.IsCluster);
        Assert.Equal(3, s.MasterNodes.Count);
        // 여러 노드에 흩어지는 키들
        var keys = Enumerable.Range(0, 60).Select(i => $"it:cluster:{i}").ToList();
        foreach (var k in keys) await s.CommandAsync(["SET", k, $"v-{k}"]);
        await s.CommandAsync(["HSET", "it:cluster:h", "f", "1"]);
        foreach (var k in keys) Assert.Equal($"v-{k}", await s.StringAsync(k));
        var found = await s.ScanKeysAsync("it:cluster:*", limit: 10_000);
        Assert.Equal(keys.Append("it:cluster:h").ToHashSet(), found.Keys.ToHashSet());
        Assert.True(await s.DbSizeAsync() >= 61);
        Assert.Equal([0], (await s.DatabasesAsync()).Select(d => d.Index));
        var meta = await s.KeyMetaAsync(["it:cluster:1", "it:cluster:h", "nope"]);
        Assert.True(meta["it:cluster:h"].Type == "hash" && meta["nope"].Type == "none");
        Assert.Equal("hash", (await s.KeyInfoAsync("it:cluster:h")).Type);
        // 콘솔: 다른 노드의 키도 MOVED 없이
        foreach (var k in keys.Take(10))
            Assert.Equal(RespValue.BulkString($"v-{k}"), await s.ConsoleAsync(["GET", k]));
        Assert.Equal(RedisCommandErrorKind.ClusterSingleDB,
            (await Assert.ThrowsAsync<RedisCommandException>(() => s.ConsoleAsync(["SELECT", "1"]))).Kind);
        // 노드별 INFO
        foreach (var n in s.MasterNodes)
            Assert.Contains((await s.InfoAsync("server", node: n)).First().Items, p => p.Key == "tcp_port");
        // Pub/Sub 은 아무 노드에서
        await using (var sub = await s.SubscriberAsync())
        {
            sub.Subscribe(["it:news"]);
            var got = Env.First(sub.Messages, m => m.Kind == "message");
            await Task.Delay(300);
            _ = await s.PublishAsync("it:news", "hello");
            Assert.Equal("hello", (await got).Payload);
        }
        foreach (var k in keys) await s.CommandAsync(["DEL", k]);
        await s.CommandAsync(["DEL", "it:cluster:h"]);
    }

    [Fact]
    public async Task SentinelResolvesMaster()
    {
        var port = Env.Port("GODDB_REDIS_SENTINEL_PORT");
        await using (var s = await RedisSession.ConnectAsync(Profile(RedisMode.Sentinel, port), Env.Password))
        {
            Assert.Equal(new RedisTopology.Sentinel("mymaster", "127.0.0.1:36380"), s.Topology);
            await s.CommandAsync(["SET", "it:sentinel", "ok"]);
            Assert.Equal("ok", await s.StringAsync("it:sentinel"));
        }

        var bad = Profile(RedisMode.Sentinel, port);
        bad.SentinelMaster = "nope";
        var e = await Assert.ThrowsAsync<RedisCommandException>(() => RedisSession.ConnectAsync(bad, Env.Password));
        Assert.Equal(RedisCommandErrorKind.SentinelMasterNotFound, e.Kind);
        Assert.Equal("nope", e.MasterName);
    }
}
