using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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

/// 쿼리 히스토리: %APPDATA%\GodDB\history.jsonl (한 줄에 한 건, 최근 maxEntries 건만 유지)
public sealed class QueryHistory(string file, int maxEntries = 5000)
{
    private static readonly Lazy<QueryHistory> SharedLazy = new(() => new QueryHistory(Path.Combine(AppPaths.DataDirectory, "history.jsonl")));
    public static QueryHistory Shared => SharedLazy.Value;

    public string File { get; } = file;
    public int MaxEntries { get; } = maxEntries;
    private readonly Lock _lock = new();
    private List<HistoryEntry>? _cache;

    internal static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new Iso8601SecondsDateTimeConverter() },
    };

    /// 최신이 먼저
    public List<HistoryEntry> All()
    {
        lock (_lock) return [.. LoadLocked()];
    }

    private List<HistoryEntry> LoadLocked()
    {
        if (_cache != null) return _cache;
        string text;
        try { text = System.IO.File.ReadAllText(File, Encoding.UTF8); }
        catch { return _cache = []; }
        var list = new List<HistoryEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (JsonSerializer.Deserialize<HistoryEntry>(line, Json) is { } e) list.Add(e); }
            catch { }
        }
        list.Reverse();
        return _cache = list.Take(MaxEntries).ToList();
    }

    public void Append(HistoryEntry entry)
    {
        lock (_lock)
        {
            var list = LoadLocked();
            list.Insert(0, entry);
            string line;
            try { line = JsonSerializer.Serialize(entry, Json); } catch { return; }
            try
            {
                if (Path.GetDirectoryName(File) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                if (list.Count > MaxEntries)
                {
                    // 오래된 것을 잘라 파일을 다시 쓴다 (넘칠 때만, 한 번에 10% 여유)
                    list = list.Take(MaxEntries * 9 / 10).ToList();
                    var body = string.Join("\n", Enumerable.Reverse(list).Select(e => JsonSerializer.Serialize(e, Json))) + "\n";
                    var tmp = File + ".tmp";
                    System.IO.File.WriteAllText(tmp, body, new UTF8Encoding(false));
                    System.IO.File.Move(tmp, File, overwrite: true);
                }
                else
                {
                    System.IO.File.AppendAllText(File, line + "\n", new UTF8Encoding(false));
                }
            }
            catch { }
            _cache = list;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            try { System.IO.File.Delete(File); } catch { }
            _cache = [];
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
public sealed record SavedTab(
    [property: JsonPropertyName("profileID")] Guid ProfileId,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("selected")] bool Selected);

public sealed class TabStore(string file)
{
    private static readonly Lazy<TabStore> SharedLazy = new(() => new TabStore(Path.Combine(AppPaths.DataDirectory, "tabs.json")));
    public static TabStore Shared => SharedLazy.Value;

    public string File { get; } = file;

    public List<SavedTab> Load()
    {
        try { return JsonSerializer.Deserialize<List<SavedTab>>(System.IO.File.ReadAllText(File), QueryHistory.Json) ?? []; }
        catch { return []; }
    }

    public void Save(IEnumerable<SavedTab> tabs)
    {
        try
        {
            if (Path.GetDirectoryName(File) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(tabs.ToList(), QueryHistory.Json), new UTF8Encoding(false));
            System.IO.File.Move(tmp, File, overwrite: true);
        }
        catch { }
    }
}
