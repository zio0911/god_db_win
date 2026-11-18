#pragma warning disable xUnit1051 // 통합 테스트는 자체 시간 제한을 쓴다
using GodDB.Core;
using GodDB.Core.Drivers;
using GodDB.Core.Redis;

namespace GodDB.Integration.Tests;

/// 큰 값 · 큰 결과 (실제 DB). Mac: MemorySafetyIntegrationTests.swift. 환경변수가 없으면 건너뛴다.
public class MemorySafetyIntegrationTests
{
    [Fact]
    public async Task BigCellsAreCappedInGridButNotInExport()
    {
        TestDb.Port(DatabaseKind.Mysql);
        await using var s = await ExportIntegrationTests.Session(DatabaseKind.Mysql);
        var table = ExportIntegrationTests.Unique("tb_big_it");
        try
        {
            await s.RawAsync($"CREATE TABLE {table} (id int PRIMARY KEY, body longtext)");
            await s.RawAsync("SET GLOBAL max_allowed_packet = 67108864");
            await s.RawAsync($"INSERT INTO {table} VALUES (1, REPEAT('가', 1500000)), (2, 'small')");
            var r = await s.ExecuteAsync($"SELECT id, body FROM {table} ORDER BY id", 500);
            Assert.Equal(1, r.TruncatedCells);
            Assert.Equal(DbSession.MaxCellCharacters + 1, r.Rows[0].Values[1]?.Length);   // 앞부분 + …
            Assert.Equal("small", r.Rows[1].Values[1]);
            // 내보내기는 자르지 않는다
            var full = await s.ExecuteAsync($"SELECT id, body FROM {table} ORDER BY id", 500, capCells: false);
            Assert.True(full.TruncatedCells == 0 && full.Rows[0].Values[1]?.Length == 1_500_000);
        }
        finally { try { await s.RawAsync(s.Dialect.DropTableSql(table)); } catch { } }
    }

    [Fact]
    public async Task PagesStreamWithoutGaps()
    {
        TestDb.Port(DatabaseKind.Mysql);
        await using var s = await ExportIntegrationTests.Session(DatabaseKind.Mysql);
        var table = ExportIntegrationTests.Unique("tb_stream_it");
        try
        {
            await s.RawAsync($"CREATE TABLE {table} (id int PRIMARY KEY)");
            await s.RawAsync("SET SESSION cte_max_recursion_depth = 100000");
            await s.RawAsync($"INSERT INTO {table} WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 3500) SELECT i FROM n");
            // 보이지 않는 페이징: 1,000행씩 이어 붙이기
            var all = new List<int>();
            var page = 0;
            while (true)
            {
                var r = await s.ExecuteAsync($"SELECT id FROM {table} ORDER BY id", 1_000, page);
                all.AddRange(r.Rows.Select(x => int.TryParse(x.Values[0], out var v) ? v : (int?)null).OfType<int>());
                if (!r.HasMore) break;
                page++;
            }
            Assert.Equal(Enumerable.Range(1, 3500), all);
        }
        finally { try { await s.RawAsync(s.Dialect.DropTableSql(table)); } catch { } }
    }

    [Fact]
    public async Task BigRedisStringIsReadPartially()
    {
        var port = TestDb.Port(DatabaseKind.Redis);
        var p = new ConnectionProfile { Kind = DatabaseKind.Redis, Name = "r", Host = "127.0.0.1", Port = port, Username = "", Database = "0", UseTLS = false };
        await using var s = await RedisSession.ConnectAsync(p, TestDb.Password);
        var id = Guid.NewGuid().ToString("N")[..8];
        string bigKey = $"goddb:it:big:{id}", smallKey = $"goddb:it:small:{id}";
        try
        {
            var big = new string('x', 3 * 1_048_576);
            await s.CommandAsync(["SET", bigKey, big]);
            var r = await s.StringPrefixAsync(bigKey);
            Assert.True(r.Truncated && r.Total == big.Length && System.Text.Encoding.UTF8.GetByteCount(r.Value) == 1_048_576);
            await s.CommandAsync(["SET", smallKey, "hi"]);
            var small = await s.StringPrefixAsync(smallKey);
            Assert.True(!small.Truncated && small.Value == "hi");
        }
        finally { try { await s.CommandAsync(["DEL", bigKey, smallKey]); } catch { } }
    }
}
