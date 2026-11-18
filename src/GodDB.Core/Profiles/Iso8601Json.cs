using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodDB.Core.Profiles;

/// Swift JSONEncoder `.iso8601` 과 같은 날짜 형식: UTC, 초 단위, "2026-11-13T05:46:25Z".
/// Mac 판 `.iso8601` 디코더는 소수 초가 있으면 읽지 못하므로 쓸 때는 반드시 초까지만 쓴다.
public sealed class Iso8601SecondsConverter : JsonConverter<DateTimeOffset>
{
    public static string Format(DateTimeOffset d) =>
        d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? throw new JsonException("date");
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return d;
        throw new JsonException("date: " + s);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Format(value));
}

/// DateTime (로컬 시각으로 읽음) 판
public sealed class Iso8601SecondsDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? throw new JsonException("date");
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return d.LocalDateTime;
        throw new JsonException("date: " + s);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Iso8601SecondsConverter.Format(new DateTimeOffset(value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Local) : value)));
}
