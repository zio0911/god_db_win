using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GodDB.Core.Platform;

namespace GodDB.Core.Ai;

/// 다이어그램 · 어시스턴트 AI 대화 하나
public sealed class DiagramConversation
{
    public sealed record Message(
        [property: JsonPropertyName("role"), JsonPropertyOrder(1)] string Role,
        [property: JsonPropertyName("text"), JsonPropertyOrder(2)] string Text,
        [property: JsonPropertyName("failed"), JsonPropertyOrder(0)] bool Failed = false);

    // 키 이름 · 순서는 Mac 판(JSONEncoder sortedKeys)과 같게
    [JsonPropertyName("created"), JsonPropertyOrder(0)] public DateTime Created { get; set; }
    [JsonPropertyName("id"), JsonPropertyOrder(1)] public Guid Id { get; set; }
    [JsonPropertyName("messages"), JsonPropertyOrder(2)] public List<Message> Messages { get; set; } = [];
    [JsonPropertyName("updated"), JsonPropertyOrder(3)] public DateTime Updated { get; set; }

    public DiagramConversation() : this(null) { }

    public DiagramConversation(Guid? id = null, DateTime? created = null, IEnumerable<Message>? messages = null)
    {
        Id = id ?? Guid.NewGuid();
        Created = created ?? DateTime.UtcNow;
        Updated = Created;
        Messages = messages?.ToList() ?? [];
    }

    /// 목록에 보일 제목 (첫 질문 첫 줄)
    [JsonIgnore]
    public string Title
    {
        get
        {
            var q = Messages.FirstOrDefault(m => m.Role == "user")?.Text ?? "";
            var line = q.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return line.Length > 40 ? AiText.Prefix(line, 40) + "…" : line;
        }
    }
}

/// 다이어그램 AI 대화 기록. 다이어그램(접속 · DB · 테이블 묶음)마다 파일 하나.
///   %APPDATA%\GodDB\diagram-chats\&lt;scope 해시&gt;.json  (Mac: ~/Library/Application Support/GodDB/diagram-chats)
/// 오래 걸리는 분석을 다시 하지 않도록 남겨 두고, keepDays 가 지난 대화는 지운다. 파일은 본인만 읽고 쓸 수 있게 한다.
public sealed class DiagramChatStore
{
    private static readonly Lazy<DiagramChatStore> SharedStore = new(() => new DiagramChatStore(AppPaths.ChatsDirectory));
    public static DiagramChatStore Shared => SharedStore.Value;

    public string Directory { get; }
    public int KeepDays { get; }
    private readonly Lock _lock = new();
    private readonly LocalDataCrypto? _crypto;
    private LocalDataCrypto Crypto => _crypto ?? LocalDataCrypto.Shared;

    /// 파일마다 `GDBENC1` 통째 암호화 (질문 · 답에 SQL 이 들어 있으므로). 예전 평문은 처음 읽을 때 옮긴다.
    /// crypto 를 비우면 <see cref="LocalDataCrypto.Shared"/>.
    public DiagramChatStore(string directory, int keepDays = 90, LocalDataCrypto? crypto = null)
    {
        Directory = directory;
        KeepDays = keepDays;
        _crypto = crypto;
    }

    /// 다이어그램을 구분하는 값 (접속 id, DB, 테이블 이름들)
    public static string Scope(Guid? profileId, string database, IEnumerable<string> tables) =>
        $"{profileId?.ToString().ToUpperInvariant() ?? "-"}|{database}|{string.Join(",", tables.Select(t => t.ToLowerInvariant()).Order(StringComparer.Ordinal))}";

    internal string FilePath(string scope)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(scope));
        return Path.Combine(Directory, Convert.ToHexStringLower(hash.AsSpan(0, 12)) + ".json");
    }

    /// 최근에 쓴 것부터. 오래된 대화는 여기서 지운다.
    public List<DiagramConversation> Load(string scope, DateTime? now = null)
    {
        lock (_lock)
        {
            if (Read(scope) is not { } all) return [];
            var cutoff = (now ?? DateTime.UtcNow).AddDays(-KeepDays);
            var kept = all.Where(c => c.Updated >= cutoff && c.Messages.Count > 0).ToList();
            if (kept.Count != all.Count) Write(scope, kept);
            return kept.OrderByDescending(c => c.Updated).ToList();
        }
    }

    public void Save(DiagramConversation conversation, string scope)
    {
        if (conversation.Messages.Count == 0) return;
        lock (_lock)
        {
            if (Read(scope) is not { } all) return;
            var i = all.FindIndex(c => c.Id == conversation.Id);
            if (i >= 0) all[i] = conversation; else all.Add(conversation);
            Write(scope, all);
        }
    }

    public void Delete(Guid id, string scope)
    {
        lock (_lock)
        {
            if (Read(scope) is not { } all) return;
            Write(scope, all.Where(c => c.Id != id).ToList());
        }
    }

    public void DeleteAll(string scope)
    {
        lock (_lock) { try { File.Delete(FilePath(scope)); } catch { } }
    }

    /// 모든 다이어그램에서 오래된 대화 정리 (앱 시작 시)
    public void PruneAll(DateTime? now = null)
    {
        lock (_lock)
        {
            var cutoff = (now ?? DateTime.UtcNow).AddDays(-KeepDays);
            string[] files;
            try { files = System.IO.Directory.GetFiles(Directory, "*.json"); } catch { return; }
            foreach (var f in files)
            {
                // 예전 평문 파일은 여기서 암호화된다 (ReadDocument 가 옮김)
                if (Crypto.ReadDocumentData(f) is not { } data) continue;
                List<DiagramConversation>? list;
                try { list = JsonSerializer.Deserialize<List<DiagramConversation>>(data, Json); } catch { continue; }
                if (list == null) continue;
                var kept = list.Where(c => c.Updated >= cutoff).ToList();
                if (kept.Count == 0) { try { File.Delete(f); } catch { } }
                else if (kept.Count != list.Count) WriteFile(f, kept);
            }
        }
    }

    /// 대화 목록. 파일이 없으면 [], 키가 없어 못 읽으면 null (그때는 파일을 건드리지 않는다).
    private List<DiagramConversation>? Read(string scope)
    {
        var r = Crypto.ReadDocument(FilePath(scope));
        switch (r.Status)
        {
            case LocalReadStatus.Missing or LocalReadStatus.Damaged: return [];
            case LocalReadStatus.KeyUnavailable: return null;
        }
        try { return JsonSerializer.Deserialize<List<DiagramConversation>>(r.Data!, Json) ?? []; }
        catch { return []; }
    }

    private void Write(string scope, List<DiagramConversation> list)
    {
        var path = FilePath(scope);
        if (list.Count == 0) { try { File.Delete(path); } catch { } return; }
        WriteFile(path, list);
    }

    /// 암호화해서 원자적으로 쓴다 (본인만 접근, Mac: 권한 600). 키가 없으면 쓰지 않는다.
    private void WriteFile(string path, List<DiagramConversation> list)
    {
        try { Crypto.WriteDocument(JsonSerializer.SerializeToUtf8Bytes(list, Json), path); }
        catch (Exception e) { LocalDataCrypto.Warn($"AI chat not saved ({e.Message})"); /* 기록 저장 실패는 대화를 막지 않는다 */ }
    }

    /// 테스트용: 예전 평문 파일을 만들 때 쓰는 직렬화
    internal static byte[] Encode(List<DiagramConversation> list) => JsonSerializer.SerializeToUtf8Bytes(list, Json);

    internal static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new Iso8601Converter() },
        // Mac 처럼 한글을 그대로 둔다 (\uXXXX 로 바꾸지 않음)
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// Mac JSONEncoder .iso8601 과 같은 모양 (초 단위, UTC "Z")
    private sealed class Iso8601Converter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime;

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}
