using System.Globalization;
using System.Text;

namespace GodDB.Core.Redis;

/// 마스터 노드 하나가 맡는 슬롯 구간
public sealed record RedisSlotRange(int Start, int End, string Master, IReadOnlyList<string> Replicas)
{
    public bool Equals(RedisSlotRange? o) =>
        o is not null && Start == o.Start && End == o.End && Master == o.Master && Replicas.SequenceEqual(o.Replicas);
    public override int GetHashCode() => HashCode.Combine(Start, End, Master);
}

/// 서버 오류가 리다이렉트인지: MOVED 3999 127.0.0.1:6381 / ASK 3999 127.0.0.1:6381
public abstract record RedisRedirect(int Slot, string Address)
{
    public sealed record Moved(int Slot, string Address) : RedisRedirect(Slot, Address);
    public sealed record Ask(int Slot, string Address) : RedisRedirect(Slot, Address);
}

/// Redis Cluster 계산 (슬롯, 키 위치, 슬롯 표, 리다이렉트)
public static class RedisCluster
{
    public const int SlotCount = 16384;

    /// CRC16-XMODEM (Redis Cluster 명세)
    private static readonly ushort[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var crc = (ushort)(i << 8);
        for (int k = 0; k < 8; k++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        return crc;
    }).ToArray();

    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (var b in bytes) crc = (ushort)((crc << 8) ^ Table[(byte)(crc >> 8) ^ b]);
        return crc;
    }

    /// 키의 슬롯. {태그} 가 있으면 태그만으로 계산 (같은 태그는 같은 노드)
    public static int Slot(string key)
    {
        ReadOnlySpan<byte> bytes = Encoding.UTF8.GetBytes(key);
        int open = bytes.IndexOf((byte)'{');
        if (open >= 0)
        {
            int close = bytes[(open + 1)..].IndexOf((byte)'}');
            if (close > 0) bytes = bytes.Slice(open + 1, close);   // close 는 open+1 기준 위치 → 0 이면 빈 태그
        }
        return Crc16(bytes) % SlotCount;
    }

    // 키가 없는 명령
    private static readonly HashSet<string> Keyless =
    [
        "PING", "ECHO", "INFO", "DBSIZE", "TIME", "SELECT", "CONFIG", "CLIENT", "CLUSTER",
        "COMMAND", "SLOWLOG", "MEMORY STATS", "LASTSAVE", "ROLE", "SCAN", "KEYS", "RANDOMKEY",
        "FLUSHDB", "FLUSHALL", "PUBLISH", "PUBSUB", "SUBSCRIBE", "PSUBSCRIBE", "AUTH", "HELLO",
        "MULTI", "EXEC", "DISCARD", "SCRIPT", "FUNCTION", "ACL", "LATENCY", "MONITOR", "LOLWUT",
        "SHUTDOWN", "SAVE", "BGSAVE", "BGREWRITEAOF", "DEBUG", "SWAPDB", "WAIT", "READONLY", "READWRITE",
    ];

    /// 명령에서 노드를 고를 기준이 되는 키 (없으면 null → 아무 노드)
    public static string? RoutingKey(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return null;
        var name = args[0].ToUpperInvariant();
        if (Keyless.Contains(name)) return null;
        switch (name)
        {
            case "MEMORY" or "OBJECT":   // MEMORY USAGE key / OBJECT ENCODING key
            case "XINFO":                // XINFO STREAM key / GROUPS key / CONSUMERS key group
            case "XGROUP":               // XGROUP CREATE key …
            case "BITOP":
                return args.Count > 2 ? args[2] : null;
            case "XREAD" or "XREADGROUP":
                {
                    int i = -1;
                    for (int k = 0; k < args.Count; k++) if (args[k].ToUpperInvariant() == "STREAMS") { i = k; break; }
                    return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
                }
            case "EVAL" or "EVALSHA" or "EVAL_RO" or "EVALSHA_RO" or "FCALL" or "FCALL_RO":
                return args.Count > 3 && RedisText.ParseInt(args[2]) is > 0 ? args[3] : null;
            default:
                return args.Count > 1 ? args[1] : null;
        }
    }

    /// CLUSTER SLOTS 응답 → 슬롯 표. ip 가 비어 있으면 seedHost 를 쓴다.
    public static List<RedisSlotRange> ParseSlots(RespValue v, string seedHost)
    {
        string? Addr(RespValue node)
        {
            var a = node.AsArray;
            if (a == null || a.Count < 2 || a[1].AsInt is not long port) return null;
            var host = a[0].AsString ?? "";
            if (host.Length == 0 || host == "?") host = seedHost;
            return host + ":" + port.ToString(CultureInfo.InvariantCulture);
        }
        var result = new List<RedisSlotRange>();
        foreach (var r in v.AsArray ?? [])
        {
            var a = r.AsArray;
            if (a == null || a.Count < 3 || a[0].AsInt is not long s || a[1].AsInt is not long e || Addr(a[2]) is not string m) continue;
            result.Add(new RedisSlotRange((int)s, (int)e, m, a.Skip(3).Select(Addr).OfType<string>().ToList()));
        }
        return result.OrderBy(x => x.Start).ToList();
    }

    public static RedisRedirect? Redirect(string message)
    {
        var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || RedisText.ParseInt(parts[1]) is not long slot) return null;
        return parts[0] switch
        {
            "MOVED" => new RedisRedirect.Moved((int)slot, parts[2]),
            "ASK" => new RedisRedirect.Ask((int)slot, parts[2]),
            _ => null,
        };
    }

    /// "host:port" → (host, port). IPv6 는 마지막 : 기준
    public static (string Host, int Port)? Split(string address)
    {
        int i = address.LastIndexOf(':');
        if (i < 0 || !int.TryParse(address.AsSpan(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)) return null;
        var host = address[..i];
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        return (host, port);
    }

    /// 센티널 주소 목록: 호스트 칸에 "h1:26379, h2:26379" 또는 "h1" (기본 포트)
    public static List<(string Host, int Port)> SentinelAddresses(string hostField, int defaultPort)
    {
        var result = new List<(string, int)>();
        foreach (var s in hostField.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (s.Contains(':') && Split(s) is { } hp) result.Add(hp);
            else result.Add((s, defaultPort));
        }
        return result;
    }
}
