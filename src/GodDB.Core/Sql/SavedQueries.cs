using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodDB.Core.Dialects;
using GodDB.Core.Platform;
using GodDB.Core.Profiles;

namespace GodDB.Core.Sql;

/// 저장 쿼리 (Ctrl+Alt+S 저장 · Ctrl+Shift+K 목록): 자주 쓰는 쿼리를 이름 · 폴더로 저장하고, `:이름` 빈칸을 채워 실행한다.
/// (Mac SavedQueries.swift)
public sealed record SavedQuery
{
    /// 어느 접속에서 보이는지
    public enum QueryScope
    {
        /// 모든 접속
        All,
        /// 저장한 접속(ProfileId)에서만
        Profile,
    }

    public Guid Id { get; init; }
    public string Name { get; init; }
    /// 폴더 (없으면 빈 문자열)
    public string Folder { get; init; }
    public string Sql { get; init; }
    public QueryScope Scope { get; init; }
    /// Scope == Profile 일 때 그 접속
    public Guid? ProfileId { get; init; }
    /// 빈칸별 마지막 입력값 (이름 → 값)
    public IReadOnlyDictionary<string, string> LastValues { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }

    public SavedQuery(string name, string sql, string folder = "", QueryScope scope = QueryScope.All, Guid? profileId = null,
                      IReadOnlyDictionary<string, string>? lastValues = null, DateTimeOffset? createdAt = null,
                      DateTimeOffset? updatedAt = null, DateTimeOffset? lastUsedAt = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Name = name;
        Folder = folder;
        Sql = sql;
        Scope = scope;
        ProfileId = scope == QueryScope.Profile ? profileId : null;
        LastValues = lastValues ?? new Dictionary<string, string>();
        CreatedAt = createdAt ?? DateTimeOffset.Now;
        UpdatedAt = updatedAt ?? CreatedAt;
        LastUsedAt = lastUsedAt;
    }

    public bool Equals(SavedQuery? o) =>
        o is not null && Id == o.Id && Name == o.Name && Folder == o.Folder && Sql == o.Sql && Scope == o.Scope
        && ProfileId == o.ProfileId && CreatedAt == o.CreatedAt && UpdatedAt == o.UpdatedAt && LastUsedAt == o.LastUsedAt
        && LastValues.Count == o.LastValues.Count && LastValues.All(kv => o.LastValues.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Sql);

    /// 이 접속을 보고 있을 때 보이는지 (`이 접속만` 은 그 접속에서만)
    public bool IsVisible(Guid? profileId) => Scope == QueryScope.All || (profileId != null && ProfileId == profileId);

    /// 최근 사용 순 정렬 기준 (쓴 적 없으면 만든 시각)
    public DateTimeOffset Recency => LastUsedAt ?? CreatedAt;

    /// 기본 이름: 첫 줄(공백 아닌) 앞 40자
    public static string DefaultName(string sql)
    {
        var first = sql.Split(['\n', '\r']).Select(l => l.Trim(' ', '\t')).FirstOrDefault(l => l.Length > 0) ?? "";
        var info = new StringInfo(first);
        return info.LengthInTextElements > 40 ? info.SubstringByTextElements(0, 40) : first;
    }
}

// MARK: - 빈칸 (파라미터)

/// `:이름` 빈칸 찾기 · 값 변환 · 채워 넣기 (Mac SQLParameters)
public static partial class SqlParameters
{
    /// 빈칸 하나가 나온 자리 (원문 위치, `:` 포함)
    public readonly record struct Occurrence(string Name, int Start, int Length);

    /// 입력 창 한 줄의 값. IsNull: NULL 체크, Raw: "그대로 넣기" 체크 (SQL 조각으로 그대로)
    public sealed record Input(string Value = "", bool IsNull = false, bool Raw = false);

    /// 빈칸 이름 (처음 나온 순서, 중복 제거)
    public static List<string> Names(string sql, DatabaseKind kind)
    {
        var seen = new HashSet<string>();
        return Occurrences(sql, kind).Select(o => o.Name).Where(seen.Add).ToList();
    }

    private static bool IsNameStart(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
    private static bool IsNameChar(char c) => IsNameStart(c) || c is >= '0' and <= '9';

    /// 빈칸이 나온 모든 자리. 문자열 리터럴(' " `) · 주석(-- /* */ 와 MySQL 의 #) · PostgreSQL `$$` 인용 ·
    /// `::type` 캐스트 · `:=` 안/뒤의 `:` 는 빈칸이 아니다.
    public static List<Occurrence> Occurrences(string sql, DatabaseKind kind)
    {
        var n = sql.Length;
        char? At(int i) => i < n ? sql[i] : null;
        var result = new List<Occurrence>();
        var i = 0;
        while (i < n)
        {
            var c = sql[i];
            // 문자열 · 따옴표 이름: 같은 따옴표가 나올 때까지 ('' 처럼 겹친 따옴표는 닫고 다시 여는 것과 같다)
            if (c is '\'' or '"' or '`')
            {
                var j = i + 1;
                while (j < n && sql[j] != c)
                {
                    if (kind == DatabaseKind.Mysql && sql[j] == '\\' && c != '`') j++;
                    j++;
                }
                i = j + 1;
                continue;
            }
            // 줄 주석: -- 와 MySQL 의 #
            if ((c == '-' && At(i + 1) == '-') || (kind == DatabaseKind.Mysql && c == '#'))
            {
                while (i < n && sql[i] != '\n') i++;
                continue;
            }
            // 블록 주석
            if (c == '/' && At(i + 1) == '*')
            {
                i += 2;
                while (i < n && !(sql[i] == '*' && At(i + 1) == '/')) i++;
                i += 2;
                continue;
            }
            // PostgreSQL 달러 인용 ($$ … $$, $tag$ … $tag$)
            if (kind == DatabaseKind.Postgres && c == '$' && SqlText.DollarTagEnd(sql, i) is int tagEnd)
            {
                var tag = sql[i..(tagEnd + 1)];
                var close = sql.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
                i = close >= 0 ? close + tag.Length : n;
                continue;
            }
            if (c == ':')
            {
                // ::type 캐스트, := 대입
                if (At(i + 1) == ':')
                {
                    i += 2;
                    while (i < n && sql[i] == ':') i++;
                    continue;
                }
                if (At(i + 1) == '=') { i += 2; continue; }
                if (At(i + 1) is char s && IsNameStart(s))
                {
                    var j = i + 1;
                    while (At(j) is char nc && IsNameChar(nc)) j++;
                    result.Add(new Occurrence(sql[(i + 1)..j], i, j - i));
                    i = j;
                    continue;
                }
            }
            i++;
        }
        return result;
    }

    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial Regex NumberRegex();

    /// 숫자 꼴(`^-?\d+(\.\d+)?$`)인지
    public static bool IsNumber(string s) => NumberRegex().IsMatch(s) && !s.EndsWith('\n');

    /// 값 하나 변환: 숫자 꼴은 그대로, 그 밖은 다이얼렉트 문자열 인용
    public static string Literal(string value, ISqlDialect dialect)
    {
        var t = value.Trim(' ', '\t');
        return IsNumber(t) ? t : dialect.QuoteString(value);
    }

    /// 입력 한 줄을 SQL 조각으로: NULL → `NULL`, 그대로 넣기 → 입력 그대로,
    /// 쉼표가 있으면 목록으로 펼쳐 각각 변환 (`1,2,3` → `1, 2, 3`, `a,b` → `'a', 'b'`)
    public static string Render(Input input, ISqlDialect dialect)
    {
        if (input.IsNull) return "NULL";
        if (input.Raw) return input.Value;
        if (input.Value.Contains(','))
            return string.Join(", ", input.Value.Split(',').Select(p => Literal(p.Trim(' ', '\t'), dialect)));
        return Literal(input.Value, dialect);
    }

    /// 빈칸을 SQL 조각으로 바꾼다. fragments 에 없는 빈칸은 그대로 둔다.
    public static string Substitute(string sql, IReadOnlyDictionary<string, string> fragments, DatabaseKind kind)
    {
        var sb = new StringBuilder(sql);
        foreach (var occ in Enumerable.Reverse(Occurrences(sql, kind)))
        {
            if (!fragments.TryGetValue(occ.Name, out var f)) continue;
            sb.Remove(occ.Start, occ.Length).Insert(occ.Start, f);
        }
        return sb.ToString();
    }

    /// 입력값으로 채운 SQL
    public static string Fill(string sql, IReadOnlyDictionary<string, Input> inputs, ISqlDialect dialect) =>
        Substitute(sql, inputs.ToDictionary(kv => kv.Key, kv => Render(kv.Value, dialect)), dialect.Kind);
}

// MARK: - 목록 (saved-queries.json)

/// 저장 쿼리 목록. 형식: `{"version":1,"queries":[…]}`
public sealed class SavedQueryLibrary
{
    public int Version { get; set; } = 1;
    public List<SavedQuery> Queries { get; set; } = [];

    public SavedQueryLibrary() { }
    public SavedQueryLibrary(IEnumerable<SavedQuery> queries) { Queries = [.. queries]; }

    public override bool Equals(object? obj) =>
        obj is SavedQueryLibrary o && Version == o.Version && Queries.SequenceEqual(o.Queries);
    public override int GetHashCode() => Queries.Count;

    /// 폴더 묶음 하나 (Folder 가 빈 문자열이면 폴더 없음)
    public sealed record Group(string Folder, IReadOnlyList<SavedQuery> Queries);

    public SavedQuery? this[Guid id] => Queries.FirstOrDefault(q => q.Id == id);

    /// Mac localizedStandardCompare 와 비슷한 비교 (대소문자 무시 · 숫자는 값 순서)
    internal static int StandardCompare(string a, string b) =>
        string.Compare(a, b, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    /// 쓰인 폴더 이름 (자동완성용, 가나다순)
    public List<string> Folders
    {
        get
        {
            var list = Queries.Select(q => q.Folder).Where(f => f.Length > 0).Distinct().ToList();
            list.Sort(StandardCompare);
            return list;
        }
    }

    /// 이 접속에서 보이는 것 중 검색어(이름 · SQL · 폴더, 대소문자 무시)에 맞는 것
    public List<SavedQuery> Search(string query, Guid? profileId)
    {
        var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Queries.Where(q =>
        {
            if (!q.IsVisible(profileId)) return false;
            var hay = (q.Name + "\n" + q.Sql + "\n" + q.Folder).ToLowerInvariant();
            return words.All(w => hay.Contains(w, StringComparison.Ordinal));
        }).ToList();
    }

    /// 폴더별로 묶고 최근 사용 순으로 정렬한다. 묶음도 그 안에서 가장 최근에 쓴 것 순.
    public static List<Group> Grouped(IEnumerable<SavedQuery> list)
    {
        var sorted = list.ToList();
        sorted.Sort((a, b) => a.Recency != b.Recency ? b.Recency.CompareTo(a.Recency) : StandardCompare(a.Name, b.Name));
        var order = new List<string>();
        var byFolder = new Dictionary<string, List<SavedQuery>>();
        foreach (var q in sorted)
        {
            if (!byFolder.TryGetValue(q.Folder, out var l)) { order.Add(q.Folder); byFolder[q.Folder] = l = []; }
            l.Add(q);
        }
        return order.Select(f => new Group(f, byFolder[f])).ToList();
    }

    public void Add(SavedQuery query) => Queries.Add(query);

    public void Update(Guid id, Func<SavedQuery, SavedQuery> change, DateTimeOffset? now = null)
    {
        var i = Queries.FindIndex(q => q.Id == id);
        if (i < 0) return;
        Queries[i] = change(Queries[i]) with { UpdatedAt = now ?? DateTimeOffset.Now };
    }

    public void Rename(Guid id, string name, DateTimeOffset? now = null)
    {
        var n = name.Trim();
        if (n.Length == 0) return;
        Update(id, q => q with { Name = n }, now);
    }

    public void SetFolder(Guid id, string folder, DateTimeOffset? now = null) =>
        Update(id, q => q with { Folder = folder.Trim() }, now);

    /// 복제: 이름 뒤에 " 사본", 입력값 · 범위는 그대로, 사용 기록은 없이
    public SavedQuery? Duplicate(Guid id, string suffix, DateTimeOffset? now = null)
    {
        if (this[id] is not { } q) return null;
        var copy = new SavedQuery(q.Name + suffix, q.Sql, q.Folder, q.Scope, q.ProfileId,
                                  new Dictionary<string, string>(q.LastValues), now ?? DateTimeOffset.Now);
        Queries.Add(copy);
        return copy;
    }

    public void Delete(Guid id) => Queries.RemoveAll(q => q.Id == id);

    /// 실행 · 넣기 기록: 마지막 사용 시각과 입력값 (값은 이 쿼리의 빈칸만 남긴다)
    public void MarkUsed(Guid id, IReadOnlyDictionary<string, string>? values = null, DateTimeOffset? now = null)
    {
        var i = Queries.FindIndex(q => q.Id == id);
        if (i < 0) return;
        var q = Queries[i] with { LastUsedAt = now ?? DateTimeOffset.Now };
        if (values != null) q = q with { LastValues = new Dictionary<string, string>(values) };
        Queries[i] = q;
    }
}

/// saved-queries.json 읽기 · 쓰기. Mac 과 같이 GDBENC1 로 통째 암호화한다 (LocalDataCrypto, 키 없으면 쓰지 않음).
/// JSON 은 Mac 과 같다: 키 정렬, 날짜 ISO 8601(초), UUID 대문자, 없는 값은 null.
public sealed class SavedQueryStore(string file, LocalDataCrypto? crypto = null)
{
    private LocalDataCrypto Crypto => crypto ?? LocalDataCrypto.Shared;

    private static readonly Lazy<SavedQueryStore> SharedLazy = new(() => new SavedQueryStore(Path.Combine(AppPaths.DataDirectory, "saved-queries.json")));
    public static SavedQueryStore Shared => SharedLazy.Value;

    public string File { get; } = file;

    public SavedQueryLibrary Load()
    {
        try { return ReadBytes() is { } data ? Decode(data) : new SavedQueryLibrary(); }
        catch { return new SavedQueryLibrary(); }
    }

    public void Save(SavedQueryLibrary library)
    {
        try { WriteBytes(Encode(library)); }
        catch (Exception e) { System.Diagnostics.Trace.WriteLine($"GodDB: saved queries not saved ({e.Message})"); }
    }

    private byte[]? ReadBytes() => Crypto.ReadDocumentData(File);

    private void WriteBytes(byte[] data) => Crypto.WriteDocument(data, File);

    // MARK: JSON (Mac SavedQueryStore.encode / decode)

    private static string Date(DateTimeOffset d) => Iso8601SecondsConverter.Format(d);

    private static DateTimeOffset? ReadDate(JsonNode? n) =>
        n?.GetValueKind() == JsonValueKind.String
        && DateTimeOffset.TryParse(n.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;

    private static string? ReadString(JsonNode? n) => n?.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;

    public static byte[] Encode(SavedQueryLibrary library)
    {
        var queries = new JsonArray();
        foreach (var q in library.Queries)
        {
            var values = new JsonObject();
            foreach (var kv in q.LastValues.OrderBy(kv => kv.Key, StringComparer.Ordinal)) values[kv.Key] = kv.Value;
            // 키는 정렬 순서로 (Swift .sortedKeys)
            queries.Add(new JsonObject
            {
                ["createdAt"] = Date(q.CreatedAt),
                ["folder"] = q.Folder,
                ["id"] = q.Id.ToString().ToUpperInvariant(),
                ["lastUsedAt"] = q.LastUsedAt is { } u ? Date(u) : null,
                ["lastValues"] = values,
                ["name"] = q.Name,
                ["profileID"] = q.ProfileId?.ToString().ToUpperInvariant(),
                ["scope"] = q.Scope == SavedQuery.QueryScope.Profile ? "profile" : "all",
                ["sql"] = q.Sql,
                ["updatedAt"] = Date(q.UpdatedAt),
            });
        }
        var root = new JsonObject { ["queries"] = queries, ["version"] = library.Version };
        return Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    /// Mac 판이 쓴 파일, 빠진 칸이 있는 파일도 읽는다 (빠진 칸은 기본값)
    public static SavedQueryLibrary Decode(byte[] data)
    {
        var root = JsonNode.Parse(data) as JsonObject ?? throw new JsonException("saved queries");
        var lib = new SavedQueryLibrary { Version = root["version"]?.GetValue<int>() ?? 1 };
        if (root["queries"] is not JsonArray arr) return lib;
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            var id = Guid.Parse(ReadString(o["id"]) ?? throw new JsonException("id"));
            var name = ReadString(o["name"]) ?? throw new JsonException("name");
            var sql = ReadString(o["sql"]) ?? throw new JsonException("sql");
            var scope = ReadString(o["scope"]) == "profile" ? SavedQuery.QueryScope.Profile : SavedQuery.QueryScope.All;
            Guid? profile = Guid.TryParse(ReadString(o["profileID"]), out var p) ? p : null;
            var values = new Dictionary<string, string>();
            if (o["lastValues"] is JsonObject lv)
                foreach (var kv in lv) if (ReadString(kv.Value) is { } v) values[kv.Key] = v;
            var created = ReadDate(o["createdAt"]) ?? DateTimeOffset.UnixEpoch;
            lib.Queries.Add(new SavedQuery(name, sql, ReadString(o["folder"]) ?? "", scope, profile, values, created,
                                           ReadDate(o["updatedAt"]) ?? created, ReadDate(o["lastUsedAt"]), id)
            {
                // 저장된 그대로 읽는다 (Scope 가 all 이면 생성자가 ProfileId 를 지우므로 Mac 디코더처럼 그대로 둔다)
                ProfileId = profile,
            });
        }
        return lib;
    }
}
