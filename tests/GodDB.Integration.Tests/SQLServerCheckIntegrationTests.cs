#pragma warning disable xUnit1051 // 통합 테스트는 자체 시간 제한을 쓴다
using GodDB.Core;
using GodDB.Core.Drivers;

namespace GodDB.Integration.Tests;

/// 실제 DB 에 검사만 맡기기 (실행하지 않음). Mac SQLLintTests.swift 의 "SQL server check".
public class SQLServerCheckIntegrationTests
{
    [Theory]
    [InlineData(DatabaseKind.Mysql)]
    [InlineData(DatabaseKind.Postgres)]
    [InlineData(DatabaseKind.Mssql)]
    public async Task ChecksWithoutRunning(DatabaseKind kind)
    {
        await using var s = await ExportIntegrationTests.Session(kind);
        var t = "goddb_lint_" + It.Suffix();
        await s.RawAsync($"CREATE TABLE {t} (id int, name varchar(20))");
        try
        {
            await s.RawAsync($"INSERT INTO {t} (id, name) VALUES (1, 'a')");
            // 올바른 문장: 오류 없음, DELETE 는 실행되지 않아야 한다
            var ok = await s.ServerCheckAsync($"DELETE FROM {t} WHERE id = 1");
            Assert.NotNull(ok);
            Assert.Null(ok!.Failure);
            var count = (await s.RawAsync($"SELECT COUNT(*) FROM {t}")).Rows.First()[0];
            Assert.True(count == "1", "검사가 DELETE 를 실행함");

            // 없는 컬럼
            var sql = $"SELECT a.id, a.username FROM {t} a";
            var bad = await s.ServerCheckAsync(sql);
            var failure = bad?.Failure;
            Assert.NotNull(failure);
            var marked = failure!.Start is int st ? sql.Substring(st, failure.Length!.Value) : null;
            Assert.True(marked?.EndsWith("username") == true, $"{marked} {failure.Message}");

            // 문법 오류
            var syntax = await s.ServerCheckAsync($"SELECT id FORM {t}");
            Assert.NotNull(syntax?.Failure);

            // 검사 뒤에도 세션은 그대로 쓸 수 있다
            var again = (await s.RawAsync($"SELECT name FROM {t}")).Rows.First()[0];
            Assert.Equal("a", again);
        }
        finally
        {
            try { await s.RawAsync($"DROP TABLE {t}"); } catch { }
        }
    }
}
