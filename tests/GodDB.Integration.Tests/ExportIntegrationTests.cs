#pragma warning disable xUnit1051 // 통합 테스트는 자체 시간 제한을 쓴다
using System.Text;
using GodDB.Core;
using GodDB.Core.Drivers;
using GodDB.Core.Results;

namespace GodDB.Integration.Tests;

/// 여러 페이지짜리 결과를 페이지 단위로 전체 내보내기 (실제 DB). Mac: ExportIntegrationTests.swift
/// 다른 테스트와 같은 DB 를 함께 쓰므로 테이블 이름은 실행마다 다르게 만들고 끝나면 지운다.
public class ExportIntegrationTests
{
    public const int Rows = 12_345;

    internal static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    internal static async Task<DbSession> Session(DatabaseKind kind)
    {
        if (kind == DatabaseKind.Mssql)
        {
            await using var admin = await DbSession.ConnectAsync(TestDb.Profile(kind, "master"), TestDb.PasswordFor(kind));
            // 여러 테스트가 동시에 만들 수 있어 이미 있다는 오류는 무시한다
            try { await admin.RawAsync("IF DB_ID('shop') IS NULL CREATE DATABASE shop"); } catch { }
        }
        return await DbSession.ConnectAsync(TestDb.Profile(kind, "shop"), TestDb.PasswordFor(kind));
    }

    private static async Task Seed(DbSession s, DatabaseKind kind, string table)
    {
        const int n = Rows;
        switch (kind)
        {
            case DatabaseKind.Mysql:
                await s.RawAsync($"CREATE TABLE {table} (id int PRIMARY KEY, name varchar(50))");
                await s.RawAsync("SET SESSION cte_max_recursion_depth = 100000");
                await s.RawAsync($"INSERT INTO {table} WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {n}) SELECT i, CONCAT('이름', i) FROM n");
                break;
            case DatabaseKind.Postgres:
                await s.RawAsync($"CREATE TABLE {table} (id int PRIMARY KEY, name varchar(50))");
                await s.RawAsync($"INSERT INTO {table} SELECT i, '이름' || i FROM generate_series(1, {n}) i");
                break;
            case DatabaseKind.Mssql:
                await s.RawAsync($"CREATE TABLE {table} (id int PRIMARY KEY, name nvarchar(50))");
                await s.RawAsync($"""
                    INSERT INTO {table} SELECT TOP ({n}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), N'이름' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS nvarchar(10))
                    FROM sys.all_objects a CROSS JOIN sys.all_objects b
                    """);
                break;
        }
    }

    [Theory]
    [InlineData(DatabaseKind.Mysql)]
    [InlineData(DatabaseKind.Postgres)]
    [InlineData(DatabaseKind.Mssql)]
    public async Task ExportsEveryRowAcrossPages(DatabaseKind kind)
    {
        TestDb.Port(kind);
        await using var s = await Session(kind);
        var table = Unique("tb_export_it");
        var path = Path.Combine(Path.GetTempPath(), $"goddb-export-it-{table}.csv");
        try
        {
            await Seed(s, kind, table);
            var sql = $"SELECT id, name FROM {table}";
            var plan = ResultExportPlan.StableSort(sql, null, ["id", "name"], ["id"]);
            Assert.True(plan.Stable);
            Assert.Equal(Rows, await s.CountAsync(sql));

            var w = new ResultExportWriter(ExportFormat.Csv, ["id", "name"], path, new ExportOptions { ExcelBom = false });
            var reported = 0;
            var truncated = await s.ExportAllAsync(sql, plan.Sort, w, 5_000, new SyncProgress(n => reported = n));
            w.Finish();
            Assert.False(truncated);
            Assert.Equal(Rows, w.RowsWritten);
            Assert.Equal(Rows, reported);

            var lines = File.ReadAllText(path, Encoding.UTF8).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
            var ids = lines.Select(l => int.TryParse(l.Split(',')[0], out var v) ? v : (int?)null).OfType<int>().ToList();
            Assert.Equal(Rows, ids.Count);
            Assert.Equal(Rows, ids.Distinct().Count());              // 겹친 행 없음
            Assert.Equal(Enumerable.Range(1, Rows), ids);             // 빠진 행 없음, 기본 키 순서
            Assert.Equal("1,이름1", lines[0]);
        }
        finally
        {
            try { await s.RawAsync(s.Dialect.DropTableSql(table)); } catch { }
            File.Delete(path);
        }
    }

    /// 전체 내보내기 흐름 (COUNT 확인 → 페이지 조회 → 저장)과 Excel 최대 행 자르기 대신 쓰는 진행 보고
    [Fact]
    public async Task RunnerExportsAllWithCountCheck()
    {
        TestDb.Port(DatabaseKind.Postgres);
        await using var s = await Session(DatabaseKind.Postgres);
        var table = Unique("tb_export_run");
        var path = Path.Combine(Path.GetTempPath(), $"goddb-export-run-{table}.json");
        try
        {
            await s.RawAsync($"CREATE TABLE {table} (id int PRIMARY KEY, name varchar(50))");
            await s.RawAsync($"INSERT INTO {table} SELECT i, 'n' || i FROM generate_series(1, 7000) i");
            var sql = $"SELECT id, name FROM {table}";
            var first = await s.ExecuteAsync(sql, 500);
            first.Sql = sql;
            var req = ExportRequest.Prepare(first, DatabaseKind.Postgres, await s.LoadSchemaAsync("public"), DateTimeOffset.UtcNow)!;
            Assert.True(req.CanFetchAll && req.Stable);
            var choice = new ExportChoice { Format = ExportFormat.Json, FetchAll = true };
            var last = new List<ExportProgress>();
            var o = await ResultExportRunner.RunAsync(s, null, req, choice, path, new SyncProgress<ExportProgress>(last.Add));
            Assert.Equal(ExportOutcomeKind.Success, o.Kind);
            Assert.Equal(7000, o.Written);
            Assert.Equal(7000, o.Total);
            Assert.Equal("JSON · 전체 7000행 · COUNT 일치", o.AuditResult);
            Assert.Equal(7000, last[^1].Written);
            Assert.Equal(7000, System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetArrayLength());
        }
        finally
        {
            try { await s.RawAsync(s.Dialect.DropTableSql(table)); } catch { }
            File.Delete(path);
        }
    }

    /// PostgreSQL: ORDER BY 없는 페이지 조회가 어긋나지 않도록 순차 스캔이 항상 처음부터 시작해야 한다
    [Fact]
    public async Task PostgresSessionReadsFromStart()
    {
        TestDb.Port(DatabaseKind.Postgres);
        await using var s = await Session(DatabaseKind.Postgres);
        var r = await s.ExecuteAsync("SHOW synchronize_seqscans", 0);
        Assert.Equal("off", r.Rows.FirstOrDefault()?.Values.FirstOrDefault());
    }
}

/// 바로 부르는 IProgress (Progress&lt;T&gt; 는 스레드 풀로 늦게 보낸다)
internal sealed class SyncProgress(Action<int> report) : IProgress<int>
{
    public void Report(int value) => report(value);
}

internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
