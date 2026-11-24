using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using GodDB.Core.Platform;
using GodDB.Core.Profiles;

namespace GodDB.Core.Audit;

/// 실행한 SQL 한 건 (JSON 칸 이름은 Mac 판 history.jsonl 과 같다)
public sealed record HistoryEntry
{
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("date")] public DateTime Date { get; init; } = DateTime.Now;
    [JsonPropertyName("profileID")] public Guid ProfileId { get; init; }
    [JsonPropertyName("profileName")] public string ProfileName { get; init; } = "";
    [JsonPropertyName("database")] public string Database { get; init; } = "";
    [JsonPropertyName("sql")] public string Sql { get; init; } = "";
    [JsonPropertyName("elapsedMs")] public int ElapsedMs { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    /// 결과 행 수 또는 영향 받은 행 요약 (없으면 null)
    [JsonPropertyName("summary")] public string? Summary { get; init; }

    public HistoryEntry() { }

    public HistoryEntry(Guid profileId, string profileName, string database, string sql, int elapsedMs, bool ok,
                        string? summary = null, DateTime? date = null)
    {
        ProfileId = profileId;
        ProfileName = profileName;
        Database = database;
        Sql = sql;
        ElapsedMs = elapsedMs;
        Ok = ok;
        Summary = summary;
        Date = date ?? DateTime.Now;
    }
}

/// 쿼리 히스토리: %APPDATA%\GodDB\history.jsonl
///
/// 암호화된 줄 단위 파일 (<see cref="LocalDataCrypto"/> 의 `GDBENC1` 형식, Mac 판과 같음): 첫 줄 `GDBENC1`, 그다음 한 줄에 한 건
/// (HistoryEntry JSON 하나를 봉투 하나로). 새 기록은 줄 하나만 덧붙인다. 최근 maxEntries 건과 보관 기간(keepDays)만 유지.
///
/// - 예전 평문 파일은 처음 읽을 때 암호화해 원자적으로 바꿔 쓴다 (평문 파일은 남지 않음).
/// - 키를 못 얻으면 메모리에서만 기록하고 디스크에는 아무것도 쓰지 않는다 (평문도, 덮어쓰기도 없음).
///   나중에 키가 되면 디스크 내용과 합쳐 저장한다.
public sealed class QueryHistory
{
    private static readonly Lazy<QueryHistory> SharedLazy = new(() => new QueryHistory(Path.Combine(AppPaths.DataDirectory, "history.jsonl")));
    public static QueryHistory Shared => SharedLazy.Value;

    /// 설정 키: 보관 일수 (0 = 영구)
    public const string KeepDaysKey = "GodDB.history.keepDays";
    /// 설정 화면의 선택지 (0 = 영구)
    public static readonly IReadOnlyList<int> KeepDaysChoices = [7, 30, 90, 0];
    public const int DefaultKeepDays = 30;

    /// 설정 > 일반 > 히스토리 보관 기간 (기본 30일, 0 = 영구)
    public static int KeepDays
    {
        get => Prefs.Get(KeepDaysKey, DefaultKeepDays);
        set => Prefs.Set(KeepDaysKey, Math.Max(0, value));
    }

    public string File { get; }
    public int MaxEntries { get; }
    private readonly LocalDataCrypto? _crypto;
    private LocalDataCrypto Crypto => _crypto ?? LocalDataCrypto.Shared;
    private readonly Func<int> _keepDays;
    private readonly Func<DateTime> _now;
    private readonly Lock _lock = new();
    /// 최신이 먼저
    private List<HistoryEntry>? _cache;
    /// 키가 없어 디스크에 쓰지 못한 기록 (최신이 먼저). 키가 되면 디스크 내용과 합친다.
    private List<HistoryEntry> _pending = [];
    /// 디스크를 읽거나 쓰지 못한 상태 (키 없음) — 메모리에서만 기록
    private bool _degraded;
    /// 보관 기간 정리를 마지막으로 한 때 (하루 한 번)
    private DateTime? _lastPrune;

    /// crypto 를 비우면 <see cref="LocalDataCrypto.Shared"/> (앱이 켜질 때 OS 키 저장소로 정해진다)
    public QueryHistory(string file, int maxEntries = 5000, LocalDataCrypto? crypto = null,
                        Func<int>? keepDays = null, Func<DateTime>? now = null)
    {
        File = file;
        MaxEntries = maxEntries;
        _crypto = crypto;
        _keepDays = keepDays ?? (() => KeepDays);
        _now = now ?? (() => DateTime.Now);
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new Iso8601SecondsDateTimeConverter() },
    };

    internal static byte[] Encode(HistoryEntry e) => JsonSerializer.SerializeToUtf8Bytes(e, Json);

    /// 최신이 먼저
    public List<HistoryEntry> All()
    {
        lock (_lock)
        {
            var list = LoadLocked();
            if (PruneIfDueLocked(ref list))
            {
                _cache = list;
                RewriteLocked(list);
            }
            return [.. list];
        }
    }

    /// 키가 없어 메모리에서만 기록 중인지
    public bool IsMemoryOnly
    {
        get { lock (_lock) return _degraded; }
    }

    // MARK: 읽기

    private sealed class DiskRead
    {
        /// 최신이 먼저
        public List<HistoryEntry> Entries = [];
        public bool LegacyPlaintext;
        public bool KeyUnavailable;
        public int DamagedLines;
    }

    private DiskRead ReadDisk()
    {
        var r = new DiskRead();
        byte[] data;
        try
        {
            if (!System.IO.File.Exists(File)) return r;
            data = System.IO.File.ReadAllBytes(File);
        }
        catch { return r; }
        if (data.Length == 0) return r;
        var list = new List<HistoryEntry>();
        if (LocalDataCrypto.IsEncrypted(data))
        {
            foreach (var line in LocalDataCrypto.BodyLines(data))
            {
                try
                {
                    var plain = Crypto.OpenLine(line);
                    HistoryEntry? e = null;
                    try { e = JsonSerializer.Deserialize<HistoryEntry>(plain, Json); } catch { }
                    if (e != null) list.Add(e); else r.DamagedLines++;
                }
                catch (LocalDataCryptoException ex) when (ex.Kind == LocalDataCryptoErrorKind.KeyUnavailable)
                {
                    r.KeyUnavailable = true;
                    return r;
                }
                catch { r.DamagedLines++; }
            }
        }
        else
        {
            r.LegacyPlaintext = true;
            foreach (var line in Encoding.UTF8.GetString(data).Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                try { if (JsonSerializer.Deserialize<HistoryEntry>(line, Json) is { } e) list.Add(e); }
                catch { }
            }
        }
        list.Reverse();
        r.Entries = list;
        return r;
    }

    private List<HistoryEntry> LoadLocked()
    {
        if (_cache != null && !_degraded) return _cache;
        // 메모리 상태인데 키가 아직 없으면 그대로
        if (_cache != null && _degraded && !Crypto.IsKeyAvailable) return _cache;

        var disk = ReadDisk();
        if (disk.KeyUnavailable)
        {
            LocalDataCrypto.Warn("history key unavailable, keeping history in memory only");
            _degraded = true;
            _cache = [.. _pending];
            return _cache;
        }
        if (disk.DamagedLines > 0)
        {
            LocalDataCrypto.Warn($"skipped {disk.DamagedLines} unreadable history lines");
            // 한 줄도 못 읽었으면(키가 바뀜 등) 다시 쓰기 전에 옆으로 치워 둔다
            if (disk.Entries.Count == 0) LocalDataCrypto.MoveAside(File);
        }
        var list = _pending.Concat(disk.Entries).ToList();
        var overflow = list.Count > MaxEntries;
        if (overflow) list = list.Take(MaxEntries).ToList();
        var pruned = PruneIfDueLocked(ref list);
        var mustRewrite = disk.LegacyPlaintext || _pending.Count > 0 || pruned || overflow || disk.DamagedLines > 0;
        _cache = list;
        _degraded = false;
        if (mustRewrite)
        {
            RewriteLocked(list);
            if (disk.LegacyPlaintext && _degraded)
                LocalDataCrypto.Warn("history key unavailable, plaintext history left as is (not migrated)");
        }
        return list;
    }

    // MARK: 보관 기간

    /// 보관 기간이 지난 것을 뺀다 (하루에 한 번). 뺀 게 있으면 true.
    private bool PruneIfDueLocked(ref List<HistoryEntry> list, bool force = false)
    {
        var t = _now();
        if (!force && _lastPrune is { } last && Math.Abs((t - last).TotalSeconds) < 86_400) return false;
        _lastPrune = t;
        var trimmed = Retained(list, _keepDays(), t);
        if (trimmed.Count == list.Count) return false;
        list = trimmed;
        return true;
    }

    /// keepDays 일보다 오래된 기록을 뺀다 (0 이하 = 영구)
    public static List<HistoryEntry> Retained(IEnumerable<HistoryEntry> list, int keepDays, DateTime now)
    {
        if (keepDays <= 0) return list.ToList();
        var cutoff = now.ToUniversalTime().AddDays(-keepDays);
        return list.Where(e => e.Date.ToUniversalTime() >= cutoff).ToList();
    }

    /// 설정에서 보관 기간을 바꿨을 때 바로 정리
    public void ApplyRetentionNow()
    {
        lock (_lock)
        {
            var list = LoadLocked();
            if (PruneIfDueLocked(ref list, force: true))
            {
                _cache = list;
                RewriteLocked(list);
            }
        }
    }

    // MARK: 쓰기

    /// 파일 전체를 다시 쓴다 (원자적). 키가 없으면 쓰지 않고 메모리 상태로.
    private void RewriteLocked(List<HistoryEntry> list)
    {
        try
        {
            if (list.Count == 0)
            {
                try { System.IO.File.Delete(File); } catch { }
            }
            else
            {
                using var body = new MemoryStream();
                body.Write(LocalDataCrypto.Header);
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    body.Write(Encoding.ASCII.GetBytes(Crypto.SealLine(Encode(list[i]))));
                    body.WriteByte((byte)'\n');
                }
                LocalDataCrypto.WriteAtomically(body.ToArray(), File);
            }
            _pending = [];
            _degraded = false;
        }
        catch (Exception e)
        {
            LocalDataCrypto.Warn($"history not saved ({e.Message}), keeping it in memory");
            // pending(아직 디스크에 없는 새 기록)은 그대로 두고, 키가 되면 디스크 내용과 다시 합친다
            _degraded = true;
            _lastPrune = null;
        }
    }

    public void Append(HistoryEntry entry)
    {
        lock (_lock)
        {
            var list = LoadLocked();
            list = [entry, .. list];
            var rewrite = PruneIfDueLocked(ref list);
            if (list.Count > MaxEntries)
            {
                // 오래된 것을 잘라 파일을 다시 쓴다 (넘칠 때만, 한 번에 10% 여유)
                list = list.Take(MaxEntries * 9 / 10).ToList();
                rewrite = true;
            }
            _cache = list;
            // 디스크에 들어가기 전까지는 pending 에 둔다 (쓰기에 성공하면 비운다)
            _pending.Insert(0, entry);
            if (_pending.Count > MaxEntries) _pending.RemoveRange(MaxEntries, _pending.Count - MaxEntries);
            if (_degraded) return;  // 키가 없어 디스크를 못 읽은 상태: 덮어쓰지 않고 메모리에만
            if (rewrite || !System.IO.File.Exists(File))
            {
                RewriteLocked(list);
                return;
            }
            try
            {
                var line = Crypto.SealLine(Encode(entry));
                FileStream fs;
                try { fs = new FileStream(File, FileMode.Open, FileAccess.Write, FileShare.Read); }
                catch (IOException)
                {
                    RewriteLocked(list);
                    return;
                }
                using (fs)
                {
                    fs.Seek(0, SeekOrigin.End);
                    fs.Write(Encoding.ASCII.GetBytes(line + "\n"));
                }
                _pending = [];
            }
            catch (Exception e)
            {
                LocalDataCrypto.Warn($"history entry not saved ({e.Message}), keeping it in memory");
                _degraded = true;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            try { System.IO.File.Delete(File); } catch { }
            _cache = [];
            _pending = [];
            _degraded = false;
        }
    }

    /// 검색어(공백으로 나눈 모든 단어 포함, 대소문자 무시)와 접속으로 거른다
    public static List<HistoryEntry> Filter(IEnumerable<HistoryEntry> list, string query, Guid? profileId)
    {
        var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return list.Where(e =>
            (profileId == null || e.ProfileId == profileId)
            && words.All(w => e.Sql.ToLowerInvariant().Contains(w, StringComparison.Ordinal)
                           || e.Database.ToLowerInvariant().Contains(w, StringComparison.Ordinal)))
            .ToList();
    }
}

/// 쿼리 탭 자동 저장: %APPDATA%\GodDB\tabs.json
/// (`GDBENC1` 통째 암호화 — 봉투 하나에 JSON 전체. 예전 평문은 처음 읽을 때 옮긴다. 나이로 지우지는 않는다)
public sealed record SavedTab(
    [property: JsonPropertyName("profileID")] Guid ProfileId,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("selected")] bool Selected);

public sealed class TabStore(string file, LocalDataCrypto? crypto = null)
{
    private static readonly Lazy<TabStore> SharedLazy = new(() => new TabStore(Path.Combine(AppPaths.DataDirectory, "tabs.json")));
    public static TabStore Shared => SharedLazy.Value;

    public string File { get; } = file;
    private LocalDataCrypto Crypto => crypto ?? LocalDataCrypto.Shared;

    public List<SavedTab> Load()
    {
        if (Crypto.ReadDocumentData(File) is not { } data) return [];
        try { return JsonSerializer.Deserialize<List<SavedTab>>(data, QueryHistory.Json) ?? []; }
        catch { return []; }
    }

    /// 암호화해서 저장. 키가 없으면 쓰지 않는다 (평문으로 쓰지 않음).
    public void Save(IEnumerable<SavedTab> tabs)
    {
        byte[] data;
        try { data = JsonSerializer.SerializeToUtf8Bytes(tabs.ToList(), QueryHistory.Json); } catch { return; }
        try { Crypto.WriteDocument(data, File); }
        catch (Exception e) { LocalDataCrypto.Warn($"tabs not saved ({e.Message})"); }
    }
}
