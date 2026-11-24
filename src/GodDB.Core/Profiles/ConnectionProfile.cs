using System.Text.Json;
using System.Text.Json.Serialization;
using GodDB.Core.Platform;
using static GodDB.Core.L10n;

namespace GodDB.Core;

/// 지원하는 DB 종류
public enum DatabaseKind { Mysql, Postgres, Mssql, Redis }

/// Redis 접속 방식
public enum RedisMode
{
    /// 단일 서버 (또는 ElastiCache 클러스터 모드 끔)
    Standalone,
    /// Redis Cluster: 노드 하나만 적으면 나머지 노드와 슬롯을 찾아 키마다 맞는 노드로 보낸다
    Cluster,
    /// Sentinel: 센티널에게 현재 마스터 주소를 물어 접속
    Sentinel,
}

/// 접속 환경 태그 (사이드바 · 탭 · 도구 막대에 색으로 표시)
public enum ConnectionEnvironment { None, Development, Staging, Production }

public static class DatabaseKindExtensions
{
    public static string Title(this DatabaseKind k) => k switch
    {
        DatabaseKind.Mysql => "MySQL",
        DatabaseKind.Postgres => "PostgreSQL",
        DatabaseKind.Mssql => "SQL Server",
        _ => "Redis",
    };

    /// 사이드바·탭 배지
    public static string Badge(this DatabaseKind k) => k switch
    {
        DatabaseKind.Mysql => "MY",
        DatabaseKind.Postgres => "PG",
        DatabaseKind.Mssql => "MSSQL",
        _ => "RD",
    };

    public static int DefaultPort(this DatabaseKind k) => k switch
    {
        DatabaseKind.Mysql => 3306,
        DatabaseKind.Postgres => 5432,
        DatabaseKind.Mssql => 1433,
        _ => 6379,
    };

    /// 사이드바 DB 선택 드롭다운이 의미하는 것
    public static string ContainerTitle(this DatabaseKind k) => k == DatabaseKind.Postgres ? Tr("스키마") : "DB";

    /// SQL DB 인지 (Redis 는 아님)
    public static bool IsSql(this DatabaseKind k) => k != DatabaseKind.Redis;

    public static string Title(this RedisMode m) => m switch
    {
        RedisMode.Standalone => Tr("단일"),
        RedisMode.Cluster => "Cluster",
        _ => "Sentinel",
    };

    public static string Title(this ConnectionEnvironment e) => e switch
    {
        ConnectionEnvironment.None => Tr("표시 안 함"),
        ConnectionEnvironment.Development => Tr("개발"),
        ConnectionEnvironment.Staging => Tr("스테이징"),
        _ => Tr("운영"),
    };
}

public sealed record SshConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "ec2-user";
    /// 개인키 파일 경로. 비어 있으면 ssh-agent / ~/.ssh/config 설정을 따른다.
    public string KeyPath { get; set; } = "";
}

/// 저장되는 접속 정보. 비밀번호는 여기 담지 않고 자격 증명 관리자에 따로 보관한다.
/// JSON 형식은 Mac 판 profiles.json 과 같다 (접속 정보 파일을 서로 주고받을 수 있음).
public sealed record ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DatabaseKind Kind { get; set; } = DatabaseKind.Mysql;
    public string Name { get; set; } = Tr("새 접속");
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3306;
    public string Username { get; set; } = "";
    public string Database { get; set; } = "";
    public bool UseTLS { get; set; } = true;
    public bool ReadOnly { get; set; }
    public SshConfig? Ssh { get; set; }
    /// true 면 비밀번호를 자격 증명 관리자에 저장, false 면 연결할 때마다 묻는다.
    public bool SavePassword { get; set; } = true;
    public RedisMode RedisMode { get; set; } = RedisMode.Standalone;
    /// Sentinel 마스터 이름 (예: mymaster)
    public string SentinelMaster { get; set; } = "";
    /// 사이드바 그룹 이름 (비우면 그룹 없음). 한 단계만.
    public string Group { get; set; } = "";
    public ConnectionEnvironment Environment { get; set; } = ConnectionEnvironment.None;
    /// SQL Server Windows 통합 인증 (Windows 판에만 있는 항목. Mac 판은 이 칸을 무시한다)
    public bool WindowsAuth { get; set; }
    /// TLS 서버 인증서 확인 (Mac tlsVerify. 예전 파일에 없으면 true). DBeaver 가져오기가 "확인 안 함" 설정을 옮긴다
    public bool TlsVerify { get; set; } = true;
    /// Amazon Redshift (Kind 는 Postgres — PostgreSQL 프로토콜로 연결). 아이콘 · 종류 표시용,
    /// 실제 Redshift 여부는 연결할 때 서버 버전으로 다시 알아낸다.
    public bool Redshift { get; set; }
    /// 마지막으로 연결에 성공한 시각 (사이드바 "최근 연결순" 정렬)
    public DateTimeOffset? LastConnectedAt { get; set; }
    /// 사용자가 신뢰한 서버 인증서(맨 앞, DER)의 SHA-256 "3F:A2:…" (없으면 null)
    [JsonPropertyName("tlsPinSHA256")]
    public string? TlsPinSHA256 { get; set; }

    /// 비밀번호가 필요한 접속인지 (Windows 인증이면 필요 없음)
    [JsonIgnore]
    public bool NeedsPassword => !(Kind == DatabaseKind.Mssql && WindowsAuth);

    /// 화면에 보이는 DB 종류 (Redshift 포함). 바꾸면 포트(이전 종류의 기본값일 때) · Redshift 표시 · TLS 를 맞춘다
    [JsonIgnore]
    public ConnectionType Type
    {
        get => ConnectionTypes.Of(Kind, Redshift);
        set
        {
            var old = Type;
            if (value == old) return;
            // 포트가 이전 종류의 기본값이면 새 종류의 기본값으로
            if (Port == old.DefaultPort()) Port = value.DefaultPort();
            Kind = value.Kind();
            Redshift = value == ConnectionType.Redshift;
            // Redshift 는 TLS 가 기본
            if (value == ConnectionType.Redshift) UseTLS = true;
        }
    }

    /// 표시용 종류 이름 (Redshift 면 "Redshift")
    [JsonIgnore]
    public string TypeTitle => Type.Title();
}

/// 접속 목록을 %APPDATA%\GodDB\profiles.json 에 저장한다.
public sealed class ProfileStore(string? filePath = null)
{
    public string FilePath { get; } = filePath ?? AppPaths.ProfilesFile;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // lastConnectedAt: Mac profiles.json(JSONEncoder 기본값)과 같은 "2001-01-01 부터 초" 숫자
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new ReferenceDateConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public List<ConnectionProfile> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var text = File.ReadAllText(FilePath);
            // 예전 버전이 만든 파일(상속 권한)도 나만 읽게 맞춘다 (Mac: 0644 → 0600)
            if (!UserOnlyFile.IsRestrictedSafe(FilePath)) UserOnlyFile.Restrict(FilePath);
            return JsonSerializer.Deserialize<List<ConnectionProfile>>(text, Json) ?? [];
        }
        catch { return []; }
    }

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        AppPaths.Ensure(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(profiles.ToList(), Json));
        // 접속 목록(호스트 · 계정 · SSH 키 경로)은 나만 읽고 쓰게 (Mac: 0600). 비밀번호는 자격 증명 관리자에 따로 있다
        UserOnlyFile.Restrict(tmp);
        File.Move(tmp, FilePath, overwrite: true);
    }
}
