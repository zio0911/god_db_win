using System.Text.Json;
using GodDB.Core.Sql;

namespace GodDB.Core.Tests;

public class SqlDangerTests
{
    [Fact]
    public void DeleteAndUpdateWithoutWhere()
    {
        Assert.Equal(SqlDanger.DeleteAll, SqlText.Danger("DELETE FROM orders"));
        Assert.Equal(SqlDanger.DeleteAll, SqlText.Danger("  -- 정리\n delete from orders;"));
        Assert.Equal(SqlDanger.UpdateAll, SqlText.Danger("UPDATE users SET name = 'x'"));
        Assert.Equal(SqlDanger.DeleteAll, SqlText.Danger("DELETE TOP (10) FROM logs"));
    }

    [Fact]
    public void WhereMakesItSafe()
    {
        Assert.Null(SqlText.Danger("DELETE FROM orders WHERE id = 1"));
        Assert.Null(SqlText.Danger("update users\nset a = 1\nwhere id in (select id from t where x = 2)"));
        Assert.Null(SqlText.Danger("DELETE o FROM orders o JOIN users u ON u.id = o.uid WHERE u.gone = 1"));
    }

    [Fact]
    public void WhereOnlyInsideStringOrSubqueryDoesNotCount()
    {
        Assert.Equal(SqlDanger.UpdateAll, SqlText.Danger("UPDATE t SET note = 'where am i'"));
        Assert.Equal(SqlDanger.UpdateAll, SqlText.Danger("UPDATE t SET a = (SELECT max(b) FROM u WHERE u.k = 1)"));
        Assert.Equal(SqlDanger.UpdateAll, SqlText.Danger("UPDATE t SET a = 1 /* where */"));
        Assert.Equal(SqlDanger.UpdateAll, SqlText.Danger("UPDATE t SET `where` = 1"));
    }

    [Fact]
    public void TruncateAndDrop()
    {
        Assert.Equal(SqlDanger.Truncate, SqlText.Danger("TRUNCATE TABLE logs"));
        Assert.Equal(SqlDanger.Drop, SqlText.Danger("drop table if exists tmp"));
        Assert.Equal(SqlDanger.Drop, SqlText.Danger("DROP DATABASE shop"));
    }

    [Fact]
    public void OtherStatementsAreFine()
    {
        Assert.Null(SqlText.Danger("SELECT * FROM orders"));
        Assert.Null(SqlText.Danger("INSERT INTO t VALUES (1)"));
        Assert.Null(SqlText.Danger("ALTER TABLE t ADD c INT"));
    }
}

public class ConnectionEnvironmentTests
{
    [Fact]
    public void DecodesOldProfilesWithoutEnvironment()
    {
        var json = """{"id":"6F1C2D3E-0000-4000-8000-000000000001","name":"a","host":"h","port":3306,"username":"u"}""";
        var p = JsonSerializer.Deserialize<ConnectionProfile>(json, ProfileStore.Json)!;
        Assert.Equal(ConnectionEnvironment.None, p.Environment);
    }

    [Fact]
    public void RoundTripsEnvironment()
    {
        var p = new ConnectionProfile { Name = "prod", Environment = ConnectionEnvironment.Production };
        var back = JsonSerializer.Deserialize<ConnectionProfile>(JsonSerializer.Serialize(p, ProfileStore.Json), ProfileStore.Json)!;
        Assert.Equal(ConnectionEnvironment.Production, back.Environment);
    }
}

public class ScriptDelimiterTests
{
    [Fact]
    public void MysqlDelimiterKeepsProcedureBodyTogether()
    {
        var script = """
            DROP PROCEDURE IF EXISTS p1;
            DELIMITER $$
            CREATE PROCEDURE p1()
            BEGIN
              SELECT 1;
              SELECT 2;
            END$$
            DELIMITER ;
            CALL p1();
            """;
        var s = SqlText.Statements(script).Select(x => x.Sql).ToList();
        Assert.Equal(3, s.Count);
        Assert.Equal("DROP PROCEDURE IF EXISTS p1", s[0]);
        Assert.True(s[1].StartsWith("CREATE PROCEDURE p1()") && s[1].EndsWith("END"));
        Assert.Contains("SELECT 1;\n  SELECT 2;", s[1]);
        Assert.Equal("CALL p1()", s[2]);
    }

    [Fact]
    public void SlashDelimiter()
    {
        var s = SqlText.Statements("DELIMITER //\nSELECT 1; SELECT 2 //\nDELIMITER ;\nSELECT 3;").Select(x => x.Sql).ToList();
        Assert.Equal(["SELECT 1; SELECT 2", "SELECT 3"], s);
    }

    [Fact]
    public void GoSplitsBatchesOnly()
    {
        var script = """
            CREATE PROCEDURE dbo.p AS
            BEGIN
              SELECT 1;
              SELECT 2;
            END
            GO
            EXEC dbo.p;
            go
            SELECT 'GO';
            """;
        var s = SqlText.Statements(script).Select(x => x.Sql).ToList();
        Assert.Equal(3, s.Count);
        Assert.Contains("SELECT 1;\n  SELECT 2;", s[0]);
        Assert.Equal("EXEC dbo.p;", s[1]);
        Assert.Equal("SELECT 'GO';", s[2]);
    }

    [Fact]
    public void PlainScriptsUnchanged()
    {
        Assert.Equal(["SELECT 1", "SELECT 'a;b'", "-- x;\nSELECT 3"],
                     SqlText.Statements("SELECT 1; SELECT 'a;b'; -- x;\nSELECT 3").Select(x => x.Sql).ToList());
        // GOOD 같은 단어나 문장 중간 GO 는 구분자가 아니다
        Assert.Equal(2, SqlText.Statements("SELECT good FROM t; SELECT 2").Count);
    }
}
