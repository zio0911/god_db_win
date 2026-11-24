using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static GodDB.Core.L10n;

namespace GodDB.Core.Profiles;

public enum DBeaverParseErrorKind { NotDBeaver, NoConnections, NothingSupported }

/// DBeaver 파일을 읽지 못한 이유 (Mac DBeaverImport.ParseError)
public sealed class DBeaverImportException(DBeaverParseErrorKind kind, IReadOnlyList<string>? names = null)
    : Exception(Describe(kind, names ?? []))
{
    public DBeaverParseErrorKind Kind { get; } = kind;
    /// NothingSupported 일 때 건너뛴 접속 이름
    public IReadOnlyList<string> Names { get; } = names ?? [];

    private static string Describe(DBeaverParseErrorKind kind, IReadOnlyList<string> names) => kind switch
    {
        DBeaverParseErrorKind.NotDBeaver =>
            Tr("DBeaver 접속 정보 파일이 아니에요. (DBeaver: 파일 > 내보내기 > DBeaver > 프로젝트 로 만든 .dbp, 또는 작업 폴더의 data-sources.json)"),
        DBeaverParseErrorKind.NoConnections => Tr("DBeaver 파일에 접속이 없어요."),
        _ => Tr("지원하지 않는 DB 예요: %@", string.Join(", ", names)),
    };
}

/// DBeaver 접속 정보를 읽는다. (Mac DBeaverImport.swift)
///   - 프로젝트 내보내기 파일 (.dbp, ZIP: projects/&lt;프로젝트&gt;/.dbeaver/data-sources*.json)
///   - 작업 폴더의 data-sources.json (같은 폴더의 credentials-config.json 도 함께)
/// DBeaver 는 사용자 이름을 비밀번호와 함께 credentials-config.json 에 암호화해 둔다.
/// 사용자 이름과 저장된 DB 비밀번호를 꺼낸다 (Mac #195). 비밀번호는 가져오기를 누르면 자격 증명 관리자에 넣고, 화면 · 로그에는 남기지 않는다.
public static class DBeaverImport
{
    public sealed record Source(ConnectionProfile Profile, IReadOnlyList<string> Notes)
    {
        /// DBeaver 에 저장돼 있던 DB 비밀번호 (없으면 null — 처음 연결할 때 입력)
        public string? Password { get; init; }
    }

    public sealed record Result(IReadOnlyList<Source> Sources, IReadOnlyList<string> Skipped);

    /// Windows DBeaver 의 기본 작업 폴더 (Mac: ~/Library/DBeaverData/workspace6/General/.dbeaver)
    public static string DefaultWorkspaceFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DBeaverData", "workspace6", "General", ".dbeaver");

    // MARK: - 진입점

    /// 파일을 읽는다. data-sources.json 이면 같은 폴더의 credentials-config.json 도 읽는다.
    public static Result ParseFile(string path)
    {
        var data = File.ReadAllBytes(path);
        if (ZipReader.IsZip(data)) return Parse(data);
        byte[]? creds = null;
        try
        {
            var c = Path.Combine(Path.GetDirectoryName(path) ?? "", "credentials-config.json");
            if (File.Exists(c)) creds = File.ReadAllBytes(c);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Parse(data, creds);
    }

    /// .dbp(ZIP) 또는 data-sources.json 내용을 읽는다.
    public static Result Parse(byte[] data, byte[]? credentials = null)
    {
        List<Project> projects;
        if (ZipReader.IsZip(data)) projects = ProjectsInArchive(data);
        else
        {
            var ds = DataSources(data) ?? throw new DBeaverImportException(DBeaverParseErrorKind.NotDBeaver);
            projects = [new Project("", [ds], credentials != null ? UsersInCredentials(credentials) : [])];
        }
        var multi = projects.Count(p => p.DataSources.Any(d => d.Connections.Count > 0)) > 1;
        var sources = new List<Source>();
        var skipped = new List<string>();
        var total = 0;
        foreach (var project in projects)
            foreach (var ds in project.DataSources)
                foreach (var (id, conn) in ds.Connections)
                {
                    total++;
                    var folder = ds.FolderPath(Str(conn, "folder"));
                    if (multi) folder = folder.Length == 0 ? project.Name : project.Name + "/" + folder;
                    if (MakeSource(id, conn, folder, project.Users.GetValueOrDefault(id)) is { } s) sources.Add(s);
                    else skipped.Add(Str(conn, "name") ?? id);
                }
        if (total == 0) throw new DBeaverImportException(DBeaverParseErrorKind.NoConnections);
        skipped.Sort(StringComparer.Ordinal);
        if (sources.Count == 0) throw new DBeaverImportException(DBeaverParseErrorKind.NothingSupported, skipped);
        sources = [.. sources.OrderBy(s => s.Profile.Group, StringComparer.Ordinal).ThenBy(s => s.Profile.Name, StringComparer.Ordinal)];
        return new Result(sources, skipped);
    }

    /// data-sources.json 처럼 보이는지 (최상위 "connections" 안에 "provider" 가 있는 객체)
    public static bool LooksLikeDBeaver(byte[] data) => ZipReader.IsZip(data) || DataSources(data) != null;

    // MARK: - 파일 구조

    internal sealed record DataSourcesFile(Dictionary<string, JsonElement> Connections, Dictionary<string, JsonElement> Folders)
    {
        /// 폴더 키 → "상위/하위" 경로. 새 DBeaver 는 키가 이미 전체 경로, 옛 DBeaver 는 parent 를 따라간다.
        public string FolderPath(string? key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            var parts = new List<string> { key };
            var seen = new HashSet<string> { key };
            var cur = key;
            while (!cur.Contains('/') && Folders.TryGetValue(cur, out var f) && Str(f, "parent") is { Length: > 0 } parent
                   && !seen.Contains(parent))
            {
                parts.Insert(0, parent);
                seen.Add(parent);
                cur = parent;
            }
            return string.Join("/", parts);
        }
    }

    internal sealed record Project(string Name, List<DataSourcesFile> DataSources, Dictionary<string, Users> Users);

    /// 접속 id → DB 사용자, SSH 사용자, DB 비밀번호 (DBeaver 에 저장한 경우)
    public sealed record Users(string? Database, string? Ssh, string? Password = null);

    internal static DataSourcesFile? DataSources(byte[] data)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(data);
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("connections", out var conns)
            || conns.ValueKind != JsonValueKind.Object) return null;
        var connections = new Dictionary<string, JsonElement>();
        foreach (var c in conns.EnumerateObject())
        {
            if (c.Value.ValueKind != JsonValueKind.Object || !c.Value.TryGetProperty("provider", out var pv)
                || pv.ValueKind != JsonValueKind.String) return null;
            connections[c.Name] = c.Value;
        }
        var folders = new Dictionary<string, JsonElement>();
        if (root.TryGetProperty("folders", out var fs) && fs.ValueKind == JsonValueKind.Object)
            foreach (var f in fs.EnumerateObject())
                if (f.Value.ValueKind == JsonValueKind.Object) folders[f.Name] = f.Value;
        return new DataSourcesFile(connections, folders);
    }

    internal static List<Project> ProjectsInArchive(byte[] data)
    {
        Dictionary<string, byte[]> files;
        try
        {
            files = ZipReader.Read(data, path =>
            {
                var name = path[(path.LastIndexOf('/') + 1)..];
                return path.Contains("/.dbeaver/") && (name == "credentials-config.json"
                    || (name.StartsWith("data-sources", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)));
            });
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
        {
            throw new DBeaverImportException(DBeaverParseErrorKind.NotDBeaver);
        }
        // projects/<이름>/.dbeaver/<파일>
        var byProject = new SortedDictionary<string, (List<DataSourcesFile> Ds, byte[]? Creds)>(StringComparer.Ordinal);
        foreach (var (path, content) in files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var r = path.IndexOf("/.dbeaver/", StringComparison.Ordinal);
            if (r < 0) continue;
            var dir = path[..r];
            var name = path[(path.LastIndexOf('/') + 1)..];
            var entry = byProject.TryGetValue(dir, out var e) ? e : ([], null);
            if (name == "credentials-config.json") entry.Creds = content;
            else if (DataSources(content) is { } ds) entry.Ds.Add(ds);
            byProject[dir] = entry;
        }
        if (byProject.Count == 0) throw new DBeaverImportException(DBeaverParseErrorKind.NotDBeaver);
        return [.. byProject.Select(kv => new Project(kv.Key[(kv.Key.LastIndexOf('/') + 1)..], kv.Value.Ds,
            kv.Value.Creds != null ? UsersInCredentials(kv.Value.Creds) : []))];
    }

    // MARK: - 접속 하나

    /// DBeaver 의 Redshift 접속: provider "postgresql" + driver "postgres-redshift-jdbc",
    /// 옛 DBeaver 는 provider "generic" + driver "aws_redshift" / "aws_redshift_42"
    internal static bool IsRedshift(string provider, string driver) => driver.Contains("redshift", StringComparison.OrdinalIgnoreCase);

    internal static DatabaseKind? KindOf(string provider, string driver)
    {
        if (IsRedshift(provider, driver)) return DatabaseKind.Postgres;
        return provider.ToLowerInvariant() switch
        {
            "mysql" or "mariadb" => DatabaseKind.Mysql,
            "postgresql" => DatabaseKind.Postgres,
            "sqlserver" or "mssql" => DatabaseKind.Mssql,
            _ => null,
        };
    }

    /// 문자열 또는 숫자 값 (Mac string(_:))
    internal static string? Str(JsonElement? obj, string key)
    {
        if (obj is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty(key, out var v)) return null;
        return Text(v);
    }

    internal static string? Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        // NSNumber.stringValue 처럼 2222.0 → "2222" (DBeaver 는 포트를 2222.0 처럼 쓰기도 한다)
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture)
            : v.GetDouble() is var d && d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : v.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        JsonValueKind.True => "1",   // NSNumber(true).stringValue
        JsonValueKind.False => "0",
        _ => null,
    };

    private static JsonElement? Obj(JsonElement? obj, string key) =>
        obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static bool? Bool(JsonElement? obj, string key) =>
        obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(key, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    private static IEnumerable<(string Key, JsonElement Value)> Props(JsonElement? obj) =>
        obj is { ValueKind: JsonValueKind.Object } o ? o.EnumerateObject().Select(p => (p.Name, p.Value)) : [];

    internal static Source? MakeSource(string id, JsonElement conn, string group, Users? users)
    {
        var provider = Str(conn, "provider") ?? "";
        var driver = Str(conn, "driver") ?? "";
        if (KindOf(provider, driver) is not { } kind) return null;
        var cfg = Obj(conn, "configuration");
        var url = Str(cfg, "url") ?? "";
        var redshift = IsRedshift(provider, driver);
        var p = new ConnectionProfile
        {
            Id = StableId(id),
            Kind = kind,
            Name = Str(conn, "name") ?? id,
            Port = redshift ? 5439 : kind.DefaultPort(),
            // Redshift 드라이버는 SSL 이 기본 (끈 설정은 아래 sslmode=disable / ssl=false 에서 끔)
            UseTLS = redshift,
            Redshift = redshift,
            Host = Str(cfg, "host")?.Trim(' ', '\t') ?? "",
        };
        if (Str(cfg, "port") is { } port && int.TryParse(port.Trim(' ', '\t'), out var portNumber)) p.Port = portNumber;
        p.Database = Str(cfg, "database") ?? "";
        // 주소(URL) 방식으로 만든 접속은 URL 에서 읽는다
        if (p.Host.Length == 0 && DataGripImport.ProfileFromJdbc(url) is { } fromUrl)
        {
            p.Host = fromUrl.Host;
            p.Port = fromUrl.Port;
            if (p.Database.Length == 0) p.Database = fromUrl.Database;
        }
        if (p.Host.Length == 0) return null;
        p.ReadOnly = Bool(conn, "read-only") ?? false;
        p.Group = group;
        p.Username = users?.Database ?? Str(cfg, "user") ?? "";

        var notes = new List<string>();
        var handlers = Obj(cfg, "handlers");

        // TLS: SSL 설정이 켜져 있으면 사용, 서버 인증서 확인을 끈 설정이면 확인 안 함
        var props = Obj(cfg, "provider-properties");
        foreach (var (hid, h) in Props(handlers))
        {
            if (h.ValueKind != JsonValueKind.Object || Bool(h, "enabled") != true || !hid.Contains("ssl", StringComparison.OrdinalIgnoreCase)) continue;
            p.UseTLS = true;
            // 핸들러 속성이 먼저, 없는 키만 provider-properties 에서 (Mac merging { a, _ in a })
            var hp = new Dictionary<string, JsonElement>();
            foreach (var (k, v) in Props(Obj(h, "properties"))) hp[k] = v;
            foreach (var (k, v) in Props(props)) hp.TryAdd(k, v);
            foreach (var (k, v) in hp)
            {
                var key = k.ToLowerInvariant();
                var value = (Text(v) ?? "").ToLowerInvariant();
                if (key.EndsWith("sslmode", StringComparison.Ordinal))
                {
                    if (value == "disable") p.UseTLS = false;
                    if (value is "allow" or "prefer" or "require") p.TlsVerify = false;
                }
                if (key.Contains("verify") && value == "false") p.TlsVerify = false;
                if (key.Contains("trustservercertificate") && value == "true") p.TlsVerify = false;
            }
        }
        if (!Props(handlers).Any(h => h.Key.Contains("ssl", StringComparison.OrdinalIgnoreCase)))
        {
            var u = url.ToLowerInvariant();
            string[] on = ["usessl=true", "sslmode=require", "sslmode=verify", "ssl=true", "encrypt=true"];
            if (on.Any(u.Contains)) p.UseTLS = true;
            if (redshift)
            {
                var merged = new Dictionary<string, JsonElement>();
                foreach (var (k, v) in Props(props)) merged[k] = v;
                foreach (var (k, v) in Props(Obj(cfg, "properties"))) merged.TryAdd(k, v);
                var off = merged.Any(kv =>
                {
                    var key = kv.Key.ToLowerInvariant();
                    var value = (Text(kv.Value) ?? "").ToLowerInvariant();
                    return (key == "ssl" && value == "false") || (key == "sslmode" && value == "disable");
                });
                if (off || u.Contains("ssl=false") || u.Contains("sslmode=disable")) p.UseTLS = false;
            }
        }

        // SSH 터널
        if (Obj(handlers, "ssh_tunnel") is { } sshHandler && Bool(sshHandler, "enabled") == true)
        {
            var sp = Obj(sshHandler, "properties");
            var ssh = new SshConfig
            {
                Host = Str(sp, "host") ?? "",
                Port = int.TryParse(Str(sp, "port"), out var sshPort) ? sshPort : 22,
                Username = users?.Ssh ?? Str(sshHandler, "user") ?? Str(sp, "user") ?? "",
                KeyPath = "",
            };
            switch ((Str(sp, "authType") ?? "").ToUpperInvariant())
            {
                case "PUBLIC_KEY":
                    ssh.KeyPath = Str(sp, "keyPath") ?? "";
                    break;
                case "PASSWORD":
                    notes.Add(Tr("SSH 비밀번호 방식이에요. GodDB 는 키 파일이나 ssh-agent 로 연결하니 접속 편집에서 키를 지정해 주세요."));
                    break;
                // AGENT: 키 파일 없이 ssh-agent
            }
            if ((int.TryParse(Str(sp, "jumpServer.count"), out var jumps) ? jumps : 0) > 0)
                notes.Add(Tr("SSH 점프 서버 설정은 가져오지 않았어요."));
            if (ssh.Username.Length == 0) notes.Add(Tr("SSH 사용자 이름이 없어요. 접속 편집에서 넣어 주세요."));
            p.Ssh = ssh;
        }
        if (Props(handlers).Any(h => h.Key.Contains("proxy", StringComparison.OrdinalIgnoreCase) && Bool(h.Value, "enabled") == true))
            notes.Add(Tr("프록시 설정은 가져오지 않았어요."));
        if (p.Username.Length == 0) notes.Add(Tr("사용자 이름이 없어요. 접속 편집에서 넣어 주세요."));
        var password = string.IsNullOrEmpty(users?.Password) ? null : users.Password;
        // Mac 은 "키체인에 넣어요". Windows 는 비밀번호를 자격 증명 관리자에 둔다
        notes.Add(password == null ? Tr("비밀번호는 처음 연결할 때 입력해요.") : Tr("DBeaver 에 저장된 비밀번호도 가져와 자격 증명 관리자에 넣어요."));
        return new Source(p, notes) { Password = password };
    }

    /// DBeaver 접속 id → 늘 같은 UUID (다시 가져오면 같은 접속으로 인식). Mac 과 같은 값 (바이트 순서 big-endian)
    internal static Guid StableId(string id)
    {
        var b = SHA256.HashData(Encoding.UTF8.GetBytes("dbeaver:" + id))[..16];
        b[6] = (byte)((b[6] & 0x0F) | 0x50);  // 버전 5 모양
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b, bigEndian: true);
    }

    // MARK: - credentials-config.json (사용자 이름 · 비밀번호)

    /// DBeaver 가 credentials-config.json 을 잠그는 데 쓰는 고정 키 (DBeaver 소스에 공개된 값)
    internal static readonly byte[] CredentialsKey =
        [0xBA, 0xBB, 0x4A, 0x9F, 0x77, 0x4A, 0xB8, 0x53, 0xC9, 0x6C, 0x2D, 0x65, 0x3D, 0xFE, 0x54, 0x4A];

    /// 접속 id → 사용자 이름 · DB 비밀번호. 푼 원문 버퍼는 바로 지운다.
    /// 풀 수 없으면 빈 결과 (사용자 이름 · 비밀번호 없이 가져온다).
    public static Dictionary<string, Users> UsersInCredentials(byte[] data)
    {
        var output = new Dictionary<string, Users>();
        if (data.Length <= 16 || (data.Length - 16) % 16 != 0) return output;
        byte[] plain;
        try
        {
            using var aes = Aes.Create();
            aes.Key = CredentialsKey;
            plain = aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.PKCS7);
        }
        catch (CryptographicException) { return output; }
        try
        {
            var reader = new Utf8JsonReader(plain);
            if (!JsonDocument.TryParseValue(ref reader, out var doc)) return output;
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return output;
                foreach (var conn in doc.RootElement.EnumerateObject())
                {
                    if (conn.Value.ValueKind != JsonValueKind.Object) continue;
                    var main = Obj(conn.Value, "#connection");
                    var db = SectionString(main, "user");
                    var ssh = SectionString(Obj(conn.Value, "network/ssh_tunnel"), "user");
                    var password = SectionString(main, "password");
                    if (db != null || ssh != null || password != null) output[conn.Name] = new Users(db, ssh, password);
                }
            }
        }
        catch (JsonException) { }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return output;
    }

    /// 사용자 이름 · 비밀번호만 읽는다 (문자열이 아니면 없음 — Mac 의 느슨한 디코딩과 같음)
    private static string? SectionString(JsonElement? section, string key) =>
        section is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

/// 아주 작은 ZIP 읽기 (DBeaver 프로젝트 내보내기 .dbp 용). Mac 은 직접 만든 ZipReader, Windows 는 .NET ZipArchive 를 쓴다.
public static class ZipReader
{
    public static bool IsZip(byte[] data) =>
        data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04;

    /// 파일 이름 → 내용. `filter` 가 true 인 항목만 푼다. 너무 큰 항목은 거절한다.
    public static Dictionary<string, byte[]> Read(byte[] data, Func<string, bool> filter, long maxEntrySize = 16 << 20)
    {
        var output = new Dictionary<string, byte[]>();
        using var zip = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        foreach (var e in zip.Entries)
        {
            if (!filter(e.FullName)) continue;
            if (e.Length > maxEntrySize) throw new NotSupportedException("zip entry too large");
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            output[e.FullName] = ms.ToArray();
        }
        return output;
    }
}
