using GodDB.Core;
using GodDB.Core.Schema;
using GodDB.Core.Sql;

namespace GodDB.Integration.Tests;

/// Mac ScriptImportIntegrationTests.swift — DELIMITER / GO 스크립트 실행 (실제 DB). 환경변수가 없으면 건너뛴다.
/// 프로시저 이름은 다른 테스트와 겹치지 않게 실행마다 다르게.
/// csvImportInOneTransaction 은 CSV 가져오기(CSVImport) · executeInTransaction 미포팅이라 옮기지 않았다.
public class ScriptImportIntegrationTests
{
    [Fact]
    public async Task MysqlDelimiterProcedure()
    {
        TestDb.Port(DatabaseKind.Mysql);
        var p = $"p_delim_it_{It.Suffix()}";
        await using var s = await It.SessionAsync(DatabaseKind.Mysql);
        try
        {
            var script = $"""
                DROP PROCEDURE IF EXISTS {p};
                DELIMITER $$
                CREATE PROCEDURE {p}(OUT answer INT)
                BEGIN
                  DECLARE x INT DEFAULT 1;
                  SET answer = x + 41;
                END$$
                DELIMITER ;
                CALL {p}(@answer);
                SELECT @answer;
                """;
            QueryResult? last = null;
            foreach (var st in SqlText.Statements(script)) last = await s.ExecuteAsync(st.Sql, limit: 0);
            Assert.Equal("42", last?.Rows.FirstOrDefault()?.Values[0]);
        }
        finally { await It.CleanupAsync(DatabaseKind.Mysql, "shop", $"DROP PROCEDURE IF EXISTS {p}"); }
    }

    /// 결과 집합을 돌려주는 프로시저 CALL (#69)
    [Fact]
    public async Task MysqlCallReturnsResultSet()
    {
        TestDb.Port(DatabaseKind.Mysql);
        var sfx = It.Suffix();
        string rowsP = $"p_rows_it_{sfx}", twoP = $"p_two_it_{sfx}";
        await using var s = await It.SessionAsync(DatabaseKind.Mysql);
        try
        {
            var script = $"""
                DROP PROCEDURE IF EXISTS {rowsP};
                DROP PROCEDURE IF EXISTS {twoP};
                DELIMITER $$
                CREATE PROCEDURE {rowsP}(IN n INT)
                BEGIN
                  SELECT n AS a, 'x' AS b UNION ALL SELECT n + 1, 'y';
                END$$
                CREATE PROCEDURE {twoP}()
                BEGIN
                  SELECT 1 AS one;
                  SELECT 'p', 'q', 'r';
                END$$
                DELIMITER ;
                """;
            foreach (var st in SqlText.Statements(script)) await s.ExecuteAsync(st.Sql, limit: 0);
            var r = await s.ExecuteAsync($"CALL {rowsP}(41)", limit: 0);
            Assert.Equal(["a", "b"], r.Columns.Select(c => c.Name));
            Assert.Equal([["41", "x"], ["42", "y"]], r.Rows.Select(x => x.Values.ToArray()));
            // 앱처럼 페이지 크기를 붙여 실행해도 (CALL 은 감싸지 않음)
            var paged = await s.ExecuteAsync($"CALL {rowsP}(1)", limit: 100);
            Assert.Equal(2, paged.Rows.Count);
            // 결과 집합이 둘이면 첫 번째만
            var two = await s.ExecuteAsync($"CALL {twoP}()", limit: 0);
            Assert.Equal(["one"], two.Columns.Select(c => c.Name));
            Assert.Single(two.Rows);
            // 같은 연결로 계속 쓸 수 있다
            Assert.Equal("7", (await s.RawAsync("SELECT 7")).Rows.FirstOrDefault()?[0]);
        }
        finally { await It.CleanupAsync(DatabaseKind.Mysql, "shop", $"DROP PROCEDURE IF EXISTS {rowsP}", $"DROP PROCEDURE IF EXISTS {twoP}"); }
    }

    [Fact]
    public async Task MssqlGoBatches()
    {
        TestDb.Port(DatabaseKind.Mssql);
        var p = $"p_go_it_{It.Suffix()}";
        await using var s = await It.SessionAsync(DatabaseKind.Mssql);
        try
        {
            var script = $"""
                IF OBJECT_ID('dbo.{p}') IS NOT NULL DROP PROCEDURE dbo.{p};
                GO
                CREATE PROCEDURE dbo.{p} AS
                BEGIN
                  DECLARE @x int = 1;
                  SELECT @x + 41 AS answer;
                END
                GO
                EXEC dbo.{p};
                """;
            QueryResult? last = null;
            foreach (var st in SqlText.Statements(script)) last = await s.ExecuteAsync(st.Sql, limit: 0);
            Assert.Equal("42", last?.Rows.FirstOrDefault()?.Values[0]);
        }
        finally { await It.CleanupAsync(DatabaseKind.Mssql, "shop", $"IF OBJECT_ID('dbo.{p}') IS NOT NULL DROP PROCEDURE dbo.{p}"); }
    }

    /// (추가 확인) PostgreSQL $$ 함수 본문: 안의 세미콜론으로 나누지 않고 한 문장으로 실행된다
    [Fact]
    public async Task PostgresDollarQuotedFunction()
    {
        TestDb.Port(DatabaseKind.Postgres);
        var f = $"fn_dollar_it_{It.Suffix()}";
        await using var s = await It.SessionAsync(DatabaseKind.Postgres);
        try
        {
            var script = $"""
                CREATE OR REPLACE FUNCTION {f}() RETURNS int LANGUAGE plpgsql AS $$
                DECLARE x int := 1;
                BEGIN
                  x := x + 41;
                  RETURN x;
                END;
                $$;
                SELECT {f}();
                """;
            var statements = SqlText.Statements(script);
            Assert.Equal(2, statements.Count);
            QueryResult? last = null;
            foreach (var st in statements) last = await s.ExecuteAsync(st.Sql, limit: 0);
            Assert.Equal("42", last?.Rows.FirstOrDefault()?.Values[0]);
        }
        finally { await It.CleanupAsync(DatabaseKind.Postgres, "shop", $"DROP FUNCTION IF EXISTS {f}()"); }
    }
}
