#pragma warning disable xUnit1051 // 통합 테스트는 자체 시간 제한을 쓴다
using GodDB.Core;
using GodDB.Core.Drivers;

namespace GodDB.Integration.Tests;

/// Mac QueryTimeLimitTests — 실제 서버: 연결하면 세션에 제한이 걸려 있다 (#198)
/// (설정을 바꾸지 않았으면 기본 30분. 설정 파일에 값이 있으면 건너뛴다 — Mac 과 같은 조건)
public class QueryTimeLimitIntegrationTests
{
    private static void NeedDefault()
    {
        if (Prefs.Contains(QueryTimeLimit.Key)) Assert.Skip("실행 시간 제한 설정이 바뀌어 있음");
    }

    [Fact]
    public async Task PostgresSessionHasTimeout()
    {
        TestDb.Port(DatabaseKind.Postgres);
        NeedDefault();
        await using var s = await ExportIntegrationTests.Session(DatabaseKind.Postgres);
        Assert.Equal("30min", (await s.RawAsync("SHOW statement_timeout")).Rows.FirstOrDefault()?.FirstOrDefault());
    }

    [Fact]
    public async Task MysqlSessionHasTimeout()
    {
        TestDb.Port(DatabaseKind.Mysql);
        NeedDefault();
        await using var s = await ExportIntegrationTests.Session(DatabaseKind.Mysql);
        Assert.Equal("1800000", (await s.RawAsync("SELECT @@SESSION.max_execution_time")).Rows.FirstOrDefault()?.FirstOrDefault());
    }
}
