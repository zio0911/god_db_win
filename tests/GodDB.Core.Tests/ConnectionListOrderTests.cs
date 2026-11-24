using System.Text.Json;
using GodDB.Core.Profiles;

namespace GodDB.Core.Tests;

/// 사이드바 접속 목록 찾기 · 정렬 (Mac ConnectionListOrderTests, #173)
public class ConnectionListOrderTests
{
    private static ConnectionProfile Profile(string name, DatabaseKind kind = DatabaseKind.Mysql, string group = "",
        string host = "h.example.test", string db = "", double? last = null) => new()
    {
        Kind = kind, Name = name, Host = host, Port = kind.DefaultPort(), Database = db, Group = group,
        LastConnectedAt = last is { } t ? DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000)) : null,
    };

    [Fact]
    public void FindsByNameHostDatabaseGroupAndType()
    {
        var p = Profile("주문 DB", DatabaseKind.Postgres, group: "운영", host: "orders.example.test", db: "shop");
        p.Ssh = new SshConfig { Host = "bastion.example.test" };
        p.Environment = ConnectionEnvironment.Production;
        foreach (var q in new[] { "주문", "ORDERS", "shop", "운영", "postgres", "PostgreSQL", "bastion", "주문 shop", "" })
            Assert.True(ConnectionListOrder.Matches(p, q), q);
        Assert.False(ConnectionListOrder.Matches(p, "회원"));
        Assert.False(ConnectionListOrder.Matches(p, "redshift"));
        var rs = Profile("분석", DatabaseKind.Postgres);
        rs.Redshift = true;
        Assert.True(ConnectionListOrder.Matches(rs, "redshift"));
        Assert.False(ConnectionListOrder.Matches(p, "주문 회원"));  // 낱말이 모두 들어 있어야
    }

    [Fact]
    public void SortsByEachOrder()
    {
        var a = Profile("b-mysql", DatabaseKind.Mysql, last: 100);
        var b = Profile("a-redis", DatabaseKind.Redis);
        var c = Profile("c-pg", DatabaseKind.Postgres, last: 300);
        var d = Profile("A-pg", DatabaseKind.Postgres, last: 200);
        ConnectionProfile[] all = [a, b, c, d];
        IEnumerable<string> Names(ConnectionSort s, bool connectedFirst = false, HashSet<Guid>? connected = null) =>
            ConnectionListOrder.Sorted(all, s, connectedFirst, id => connected?.Contains(id) == true).Select(p => p.Name);
        Assert.Equal(["b-mysql", "a-redis", "c-pg", "A-pg"], Names(ConnectionSort.Manual));
        Assert.Equal(["A-pg", "a-redis", "b-mysql", "c-pg"], Names(ConnectionSort.Name));
        Assert.Equal(["c-pg", "A-pg", "b-mysql", "a-redis"], Names(ConnectionSort.Recent));  // 연결한 적 없으면 뒤
        Assert.Equal(["b-mysql", "A-pg", "c-pg", "a-redis"], Names(ConnectionSort.Type));
        var r = Profile("0-rs", DatabaseKind.Postgres);
        r.Redshift = true;
        Assert.Equal(["b-mysql", "A-pg", "c-pg", "0-rs", "0-ms", "a-redis"],
            ConnectionListOrder.Sorted([.. all, r, Profile("0-ms", DatabaseKind.Mssql)], ConnectionSort.Type).Select(p => p.Name));
        Assert.Equal(["b-mysql", "c-pg", "A-pg", "a-redis"], Names(ConnectionSort.Name, connectedFirst: true, connected: [c.Id, a.Id]));
    }

    [Fact]
    public void GroupOrder()
    {
        ConnectionProfile[] ps = [Profile("1", group: "운영", last: 10), Profile("2", group: "개발", last: 50),
                                  Profile("3"), Profile("4", group: "운영", last: 90), Profile("5", group: "기타")];
        Assert.Equal(["개발", "기타", "운영"], ConnectionListOrder.SortedGroups(ps, ConnectionSort.Name));
        Assert.Equal(["운영", "개발", "기타"], ConnectionListOrder.SortedGroups(ps, ConnectionSort.Manual));
        Assert.Equal(["운영", "개발", "기타"], ConnectionListOrder.SortedGroups(ps, ConnectionSort.Recent));
    }

    [Fact]
    public void ManualMoves()
    {
        ConnectionProfile[] ps = [Profile("a"), Profile("g1", group: "운영"), Profile("b"), Profile("g2", group: "운영"), Profile("c")];
        static IEnumerable<string> Names(IEnumerable<ConnectionProfile> x) => x.Select(p => p.Name).ToList();
        Guid Id(string n) => ps.First(p => p.Name == n).Id;
        // 같은 그룹 안에서만 움직인다
        Assert.Equal(["a", "g1", "c", "b", "g2"], Names(ConnectionListOrder.Moved(ps, Id("c"), ConnectionListOrder.Move.Up)));
        Assert.Equal(["c", "a", "g1", "b", "g2"], Names(ConnectionListOrder.Moved(ps, Id("c"), ConnectionListOrder.Move.Top)));
        Assert.Equal(["a", "b", "g2", "g1", "c"], Names(ConnectionListOrder.Moved(ps, Id("g1"), ConnectionListOrder.Move.Down)));
        Assert.Equal(Names(ps), Names(ConnectionListOrder.Moved(ps, Id("a"), ConnectionListOrder.Move.Up)));        // 이미 맨 위
        Assert.Equal(Names(ps), Names(ConnectionListOrder.Moved(ps, Id("g2"), ConnectionListOrder.Move.Bottom)));   // 이미 맨 아래
        // 끌어다 놓기: 놓은 접속 앞으로, 그 그룹으로
        var dropped = ConnectionListOrder.Moved(ps, Id("c"), before: Id("g1"));
        Assert.Equal(["a", "c", "g1", "b", "g2"], Names(dropped));
        Assert.Equal("운영", dropped.First(p => p.Name == "c").Group);
        Assert.Equal(Names(ps), Names(ConnectionListOrder.Moved(ps, Id("a"), before: Id("a"))));
        // 보이는 순서 그대로 (그룹 없는 접속 → 그룹)
        Assert.Equal(["a", "b", "c", "g1", "g2"], Names(ConnectionListOrder.VisibleOrder(ps, ConnectionSort.Name)));
    }

    [Fact]
    public void GroupMoves()
    {
        ConnectionProfile[] ps = [Profile("x"), Profile("a1", group: "A"), Profile("b1", group: "B"), Profile("y"),
                                  Profile("a2", group: "A"), Profile("c1", group: "C")];
        static IEnumerable<string> Names(IEnumerable<ConnectionProfile> x) => x.Select(p => p.Name).ToList();
        static IEnumerable<string> Groups(IReadOnlyList<ConnectionProfile> x) => ConnectionListOrder.SortedGroups(x, ConnectionSort.Manual);
        Assert.Equal(["C", "A", "B"], Groups(ConnectionListOrder.MovedGroup(ps, "C", ConnectionListOrder.Move.Top)));
        Assert.Equal(["B", "A", "C"], Groups(ConnectionListOrder.MovedGroup(ps, "A", ConnectionListOrder.Move.Down)));
        Assert.Equal(["A", "C", "B"], Groups(ConnectionListOrder.MovedGroup(ps, "B", ConnectionListOrder.Move.Bottom)));
        Assert.Equal(Names(ps), Names(ConnectionListOrder.MovedGroup(ps, "A", ConnectionListOrder.Move.Up)));   // 이미 맨 위
        // 그룹 안 순서 · 그룹 없는 접속 순서는 그대로
        Assert.Equal(["x", "y", "c1", "a1", "a2", "b1"], Names(ConnectionListOrder.MovedGroup(ps, "C", ConnectionListOrder.Move.Top)));
        // 끌어다 놓기
        Assert.Equal(["A", "C", "B"], Groups(ConnectionListOrder.MovedGroup(ps, "C", before: "B")));
        Assert.Equal(Names(ps), Names(ConnectionListOrder.MovedGroup(ps, "A", before: "A")));
    }

    [Fact]
    public void LastConnectedIsSavedAndOldFilesRead()
    {
        var p = Profile("x", last: 1_700_000_000);
        var back = JsonSerializer.Deserialize<ConnectionProfile>(JsonSerializer.Serialize(p, ProfileStore.Json), ProfileStore.Json)!;
        Assert.Equal(p.LastConnectedAt, back.LastConnectedAt);
        p.LastConnectedAt = null;
        const string old = """{"id":"6F1C2A6E-1B8B-4B6E-9C55-0C1F0E6A9A10","kind":"mysql","name":"a","host":"h","port":3306,"username":"u"}""";
        Assert.Null(JsonSerializer.Deserialize<ConnectionProfile>(old, ProfileStore.Json)!.LastConnectedAt);
    }

    /// Windows: profiles.json 의 lastConnectedAt 은 Mac(JSONEncoder 기본값)과 같은 "2001-01-01 부터 초" 숫자
    [Fact]
    public void LastConnectedUsesMacReferenceDateNumber()
    {
        var p = Profile("x", last: 1_700_000_000);
        var json = JsonSerializer.Serialize(p, ProfileStore.Json);
        Assert.Contains("\"lastConnectedAt\": 721692800", json);
        const string mac = """{"id":"6F1C2A6E-1B8B-4B6E-9C55-0C1F0E6A9A10","kind":"mysql","name":"a","host":"h","port":3306,"username":"u","lastConnectedAt":721692800.5}""";
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_500),
            JsonSerializer.Deserialize<ConnectionProfile>(mac, ProfileStore.Json)!.LastConnectedAt);
    }

    [Fact]
    public void Translated()
    {
        foreach (var s in ConnectionSorts.All) Assert.True(L10n.EnglishTable.ContainsKey(s.Title()) || L10n.IsEnglish, s.ToString());
        Assert.Equal("5 total · 2 shown", L10n.Format(L10n.EnglishTable["%@개 중 %@개"], "5", "2"));
    }
}
