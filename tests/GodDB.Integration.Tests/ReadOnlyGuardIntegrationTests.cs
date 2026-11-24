#pragma warning disable xUnit1051 // 통합 테스트는 자체 시간 제한을 쓴다
using GodDB.Core;
using GodDB.Core.Drivers;
using GodDB.Core.Sql;

namespace GodDB.Integration.Tests;

/// Mac ReadOnlyGuardIntegrationTests (ReadOnlyGuardTests.swift) — 실제 서버에서 읽기전용 우회가 막히는지.
/// (Mac 은 tb_ro_guard_it 를 쓰지만, 다른 테스트와 겹치지 않게 이름 뒤에 무작위 접미사를 붙이고 끝나면 지운다.
///  mssqlBrokerAndSequence 는 서버 없이 도는 검사라 GodDB.Core.Tests ReadOnlyGuardTests 에 있다)
public class ReadOnlyGuardIntegrationTests
{
    /// 쓰기 가능한 접속으로 한 행짜리 테이블을 만들고, 같은 주소의 읽기전용 접속을 연다
    private static async Task<(DbSession Admin, DbSession Ro, string Table)> SessionsAsync(DatabaseKind kind, string database)
    {
        var (p, pw) = It.Multi(kind, database);
        var table = "tb_ro_guard_" + It.Suffix();
        var admin = await DbSession.ConnectAsync(p, pw);
        await admin.RawAsync($"CREATE TABLE {table} (id int PRIMARY KEY)");
        await admin.RawAsync($"INSERT INTO {table} (id) VALUES (1)");
        return (admin, await DbSession.ConnectAsync(p with { ReadOnly = true }, pw), table);
    }

    private static async Task<string?> RowCountAsync(DbSession s, string table) =>
        (await s.RawAsync($"SELECT COUNT(*) FROM {table}")).Rows.FirstOrDefault()?[0];

    [Fact]
    public async Task MssqlRefusesBackToBackDelete()
    {
        TestDb.Port(DatabaseKind.Mssql);
        var (admin, ro, table) = await SessionsAsync(DatabaseKind.Mssql, "master");
        try
        {
            var e = await Assert.ThrowsAsync<GodDbException>(() => ro.ExecuteAsync($"SELECT 1 DELETE FROM {table}", 0));
            Assert.Equal(DbErrorKind.ReadOnlyRejected, e.Kind);
            Assert.Equal(ReadOnlyViolation.Keyword("DELETE"), e.Violation);
            await Assert.ThrowsAsync<GodDbException>(() => ro.ExecuteAsync($"SELECT * INTO {table}_copy FROM {table}", 0));
            // 멀쩡한 조회는 된다
            var r = await ro.ExecuteAsync($"SELECT id FROM {table}", 0);
            Assert.Single(r.Rows);
            Assert.Equal("1", await RowCountAsync(admin, table));
        }
        finally
        {
            await ro.CloseAsync();
            try { await admin.RawAsync($"DROP TABLE IF EXISTS {table}"); } catch { }
            await admin.CloseAsync();
        }
    }

    [Fact]
    public async Task PostgresRefusesSessionReadOnlyOff()
    {
        TestDb.Port(DatabaseKind.Postgres);
        var (admin, ro, table) = await SessionsAsync(DatabaseKind.Postgres, "shop");
        try
        {
            const string off = "SELECT set_config('default_transaction_read_only', 'off', false)";
            var e = await Assert.ThrowsAsync<GodDbException>(() => ro.ExecuteAsync(off, 0));
            Assert.Equal(ReadOnlyViolation.Function("set_config"), e.Violation);
            // 앱 검사를 건너뛰어도: 문장마다 BEGIN READ ONLY … ROLLBACK 이라 세션 설정이 남지 않는다
            await ro.ReadOnlyGuardedRawAsync(off);
            Assert.Equal("on", (await ro.RawAsync("SHOW default_transaction_read_only")).Rows.FirstOrDefault()?[0]);
            // 세션의 READ ONLY 를 (가드 밖에서) 직접 꺼도 감싼 트랜잭션은 읽기전용이라 쓰기가 거부된다
            await ro.RawAsync("SET SESSION CHARACTERISTICS AS TRANSACTION READ WRITE");
            await Assert.ThrowsAnyAsync<Exception>(() => ro.ReadOnlyGuardedRawAsync($"DELETE FROM {table}"));
            // 트랜잭션 안에서 읽기전용을 끄려 해도 거부된다
            await Assert.ThrowsAnyAsync<Exception>(() => ro.ReadOnlyGuardedRawAsync("SELECT set_config('transaction_read_only', 'off', true)"));
            // 조회는 그대로 된다
            var r = await ro.ExecuteAsync($"SELECT id FROM {table}", 0);
            Assert.Single(r.Rows);
            Assert.Equal("1", await RowCountAsync(admin, table));
        }
        finally
        {
            await ro.CloseAsync();
            try { await admin.RawAsync($"DROP TABLE IF EXISTS {table}"); } catch { }
            await admin.CloseAsync();
        }
    }
}

/// Mac MSSQLPartialResultTests.swift — SQL Server: 행을 읽는 도중 연결이 끊기면 받은 데까지를 정상 결과처럼 돌려주지 않는다 (#192).
/// Mac 은 FreeTDS 가 "행 없음" 으로 끝내는 경우를 따로 막았고, SqlClient 는 읽는 도중 끊기면 예외를 던진다 — 같은 결과인지 확인한다.
public class MSSQLPartialResultTests
{
    [Fact]
    public async Task KilledWhileReadingIsAnErrorNotFewerRows()
    {
        TestDb.Port(DatabaseKind.Mssql);
        await using var reader = await It.ConnectAsync(DatabaseKind.Mssql, "master");
        await using var killer = await It.ConnectAsync(DatabaseKind.Mssql, "master");
        var spid = (await reader.RawAsync("SELECT @@SPID")).Rows.FirstOrDefault()?[0];
        Assert.NotNull(spid);

        const int total = 2_000_000;
        var big = $"SELECT TOP {total} a.object_id FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c";
        var reading = Task.Run(async () =>
        {
            try { return (Rows: (await reader.RawAsync(big)).Rows.Count, Error: (Exception?)null); }
            catch (Exception e) { return (Rows: -1, Error: e); }
        });
        await Task.Delay(700);
        try { await killer.RawAsync($"KILL {spid}"); } catch { }

        var result = await reading;
        // 끊기기 전에 다 읽었을 수는 있다 — 그렇다면 전부여야 한다 (일부만은 안 됨)
        if (result.Error == null) Assert.True(result.Rows == total, $"읽는 도중 끊겼는데 {result.Rows}행을 정상 결과로 돌려줌");
    }

    /// 읽는 도중 서버 오류(0 으로 나누기)도 일부 행이 아니라 오류
    [Fact]
    public async Task ServerErrorMidStreamIsAnError()
    {
        TestDb.Port(DatabaseKind.Mssql);
        await using var s = await It.ConnectAsync(DatabaseKind.Mssql, "master");
        await Assert.ThrowsAnyAsync<Exception>(() => s.RawAsync("SELECT 10 / (v - 3) FROM (VALUES (1), (2), (3), (4)) t(v)"));
    }
}

/// Mac PasswordProviderTests.swift — 저장된 비밀번호는 연결 뒤 세션이 들고 있지 않고, 새 연결이 필요할 때 다시 읽는다 (1.4.1).
/// Mac 은 AI 데이터 찾기의 별도 연결(sideConnection)로 확인한다. 그 기능은 다른 포팅 작업이라, 같은 길(LiveEndpoint —
/// 취소 · 다시 연결 · 별도 연결이 새 연결을 열 때 쓰는 주소)로 새 드라이버를 열어 본다.
public class PasswordProviderTests
{
    private static ConnectionProfile MySql() => It.MySql() with { Name = "pw", UseTLS = false };

    [Fact]
    public async Task SideConnectionReadsPasswordFromProvider()
    {
        TestDb.Port(DatabaseKind.Mysql);
        var pw = It.MySqlPassword;
        var calls = 0;
        await using var s = await DbSession.ConnectAsync(MySql(), pw, () => { Interlocked.Increment(ref calls); return pw; });
        Assert.Equal(0, calls);                       // 처음 연결은 받은 값으로
        var e = s.LiveEndpoint()!;
        await using (var side = await MySqlDriver.ConnectAsync(e)) await side.QueryAsync("SELECT 1");
        Assert.Equal(1, calls);                       // 별도 연결은 다시 읽어서
    }

    [Fact]
    public async Task SessionDoesNotKeepPasswordWhenProviderGiven()
    {
        // 다시 읽어 온 값이 틀리면 별도 연결이 실패한다 = 세션이 처음 비밀번호를 들고 있지 않다
        TestDb.Port(DatabaseKind.Mysql);
        await using var s = await DbSession.ConnectAsync(MySql(), It.MySqlPassword, () => "wrong-password");
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var side = await MySqlDriver.ConnectAsync(s.LiveEndpoint()!);
            await side.QueryAsync("SELECT 1");
        });
    }

    [Fact]
    public async Task WithoutProviderSessionKeepsPassword()
    {
        // 이번 실행 중에만 기억하는 비밀번호(제공자 없음)는 세션이 들고 있어 별도 연결도 된다
        TestDb.Port(DatabaseKind.Mysql);
        await using var s = await DbSession.ConnectAsync(MySql(), It.MySqlPassword);
        await using var side = await MySqlDriver.ConnectAsync(s.LiveEndpoint()!);
        await side.QueryAsync("SELECT 1");
    }
}
