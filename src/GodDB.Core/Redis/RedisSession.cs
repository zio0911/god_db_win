using System.Globalization;
using System.Threading.Channels;
using GodDB.Core.Platform;
using static GodDB.Core.L10n;

namespace GodDB.Core.Redis;

/// 키 정보
public sealed record RedisKeyInfo(
    string Key,
    // string, hash, list, set, zset, stream, none …
    string Type,
    // 남은 시간(밀리초). -1 = 만료 없음, -2 = 키 없음
    long TtlMillis,
    // 원소 수 (string 은 바이트 수)
    long? Length,
    // 메모리 사용량 (MEMORY USAGE, 못 구하면 null)
    long? Memory,
    string? Encoding)
{
    public bool Exists => Type != "none" && TtlMillis != -2;
}

/// 이름-값 한 쌍 (hash 필드=값, zset 멤버=점수, INFO 키=값, 스트림 필드=값)
public sealed record RedisPair(string Key, string Value);

/// INFO 한 섹션
public sealed record RedisInfoSection(string Name, List<RedisPair> Items);

public sealed record RedisStreamEntry(string Id, IReadOnlyList<RedisPair> Fields)
{
    public bool Equals(RedisStreamEntry? o) => o is not null && Id == o.Id && Fields.SequenceEqual(o.Fields);
    public override int GetHashCode() => Id.GetHashCode();
}

public sealed record RedisStreamGroup(string Name, long Consumers, long Pending, string LastDeliveredId, long? Lag)
{
    public string Id => Name;
}

public sealed record RedisStreamConsumer(string Name, long Pending, long IdleMillis)
{
    public string Id => Name;
}

public sealed record RedisPendingEntry(string Id, string Consumer, long IdleMillis, long Deliveries);

public sealed record RedisPubSubMessage(
    DateTime Date,
    // message / pmessage / subscribe / unsubscribe / psubscribe / punsubscribe
    string Kind,
    string Channel,
    string? Pattern,
    string Payload)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// SCAN 한 번의 결과. Cursor "0" 이면 끝.
public sealed record RedisScanPage(string Cursor, IReadOnlyList<string> Keys);

/// 키 여러 개를 모은 결과. 더 있으면 Next 가 다음 cursor.
public sealed record RedisKeysPage(IReadOnlyList<string> Keys, string? Next);

/// DB 번호와 키 개수
public sealed record RedisDatabase(int Index, long Keys);

/// 목록용 키 타입·남은 시간
public sealed record RedisKeyMeta(string Type, long TtlMillis);

/// 큰 문자열은 앞부분만: Total 은 전체 바이트 수, 잘렸으면 Truncated
public sealed record RedisStringValue(string Value, long Total, bool Truncated);

/// 채널과 구독자 수
public sealed record RedisChannelCount(string Channel, long Subscribers);

/// 접속 구성
public abstract record RedisTopology
{
    public sealed record Standalone : RedisTopology;

    /// 마스터 노드 주소들 ("host:port")
    public sealed record Cluster(IReadOnlyList<string> Masters) : RedisTopology
    {
        public bool Equals(Cluster? o) => o is not null && Masters.SequenceEqual(o.Masters);
        public override int GetHashCode() => Masters.Count;
    }

    /// 마스터 이름과 접속한 주소
    public sealed record Sentinel(string Master, string Address) : RedisTopology;
}

public enum RedisCommandErrorKind { ReadOnly, UseSubscribeTab, ClusterSingleDB, SentinelMasterMissing, SentinelMasterNotFound }

public sealed class RedisCommandException : Exception
{
    public RedisCommandErrorKind Kind { get; }
    /// SentinelMasterNotFound 의 마스터 이름
    public string? MasterName { get; }

    public RedisCommandException(RedisCommandErrorKind kind, string? masterName = null) : base(Describe(kind, masterName))
    {
        Kind = kind;
        MasterName = masterName;
    }

    private static string Describe(RedisCommandErrorKind kind, string? n) => kind switch
    {
        RedisCommandErrorKind.ReadOnly => Tr("읽기전용 접속이라 이 명령은 실행할 수 없어요. (조회 명령만 허용)"),
        RedisCommandErrorKind.UseSubscribeTab => Tr("구독 명령은 Pub/Sub 탭에서 쓰세요."),
        RedisCommandErrorKind.ClusterSingleDB => Tr("Redis Cluster 는 DB 0 하나만 쓸 수 있어요."),
        RedisCommandErrorKind.SentinelMasterMissing => Tr("Sentinel 마스터 이름을 접속 편집에서 넣어 주세요. (예: mymaster)"),
        _ => Tr("센티널이 마스터 %@ 를 모른다고 해요. 마스터 이름을 확인해 주세요.", n),
    };
}

/// Redis 접속 하나 (필요하면 SSH 터널 포함).
/// Cluster 면 노드마다 연결을 열고 키의 슬롯에 맞는 노드로 보낸다 (MOVED/ASK 자동 처리).
/// Sentinel 이면 센티널에게 현재 마스터를 물어 접속한다.
/// Mac 판은 actor 이고, 여기서는 상태를 잠금으로 보호한다 (명령은 연결마다 파이프라인으로 동시에 보내도 된다).
public sealed class RedisSession : IAsyncDisposable
{
    public ConnectionProfile Profile { get; }
    private readonly object _sync = new();
    private RedisTopology _topology = new RedisTopology.Standalone();
    private int _db;
    /// 연결할 때 받은 비밀번호. 다시 읽어 올 곳(passwordProvider)이 있으면 연결 뒤 비운다 (메모리에 덜 남게, 1.4.1)
    private string? _storedPassword;
    private Func<string?>? _passwordProvider;
    /// 노드 · 구독 · 스트림 연결을 새로 열 때 쓰는 비밀번호
    private string? _password => _storedPassword ?? _passwordProvider?.Invoke();
    /// 자동 새로고침처럼 사람이 하지 않은 명령 — 마지막 사용 시각을 바꾸지 않는다 (Mac @TaskLocal passive)
    private static readonly AsyncLocal<bool> PassiveFlag = new();
    public static bool Passive => PassiveFlag.Value;
    /// 마지막으로 사람이 이 접속을 쓴 시각 (UTC) — 운영 접속 자동 끊기에 쓴다
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
    /// 연결 과정 명령(AUTH · SELECT · INFO · CLUSTER SLOTS · 센티널 조회)의 응답 제한
    private TimeSpan _timeout = TimeSpan.FromSeconds(10);
    /// 노드 주소("host:port") → 연결 / SSH 터널
    private readonly Dictionary<string, RedisConnection> _conns = new();
    private readonly Dictionary<string, SshTunnel> _tunnels = new();
    private readonly SemaphoreSlim _openGate = new(1, 1);
    /// 키 없는 명령을 보낼 노드
    private string _primary = "";
    private List<RedisSlotRange> _slots = [];

    public RedisSession(ConnectionProfile profile) { Profile = profile; }

    public RedisTopology Topology { get { lock (_sync) return _topology; } }
    public int Db { get { lock (_sync) return _db; } }

    public static Task<RedisSession> ConnectAsync(ConnectionProfile profile, string? password, CancellationToken ct = default) =>
        ConnectAsync(profile, password, TimeSpan.FromSeconds(10), ct);

    public static Task<RedisSession> ConnectAsync(ConnectionProfile profile, string? password, TimeSpan timeout,
                                                  CancellationToken ct = default) =>
        ConnectAsync(profile, password, timeout, null, ct);

    /// - passwordProvider: 주면 연결한 뒤 비밀번호를 들고 있지 않고, 새 연결이 필요할 때마다 여기서 읽는다
    public static async Task<RedisSession> ConnectAsync(ConnectionProfile profile, string? password, TimeSpan timeout,
                                                        Func<string?>? passwordProvider, CancellationToken ct = default)
    {
        var s = new RedisSession(profile) { _timeout = timeout };
        try
        {
            await s.OpenAsync(password, ct);
            if (passwordProvider != null)
            {
                s._passwordProvider = passwordProvider;
                s._storedPassword = null;
            }
        }
        catch
        {
            await s.CloseAsync();
            throw;
        }
        return s;
    }

    /// 접속 화면의 "DB" 칸 (비어 있으면 0)
    public static int DatabaseIndex(string text) =>
        int.TryParse(text.Trim(' ', '\t'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : 0;

    public bool IsCluster => Topology is RedisTopology.Cluster;

    /// 화면 표시용: "Cluster · 마스터 3개" / "Sentinel mymaster → 10.0.0.5:6379"
    public string TopologyText => Topology switch
    {
        RedisTopology.Cluster c => Tr("Cluster · 마스터 %@개", c.Masters.Count),
        RedisTopology.Sentinel s => $"Sentinel {s.Master} → {s.Address}",
        _ => "",
    };

    /// Cluster 노드 목록 (화면용)
    public IReadOnlyList<string> MasterNodes
    {
        get
        {
            lock (_sync) return _topology is RedisTopology.Cluster c ? c.Masters : [_primary];
        }
    }

    private async Task OpenAsync(string? password, CancellationToken ct)
    {
        _storedPassword = password;
        switch (Profile.RedisMode)
        {
            case RedisMode.Sentinel:
                {
                    var address = await ResolveSentinelAsync(ct);
                    lock (_sync) { _primary = address; _db = DatabaseIndex(Profile.Database); }
                    await ConnectionAsync(address, ct);
                    lock (_sync) _topology = new RedisTopology.Sentinel(Profile.SentinelMaster, address);
                    break;
                }
            default:
                {
                    var seed = $"{Profile.Host}:{Profile.Port}";
                    // Cluster 인지 확인하기 전에는 DB 0 으로 붙는다 (Cluster 는 SELECT 를 거부)
                    int wantedDB = Profile.RedisMode == RedisMode.Cluster ? 0 : DatabaseIndex(Profile.Database);
                    lock (_sync) { _primary = seed; _db = 0; }
                    var conn = await ConnectionAsync(seed, ct);
                    // 단일로 설정했어도 서버가 Cluster 면 자동으로 Cluster 로 동작한다
                    bool clusterEnabled = Profile.RedisMode == RedisMode.Cluster;
                    if (!clusterEnabled)
                    {
                        try { clusterEnabled = ((await conn.SendAsync(["INFO", "cluster"], _timeout, ct)).AsString ?? "").Contains("cluster_enabled:1"); }
                        catch (RedisServerException) { }  // INFO 를 막은 서버는 단일로 본다. 시간 초과 · 연결 끊김은 실패
                    }
                    if (clusterEnabled) await RefreshSlotsAsync(ct);
                    else if (wantedDB != 0)
                    {
                        await conn.SendAsync(["SELECT", wantedDB.ToString(CultureInfo.InvariantCulture)], _timeout, ct);
                        lock (_sync) _db = wantedDB;
                    }
                    break;
                }
        }
    }

    /// 센티널들에게 차례로 물어 현재 마스터 주소를 얻는다
    private async Task<string> ResolveSentinelAsync(CancellationToken ct)
    {
        var name = Profile.SentinelMaster.Trim(' ', '\t');
        if (name.Length == 0) throw new RedisCommandException(RedisCommandErrorKind.SentinelMasterMissing);
        Exception lastError = new RedisNotConnectedException();
        foreach (var (host, port) in RedisCluster.SentinelAddresses(Profile.Host, Profile.Port))
        {
            var addr = $"{host}:{port}";
            // 센티널은 보통 비밀번호가 없다. 필요하면 같은 비밀번호로 다시 시도
            foreach (var pw in new[] { null, _password })
            {
                try
                {
                    var c = await RedisConnection.ConnectAsync(await OptionsAsync(addr, pw, 0, ct), ct);
                    try
                    {
                        var v = await c.SendAsync(["SENTINEL", "get-master-addr-by-name", name], _timeout, ct);
                        var a = v.AsArray;
                        if (a == null || a.Count != 2 || a[0].AsString is not string h || a[1].AsInt is not long p)
                            throw new RedisCommandException(RedisCommandErrorKind.SentinelMasterNotFound, name);
                        return $"{h}:{p}";
                    }
                    finally { await c.CloseAsync(); }
                }
                catch (RedisServerException e) when (e.Code is "NOAUTH" or "WRONGPASS")
                {
                    lastError = e;
                    continue;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    lastError = e;
                    break;
                }
            }
        }
        throw lastError;
    }

    /// CLUSTER SLOTS 로 슬롯 표를 다시 읽는다
    private async Task RefreshSlotsAsync(CancellationToken ct)
    {
        string primary;
        lock (_sync) primary = _primary;
        var seedHost = RedisCluster.Split(primary)?.Host ?? Profile.Host;
        var v = await (await ConnectionAsync(primary, ct)).SendAsync(["CLUSTER", "SLOTS"], _timeout, ct);
        var parsed = RedisCluster.ParseSlots(v, seedHost);
        if (parsed.Count == 0) return;
        var masters = new List<string>();
        foreach (var r in parsed) if (!masters.Contains(r.Master)) masters.Add(r.Master);
        lock (_sync)
        {
            _slots = parsed;
            _topology = new RedisTopology.Cluster(masters);
            if (!masters.Contains(_primary) && masters.Count > 0) _primary = masters[0];
        }
    }

    /// 노드 주소에 붙을 연결 설정 (SSH 면 노드마다 터널)
    private async Task<RedisConnection.Options> OptionsAsync(string address, string? password, int database, CancellationToken ct)
    {
        var (host, port) = RedisCluster.Split(address) ?? throw new RedisNotConnectedException();
        string h = host;
        int p = port;
        if (Profile.Ssh is { } ssh)
        {
            SshTunnel? t;
            lock (_sync) _tunnels.TryGetValue(address, out t);
            if (t == null)
            {
                t = new SshTunnel(ssh, host, port);
                try { await t.StartAsync(ct); }
                catch { t.Dispose(); throw; }
                lock (_sync) _tunnels[address] = t;
            }
            p = t.LocalPort;
            h = "127.0.0.1";
        }
        return new RedisConnection.Options
        {
            Host = h, Port = p,
            ServerName = Profile.Ssh == null && !HostResolver.IsIPAddress(host) ? host : null,
            UseTls = Profile.UseTLS, Username = Profile.Username, Password = password, Database = database,
            Timeout = _timeout,
            Tls = Drivers.TlsPolicy.Of(Profile, CertificateHost(host, Profile.Host)),
        };
    }

    /// 인증서 이름 확인에 쓸 호스트: 노드 주소가 이름이면 그 이름, IP 면(Cluster · Sentinel 이 알려 준 노드) 접속 정보의 이름
    public static string CertificateHost(string node, string configured)
    {
        if (!HostResolver.IsIPAddress(node)) return node;
        var first = RedisCluster.SentinelAddresses(configured, 0).FirstOrDefault().Host ?? configured;
        return HostResolver.IsIPAddress(first) || first.Length == 0 ? node : first;
    }

    /// 첫 연결의 TLS 상태 (상태창 — F9-3)
    public Drivers.TlsStatus TlsStatus
    {
        get
        {
            lock (_sync)
                return _conns.TryGetValue(_primary, out var c) ? c.TlsStatus
                    : Profile.UseTLS ? Drivers.TlsStatus.Unchecked : Drivers.TlsStatus.Off;
        }
    }

    /// 사람이 하지 않은 명령(자동 새로고침)을 실행한다 — 마지막 사용 시각을 바꾸지 않는다
    public static async Task RunPassiveAsync(Func<Task> body)
    {
        var old = PassiveFlag.Value;
        PassiveFlag.Value = true;
        try { await body(); }
        finally { PassiveFlag.Value = old; }
    }

    /// 노드 연결 (없거나 끊겼으면 연다)
    private async Task<RedisConnection> ConnectionAsync(string address, CancellationToken ct)
    {
        lock (_sync) if (_conns.TryGetValue(address, out var c) && c.IsOpen) return c;
        await _openGate.WaitAsync(ct);
        try
        {
            lock (_sync) if (_conns.TryGetValue(address, out var c) && c.IsOpen) return c;
            var conn = await RedisConnection.ConnectAsync(await OptionsAsync(address, _password, IsCluster ? 0 : Db, ct), ct);
            lock (_sync) _conns[address] = conn;
            return conn;
        }
        finally { _openGate.Release(); }
    }

    /// 키가 있는 노드
    private string AddressForKey(string? key)
    {
        lock (_sync)
        {
            if (_topology is not RedisTopology.Cluster || key == null || _slots.Count == 0) return _primary;
            int slot = RedisCluster.Slot(key);
            return _slots.FirstOrDefault(r => r.Start <= slot && slot <= r.End)?.Master ?? _primary;
        }
    }

    private Task<RedisConnection> ConnectionForKeyAsync(string? key, CancellationToken ct) => ConnectionAsync(AddressForKey(key), ct);

    /// 명령을 맞는 노드로 보낸다. MOVED/ASK 면 따라간다. raw 면 서버 오류도 값으로 돌려준다.
    private async Task<RespValue> ExecAsync(IReadOnlyList<string> args, bool raw = false, CancellationToken ct = default)
    {
        if (!Passive) LastActivity = DateTime.UtcNow;
        var addr = AddressForKey(RedisCluster.RoutingKey(args));
        bool asking = false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var conn = await ConnectionAsync(addr, ct);
            if (asking) { try { await conn.RawAsync(["ASKING"], ct); } catch (RedisServerException) { } }
            var v = await conn.RawAsync(args, ct);
            if (v.Kind == RespKind.Error && RedisCluster.Redirect(v.Text!) is { } r)
            {
                switch (r)
                {
                    case RedisRedirect.Moved m:
                        addr = m.Address;
                        asking = false;
                        // 슬롯이 옮겨졌다 → 표를 새로 읽는다 (단일로 붙었는데 Cluster 였던 경우도 여기서 전환)
                        try { await RefreshSlotsFromAsync(m.Address, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
                        break;
                    case RedisRedirect.Ask a:
                        addr = a.Address;
                        asking = true;
                        break;
                }
                continue;
            }
            if (!raw && v.Kind == RespKind.Error) throw new RedisServerException(v.Text!);
            return v;
        }
        throw new RedisServerException("ERR too many cluster redirects");
    }

    private async Task RefreshSlotsFromAsync(string address, CancellationToken ct)
    {
        lock (_sync) if (_topology is not RedisTopology.Cluster) { _primary = address; _db = 0; }
        await RefreshSlotsAsync(ct);
    }

    public async Task CloseAsync()
    {
        List<RedisConnection> conns;
        List<SshTunnel> tunnels;
        lock (_sync)
        {
            conns = [.. _conns.Values];
            _conns.Clear();
            tunnels = [.. _tunnels.Values];
            _tunnels.Clear();
        }
        foreach (var c in conns) await c.CloseAsync();
        foreach (var t in tunnels) t.Dispose();
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    public bool IsConnected
    {
        get { lock (_sync) return _conns.TryGetValue(_primary, out var c) && c.IsOpen; }
    }

    /// 앱이 내부적으로 쓰는 명령 (읽기전용이면 쓰기 명령 차단)
    public Task<RespValue> CommandAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        if (Profile.ReadOnly && !RedisCommands.IsReadOnly(args)) throw new RedisCommandException(RedisCommandErrorKind.ReadOnly);
        return ExecAsync(args, ct: ct);
    }

    /// 콘솔에서 입력한 명령. 서버 오류도 값으로 돌려준다.
    public async Task<RespValue> ConsoleAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        if (args.Count == 0) return RespValue.Simple("");
        var name = args[0].ToUpperInvariant();
        if (RedisCommands.SubscribeCommands.Contains(name)) throw new RedisCommandException(RedisCommandErrorKind.UseSubscribeTab);
        if (Profile.ReadOnly && !RedisCommands.IsReadOnly(args)) throw new RedisCommandException(RedisCommandErrorKind.ReadOnly);
        if (name == "SELECT" && IsCluster) throw new RedisCommandException(RedisCommandErrorKind.ClusterSingleDB);
        var v = await ExecAsync(args, raw: true, ct: ct);
        if (name == "SELECT" && v.Kind == RespKind.Simple && args.Count > 1 && RedisText.ParseInt(args[1]) is long n)
            lock (_sync) _db = (int)n;
        return v;
    }

    public async Task SelectAsync(int index, CancellationToken ct = default)
    {
        if (IsCluster)
        {
            if (index != 0) throw new RedisCommandException(RedisCommandErrorKind.ClusterSingleDB);
            return;
        }
        await ExecAsync(["SELECT", index.ToString(CultureInfo.InvariantCulture)], ct: ct);
        lock (_sync) _db = index;
    }

    public async Task<string> PingAsync(CancellationToken ct = default) => (await ExecAsync(["PING"], ct: ct)).AsString ?? "";

    // MARK: - 서버

    /// INFO 를 섹션 목록으로. node 를 주면 그 노드의 INFO (Cluster)
    public async Task<List<RedisInfoSection>> InfoAsync(string? section = null, string? node = null, CancellationToken ct = default)
    {
        string[] args = section != null ? ["INFO", section] : ["INFO"];
        string text = node != null
            ? (await (await ConnectionAsync(node, ct)).SendAsync(args, ct)).AsString ?? ""
            : (await ExecAsync(args, ct: ct)).AsString ?? "";
        return ParseInfo(text);
    }

    public static List<RedisInfoSection> ParseInfo(string text)
    {
        var output = new List<RedisInfoSection>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
                output.Add(new RedisInfoSection(line[1..].Trim(' ', '\t'), []));
            else if (line.IndexOf(':') is var i and >= 0)
            {
                if (output.Count == 0) output.Add(new RedisInfoSection("", []));
                output[^1].Items.Add(new RedisPair(line[..i], line[(i + 1)..]));
            }
        }
        return output;
    }

    /// 쓸 수 있는 DB 번호들과 키 개수 (INFO keyspace + CONFIG GET databases)
    public async Task<List<RedisDatabase>> DatabasesAsync(CancellationToken ct = default)
    {
        // Cluster 는 DB 0 하나뿐, 키 수는 모든 마스터 합계
        if (IsCluster) return [new RedisDatabase(0, await DbSizeAsync(ct))];
        int count = 16;
        try
        {
            var v = (await ExecAsync(["CONFIG", "GET", "databases"], ct: ct)).AsArray;
            if (v != null && v.Count == 2 && v[1].AsInt is long n) count = (int)n;
        }
        catch (Exception e) when (e is RedisServerException or RedisCommandException) { }
        var keys = new Dictionary<int, long>();
        foreach (var section in await InfoAsync("keyspace", ct: ct))
        {
            foreach (var item in section.Items.Where(x => x.Key.StartsWith("db", StringComparison.Ordinal)))
            {
                if (RedisText.ParseInt(item.Key[2..]) is long i)
                {
                    var kv = item.Value.Split(',').FirstOrDefault(p => p.StartsWith("keys=", StringComparison.Ordinal));
                    if (kv != null && RedisText.ParseInt(kv[5..]) is long n) keys[(int)i] = n;
                }
            }
        }
        count = Math.Max(Math.Max(count, (keys.Count == 0 ? 0 : keys.Keys.Max()) + 1), Db + 1);
        return Enumerable.Range(0, count).Select(i => new RedisDatabase(i, keys.GetValueOrDefault(i))).ToList();
    }

    public async Task<long> DbSizeAsync(CancellationToken ct = default)
    {
        if (IsCluster)
        {
            long total = 0;
            foreach (var m in MasterNodes) total += (await (await ConnectionAsync(m, ct)).SendAsync(["DBSIZE"], ct)).AsInt ?? 0;
            return total;
        }
        return (await ExecAsync(["DBSIZE"], ct: ct)).AsInt ?? 0;
    }

    // MARK: - 키

    /// SCAN 한 번. cursor "0" 이 오면 끝.
    /// Cluster 면 마스터를 차례로 훑는다. 이때 cursor 는 "노드번호|노드cursor" 형태.
    public async Task<RedisScanPage> ScanAsync(string cursor, string pattern, int count = 500, string? type = null, CancellationToken ct = default)
    {
        var args = new List<string> { "SCAN", "", "MATCH", pattern.Length == 0 ? "*" : pattern, "COUNT", count.ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrEmpty(type)) { args.Add("TYPE"); args.Add(type); }
        if (!IsCluster)
        {
            args[1] = cursor;
            var v = (await ExecAsync(args, ct: ct)).AsArray ?? [];
            if (v.Count != 2) return new RedisScanPage("0", []);
            return new RedisScanPage(v[0].AsString ?? "0", Strings(v[1]));
        }
        var masters = MasterNodes;
        int node = 0;
        string nodeCursor = "0";
        if (cursor != "0")
        {
            var parts = cursor.Split('|', 2);
            node = RedisText.ParseInt(parts[0]) is long n ? (int)n : 0;
            nodeCursor = parts.Length > 1 ? parts[1] : "0";
        }
        if (node >= masters.Count) return new RedisScanPage("0", []);
        args[1] = nodeCursor;
        var r = (await (await ConnectionAsync(masters[node], ct)).SendAsync(args, ct)).AsArray ?? [];
        var keys = r.Count == 2 ? Strings(r[1]) : [];
        var next = r.Count == 2 ? (r[0].AsString ?? "0") : "0";
        if (next != "0") return new RedisScanPage($"{node}|{next}", keys);
        return node + 1 < masters.Count ? new RedisScanPage($"{node + 1}|0", keys) : new RedisScanPage("0", keys);
    }

    private static List<string> Strings(RespValue v) => (v.AsArray ?? []).Select(x => x.AsString).OfType<string>().ToList();

    /// 키를 limit 개까지 모은다 (SCAN 반복). 더 있으면 다음 cursor.
    public async Task<RedisKeysPage> ScanKeysAsync(string pattern, string? type = null, string cursor = "0", int limit = 1000, CancellationToken ct = default)
    {
        var cur = cursor;
        var keys = new List<string>();
        var seen = new HashSet<string>();
        int rounds = 0;
        do
        {
            var r = await ScanAsync(cur, pattern, type: type, ct: ct);
            foreach (var k in r.Keys) if (seen.Add(k)) keys.Add(k);
            cur = r.Cursor;
            rounds += 1;
        } while (cur != "0" && keys.Count < limit && rounds < 2000);
        return new RedisKeysPage(keys, cur == "0" ? null : cur);
    }

    /// 여러 키의 타입과 남은 시간(밀리초)을 파이프라인으로 한 번에
    public async Task<Dictionary<string, RedisKeyMeta>> KeyMetaAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        // 노드별로 묶어서 각 노드에 파이프라인
        var byNode = new Dictionary<string, List<string>>();
        foreach (var k in keys)
        {
            var a = AddressForKey(k);
            if (!byNode.TryGetValue(a, out var list)) byNode[a] = list = [];
            list.Add(k);
        }
        var pending = new List<(string Key, Task<RespValue> Type, Task<RespValue> Ttl)>();
        foreach (var (addr, ks) in byNode)
        {
            var conn = await ConnectionAsync(addr, ct);
            foreach (var k in ks) pending.Add((k, conn.SendAsync(["TYPE", k], ct), conn.SendAsync(["PTTL", k], ct)));
        }
        var output = new Dictionary<string, RedisKeyMeta>();
        foreach (var (k, t, p) in pending)
            output[k] = new RedisKeyMeta((await t).AsString ?? "none", (await p).AsInt ?? -2);
        return output;
    }

    public async Task<RedisKeyInfo> KeyInfoAsync(string key, CancellationToken ct = default)
    {
        var conn = await ConnectionForKeyAsync(key, ct);
        var type = (await conn.SendAsync(["TYPE", key], ct)).AsString ?? "none";
        var ttl = (await conn.SendAsync(["PTTL", key], ct)).AsInt ?? -2;
        string[]? lenCmd = type switch
        {
            "string" => ["STRLEN", key],
            "hash" => ["HLEN", key],
            "list" => ["LLEN", key],
            "set" => ["SCARD", key],
            "zset" => ["ZCARD", key],
            "stream" => ["XLEN", key],
            _ => null,
        };
        long? length = null;
        if (lenCmd != null) length = await TryAsync(async () => (await conn.SendAsync(lenCmd, ct)).AsInt);
        var memory = await TryAsync(async () => (await conn.SendAsync(["MEMORY", "USAGE", key], ct)).AsInt);
        var encoding = await TryAsync(async () => (await conn.SendAsync(["OBJECT", "ENCODING", key], ct)).AsString);
        return new RedisKeyInfo(key, type, ttl, length, memory, encoding);
    }

    /// Swift try? 처럼 서버 오류면 null
    private static async Task<T?> TryAsync<T>(Func<Task<T?>> f)
    {
        try { return await f(); }
        catch (RedisServerException) { return default; }
    }

    // MARK: - 타입별 값

    public async Task<string?> StringAsync(string key, CancellationToken ct = default) => (await ExecAsync(["GET", key], ct: ct)).AsString;

    /// 큰 문자열은 앞부분만 (메모리 보호). Total 은 전체 바이트 수, 잘렸으면 Truncated.
    public async Task<RedisStringValue> StringPrefixAsync(string key, int maxBytes = 1_048_576, CancellationToken ct = default)
    {
        var total = (await ExecAsync(["STRLEN", key], ct: ct)).AsInt ?? 0;
        if (total <= maxBytes) return new RedisStringValue((await ExecAsync(["GET", key], ct: ct)).AsString ?? "", total, false);
        var part = (await ExecAsync(["GETRANGE", key, "0", (maxBytes - 1).ToString(CultureInfo.InvariantCulture)], ct: ct)).AsString ?? "";
        return new RedisStringValue(part, total, true);
    }

    /// HSCAN 으로 limit 개까지
    public async Task<List<RedisPair>> HashAsync(string key, string pattern = "*", int limit = 1000, CancellationToken ct = default)
    {
        var cur = "0";
        var output = new List<RedisPair>();
        do
        {
            var v = (await ExecAsync(["HSCAN", key, cur, "MATCH", pattern.Length == 0 ? "*" : pattern, "COUNT", "500"], ct: ct)).AsArray ?? [];
            if (v.Count != 2) break;
            cur = v[0].AsString ?? "0";
            output.AddRange(Pairs(v[1]));
        } while (cur != "0" && output.Count < limit);
        return output;
    }

    private static List<RedisPair> Pairs(RespValue v)
    {
        var items = v.AsArray ?? [];
        var output = new List<RedisPair>(items.Count / 2);
        for (int i = 0; i + 1 < items.Count; i += 2) output.Add(new RedisPair(items[i].AsString ?? "", items[i + 1].AsString ?? ""));
        return output;
    }

    public async Task<List<string>> ListAsync(string key, int start, int count, CancellationToken ct = default) =>
        Strings(await ExecAsync(["LRANGE", key, start.ToString(CultureInfo.InvariantCulture), (start + count - 1).ToString(CultureInfo.InvariantCulture)], ct: ct));

    public async Task<List<string>> SetAsync(string key, string pattern = "*", int limit = 1000, CancellationToken ct = default)
    {
        var cur = "0";
        var output = new List<string>();
        do
        {
            var v = (await ExecAsync(["SSCAN", key, cur, "MATCH", pattern.Length == 0 ? "*" : pattern, "COUNT", "500"], ct: ct)).AsArray ?? [];
            if (v.Count != 2) break;
            cur = v[0].AsString ?? "0";
            output.AddRange(Strings(v[1]));
        } while (cur != "0" && output.Count < limit);
        return output;
    }

    /// 점수 순 (reverse 면 높은 점수부터). (멤버, 점수)
    public async Task<List<RedisPair>> ZsetAsync(string key, int start, int count, bool reverse = false, CancellationToken ct = default)
    {
        var args = new List<string> { "ZRANGE", key, start.ToString(CultureInfo.InvariantCulture), (start + count - 1).ToString(CultureInfo.InvariantCulture) };
        if (reverse) args.Add("REV");
        args.Add("WITHSCORES");
        return Pairs(await ExecAsync(args, ct: ct));
    }

    // MARK: - Streams

    internal static List<RedisStreamEntry> Entries(RespValue v)
    {
        var output = new List<RedisStreamEntry>();
        foreach (var e in v.AsArray ?? [])
        {
            var parts = e.AsArray;
            if (parts == null || parts.Count != 2 || parts[0].AsString is not string id) continue;
            output.Add(new RedisStreamEntry(id, Pairs(parts[1])));
        }
        return output;
    }

    /// 최신 항목부터 (before 가 있으면 그 ID 보다 이전)
    public async Task<List<RedisStreamEntry>> StreamLatestAsync(string key, int count, string? before = null, CancellationToken ct = default)
    {
        var end = before != null ? "(" + before : "+";
        return Entries(await ExecAsync(["XREVRANGE", key, end, "-", "COUNT", count.ToString(CultureInfo.InvariantCulture)], ct: ct));
    }

    public async Task<List<RedisStreamEntry>> StreamRangeAsync(string key, string start, string end, int count, CancellationToken ct = default) =>
        Entries(await ExecAsync(["XRANGE", key, start.Length == 0 ? "-" : start, end.Length == 0 ? "+" : end, "COUNT", count.ToString(CultureInfo.InvariantCulture)], ct: ct));

    /// XINFO STREAM 의 주요 값
    public async Task<List<RedisPair>> StreamInfoAsync(string key, CancellationToken ct = default)
    {
        var v = (await ExecAsync(["XINFO", "STREAM", key], ct: ct)).AsArray ?? [];
        var output = new List<RedisPair>();
        for (int i = 0; i + 1 < v.Count; i += 2)
        {
            var name = v[i].AsString ?? "";
            string value;
            if (v[i + 1].AsArray is { } arr)
                // first-entry / last-entry 는 [id, [f, v…]]
                value = arr.Count == 2 && arr[0].AsString is string id ? id : $"{arr.Count} items";
            else
                value = v[i + 1].AsString ?? "(nil)";
            output.Add(new RedisPair(name, value));
        }
        return output;
    }

    internal static Dictionary<string, RespValue> Dict(RespValue v)
    {
        var a = v.AsArray ?? [];
        var d = new Dictionary<string, RespValue>();
        for (int i = 0; i + 1 < a.Count; i += 2) if (a[i].AsString is string k) d[k] = a[i + 1];
        return d;
    }

    public async Task<List<RedisStreamGroup>> StreamGroupsAsync(string key, CancellationToken ct = default) =>
        ((await ExecAsync(["XINFO", "GROUPS", key], ct: ct)).AsArray ?? []).Select(g =>
        {
            var d = Dict(g);
            return new RedisStreamGroup(d.GetValueOrDefault("name")?.AsString ?? "", d.GetValueOrDefault("consumers")?.AsInt ?? 0,
                d.GetValueOrDefault("pending")?.AsInt ?? 0, d.GetValueOrDefault("last-delivered-id")?.AsString ?? "",
                d.GetValueOrDefault("lag")?.AsInt);
        }).ToList();

    public async Task<List<RedisStreamConsumer>> StreamConsumersAsync(string key, string group, CancellationToken ct = default) =>
        ((await ExecAsync(["XINFO", "CONSUMERS", key, group], ct: ct)).AsArray ?? []).Select(g =>
        {
            var d = Dict(g);
            return new RedisStreamConsumer(d.GetValueOrDefault("name")?.AsString ?? "", d.GetValueOrDefault("pending")?.AsInt ?? 0,
                d.GetValueOrDefault("idle")?.AsInt ?? 0);
        }).ToList();

    public async Task<List<RedisPendingEntry>> StreamPendingAsync(string key, string group, int count = 100, CancellationToken ct = default)
    {
        var output = new List<RedisPendingEntry>();
        foreach (var e in (await ExecAsync(["XPENDING", key, group, "-", "+", count.ToString(CultureInfo.InvariantCulture)], ct: ct)).AsArray ?? [])
        {
            var a = e.AsArray;
            if (a == null || a.Count < 4) continue;
            output.Add(new RedisPendingEntry(a[0].AsString ?? "", a[1].AsString ?? "", a[2].AsInt ?? 0, a[3].AsInt ?? 0));
        }
        return output;
    }

    // MARK: - Pub/Sub

    public async Task<long> PublishAsync(string channel, string message, CancellationToken ct = default) =>
        (await CommandAsync(["PUBLISH", channel, message], ct)).AsInt ?? 0;

    /// 활성 채널과 구독자 수
    public async Task<List<RedisChannelCount>> ActiveChannelsAsync(string pattern = "*", CancellationToken ct = default)
    {
        var names = Strings(await ExecAsync(["PUBSUB", "CHANNELS", pattern.Length == 0 ? "*" : pattern], ct: ct));
        if (names.Count == 0) return [];
        var v = (await ExecAsync(["PUBSUB", "NUMSUB", .. names.Take(500)], ct: ct)).AsArray ?? [];
        var output = new List<RedisChannelCount>();
        for (int i = 0; i + 1 < v.Count; i += 2) output.Add(new RedisChannelCount(v[i].AsString ?? "", v[i + 1].AsInt ?? 0));
        return output.OrderBy(c => c.Channel, StringComparer.Ordinal).ToList();
    }

    private async Task<RedisConnection> ExtraConnectionAsync(string address, CancellationToken ct)
    {
        int db;
        lock (_sync) db = _topology is RedisTopology.Cluster ? 0 : _db;
        return await RedisConnection.ConnectAsync(await OptionsAsync(address, _password, db, ct), ct);
    }

    /// 구독 전용 연결을 따로 연다.
    public async Task<RedisSubscriber> SubscriberAsync(CancellationToken ct = default)
    {
        // Cluster 에서도 PUBLISH 는 모든 노드에 전달되므로 아무 노드에 구독하면 된다
        string primary;
        lock (_sync) primary = _primary;
        return new RedisSubscriber(await ExtraConnectionAsync(primary, ct));
    }

    /// 스트림 실시간 보기용 연결 (XREAD BLOCK 이 일반 명령을 막지 않도록 따로 연다).
    /// 받은 항목은 Entries 로 흘러나온다. 그만 보려면 Dispose.
    public async Task<RedisStreamTail> StreamTailAsync(string key, string from = "$", CancellationToken ct = default)
    {
        var conn = await ExtraConnectionAsync(AddressForKey(key), ct);
        return new RedisStreamTail(conn, key, from);
    }
}

/// 스트림 실시간 보기 (XREAD BLOCK 반복)
public sealed class RedisStreamTail : IAsyncDisposable, IDisposable
{
    private readonly RedisConnection _conn;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<RedisStreamEntry> _channel = Channel.CreateUnbounded<RedisStreamEntry>(new() { SingleReader = true });

    /// 새 항목 (연결이 끊기면 오류로 끝나고, Dispose 하면 그냥 끝난다)
    public ChannelReader<RedisStreamEntry> Entries => _channel.Reader;

    internal RedisStreamTail(RedisConnection conn, string key, string from)
    {
        _conn = conn;
        _ = Task.Run(() => Loop(key, from));
    }

    private async Task Loop(string key, string id)
    {
        var last = id;
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var v = await _conn.SendAsync(["XREAD", "COUNT", "100", "BLOCK", "2000", "STREAMS", key, last], _cts.Token);
                if (v.AsArray is not { } streams) continue;
                foreach (var s in streams)
                {
                    var parts = s.AsArray;
                    if (parts == null || parts.Count != 2) continue;
                    foreach (var e in RedisSession.Entries(parts[1]))
                    {
                        last = e.Id;
                        _channel.Writer.TryWrite(e);
                    }
                }
            }
        }
        catch (Exception e) { error = _cts.IsCancellationRequested ? null : e; }
        _channel.Writer.TryComplete(error);
        await _conn.CloseAsync();
    }

    public void Dispose()
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        _ = _conn.CloseAsync();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// 구독 연결
public sealed class RedisSubscriber : IAsyncDisposable
{
    private readonly RedisConnection _connection;
    private readonly Channel<RedisPubSubMessage> _channel = Channel.CreateUnbounded<RedisPubSubMessage>(new() { SingleReader = true });

    /// 받은 메시지 (구독/해지 확인 포함). 연결이 끊기면 끝난다.
    public ChannelReader<RedisPubSubMessage> Messages => _channel.Reader;

    internal RedisSubscriber(RedisConnection connection)
    {
        _connection = connection;
        connection.SetPush(v => { if (Parse(v) is { } m) _channel.Writer.TryWrite(m); }, () => _channel.Writer.TryComplete());
    }

    public static RedisPubSubMessage? Parse(RespValue v)
    {
        var a = v.AsArray;
        if (a == null || a.Count == 0 || a[0].AsString is not string kind) return null;
        switch (kind)
        {
            case "message" when a.Count == 3:
                return new(DateTime.Now, kind, a[1].AsString ?? "", null, a[2].AsString ?? "");
            case "pmessage" when a.Count == 4:
                return new(DateTime.Now, kind, a[2].AsString ?? "", a[1].AsString, a[3].AsString ?? "");
            case "subscribe" or "unsubscribe" or "psubscribe" or "punsubscribe":
                return new(DateTime.Now, kind, a.Count > 1 ? (a[1].AsString ?? "") : "", null, a.Count > 2 ? (a[2].AsString ?? "") : "");
            default:
                return null;
        }
    }

    public void Subscribe(IReadOnlyList<string> channels) { if (channels.Count > 0) _connection.Fire(["SUBSCRIBE", .. channels]); }
    public void PSubscribe(IReadOnlyList<string> patterns) { if (patterns.Count > 0) _connection.Fire(["PSUBSCRIBE", .. patterns]); }
    public void Unsubscribe(IReadOnlyList<string> channels) => _connection.Fire(["UNSUBSCRIBE", .. channels]);
    public void PUnsubscribe(IReadOnlyList<string> patterns) => _connection.Fire(["PUNSUBSCRIBE", .. patterns]);

    public Task CloseAsync() => _connection.CloseAsync();

    public ValueTask DisposeAsync() => new(CloseAsync());
}
