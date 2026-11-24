using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GodDB.Core.Audit;
using GodDB.Core.Platform;
using static GodDB.Core.L10n;

namespace GodDB.Core.Profiles;

/// 접속 정보 내보내기 / 가져오기 파일 (Mac 판과 같은 형식 — 서로 주고받을 수 있다).
/// 비밀번호는 기본으로 넣지 않는다. 넣을 때는 사용자가 정한 암호로 AES-GCM 암호화한다
/// (PBKDF2-SHA256 로 키를 만든다). SSH 개인키 파일은 넣지 않고 경로만 넣는다.
public sealed record ConnectionBundle
{
    public const string FormatName = "goddb-connections";

    public sealed record EncryptedPasswords
    {
        [JsonRequired] public string Kdf { get; init; } = "pbkdf2-sha256";
        [JsonRequired] public int Rounds { get; init; }
        /// 16바이트 base64
        [JsonRequired] public string Salt { get; init; } = "";
        /// AES-GCM combined (nonce 12 + ciphertext + tag 16), base64. 안에는 {접속 id: 비밀번호} JSON.
        [JsonRequired] public string Data { get; init; } = "";
    }

    [JsonRequired] public string Format { get; init; } = FormatName;
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public DateTimeOffset ExportedAt { get; init; }
    [JsonRequired] public string App { get; init; } = "";
    [JsonRequired] public List<ConnectionProfile> Profiles { get; init; } = [];
    public EncryptedPasswords? Passwords { get; init; }

    [JsonIgnore] public bool HasPasswords => Passwords != null;
}

public enum ConnectionTransferErrorKind { NotGodDBFile, UnsupportedVersion, WrongPassphrase, EmptyPassphrase }

public sealed class ConnectionTransferException(ConnectionTransferErrorKind kind, int version = 0) : Exception(Describe(kind, version))
{
    public ConnectionTransferErrorKind Kind { get; } = kind;
    /// UnsupportedVersion 일 때 파일 버전
    public int FileVersion { get; } = version;

    private static string Describe(ConnectionTransferErrorKind kind, int version) => kind switch
    {
        ConnectionTransferErrorKind.NotGodDBFile => Tr("GodDB 접속 정보 파일이 아니에요."),
        ConnectionTransferErrorKind.UnsupportedVersion => Tr("지원하지 않는 파일 버전이에요 (%@). GodDB 를 업데이트해 주세요.", version.ToString(CultureInfo.InvariantCulture)),
        ConnectionTransferErrorKind.WrongPassphrase => Tr("암호가 맞지 않아요."),
        _ => Tr("비밀번호를 넣으려면 암호를 정해 주세요."),
    };
}

/// 가져오기 계획: 새로 추가 / 같은 접속(id)이 이미 있음 → 덮어쓰기
public abstract record ImportAction
{
    public sealed record Add : ImportAction { public static readonly Add Instance = new(); }
    public sealed record Replace(string ExistingName) : ImportAction;
}

public static class ConnectionTransfer
{
    internal const int Rounds = 200_000;

    /// 파일 JSON 설정 (camelCase, enum 은 소문자 문자열, 날짜는 초 단위 ISO 8601 — Swift JSONEncoder 와 같음)
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new Iso8601SecondsConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// 내보내기 파일 내용을 만든다. 키를 이름순으로 정렬해 쓴다 (Mac: .sortedKeys, .prettyPrinted).
    /// <param name="passwords">함께 넣을 비밀번호 (null 이면 넣지 않음)</param>
    /// <param name="passphrase">passwords 를 암호화할 암호</param>
    public static byte[] Export(IReadOnlyList<ConnectionProfile> profiles, IReadOnlyDictionary<Guid, string>? passwords = null,
                                string? passphrase = null, string appVersion = "", DateTimeOffset? now = null)
    {
        var bundle = new ConnectionBundle
        {
            ExportedAt = now ?? DateTimeOffset.Now,
            App = $"GodDB {appVersion}".Trim(),
            Profiles = [.. profiles],
        };
        if (passwords != null)
        {
            if (string.IsNullOrEmpty(passphrase)) throw new ConnectionTransferException(ConnectionTransferErrorKind.EmptyPassphrase);
            var ids = profiles.Select(p => p.Id).ToHashSet();
            // 키는 Swift UUID.uuidString 처럼 대문자
            var plain = passwords.Where(kv => ids.Contains(kv.Key))
                .ToDictionary(kv => kv.Key.ToString("D").ToUpperInvariant(), kv => kv.Value);
            bundle = bundle with { Passwords = Encrypt(JsonSerializer.SerializeToUtf8Bytes(plain, Json), passphrase) };
        }
        var node = JsonSerializer.SerializeToNode(bundle, Json)!;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            Sorted(node)!.WriteTo(w);
        return ms.ToArray();
    }

    /// 객체 키를 이름순(서수)으로 정렬한 복사본
    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, Sorted(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    /// 파일을 읽는다. 비밀번호는 아직 풀지 않는다.
    public static ConnectionBundle Parse(byte[] data)
    {
        ConnectionBundle? bundle;
        try { bundle = JsonSerializer.Deserialize<ConnectionBundle>(data, Json); }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            throw new ConnectionTransferException(ConnectionTransferErrorKind.NotGodDBFile);
        }
        if (bundle == null || bundle.Format != ConnectionBundle.FormatName || bundle.Profiles == null)
            throw new ConnectionTransferException(ConnectionTransferErrorKind.NotGodDBFile);
        if (bundle.Version > 1) throw new ConnectionTransferException(ConnectionTransferErrorKind.UnsupportedVersion, bundle.Version);
        return bundle;
    }

    /// 들어 있는 비밀번호를 푼다.
    public static Dictionary<Guid, string> Passwords(ConnectionBundle bundle, string passphrase)
    {
        if (bundle.Passwords is not { } enc) return [];
        var plain = Decrypt(enc, passphrase);
        Dictionary<string, string>? dict;
        try { dict = JsonSerializer.Deserialize<Dictionary<string, string>>(plain); }
        catch (JsonException) { dict = null; }
        if (dict == null) throw new ConnectionTransferException(ConnectionTransferErrorKind.WrongPassphrase);
        var result = new Dictionary<Guid, string>();
        foreach (var (k, v) in dict)
            if (Guid.TryParseExact(k, "D", out var id)) result[id] = v;
        return result;
    }

    // MARK: - 가져오기 계획

    public static ImportAction Action(ConnectionProfile profile, IEnumerable<ConnectionProfile> existing) =>
        existing.FirstOrDefault(e => e.Id == profile.Id) is { } e ? new ImportAction.Replace(e.Name) : ImportAction.Add.Instance;

    /// 가져오기 창의 처음 선택: 새 접속은 고르고, 이미 있는 접속은 덮어쓰지 않도록 고르지 않는다
    public static HashSet<Guid> DefaultSelection(IEnumerable<ConnectionProfile> incoming, IReadOnlyList<ConnectionProfile> existing) =>
        incoming.Where(p => Action(p, existing) is ImportAction.Add).Select(p => p.Id).ToHashSet();

    /// 가져온 접속을 기존 목록에 합친다. 같은 id 는 덮어쓰고, 새 접속은 뒤에 붙인다.
    /// 이름이 다른 접속과 겹치면 " (2)" 를 붙인다. 넘긴 목록의 항목은 바꾸지 않는다 (복사본을 넣는다).
    public static List<ConnectionProfile> Merge(IEnumerable<ConnectionProfile> incoming, IEnumerable<ConnectionProfile> existing)
    {
        var result = existing.ToList();
        foreach (var original in incoming)
        {
            var p = original with { };
            var i = result.FindIndex(e => e.Id == p.Id);
            if (i >= 0)
            {
                result[i] = p;
                continue;
            }
            var names = result.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            if (names.Contains(p.Name))
            {
                int n = 2;
                while (names.Contains($"{p.Name} ({n})")) n++;
                p.Name = $"{p.Name} ({n})";
            }
            result.Add(p);
        }
        return result;
    }

    // MARK: - 내보내기 창

    /// 비밀번호를 넣을 때 암호 문제 (없으면 null)
    public static string? PassphraseProblem(bool includePasswords, string passphrase, string confirm)
    {
        if (!includePasswords) return null;
        if (new StringInfo(passphrase).LengthInTextElements < 8) return Tr("암호는 8자 이상으로 정해 주세요.");
        if (passphrase != confirm) return Tr("암호 확인이 달라요.");
        return null;
    }

    /// 내보내기 파일 이름 "goddb-connections-20261118.json"
    public static string ExportFileName(DateTime now) =>
        $"goddb-connections-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.json";

    /// 파일을 쓰고 본인만 읽을 수 있게 한다 (Mac: 권한 600)
    public static void WriteFile(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        UserOnlyFile.Restrict(tmp);
        File.Move(tmp, path, overwrite: true);
    }

    /// 내보낸 뒤 상태창·감사 로그에 남길 항목
    public static LogEntry ExportedLog(IReadOnlyList<ConnectionProfile> list, string path, int? passwordCount)
    {
        var count = list.Count.ToString(CultureInfo.InvariantCulture);
        var pw = passwordCount?.ToString(CultureInfo.InvariantCulture);
        return new LogEntry(LogLevel.Success, LogSource.App,
            Tr("접속 정보 %@개 내보냄 · %@", count, Path.GetFileName(path)) + (pw != null ? Tr(" · 비밀번호 %@개 (암호화)", pw) : ""),
            Detail: path + "\n" + string.Join("\n", list.Select(p => p.Name)),
            Audit: new AuditRecord(AuditAction.Export,
                Result: $"접속 정보 {count}개 내보냄" + (pw != null ? $" · 비밀번호 {pw}개 (암호화)" : ""),
                Statement: path + " · " + string.Join(", ", list.Select(p => p.Name))));
    }

    /// 가져온 뒤 상태창·감사 로그에 남길 항목
    public static LogEntry ImportedLog(IReadOnlyList<ConnectionProfile> incoming, string fileName, int savedPasswords)
    {
        var count = incoming.Count.ToString(CultureInfo.InvariantCulture);
        var saved = savedPasswords.ToString(CultureInfo.InvariantCulture);
        return new LogEntry(LogLevel.Success, LogSource.App,
            Tr("접속 정보 %@개 가져옴 · %@", count, fileName) + (savedPasswords > 0 ? Tr(" · 비밀번호 %@개", saved) : ""),
            Detail: string.Join("\n", incoming.Select(p => $"{p.Name} ({p.TypeTitle} {p.Host}:{p.Port})")),
            Audit: new AuditRecord(AuditAction.Settings,
                Result: $"접속 정보 {count}개 가져옴" + (savedPasswords > 0 ? $" · 비밀번호 {saved}개" : ""),
                Statement: fileName + " · " + string.Join(", ", incoming.Select(p => p.Name))));
    }

    // MARK: - 암호화

    /// PBKDF2-HMAC-SHA256 (암호는 UTF-8) → 32바이트 키 (Mac: CCKeyDerivationPBKDF)
    internal static byte[] Key(string passphrase, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, rounds, HashAlgorithmName.SHA256, 32);

    private const int NonceSize = 12;
    private const int TagSize = 16;

    internal static ConnectionBundle.EncryptedPasswords Encrypt(byte[] plain, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(Key(passphrase, salt, Rounds), TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);
        // CryptoKit AES.GCM.SealedBox.combined = nonce ‖ ciphertext ‖ tag
        byte[] combined = [.. nonce, .. cipher, .. tag];
        return new ConnectionBundle.EncryptedPasswords
        {
            Rounds = Rounds,
            Salt = Convert.ToBase64String(salt),
            Data = Convert.ToBase64String(combined),
        };
    }

    internal static byte[] Decrypt(ConnectionBundle.EncryptedPasswords enc, string passphrase)
    {
        byte[] salt, data;
        try
        {
            salt = Convert.FromBase64String(enc.Salt);
            data = Convert.FromBase64String(enc.Data);
        }
        catch (FormatException) { throw new ConnectionTransferException(ConnectionTransferErrorKind.NotGodDBFile); }
        if (data.Length < NonceSize + TagSize) throw new ConnectionTransferException(ConnectionTransferErrorKind.NotGodDBFile);
        var nonce = data.AsSpan(0, NonceSize);
        var cipher = data.AsSpan(NonceSize, data.Length - NonceSize - TagSize);
        var tag = data.AsSpan(data.Length - TagSize);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(Key(passphrase, salt, enc.Rounds), TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            throw new ConnectionTransferException(ConnectionTransferErrorKind.WrongPassphrase);
        }
    }
}
