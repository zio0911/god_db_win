using System.Diagnostics;
using GodDB.Core;
using GodDB.Core.Drivers;

namespace GodDB.Integration.Tests;

/// Mac CancelIntegrationTests.swift — 실행 중인 쿼리 취소 (실제 DB). 환경변수가 없으면 건너뛴다.
public class CancelIntegrationTests
{
    private static string SleepSql(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Mysql => "SELECT SLEEP(20)",
        DatabaseKind.Postgres => "SELECT pg_sleep(20)",
        _ => "WAITFOR DELAY '00:00:20'",
    };

    [Theory]
    [InlineData(DatabaseKind.Mysql)]
    [InlineData(DatabaseKind.Postgres)]
    [InlineData(DatabaseKind.Mssql)]
    public async Task CancelLongQuery(DatabaseKind kind)
    {
        TestDb.Port(kind);
        await using var s = await It.SessionAsync(kind);
        var started = Stopwatch.StartNew();
        var running = Task.Run(() => s.ExecuteAsync(SleepSql(kind), limit: 0));
        await Task.Delay(800);
        Assert.True(s.IsRunning);
        Assert.True(await s.CancelRunningAsync());
        var cancelled = false;
        try { await running; }
        catch (GodDbException e) when (e.Kind == DbErrorKind.Cancelled) { cancelled = true; }
        catch { }
        Assert.True(cancelled);   // MySQL SLEEP 처럼 오류 없이 끝나도 취소로 본다
        Assert.True(started.Elapsed.TotalSeconds < 10);
        // 취소 뒤에도 같은 세션으로 계속 쓸 수 있다 (SQL Server 는 다시 연결됨)
        var r = await s.RawAsync("SELECT 1");
        Assert.Equal("1", r.Rows.FirstOrDefault()?[0]);
        Assert.False(s.IsRunning);
        Assert.False(await s.CancelRunningAsync());
    }
}
