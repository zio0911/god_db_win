using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GodDB.Core.Modeling;

/// 모델 파일 저장소: %APPDATA%\GodDB\models\&lt;id&gt;.goddbmodel (Mac: ~/Library/Application Support/GodDB/models)
/// 파일 형식은 Mac 과 같다 (Swift JSONEncoder: 키 정렬, 날짜 ISO 8601 초 단위, UUID 대문자 문자열).
/// Swift 의 자동 Codable 과 같이: 모든 키가 있어야 하고(color 만 생략 가능, 관계는 id/parent/child 외 생략 가능),
/// format 이 "goddb-model" 이 아니거나 읽을 수 없으면 BadFile.
public sealed class ModelStore
{
    private static readonly Lazy<ModelStore> shared = new(() => new ModelStore(AppPaths.ModelsDirectory));
    public static ModelStore Shared => shared.Value;

    public string Directory { get; }

    public ModelStore(string directory)
    {
        Directory = directory;
    }

    // MARK: - 인코딩

    public static byte[] Encode(DataModel model)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
               {
                   Indented = true,
                   // Swift 처럼 한글을 그대로 쓴다 (\uXXXX 로 바꾸지 않음)
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            WriteModel(w, model);
        }
        return ms.ToArray();
    }

    // 키는 Swift .sortedKeys 처럼 사전 순으로 쓴다
    private static void WriteModel(Utf8JsonWriter w, DataModel m)
    {
        w.WriteStartObject();
        w.WriteString("created", FormatDate(m.Created));
        w.WriteString("format", m.Format);
        w.WriteString("id", FormatId(m.Id));
        w.WriteString("kind", m.Kind.RawValue());
        w.WriteString("memo", m.Memo);
        w.WriteString("name", m.Name);
        w.WriteStartArray("relations");
        foreach (var r in m.Relations) WriteRelation(w, r);
        w.WriteEndArray();
        w.WriteStartArray("tables");
        foreach (var t in m.Tables) WriteTable(w, t);
        w.WriteEndArray();
        w.WriteString("updated", FormatDate(m.Updated));
        w.WriteNumber("version", m.Version);
        w.WriteEndObject();
    }

    private static void WriteTable(Utf8JsonWriter w, ModelTable t)
    {
        w.WriteStartObject();
        // Swift 자동 Codable 은 nil 인 Optional 키를 쓰지 않는다
        if (t.Color != null) w.WriteString("color", t.Color);
        w.WriteStartArray("columns");
        foreach (var c in t.Columns) WriteColumn(w, c);
        w.WriteEndArray();
        w.WriteString("comment", t.Comment);
        w.WriteString("id", FormatId(t.Id));
        w.WriteStartArray("indexes");
        foreach (var i in t.Indexes) WriteIndex(w, i);
        w.WriteEndArray();
        w.WriteString("logicalName", t.LogicalName);
        w.WriteString("physicalName", t.PhysicalName);
        w.WriteNumber("x", t.X);
        w.WriteNumber("y", t.Y);
        w.WriteEndObject();
    }

    private static void WriteColumn(Utf8JsonWriter w, ModelColumn c)
    {
        w.WriteStartObject();
        w.WriteBoolean("autoIncrement", c.AutoIncrement);
        w.WriteString("comment", c.Comment);
        w.WriteString("dataType", c.DataType);
        w.WriteString("defaultValue", c.DefaultValue);
        w.WriteString("id", FormatId(c.Id));
        w.WriteString("logicalName", c.LogicalName);
        w.WriteBoolean("notNull", c.NotNull);
        w.WriteString("physicalName", c.PhysicalName);
        w.WriteBoolean("primaryKey", c.PrimaryKey);
        w.WriteBoolean("unique", c.Unique);
        w.WriteEndObject();
    }

    private static void WriteIndex(Utf8JsonWriter w, ModelIndex i)
    {
        w.WriteStartObject();
        w.WriteStartArray("columns");
        foreach (var c in i.Columns) w.WriteStringValue(FormatId(c));
        w.WriteEndArray();
        w.WriteString("id", FormatId(i.Id));
        w.WriteString("name", i.Name);
        w.WriteBoolean("unique", i.Unique);
        w.WriteEndObject();
    }

    private static void WriteRelation(Utf8JsonWriter w, ModelRelation r)
    {
        w.WriteStartObject();
        w.WriteString("cardinality", r.Cardinality.RawValue());
        w.WriteString("child", FormatId(r.Child));
        w.WriteBoolean("createConstraint", r.CreateConstraint);
        w.WriteString("id", FormatId(r.Id));
        w.WriteBoolean("identifying", r.Identifying);
        w.WriteString("name", r.Name);
        w.WriteString("onDelete", r.OnDelete.RawValue());
        w.WriteString("onUpdate", r.OnUpdate.RawValue());
        w.WriteBoolean("optional", r.Optional);
        w.WriteStartArray("pairs");
        foreach (var p in r.Pairs)
        {
            w.WriteStartObject();
            w.WriteString("child", FormatId(p.Child));
            w.WriteString("parent", FormatId(p.Parent));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteString("parent", FormatId(r.Parent));
        w.WriteEndObject();
    }

    /// Swift UUID.uuidString (대문자)
    internal static string FormatId(Guid id) => id.ToString("D").ToUpperInvariant();

    /// Swift .iso8601 (초 단위, UTC "Z")
    internal static string FormatDate(DateTime d) =>
        d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // MARK: - 디코딩

    /// <exception cref="ModelException">GodDB 모델 파일이 아니면 (BadFile)</exception>
    public static DataModel Decode(byte[] data)
    {
        DataModel m;
        try
        {
            using var doc = JsonDocument.Parse(data);
            m = ReadModel(doc.RootElement);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            throw ModelException.BadFile();
        }
        if (m.Format != DataModel.FormatName) throw ModelException.BadFile();
        return m;
    }

    private static DataModel ReadModel(JsonElement e)
    {
        var m = new DataModel(Id(e, "id"), Str(e, "name"), Kind(Str(e, "kind")),
                              Arr(e, "tables").Select(ReadTable), Arr(e, "relations").Select(ReadRelation), Str(e, "memo"))
        {
            Format = Str(e, "format"),
            Version = Int(e, "version"),
            Created = Date(e, "created"),
            Updated = Date(e, "updated"),
        };
        return m;
    }

    private static ModelTable ReadTable(JsonElement e)
    {
        string? color = null;
        if (e.TryGetProperty("color", out var c) && c.ValueKind != JsonValueKind.Null)
            color = c.ValueKind == JsonValueKind.String ? c.GetString() : throw new FormatException("color");
        return new ModelTable(Id(e, "id"), Str(e, "logicalName"), Str(e, "physicalName"), Str(e, "comment"),
                              Arr(e, "columns").Select(ReadColumn), Arr(e, "indexes").Select(ReadIndex),
                              Num(e, "x"), Num(e, "y"), color);
    }

    private static ModelColumn ReadColumn(JsonElement e) =>
        new(Id(e, "id"), Str(e, "logicalName"), Str(e, "physicalName"), Str(e, "dataType"), Bool(e, "primaryKey"),
            Bool(e, "notNull"), Bool(e, "autoIncrement"), Bool(e, "unique"), Str(e, "defaultValue"), Str(e, "comment"));

    private static ModelIndex ReadIndex(JsonElement e) =>
        new(Id(e, "id"), Str(e, "name"), Bool(e, "unique"), Arr(e, "columns").Select(ParseId));

    // Mac 의 ModelRelation.init(from:) 처럼 id/parent/child 외에는 없으면 기본값
    private static ModelRelation ReadRelation(JsonElement e)
    {
        bool Has(string key, out JsonElement v) => e.TryGetProperty(key, out v) && v.ValueKind != JsonValueKind.Null;
        return new ModelRelation(
            parent: Id(e, "parent"), child: Id(e, "child"), id: Id(e, "id"),
            name: Has("name", out _) ? Str(e, "name") : "",
            cardinality: Has("cardinality", out _)
                ? ModelEnumExtensions.ParseCardinality(Str(e, "cardinality")) ?? throw new FormatException("cardinality")
                : ModelCardinality.OneToMany,
            identifying: Has("identifying", out _) && Bool(e, "identifying"),
            optional: Has("optional", out _) && Bool(e, "optional"),
            pairs: Has("pairs", out _) ? Arr(e, "pairs").Select(p => new ModelRelationPair(Id(p, "child"), Id(p, "parent"))) : [],
            onDelete: Has("onDelete", out _) ? Action(Str(e, "onDelete")) : ModelFkAction.NoAction,
            onUpdate: Has("onUpdate", out _) ? Action(Str(e, "onUpdate")) : ModelFkAction.NoAction,
            createConstraint: !Has("createConstraint", out _) || Bool(e, "createConstraint"));
    }

    private static JsonElement Get(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : throw new KeyNotFoundException(key);

    private static string Str(JsonElement e, string key) =>
        Get(e, key) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : throw new FormatException(key);

    private static bool Bool(JsonElement e, string key) => Get(e, key).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException(key),
    };

    private static double Num(JsonElement e, string key) => Get(e, key).GetDouble();

    private static int Int(JsonElement e, string key)
    {
        var v = Get(e, key);
        if (v.TryGetInt32(out var i)) return i;
        // Swift 는 1.0 처럼 정수로 딱 떨어지는 수도 Int 로 읽는다
        var d = v.GetDouble();
        return d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : throw new FormatException(key);
    }

    private static IEnumerable<JsonElement> Arr(JsonElement e, string key) =>
        Get(e, key) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray().ToList() : throw new FormatException(key);

    private static Guid Id(JsonElement e, string key) => ParseId(Get(e, key));

    private static Guid ParseId(JsonElement v) =>
        v.ValueKind == JsonValueKind.String && Guid.TryParseExact(v.GetString(), "D", out var g) ? g : throw new FormatException("uuid");

    private static DateTime Date(JsonElement e, string key) =>
        DateTimeOffset.Parse(Str(e, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime;

    private static DatabaseKind Kind(string raw) => ModelEnumExtensions.ParseKind(raw) ?? throw new FormatException("kind");
    private static ModelFkAction Action(string raw) => ModelEnumExtensions.ParseFkAction(raw) ?? throw new FormatException("action");

    // MARK: - 파일

    internal string PathOf(Guid id) => Path.Combine(Directory, $"{FormatId(id)}.{DataModel.FileExtension}");

    /// 최근 수정 순 (읽을 수 없는 파일은 건너뛴다)
    public List<DataModel> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var output = new List<DataModel>();
        foreach (var f in System.IO.Directory.EnumerateFiles(Directory))
        {
            if (!string.Equals(Path.GetExtension(f), "." + DataModel.FileExtension, StringComparison.Ordinal)) continue;
            try { output.Add(Decode(File.ReadAllBytes(f))); } catch { }
        }
        return [.. output.OrderByDescending(m => m.Updated)];
    }

    public void Save(DataModel model)
    {
        AppPaths.Ensure(Directory);
        WriteFile(PathOf(model.Id), Encode(model));
    }

    public void Delete(Guid id)
    {
        try { File.Delete(PathOf(id)); } catch { }
    }

    /// 임시 파일에 쓰고 바꿔치기 (Swift .atomic)
    public static void WriteFile(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }
}
