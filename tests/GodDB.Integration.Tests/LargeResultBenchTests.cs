using System.Diagnostics;
using GodDB.Core;
using GodDB.Core.Drivers;
using GodDB.Core.Schema;

namespace GodDB.Integration.Tests;

/// Mac LargeResultBenchTests.swift — 대용량 결과 측정 (GODDB_BENCH 또는 GODDB_BENCH_MSSQL_PORT 가 있을 때만). 화면을 쓰지 않는다.
/// 준비: 각 DB 에 100만 행 tb_bench_member (member_id bigint PK + 컬럼 9개) 를 만든다.
/// 실행: GODDB_BENCH=1 GODDB_IT_PORT=33306 GODDB_IT_PASSWORD=goddbtest GODDB_PG_PORT=35432 [GODDB_BENCH_MSSQL_PORT=… GODDB_BENCH_PASSWORD=…] ./scripts/test.sh
/// exportMillion 은 결과 내보내기(ResultExportWriter / exportAll) 미포팅이라 옮기지 않았다.
public class LargeResultBenchTests(ITestOutputHelper output)
{
    /// MySQL / PostgreSQL 은 test-dbs.sh 컨테이너 (GODDB_IT_PORT / GODDB_PG_PORT), SQL Server 는 GODDB_BENCH_MSSQL_PORT.
    /// 세 DB 모두 tb_bench_member 100만 행이 있어야 한다 (없으면 건너뜀).
    private static async Task<DbSession> SessionAsync(DatabaseKind kind)
    {
        if (It.Env("GODDB_BENCH") == null && It.Env("GODDB_BENCH_MSSQL_PORT") == null)
            Assert.Skip("GODDB_BENCH 없음 — 대용량 측정은 GODDB_BENCH=1 일 때만");
        DbSession s;
        switch (kind)
        {
            case DatabaseKind.Mysql:
                if (It.Env("GODDB_IT_PORT") == null) Assert.Skip("GODDB_IT_PORT 없음");
                s = await DbSession.ConnectAsync(It.MySql() with { Name = "bench" }, It.MySqlPassword);
                break;
            case DatabaseKind.Postgres:
                if (It.Env("GODDB_PG_PORT") == null) Assert.Skip("GODDB_PG_PORT 없음");
                s = await It.ConnectAsync(DatabaseKind.Postgres, "shop");
                break;
            default:
                if (It.Env("GODDB_BENCH_MSSQL_PORT") is not { } port) { Assert.Skip("GODDB_BENCH_MSSQL_PORT 없음"); return null!; }
                s = await DbSession.ConnectAsync(new ConnectionProfile
                {
                    Kind = DatabaseKind.Mssql, Name = "bench", Host = "127.0.0.1", Port = int.Parse(port),
                    Username = "sa", Database = "GODDB_BENCH", UseTLS = false, SavePassword = false,
                }, It.Env("GODDB_BENCH_PASSWORD") ?? "");
                break;
        }
        int? n = null;
        try { n = await s.CountAsync("SELECT * FROM tb_bench_member"); } catch { }
        if (n != 1_000_000)
        {
            await s.CloseAsync();
            Assert.Skip("tb_bench_member 100만 행이 없음");
        }
        return s;
    }

    private static double Rss()
    {
        using var p = Process.GetCurrentProcess();
        return p.WorkingSet64 / 1_048_576.0;
    }

    /// 화면 "제한 없음" 이어 받기: 1,000행씩 200페이지 (메모리 상한 200,000행)
    [Theory]
    [InlineData(DatabaseKind.Mysql, false)]
    [InlineData(DatabaseKind.Mysql, true)]
    [InlineData(DatabaseKind.Postgres, false)]
    [InlineData(DatabaseKind.Postgres, true)]
    [InlineData(DatabaseKind.Mssql, false)]
    [InlineData(DatabaseKind.Mssql, true)]
    public async Task GridAppend(DatabaseKind kind, bool ordered)
    {
        var sql = "SELECT * FROM tb_bench_member" + (ordered ? " ORDER BY member_id" : "");
        await using var s = await SessionAsync(kind);
        var m0 = Rss();
        var rows = new List<QueryResult.Row>();
        var t0 = Stopwatch.StartNew();
        var marks = new List<string>();
        for (int page = 0; page < 200; page++)
        {
            var p0 = Stopwatch.StartNew();
            var r = await s.ExecuteAsync(sql, limit: 1_000, page: page);
            rows.AddRange(r.Rows);
            if (page is 0 or 49 or 99 or 199) marks.Add($"p{page + 1}: {p0.ElapsedMilliseconds}ms");
        }
        var ids = rows.Select(r => r.Values[0]).OfType<string>().ToHashSet();
        Assert.Equal(200_000, ids.Count);   // 페이지 사이에 겹치거나 빠진 행 없음
        output.WriteLine($"BENCH grid {kind} [{(ordered ? "ORDER BY" : "no ORDER BY")}] rows={rows.Count} total={t0.Elapsed.TotalSeconds:F1}s  {string.Join(", ", marks)}  rss +{(int)(Rss() - m0)}MB");
    }
}
