using GodDB.Core;
using GodDB.Core.Drivers;

namespace GodDB.Integration.Tests;

/// Mac TransactionIntegrationTests.swift — 수동 커밋 모드 (실제 DB). 환경변수가 없으면 건너뛴다.
/// 테이블 이름은 다른 테스트와 겹치지 않게 실행마다 다르게 (Mac: tb_tx_it).
public class TransactionIntegrationTests
{
    private static async Task<int> Count(DbSession s, string table) =>
        int.TryParse((await s.RawAsync($"SELECT COUNT(*) FROM {table}")).Rows.FirstOrDefault()?[0], out var n) ? n : -1;

    [Theory]
    [InlineData(DatabaseKind.Mysql)]
    [InlineData(DatabaseKind.Postgres)]
    [InlineData(DatabaseKind.Mssql)]
    public async Task ManualCommitAndRollback(DatabaseKind kind)
    {
        TestDb.Port(kind);
        var t = $"tb_tx_it_{It.Suffix()}";
        await using var a = await It.SessionAsync(kind);
        await using var b = await It.SessionAsync(kind);
        try
        {
            await a.RawAsync($"CREATE TABLE {t} (id int PRIMARY KEY)");

            a.SetAutoCommit(false);
            await a.ExecuteAsync($"SELECT * FROM {t}", limit: 10);
            Assert.False(a.InTransaction);                          // 읽기는 트랜잭션을 열지 않음
            await a.ExecuteAsync($"INSERT INTO {t} VALUES (1)", limit: 0);
            await a.ExecuteAsync($"INSERT INTO {t} VALUES (2)", limit: 0);
            Assert.Equal(new TransactionState(AutoCommit: false, Open: true, Changes: 2), a.TransactionState);
            Assert.Equal(2, await Count(a, t));                     // 같은 세션에선 보임
            if (kind != DatabaseKind.Mssql) Assert.Equal(0, await Count(b, t)); // 다른 세션은 아직 못 봄 (MSSQL 은 잠금으로 대기하므로 생략)
            var blocked = Assert.Throws<GodDbException>(() => a.SetAutoCommit(true));
            Assert.Equal(DbErrorKind.TransactionOpen, blocked.Kind); // 열린 트랜잭션이 있으면 못 켬

            Assert.Equal(2, await a.RollbackAsync());
            Assert.Equal(0, await Count(b, t));

            await a.ExecuteAsync($"INSERT INTO {t} VALUES (3)", limit: 0);
            // Mac: executeInTransaction(["INSERT … (4)"]) — 결과 편집도 열린 트랜잭션에 합류.
            // executeInTransaction 은 결과 편집(ResultEditing.swift)과 함께 아직 포팅되지 않았다.
            // 수동 커밋 모드에서 Mac 구현은 문장마다 execute(s, limit: 0) 를 부르므로 같은 호출로 대신한다.
            await a.ExecuteAsync($"INSERT INTO {t} VALUES (4)", limit: 0);
            Assert.Equal(2, a.PendingChanges);
            Assert.Equal(2, await a.CommitAsync());
            Assert.Equal(2, await Count(b, t));

            // 사용자가 직접 COMMIT 을 쓴 경우도 상태가 맞아야 한다
            await a.ExecuteAsync($"DELETE FROM {t} WHERE id = 3", limit: 0);
            Assert.True(a.InTransaction);
            await a.ExecuteAsync(kind == DatabaseKind.Mssql ? "COMMIT TRANSACTION" : "COMMIT", limit: 0);
            Assert.False(a.InTransaction);
            Assert.Equal(1, await Count(b, t));

            a.SetAutoCommit(true);
            await a.ExecuteAsync($"INSERT INTO {t} VALUES (5)", limit: 0);
            Assert.False(a.InTransaction);
            Assert.Equal(2, await Count(b, t));
        }
        finally
        {
            await a.CloseAsync();
            await b.CloseAsync();
            await It.CleanupAsync(kind, "shop", $"DROP TABLE {t}");
        }
    }

    /// (추가 확인) MySQL 의 DDL 은 암묵적으로 커밋된다 → 수동 커밋 모드에서 DDL 을 실행하면
    /// 트랜잭션을 열지 않고, 열려 있던 트랜잭션은 닫힌 것으로 본다 (Mac DBSession.beginIfNeeded / noteExecuted)
    [Fact]
    public async Task MysqlDdlCommitsImplicitly()
    {
        TestDb.Port(DatabaseKind.Mysql);
        var t = $"tb_tx_ddl_{It.Suffix()}";
        await using var a = await It.SessionAsync(DatabaseKind.Mysql);
        await using var b = await It.SessionAsync(DatabaseKind.Mysql);
        try
        {
            await a.RawAsync($"CREATE TABLE {t} (id int PRIMARY KEY)");
            a.SetAutoCommit(false);
            await a.ExecuteAsync($"INSERT INTO {t} VALUES (1)", limit: 0);
            Assert.True(a.InTransaction);
            await a.ExecuteAsync($"CREATE TABLE {t}_2 (id int)", limit: 0);
            Assert.False(a.InTransaction);
            Assert.Equal(0, a.PendingChanges);
            Assert.Equal(1, await Count(b, t));
            await a.ExecuteAsync($"DROP TABLE {t}_2", limit: 0);
            Assert.False(a.InTransaction);
            a.SetAutoCommit(true);
        }
        finally
        {
            await a.CloseAsync();
            await b.CloseAsync();
            await It.CleanupAsync(DatabaseKind.Mysql, "shop", $"DROP TABLE IF EXISTS {t}_2", $"DROP TABLE IF EXISTS {t}");
        }
    }
}
