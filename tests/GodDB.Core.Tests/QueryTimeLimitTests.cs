using GodDB.Core.Dialects;
using GodDB.Core.Drivers;

namespace GodDB.Core.Tests;

/// Mac QueryTimeLimitTests.swift — 실행 시간 제한 기본 30분 · 서버 쪽 제한 (#198).
/// 실제 서버 세션 검사는 Integration QueryTimeLimitIntegrationTests.
public class QueryTimeLimitTests
{
    [Fact]
    public void DefaultIsThirtyMinutes()
    {
        Assert.Equal(1800, QueryTimeLimit.DefaultSeconds);
        Assert.Equal("queryTimeoutSeconds", QueryTimeLimit.Key);
    }

    [Fact]
    public void ServerSideSQL()
    {
        Assert.Equal("SET statement_timeout = 600000", new PostgresDialect().StatementTimeoutSql(600_000));
        Assert.Equal("SET statement_timeout = 1000", new PostgresDialect(redshift: true).StatementTimeoutSql(1000));
        Assert.Equal("SET SESSION max_execution_time = 600000", new MySqlDialect().StatementTimeoutSql(600_000));
        Assert.Null(new MssqlDialect().StatementTimeoutSql(600_000));
    }
}
