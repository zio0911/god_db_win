using GodDB.Core.Dialects;
using GodDB.Core.Results;
using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

// MultiDBChecks.paging / renameAndDrop / serverSort 는 실제 DB 가 필요해 통합 테스트로 옮긴다.

public class PagingSqlTests
{
    [Fact]
    public void MysqlAndPostgres()
    {
        var my = new MySqlDialect().PagedSql("SELECT * FROM t;", 500, 1000);
        Assert.Equal(new PagedSql("SELECT * FROM t\nLIMIT 501 OFFSET 1000", 0), my);
        Assert.Equal("WITH x AS (SELECT 1) SELECT * FROM x\nLIMIT 11",
                     new PostgresDialect().PagedSql("WITH x AS (SELECT 1) SELECT * FROM x", 10, 0)?.Sql);
        Assert.Null(new MySqlDialect().PagedSql("SELECT * FROM t LIMIT 5", 500, 0));
        Assert.Null(new MySqlDialect().PagedSql("UPDATE t SET a = 1", 500, 0));
        Assert.Null(new MySqlDialect().PagedSql("SELECT * FROM t FOR UPDATE", 500, 0));
        Assert.Null(new MySqlDialect().PagedSql("SELECT 1", 0, 0));
    }

    [Fact]
    public void MssqlWithAndWithoutOrderBy()
    {
        var d = new MssqlDialect();
        Assert.Equal("SELECT * FROM t ORDER BY id\nOFFSET 500 ROWS FETCH NEXT 501 ROWS ONLY",
                     d.PagedSql("SELECT * FROM t ORDER BY id", 500, 500)?.Sql);
        // ORDER BY 가 없어도 이번 페이지만 받는다 (앞 페이지를 다시 받지 않음)
        Assert.Equal(new PagedSql("SELECT * FROM t\nORDER BY (SELECT NULL) OFFSET 1000 ROWS FETCH NEXT 501 ROWS ONLY", 0),
                     d.PagedSql("SELECT * FROM t;", 500, 1000));
        Assert.Equal("WITH x AS (SELECT 1 a) SELECT * FROM x\nORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT 11 ROWS ONLY",
                     d.PagedSql("WITH x AS (SELECT 1 a) SELECT * FROM x", 10, 0)?.Sql);
        // ORDER BY (SELECT NULL) 을 쓸 수 없는 문장은 TOP 으로 받고 앞부분을 버린다
        foreach (var sql in new[] { "SELECT DISTINCT a FROM t", "SELECT a FROM t UNION ALL SELECT a FROM u",
                                    "SELECT a FROM t OPTION (RECOMPILE)", "SELECT a FROM t FOR JSON PATH" })
        {
            var p = d.PagedSql(sql, 500, 1000);
            Assert.True(p?.Sql.Contains("TOP 1501") == true, sql);
            Assert.Equal(1000, p?.Skip);
        }
        Assert.Null(d.PagedSql("WITH x AS (SELECT 1 a) SELECT a FROM x UNION SELECT 2", 10, 0));
        // 서브쿼리 안의 ORDER BY 는 맨 바깥 ORDER BY 가 아니다
        Assert.Null(d.PagedSql("SELECT * FROM (SELECT TOP 10 * FROM t ORDER BY id) x", 5, 0));
        Assert.Null(d.PagedSql("SELECT TOP 10 * FROM t", 500, 0));
    }

    [Fact]
    public void CountSql()
    {
        Assert.Equal("SELECT COUNT(*) FROM (\nSELECT * FROM t\n) AS goddb_cnt", new MySqlDialect().CountSql("SELECT * FROM t;"));
        Assert.False(new MssqlDialect().CountSql("SELECT * FROM t ORDER BY id")?.Contains("ORDER BY"));
        Assert.True(new MssqlDialect().CountSql("SELECT * FROM (SELECT TOP 3 * FROM t ORDER BY id) x")?.Contains("ORDER BY id"));
        Assert.Null(new MySqlDialect().CountSql("DELETE FROM t"));
    }
}

public class RenameDropSqlTests
{
    [Fact]
    public void PerDialect()
    {
        Assert.Equal("RENAME TABLE `a` TO `b`;", new MySqlDialect().RenameTableSql("a", "b"));
        Assert.Equal("ALTER TABLE \"a\" RENAME TO \"b\";", new PostgresDialect().RenameTableSql("a", "b"));
        Assert.Equal("EXEC sp_rename N'[sales].[a]', N'b';", new MssqlDialect().RenameTableSql("sales.a", "b"));
        Assert.Equal("DROP TABLE [a];", new MssqlDialect().DropTableSql("a"));
    }
}

public class SortSqlTests
{
    [Fact]
    public void WrapsAndReplacesOrderBy()
    {
        var s = new ResultSort("email", false);
        Assert.Equal("SELECT * FROM (\nSELECT * FROM t\n) AS goddb_sort ORDER BY `email` DESC",
                     new MySqlDialect().SortedSql("SELECT * FROM t ORDER BY id;", s));
        // LIMIT 이 있으면 원래 ORDER BY 를 둔다 (같은 결과 집합을 정렬)
        Assert.True(new PostgresDialect().SortedSql("SELECT * FROM t ORDER BY id LIMIT 5", s)?.Contains("ORDER BY id LIMIT 5"));
        Assert.Equal("SELECT * FROM (\nSELECT * FROM t\n) AS goddb_sort ORDER BY [email] DESC",
                     new MssqlDialect().SortedSql("SELECT * FROM t ORDER BY id", s));
        Assert.Null(new MySqlDialect().SortedSql("SHOW TABLES", s));
    }

    [Fact]
    public void ClientCompare()
    {
        Assert.True(ResultSorting.Compare("10", "9") > 0);
        Assert.True(ResultSorting.Compare(null, "a") < 0);
        QueryResult.Row[] rows = [new(0, ["b"]), new(1, [null]), new(2, ["a"])];
        Assert.Equal([1, 2, 0], ResultSorting.Sorted(rows, 0, true).Select(r => r.Id));
    }
}
