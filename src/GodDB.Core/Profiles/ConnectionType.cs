using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodDB.Core;

/// 새 접속 화면에서 고르는 DB 종류. Redshift 는 PostgreSQL 프로토콜이라 Kind 는 Postgres + Redshift 표시. (Mac ConnectionType)
public enum ConnectionType { Mysql, Postgres, Redshift, Mssql, Redis }

public static class ConnectionTypes
{
    /// 새 접속 화면 · DB 종류순 정렬 순서 (Mac ConnectionType.allCases)
    public static readonly IReadOnlyList<ConnectionType> All =
        [ConnectionType.Mysql, ConnectionType.Postgres, ConnectionType.Redshift, ConnectionType.Mssql, ConnectionType.Redis];

    public static ConnectionType Of(DatabaseKind kind, bool redshift) => kind switch
    {
        DatabaseKind.Mysql => ConnectionType.Mysql,
        DatabaseKind.Postgres => redshift ? ConnectionType.Redshift : ConnectionType.Postgres,
        DatabaseKind.Mssql => ConnectionType.Mssql,
        _ => ConnectionType.Redis,
    };

    public static DatabaseKind Kind(this ConnectionType t) => t switch
    {
        ConnectionType.Mysql => DatabaseKind.Mysql,
        ConnectionType.Postgres or ConnectionType.Redshift => DatabaseKind.Postgres,
        ConnectionType.Mssql => DatabaseKind.Mssql,
        _ => DatabaseKind.Redis,
    };

    public static string Title(this ConnectionType t) => t == ConnectionType.Redshift ? "Redshift" : t.Kind().Title();

    public static int DefaultPort(this ConnectionType t) => t == ConnectionType.Redshift ? 5439 : t.Kind().DefaultPort();
}

/// Swift JSONEncoder 기본 날짜 형식: 2001-01-01 00:00:00 UTC 부터 초 (소수). Mac profiles.json 의 lastConnectedAt.
/// 읽을 때는 ISO 8601 글자도 받는다 (내보내기 파일 형식).
public sealed class ReferenceDateConverter : JsonConverter<DateTimeOffset>
{
    public static readonly DateTimeOffset Reference = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return Reference.AddSeconds(reader.GetDouble());
        var s = reader.GetString() ?? throw new JsonException("date");
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return d;
        throw new JsonException("date: " + s);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((value.ToUniversalTime() - Reference).TotalSeconds);
}
