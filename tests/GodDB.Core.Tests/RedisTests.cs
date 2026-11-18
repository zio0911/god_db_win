using System.Text;
using GodDB.Core.Redis;

namespace GodDB.Core.Tests;

file static class R
{
    public static RespValue B(string s) => RespValue.BulkString(s);
    public static byte[] U(string s) => Encoding.UTF8.GetBytes(s);
}

public class RedisProtocolTests
{
    private static RespValue? Decode(string s) => RespCoder.Decode(R.U(s), out _);

    [Fact]
    public void DecodesAllTypes()
    {
        Assert.Equal(RespValue.Simple("OK"), Decode("+OK\r\n"));
        Assert.Equal(RespValue.Error("ERR bad"), Decode("-ERR bad\r\n"));
        Assert.Equal(RespValue.Int(42), Decode(":42\r\n"));
        Assert.Equal(R.B("hello"), Decode("$5\r\nhello\r\n"));
        Assert.Equal(RespValue.Bulk(null), Decode("$-1\r\n"));
        Assert.Equal(RespValue.Arr(RespValue.Bulk([97]), RespValue.Int(1)), Decode("*2\r\n$1\r\na\r\n:1\r\n"));
        Assert.Equal(RespValue.Array(null), Decode("*-1\r\n"));
    }

    [Fact]
    public void WaitsForPartialData()
    {
        var b = R.U("*2\r\n$5\r\nhel");
        Assert.Null(RespCoder.Decode(b, out var used));
        Assert.Equal(0, used);   // 읽은 위치를 되돌린다
        b = [.. b, .. R.U("lo\r\n:7\r\n")];
        Assert.Equal(RespValue.Arr(R.B("hello"), RespValue.Int(7)), RespCoder.Decode(b, out used));
        Assert.Equal(b.Length, used);
    }

    [Fact]
    public void FormatsLikeRedisCli()
    {
        var v = RespValue.Arr(R.B("a"), RespValue.Int(2), RespValue.Arr(RespValue.Bulk(null)));
        Assert.Equal("1) \"a\"\n2) (integer) 2\n3) 1) (nil)", v.Formatted());
        Assert.Equal("\\xffA", RespValue.Bulk([0xff, 0x41]).AsString);
        Assert.Equal("한글", R.B("한글").AsString);
    }

    [Fact]
    public void SplitsArgsWithQuotes()
    {
        Assert.Equal(["SET", "k", "hello world"], RedisText.SplitArgs("SET  k \"hello world\""));
        Assert.Equal(["SET", "k", "it's"], RedisText.SplitArgs("SET k 'it\\'s'"));
        Assert.Equal(["SET", "k", "a\nb"], RedisText.SplitArgs("SET k \"a\\nb\""));
        Assert.Null(RedisText.SplitArgs("SET k \"open"));
        Assert.Equal([], RedisText.SplitArgs("")!);
    }

    [Fact]
    public void ScriptLines()
    {
        var text = "// 큐에 넣기\nRPUSH queue a b\n\n  # 확인\n  LRANGE queue 0 -1  \nSET k \"a b\"";
        Assert.Equal(["RPUSH queue a b", "LRANGE queue 0 -1", "SET k \"a b\""], RedisText.ScriptLines(text));
    }

    [Fact]
    public void ClassifiesCommands()
    {
        Assert.True(RedisCommands.IsReadOnly(["get", "k"]));
        Assert.False(RedisCommands.IsReadOnly(["SET", "k", "v"]));
        Assert.True(RedisCommands.IsReadOnly(["CONFIG", "GET", "maxmemory"]));
        Assert.False(RedisCommands.IsReadOnly(["CONFIG", "SET", "maxmemory", "1"]));
        Assert.False(RedisCommands.IsReadOnly(["XREADGROUP", "GROUP", "g", "c", "STREAMS", "s", ">"]));
        Assert.True(RedisCommands.IsDangerous(["flushall"]));
        Assert.True(RedisCommands.IsDangerous(["KEYS", "*"]));
        Assert.False(RedisCommands.IsDangerous(["GET", "k"]));
        Assert.Contains("HGETALL", RedisCommands.Complete("hge"));
    }

    [Fact]
    public void ParsesInfo()
    {
        var info = RedisSession.ParseInfo("# Server\r\nredis_version:7.2.4\r\n\r\n# Keyspace\r\ndb0:keys=3,expires=0\r\n");
        Assert.Equal(["Server", "Keyspace"], info.Select(s => s.Name));
        Assert.Equal(new RedisPair("redis_version", "7.2.4"), info[0].Items.First());
    }

    // ── Windows 판에서 더한 검사 (Mac 은 화면 코드·Audit 에 있던 부분) ──

    [Fact]
    public void EncodesCommands()
    {
        Assert.Equal("*2\r\n$3\r\nGET\r\n$1\r\nk\r\n", Encoding.UTF8.GetString(RespCoder.Encode([R.U("GET"), R.U("k")])));
    }

    [Fact]
    public void ClassifiesForAudit()
    {
        Assert.Equal(RedisAuditAction.Read, RedisCommands.ClassifyForAudit(["get", "k"]));
        Assert.Equal(RedisAuditAction.Read, RedisCommands.ClassifyForAudit(["CONFIG", "GET", "x"]));
        Assert.Equal(RedisAuditAction.Settings, RedisCommands.ClassifyForAudit(["CONFIG", "SET", "x", "1"]));
        Assert.Equal(RedisAuditAction.Delete, RedisCommands.ClassifyForAudit(["DEL", "k"]));
        Assert.Equal(RedisAuditAction.Execute, RedisCommands.ClassifyForAudit(["PUBLISH", "c", "m"]));
        Assert.Equal(RedisAuditAction.Update, RedisCommands.ClassifyForAudit(["HSET", "k", "f", "v"]));
        Assert.Equal("조회", RedisAuditAction.Read.Label());
    }

    [Fact]
    public void FormatsTtlAndArgs()
    {
        Assert.Equal("만료 없음", RedisFormat.TtlText(-1));
        Assert.Equal("키 없음", RedisFormat.TtlText(-2));
        Assert.Equal("45초", RedisFormat.TtlText(45_000));
        Assert.Equal("12m 3s", RedisFormat.TtlText(723_000));
        Assert.Equal("3h 20m", RedisFormat.TtlText((3 * 3600 + 20 * 60) * 1000L));
        Assert.Equal("2d 4h", RedisFormat.TtlText((2 * 86400 + 4 * 3600) * 1000L));
        Assert.Equal(["45s", "12m", "3h", "2d"],
            new[] { 45_000L, 12 * 60_000L, 3 * 3_600_000L, 2 * 86_400_000L }.Select(RedisFormat.ShortTtl));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
        Assert.Equal(7_000, RedisFormat.RemainingTtl(10_000, t0, t0.AddSeconds(3)));
        Assert.Equal(0, RedisFormat.RemainingTtl(1_000, t0, t0.AddSeconds(3)));
        Assert.Equal(-1, RedisFormat.RemainingTtl(-1, t0, t0.AddSeconds(3)));
        Assert.Equal("SET k \"a b\" \"\" \"say \\\"hi\\\"\"", RedisFormat.Display(["SET", "k", "a b", "", "say \"hi\""]));
        var (chans, pats) = RedisFormat.SplitSubscribeTargets("news, evt:* ,a?b");
        Assert.Equal(["news"], chans);
        Assert.Equal(["evt:*", "a?b"], pats);
        Assert.Equal(["ZADD", "z", "0", "m"], RedisFormat.CreateKeyArgs("zset", "z", "", "m"));
        Assert.Equal(["XADD", "s", "*", "value", "v"], RedisFormat.CreateKeyArgs("stream", "s", "", "v"));
        Assert.True(RedisFormat.ScriptHasDangerous("GET a\nFLUSHDB"));
        Assert.False(RedisFormat.ScriptHasDangerous("GET a\n# FLUSHDB"));
    }

    [Fact]
    public void ParsesPubSubMessages()
    {
        var m = RedisSubscriber.Parse(RespValue.Arr(R.B("pmessage"), R.B("evt:*"), R.B("evt:a"), R.B("x")));
        Assert.NotNull(m);
        Assert.Equal(("pmessage", "evt:a", "evt:*", "x"), (m!.Kind, m.Channel, m.Pattern, m.Payload));
        Assert.Equal("news", RedisSubscriber.Parse(RespValue.Arr(R.B("subscribe"), R.B("news"), RespValue.Int(1)))!.Channel);
        Assert.Null(RedisSubscriber.Parse(RespValue.Simple("OK")));
    }
}

public class RedisKeyTreeTests
{
    [Fact]
    public void GroupsByDelimiter()
    {
        var t = RedisKeyTree.Build(["user:10:name", "user:2:name", "user:2:email", "user:2", "session:abc", "plain"]);
        Assert.Equal(["session", "user", "plain"], t.Select(n => n.Name));
        Assert.Equal(4, t[1].Count);
        var user = t[1].Children!;
        // 숫자 순서, 폴더 먼저, 같은 이름의 키(user:2)는 폴더 뒤에
        Assert.Equal(["f:user:2", "f:user:10", "k:user:2"], user.Select(n => n.Id));
        Assert.Equal(["user:2:email", "user:2:name"], user[0].Children!.Select(n => n.Key));
        Assert.True(t[2].Key == "plain" && t[2].Children == null);
    }

    [Fact]
    public void OtherDelimiterAndEdgeCases()
    {
        var t = RedisKeyTree.Build(["a/b/c", "a/d", "x::y"], delimiter: "/");
        Assert.Equal(["a", "x::y"], t.Select(n => n.Name));
        var colon = RedisKeyTree.Build(["x::y"]);
        Assert.Equal("", colon.First().Children!.First().Name);   // 빈 조각 폴더
        Assert.Equal("k", RedisKeyTree.Build(["k"], delimiter: "").First().Key);
    }

    [Fact]
    public void FolderPattern()
    {
        Assert.Equal("user:1001:*", RedisKeyTree.Pattern("user:1001"));
        Assert.Equal("a\\*b:*", RedisKeyTree.Pattern("a*b"));
    }
}

public class RedisClusterMathTests
{
    [Fact]
    public void CrcAndSlots()
    {
        Assert.Equal(0x31C3, RedisCluster.Crc16(R.U("123456789")));
        Assert.Equal(12182, RedisCluster.Slot("foo"));
        Assert.Equal(5061, RedisCluster.Slot("bar"));
        Assert.Equal(RedisCluster.Slot("{user1000}.following"), RedisCluster.Slot("{user1000}.followers"));
        Assert.Equal(RedisCluster.Slot("{}x"), RedisCluster.Slot("{}x"));   // 빈 태그는 키 전체
        Assert.Equal(RedisCluster.Crc16(R.U("{}x")) % RedisCluster.SlotCount, RedisCluster.Slot("{}x"));
    }

    [Fact]
    public void RoutingKeys()
    {
        Assert.Equal("k", RedisCluster.RoutingKey(["GET", "k"]));
        Assert.Null(RedisCluster.RoutingKey(["INFO"]));
        Assert.Equal("k", RedisCluster.RoutingKey(["MEMORY", "USAGE", "k"]));
        Assert.Equal("s", RedisCluster.RoutingKey(["XINFO", "GROUPS", "s"]));
        Assert.Equal("s", RedisCluster.RoutingKey(["XREAD", "COUNT", "1", "BLOCK", "0", "STREAMS", "s", "$"]));
        Assert.Equal("k", RedisCluster.RoutingKey(["EVAL", "return 1", "1", "k"]));
        Assert.Null(RedisCluster.RoutingKey(["SCAN", "0"]));
    }

    [Fact]
    public void SlotsAndRedirects()
    {
        var v = RespValue.Arr(
            RespValue.Arr(RespValue.Int(5461), RespValue.Int(10922), RespValue.Arr(R.B("10.0.0.2"), RespValue.Int(6379))),
            RespValue.Arr(RespValue.Int(0), RespValue.Int(5460), RespValue.Arr(R.B(""), RespValue.Int(7000)),
                RespValue.Arr(R.B("10.0.0.9"), RespValue.Int(7003))));
        var s = RedisCluster.ParseSlots(v, seedHost: "seed");
        Assert.Equal(["seed:7000", "10.0.0.2:6379"], s.Select(r => r.Master));
        Assert.Equal(["10.0.0.9:7003"], s[0].Replicas);
        Assert.Equal(new RedisRedirect.Moved(3999, "127.0.0.1:6381"), RedisCluster.Redirect("MOVED 3999 127.0.0.1:6381"));
        Assert.Equal(new RedisRedirect.Ask(1, "h:1"), RedisCluster.Redirect("ASK 1 h:1"));
        Assert.Null(RedisCluster.Redirect("ERR x y"));
        Assert.Equal(("::1", 6379), RedisCluster.Split("[::1]:6379")!.Value);
        Assert.Equal(["a:26380", "b:26379"], RedisCluster.SentinelAddresses("a:26380, b", defaultPort: 26379).Select(a => $"{a.Host}:{a.Port}"));
    }
}

public class RedisKeyTreeRowsTests
{
    [Fact]
    public void VisibleRowsFollowExpandedFolders()
    {
        var tree = RedisKeyTree.Build(["user:1:name", "user:1:age", "user:2:name", "session:x", "plain"]);
        // 접힌 상태: 최상위만
        var top = RedisKeyTree.VisibleRows(tree, new HashSet<string>());
        Assert.Equal(["f:session", "f:user", "k:plain"], top.Select(r => r.Id));
        Assert.All(top, r => Assert.Equal(0, r.Depth));
        // user, user:1 펼침
        var open = RedisKeyTree.VisibleRows(tree, new HashSet<string> { "f:user", "f:user:1" });
        Assert.Equal(["f:session", "f:user", "f:user:1", "k:user:1:age", "k:user:1:name", "f:user:2", "k:plain"], open.Select(r => r.Id));
        Assert.Equal(2, open.First(r => r.Id == "k:user:1:age").Depth);
        // 행 id 는 겹치지 않는다
        Assert.Equal(open.Count, open.Select(r => r.Id).Distinct().Count());
        // 없어진 폴더가 펼침 목록에 남아 있어도 문제없다
        Assert.Equal(3, RedisKeyTree.VisibleRows(tree, new HashSet<string> { "f:gone" }).Count);
    }
}
