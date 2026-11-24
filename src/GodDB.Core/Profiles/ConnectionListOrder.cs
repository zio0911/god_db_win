using static GodDB.Core.L10n;

namespace GodDB.Core.Profiles;

/// 사이드바 접속 목록 정렬 (Mac ConnectionSort)
public enum ConnectionSort
{
    /// 직접 정한 순 (접속 목록에 저장된 순서 — 새 접속은 맨 아래, 끌거나 메뉴로 옮긴다)
    Manual,
    Name,
    /// 최근 연결순 (연결한 적 없는 접속은 뒤로, 그 안에서는 이름순)
    Recent,
    /// DB 종류순 (MySQL · PostgreSQL · Redshift · SQL Server · Redis), 같은 종류는 이름순
    Type,
}

public static class ConnectionSorts
{
    public static readonly IReadOnlyList<ConnectionSort> All = [ConnectionSort.Manual, ConnectionSort.Name, ConnectionSort.Recent, ConnectionSort.Type];

    public static string Title(this ConnectionSort s) => s switch
    {
        ConnectionSort.Manual => Tr("직접 정한 순"),
        ConnectionSort.Name => Tr("이름순"),
        ConnectionSort.Recent => Tr("최근 연결순"),
        _ => Tr("DB 종류순"),
    };

    /// 설정에 저장하는 값 (Mac rawValue 와 같은 manual · name · recent · type)
    public static string RawValue(this ConnectionSort s) => s switch
    {
        ConnectionSort.Manual => "manual",
        ConnectionSort.Name => "name",
        ConnectionSort.Recent => "recent",
        _ => "type",
    };

    public static ConnectionSort FromRawValue(string? raw) => raw switch
    {
        "name" => ConnectionSort.Name,
        "recent" => ConnectionSort.Recent,
        "type" => ConnectionSort.Type,
        _ => ConnectionSort.Manual,
    };
}

/// 사이드바 접속 목록: 찾기 · 정렬 · 순서 바꾸기 (Mac ConnectionListOrder.swift)
public static class ConnectionListOrder
{
    /// 찾기: 이름 · 주소 · DB 이름 · 그룹 · DB 종류 · SSH 호스트에 낱말이 모두 들어 있으면 (대소문자 무시)
    public static bool Matches(ConnectionProfile p, string query)
    {
        var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        var hay = string.Join("\n", p.Name, p.Host, p.Database, p.Group, p.TypeTitle, p.Kind.Title(), RawKind(p.Kind),
            p.Ssh?.Host ?? "", p.Environment == ConnectionEnvironment.None ? "" : p.Environment.Title()).ToLowerInvariant();
        return words.All(hay.Contains);
    }

    /// Mac DatabaseKind.rawValue (mysql · postgres · mssql · redis)
    private static string RawKind(DatabaseKind k) => k switch
    {
        DatabaseKind.Mysql => "mysql",
        DatabaseKind.Postgres => "postgres",
        DatabaseKind.Mssql => "mssql",
        _ => "redis",
    };

    /// 이름 비교 (Finder 처럼 localizedStandardCompare). 같으면 저장 순서
    private static int ByName(ConnectionProfile a, ConnectionProfile b, Dictionary<Guid, int> index)
    {
        var r = NaturalComparer.Instance.Compare(a.Name, b.Name);
        return r != 0 ? r : index[a.Id].CompareTo(index[b.Id]);
    }

    /// 정렬한 접속 목록. connectedFirst 면 연결된 접속을 먼저 (그 안의 순서는 sort 를 따른다)
    public static List<ConnectionProfile> Sorted(IReadOnlyList<ConnectionProfile> profiles, ConnectionSort sort,
        bool connectedFirst = false, Func<Guid, bool>? isConnected = null)
    {
        isConnected ??= _ => false;
        var index = new Dictionary<Guid, int>();
        for (int i = 0; i < profiles.Count; i++) index[profiles[i].Id] = i;
        int Compare(ConnectionProfile a, ConnectionProfile b)
        {
            if (connectedFirst)
            {
                bool ca = isConnected(a.Id), cb = isConnected(b.Id);
                if (ca != cb) return ca ? -1 : 1;
            }
            switch (sort)
            {
                case ConnectionSort.Manual:
                    return index[a.Id].CompareTo(index[b.Id]);
                case ConnectionSort.Name:
                    return ByName(a, b, index);
                case ConnectionSort.Recent:
                    return (a.LastConnectedAt, b.LastConnectedAt) switch
                    {
                        ({ } x, { } y) when x != y => y.CompareTo(x),
                        ({ }, null) => -1,
                        (null, { }) => 1,
                        _ => ByName(a, b, index),
                    };
                default:
                    int ka = TypeOrder(a), kb = TypeOrder(b);
                    return ka == kb ? ByName(a, b, index) : ka.CompareTo(kb);
            }
        }
        // 비교가 모두 다르게 끝나므로(마지막은 저장 순서) 안정 정렬과 같다
        var list = profiles.ToList();
        list.Sort(Compare);
        return list;
    }

    /// 그룹 순서: 이름순 · DB 종류순은 그룹 이름순, 직접 정한 순은 그룹이 처음 나온 순서, 최근 연결순은 가장 최근 연결한 접속 순
    public static List<string> SortedGroups(IReadOnlyList<ConnectionProfile> profiles, ConnectionSort sort)
    {
        var first = new Dictionary<string, int>();
        var latest = new Dictionary<string, DateTimeOffset>();
        for (int i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i];
            if (p.Group.Length == 0) continue;
            first.TryAdd(p.Group, i);
            if (p.LastConnectedAt is { } d && d > latest.GetValueOrDefault(p.Group, DateTimeOffset.MinValue)) latest[p.Group] = d;
        }
        var groups = first.Keys.ToList();
        groups.Sort((a, b) =>
        {
            switch (sort)
            {
                case ConnectionSort.Manual:
                    return first[a].CompareTo(first[b]);
                case ConnectionSort.Recent:
                    var x = latest.GetValueOrDefault(a, DateTimeOffset.MinValue);
                    var y = latest.GetValueOrDefault(b, DateTimeOffset.MinValue);
                    return x == y ? NaturalComparer.Instance.Compare(a, b) : y.CompareTo(x);
                default:
                    return NaturalComparer.Instance.Compare(a, b);
            }
        });
        return groups;
    }

    /// 화면에 보이는 순서 그대로 (그룹 없는 접속 → 그룹별). 직접 옮기기 전에 이 순서를 저장 순서로 굳힌다
    public static List<ConnectionProfile> VisibleOrder(IReadOnlyList<ConnectionProfile> profiles, ConnectionSort sort,
        bool connectedFirst = false, Func<Guid, bool>? isConnected = null)
    {
        var s = Sorted(profiles, sort, connectedFirst, isConnected);
        return [.. s.Where(p => p.Group.Length == 0), .. SortedGroups(profiles, sort).SelectMany(g => s.Where(p => p.Group == g))];
    }

    public enum Move { Up, Down, Top, Bottom }

    /// 같은 그룹 안에서 위 · 아래 · 맨 위 · 맨 아래로 (저장 순서 기준). 못 옮기면 그대로
    public static List<ConnectionProfile> Moved(IReadOnlyList<ConnectionProfile> profiles, Guid id, Move move)
    {
        var i = IndexOf(profiles, id);
        if (i < 0) return [.. profiles];
        var group = profiles[i].Group;
        var same = Enumerable.Range(0, profiles.Count).Where(x => profiles[x].Group == group).ToList();
        var k = same.IndexOf(i);
        int target;
        switch (move)
        {
            case Move.Up: if (k <= 0) return [.. profiles]; target = same[k - 1]; break;
            case Move.Down: if (k >= same.Count - 1) return [.. profiles]; target = same[k + 1]; break;
            case Move.Top: if (k <= 0) return [.. profiles]; target = same[0]; break;
            default: if (k >= same.Count - 1) return [.. profiles]; target = same[^1]; break;
        }
        var output = profiles.ToList();
        var p = output[i];
        output.RemoveAt(i);
        output.Insert(target, p);
        return output;
    }

    /// 끌어다 놓기: id 를 target 바로 앞으로 옮기고 target 의 그룹으로 넣는다.
    /// (Mac 은 값 타입이라 복사본의 그룹을 바꾼다. 여기서는 원본을 건드리지 않도록 복사본(with)을 넣는다)
    public static List<ConnectionProfile> Moved(IReadOnlyList<ConnectionProfile> profiles, Guid id, Guid before)
    {
        var i = IndexOf(profiles, id);
        if (id == before || i < 0) return [.. profiles];
        var output = profiles.ToList();
        var p = output[i];
        output.RemoveAt(i);
        var t = output.FindIndex(x => x.Id == before);
        if (t < 0) return [.. profiles];
        output.Insert(t, p.Group == output[t].Group ? p : p with { Group = output[t].Group });
        return output;
    }

    /// 그룹 순서 바꾸기 (직접 정한 순: 그룹이 처음 나온 순서). 그룹 없는 접속은 늘 앞, 그룹 안 순서는 그대로
    public static List<ConnectionProfile> MovedGroup(IReadOnlyList<ConnectionProfile> profiles, string group, Move move)
    {
        var order = SortedGroups(profiles, ConnectionSort.Manual);
        var k = order.IndexOf(group);
        if (k < 0) return [.. profiles];
        int target;
        switch (move)
        {
            case Move.Up: if (k <= 0) return [.. profiles]; target = k - 1; break;
            case Move.Down: if (k >= order.Count - 1) return [.. profiles]; target = k + 1; break;
            case Move.Top: if (k <= 0) return [.. profiles]; target = 0; break;
            default: if (k >= order.Count - 1) return [.. profiles]; target = order.Count - 1; break;
        }
        order.RemoveAt(k);
        order.Insert(target, group);
        return Rebuilt(profiles, order);
    }

    /// 그룹 끌어다 놓기: group 을 target 그룹 바로 앞으로
    public static List<ConnectionProfile> MovedGroup(IReadOnlyList<ConnectionProfile> profiles, string group, string before)
    {
        var order = SortedGroups(profiles, ConnectionSort.Manual);
        var k = order.IndexOf(group);
        if (group == before || k < 0 || !order.Contains(before)) return [.. profiles];
        order.RemoveAt(k);
        order.Insert(order.IndexOf(before), group);
        return Rebuilt(profiles, order);
    }

    /// 그룹 없는 접속 → 그룹 순서대로 (각 묶음 안 순서는 그대로)
    internal static List<ConnectionProfile> Rebuilt(IReadOnlyList<ConnectionProfile> profiles, IReadOnlyList<string> groups) =>
        [.. profiles.Where(p => p.Group.Length == 0), .. groups.SelectMany(g => profiles.Where(p => p.Group == g))];

    /// DB 종류 순서: 새 접속 화면과 같은 MySQL · PostgreSQL · Redshift · SQL Server · Redis
    internal static int TypeOrder(ConnectionProfile p) => Math.Max(0, ConnectionTypes.All.ToList().IndexOf(p.Type));

    private static int IndexOf(IReadOnlyList<ConnectionProfile> profiles, Guid id)
    {
        for (int i = 0; i < profiles.Count; i++) if (profiles[i].Id == id) return i;
        return -1;
    }
}
