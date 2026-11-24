#pragma warning disable xUnit1051 // 통합 테스트는 자체 시간 제한을 쓴다
using GodDB.Core;

namespace GodDB.Integration.Tests;

/// Mac PostgresQuoteTests.sameValueEitherSetting — 실제 서버: standard_conforming_strings 를 꺼도 켜도 같은 값으로 읽힌다 (#197)
public class PostgresQuoteIntegrationTests
{
    [Fact]
    public async Task SameValueEitherSetting()
    {
        TestDb.Port(DatabaseKind.Postgres);
        await using var s = await ExportIntegrationTests.Session(DatabaseKind.Postgres);
        const string value = @"C:\temp\a'b";
        foreach (var setting in new[] { "on", "off" })
        {
            await s.RawAsync($"SET standard_conforming_strings = {setting}");
            var got = (await s.RawAsync("SELECT " + s.Dialect.QuoteString(value))).Rows.FirstOrDefault()?.FirstOrDefault();
            Assert.True(got == value, $"standard_conforming_strings = {setting}: {got}");
        }
    }
}
