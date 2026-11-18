using GodDB.Core;
using GodDB.Core.Drivers;

namespace GodDB.Integration.Tests;

/// 빠른 찾기용 모든 스키마 테이블 목록 (실제 DB). 환경변수가 없으면 건너뛴다.
public class QuickOpenIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(DatabaseKind.Mysql)]
    [InlineData(DatabaseKind.Postgres)]
    [InlineData(DatabaseKind.Mssql)]
    public async Task ListsTablesAcrossSchemas(DatabaseKind kind)
    {
        await using var s = await DbSession.ConnectAsync(TestDb.Profile(kind), TestDb.PasswordFor(kind), Ct);
        if (kind == DatabaseKind.Mssql)
        {
            // SQL Server 는 지금 DB 의 사이드바 목록으로 찾는다 (빈 목록이라 테이블을 만들 필요 없음)
            Assert.Empty(await s.AllTablesAsync(Ct));
            return;
        }
        // 다른 테스트와 같은 DB 를 쓰므로 고유한 이름으로 만들고 지운다 (Mac: tb_qo_it 고정 이름)
        var table = "tb_qo_it_" + Guid.NewGuid().ToString("N")[..8];
        await s.RawAsync($"CREATE TABLE IF NOT EXISTS {table} (id int)", Ct);
        try
        {
            var all = await s.AllTablesAsync(Ct);
            Assert.Contains(all, t => t.Name == table && !t.IsView);
            Assert.DoesNotContain(all, t => new[] { "mysql", "information_schema", "pg_catalog" }.Contains(t.Schema));
        }
        finally
        {
            await s.RawAsync($"DROP TABLE IF EXISTS {table}", CancellationToken.None);
        }
    }
}
