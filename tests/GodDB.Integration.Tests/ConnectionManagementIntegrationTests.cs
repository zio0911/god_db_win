using GodDB.Core;
using GodDB.Core.Dialects;
using GodDB.Core.Drivers;
using GodDB.Core.Schema;

namespace GodDB.Integration.Tests;

/// 접속 시간 제한(ConnectDeadline) 경로로 실제 DB 에 연결 · Redshift 자동 감지가 일반 PostgreSQL 을 Redshift 로 보지 않는지
public class ConnectionManagementIntegrationTests
{
    [Theory]
    [InlineData(DatabaseKind.Mysql)]
    [InlineData(DatabaseKind.Postgres)]
    [InlineData(DatabaseKind.Mssql)]
    public async Task ConnectsWithinDeadlineAndKeepsSessionSetup(DatabaseKind kind)
    {
        var profile = TestDb.Profile(kind);
        await using var s = await DbSession.ConnectAsync(profile, TestDb.PasswordFor(kind), timeout: 10);
        Assert.True(s.IsConnected);
        Assert.False(s.Dialect.IsRedshift());
        var version = await s.ServerVersionAsync();
        Assert.StartsWith(kind.Title(), version);
        if (kind == DatabaseKind.Postgres)
        {
            // PostgreSQL 은 세션 설정(synchronize_seqscans = off)을 그대로 한다
            var v = (await s.RawAsync("SHOW synchronize_seqscans")).Rows.Single()[0];
            Assert.Equal("off", v);
        }
    }

    /// 읽기전용 세션 설정 오류 등으로 연결이 실패해도 드라이버를 남기지 않고, 비밀번호 오류는 그대로 인증 실패로 보인다
    [Fact]
    public async Task WrongPasswordIsAuthFailureNotTimeout()
    {
        var profile = TestDb.Profile(DatabaseKind.Postgres);
        var e = await Assert.ThrowsAnyAsync<Exception>(() => DbSession.ConnectAsync(profile, "wrong-password", timeout: 10));
        Assert.IsNotType<DbConnectTimeoutException>(e);
        Assert.True(GodDB.Core.Audit.ErrorText.IsAuthFailure(e));
    }

    /// PostgreSQL 프로시저 정의는 pg_get_functiondef 로 전체 (Mac #175 회귀가 Windows 에 없는지 실제 DB 로)
    [Fact]
    public async Task PostgresProcedureDefinitionIsFull()
    {
        var name = "p_" + It.Suffix();
        await using var s = await DbSession.ConnectAsync(TestDb.Profile(DatabaseKind.Postgres), TestDb.Password, timeout: 10);
        await s.UseAsync("public");
        try
        {
            await s.RawAsync($"CREATE PROCEDURE {name}() LANGUAGE plpgsql AS $$ BEGIN PERFORM 1; END $$");
            var def = await s.DefinitionAsync(new RoutineMeta(RoutineKind.Procedure, name));
            Assert.NotNull(def);
            Assert.Contains("CREATE OR REPLACE PROCEDURE", def);
            Assert.Contains("PERFORM 1", def);
        }
        finally
        {
            try { await s.RawAsync($"DROP PROCEDURE IF EXISTS {name}()"); } catch { }
        }
    }
}
