#pragma warning disable xUnit1051 // 통합 테스트는 자체 시간 제한을 쓴다
using System.Text;
using GodDB.Core;
using GodDB.Core.Results;

namespace GodDB.Integration.Tests;

/// Mac ExportSnapshotTests.swift — 전체 내보내기: 페이지 사이에 다른 연결이 앞쪽에 행을 넣어도,
/// 처음 시점 그대로 겹침 · 빠짐 없이 (#199)
public class ExportSnapshotTests
{
    private static async Task Run(DatabaseKind kind)
    {
        TestDb.Port(kind);
        await using var admin = await ExportIntegrationTests.Session(kind);
        await using var reader = await ExportIntegrationTests.Session(kind);
        var table = "goddb_snap_" + It.Suffix();
        var path = Path.Combine(Path.GetTempPath(), $"goddb-snap-{Guid.NewGuid():N}.csv");
        try
        {
            await admin.RawAsync($"CREATE TABLE {table} (id INT PRIMARY KEY)");
            await admin.RawAsync($"INSERT INTO {table} VALUES (10), (20), (30), (40), (50), (60)");

            var w = new ResultExportWriter(ExportFormat.Csv, ["id"], path, new ExportOptions { ExcelBom = false });
            var inserted = false;
            await reader.ExportAllAsync($"SELECT id FROM {table} ORDER BY id", null, w, chunk: 2, new SyncProgress(written =>
            {
                // 첫 페이지를 받은 뒤 다른 연결에서 맨 앞에 두 행 추가 → OFFSET 이 두 칸 밀린다
                if (written != 2 || inserted) return;
                inserted = true;
                Task.Run(() => admin.RawAsync($"INSERT INTO {table} VALUES (1), (2)")).GetAwaiter().GetResult();
            }));
            w.Finish();
            var lines = File.ReadAllText(path, Encoding.UTF8).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
            Assert.Equal(["10", "20", "30", "40", "50", "60"], lines);
            Assert.True(inserted);
        }
        finally
        {
            try { await admin.RawAsync($"DROP TABLE IF EXISTS {table}"); } catch { }
            File.Delete(path);
        }
    }

    [Fact]
    public Task Postgres() => Run(DatabaseKind.Postgres);

    [Fact]
    public Task Mysql() => Run(DatabaseKind.Mysql);
}
