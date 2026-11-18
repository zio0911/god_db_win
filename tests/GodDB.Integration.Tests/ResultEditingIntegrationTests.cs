#pragma warning disable xUnit1051 // 통합 테스트는 자체 시간 제한을 쓴다
using GodDB.Core;
using GodDB.Core.Results;

namespace GodDB.Integration.Tests;

/// Mac: ResultEditingTests.swift 의 MultiDBChecks.editing / binary (세 DB 공통)
public class ResultEditingIntegrationTests
{
    private static string Container(DatabaseKind kind) => kind == DatabaseKind.Postgres ? "public" : "shop";

    /// 결과 편집 → 트랜잭션 적용 → 실패 시 전부 되돌림
    [Theory]
    [InlineData(DatabaseKind.Mysql)]
    [InlineData(DatabaseKind.Postgres)]
    [InlineData(DatabaseKind.Mssql)]
    public async Task Editing(DatabaseKind kind)
    {
        TestDb.Port(kind);
        await using var s = await ExportIntegrationTests.Session(kind);
        var d = s.Dialect;
        var table = ExportIntegrationTests.Unique("tb_edit_it");
        try
        {
            // SQL Server 의 varchar 는 기본 정렬 규칙에서 한글을 못 담아서 nvarchar 를 쓴다
            var text = kind == DatabaseKind.Mssql ? "nvarchar(50)" : "varchar(50)";
            await s.RawAsync($"CREATE TABLE {table} (id int PRIMARY KEY, name {text} NULL, memo {text} NULL)");
            await s.RawAsync($"INSERT INTO {table} (id, name, memo) VALUES (1, 'a', 'x'), (2, 'b', 'y')");
            var tables = await s.LoadSchemaAsync(Container(kind));
            var sql = $"SELECT * FROM {table} ORDER BY id";
            var r = await s.ExecuteAsync(sql, 500);
            var target = ResultEditing.Target(sql, [.. r.Columns.Select(c => c.Name)], tables);

            var e = new PendingEdits();
            e.Updates[0] = new() { [1] = "가나다", [2] = null };
            e.Deletes = [1];
            e.Inserts = [new() { [0] = "3", [1] = "c" }];
            await s.ExecuteInTransactionAsync(ResultEditing.Statements(e, target, r.Rows, d));
            var after = (await s.ExecuteAsync(sql, 500)).Rows.Select(x => x.Values.ToArray()).ToList();
            Assert.Equal([["1", "가나다", null], ["3", "c", null]], after);

            // 중간에 실패하면 (중복 키) 앞의 변경도 되돌려야 한다
            var bad = new PendingEdits();
            bad.Updates[0] = new() { [1] = "바뀌면 안 됨" };
            bad.Inserts = [new() { [0] = "3", [1] = "dup" }];
            var rows2 = (await s.ExecuteAsync(sql, 500)).Rows;
            await Assert.ThrowsAnyAsync<Exception>(() => s.ExecuteInTransactionAsync(ResultEditing.Statements(bad, target, rows2, d)));
            var unchanged = (await s.ExecuteAsync(sql, 500)).Rows.Select(x => x.Values.ToArray()).ToList();
            Assert.Equal(after, unchanged);
            Assert.False(s.InTransaction);
        }
        finally { try { await s.RawAsync(d.DropTableSql(table)); } catch { } }
    }

    /// 수동 커밋 모드: 열린 트랜잭션에 합류하고 커밋하지 않는다
    [Theory]
    [InlineData(DatabaseKind.Mysql)]
    [InlineData(DatabaseKind.Postgres)]
    [InlineData(DatabaseKind.Mssql)]
    public async Task JoinsOpenTransactionInManualCommit(DatabaseKind kind)
    {
        TestDb.Port(kind);
        await using var s = await ExportIntegrationTests.Session(kind);
        var d = s.Dialect;
        var table = ExportIntegrationTests.Unique("tb_edit_tx");
        try
        {
            await s.RawAsync($"CREATE TABLE {table} (id int PRIMARY KEY)");
            s.SetAutoCommit(false);
            await s.ExecuteInTransactionAsync([$"INSERT INTO {table} (id) VALUES (1)", $"INSERT INTO {table} (id) VALUES (2)"]);
            Assert.True(s.InTransaction);
            Assert.Equal(2, s.PendingChanges);
            await s.RollbackAsync();
            s.SetAutoCommit(true);
            Assert.Equal("0", (await s.RawAsync($"SELECT COUNT(*) FROM {table}")).Rows[0][0]);
        }
        finally
        {
            if (s.InTransaction) { try { await s.RollbackAsync(); } catch { } }
            try { await s.RawAsync(d.DropTableSql(table)); } catch { }
        }
    }

    /// 바이너리 컬럼이 0x16진수로 보이고, 편집으로 바이너리 값을 저장할 수 있다
    [Theory]
    [InlineData(DatabaseKind.Mysql)]
    [InlineData(DatabaseKind.Postgres)]
    [InlineData(DatabaseKind.Mssql)]
    public async Task Binary(DatabaseKind kind)
    {
        TestDb.Port(kind);
        await using var s = await ExportIntegrationTests.Session(kind);
        var d = s.Dialect;
        var table = ExportIntegrationTests.Unique("tb_bin_it");
        try
        {
            var type = kind == DatabaseKind.Postgres ? "bytea" : "varbinary(16)";
            await s.RawAsync($"CREATE TABLE {table} (id int PRIMARY KEY, data {type} NULL)");
            await s.RawAsync($"INSERT INTO {table} (id, data) VALUES (1, {d.BinaryLiteral("CD01FF00")})");
            var sql = $"SELECT id, data FROM {table}";
            var r = await s.ExecuteAsync(sql, 500);
            Assert.Equal("0xCD01FF00", r.Rows.FirstOrDefault()?.Values[1]);

            var target = ResultEditing.Target(sql, [.. r.Columns.Select(c => c.Name)], await s.LoadSchemaAsync(Container(kind)));
            var e = new PendingEdits();
            e.Updates[0] = new() { [1] = "0x0A0B" };
            await s.ExecuteInTransactionAsync(ResultEditing.Statements(e, target, r.Rows, d));
            Assert.Equal("0x0A0B", (await s.ExecuteAsync(sql, 500)).Rows.FirstOrDefault()?.Values[1]);
        }
        finally { try { await s.RawAsync(d.DropTableSql(table)); } catch { } }
    }
}
