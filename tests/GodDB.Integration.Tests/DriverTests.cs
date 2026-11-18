using GodDB.Core;
using GodDB.Core.Drivers;

namespace GodDB.Integration.Tests;

/// 드라이버 값 모양이 Mac 판과 같은지 (날짜 · 시각 · 바이너리 · NULL)
public class DriverTests
{
    [Theory]
    [InlineData(DatabaseKind.Mysql)]
    [InlineData(DatabaseKind.Postgres)]
    [InlineData(DatabaseKind.Mssql)]
    public async Task SelectsValuesAsText(DatabaseKind kind)
    {
        await using var d = await TestDb.DriverAsync(kind);
        var sql = kind switch
        {
            DatabaseKind.Mysql => "SELECT 1 AS a, NULL AS b, 'x' AS c, CAST('2026-11-13' AS DATE) AS d, CAST('2026-11-13 14:46:25.398' AS DATETIME(3)) AS e, CAST('12:34:56' AS TIME) AS f, X'CD01FF00' AS g, 1.50 AS h",
            DatabaseKind.Postgres => "SELECT 1 AS a, NULL AS b, 'x' AS c, DATE '2026-11-13' AS d, TIMESTAMP '2026-11-13 14:46:25.398' AS e, TIME '12:34:56' AS f, '\\xcd01ff00'::bytea AS g, 1.50 AS h",
            _ => "SELECT 1 AS a, NULL AS b, 'x' AS c, CAST('2026-11-13' AS date) AS d, CAST('2026-11-13 14:46:25.398' AS datetime2(3)) AS e, CAST('12:34:56' AS time(0)) AS f, CAST(0xCD01FF00 AS varbinary(4)) AS g, CAST(1.50 AS decimal(5,2)) AS h",
        };
        var r = await d.QueryAsync(sql);
        Assert.Equal(["a", "b", "c", "d", "e", "f", "g", "h"], r.Columns);
        Assert.Equal(["1", null, "x", "2026-11-13", "2026-11-13 14:46:25.398", "12:34:56", "0xCD01FF00", "1.50"], r.Rows.Single());
    }

    [Theory]
    [InlineData(DatabaseKind.Mysql)]
    [InlineData(DatabaseKind.Postgres)]
    [InlineData(DatabaseKind.Mssql)]
    public async Task LocalhostResolvesAndConnects(DatabaseKind kind)
    {
        var e = TestDb.Endpoint(kind) with { Host = "localhost" };
        IDbDriver d = kind switch
        {
            DatabaseKind.Mysql => await MySqlDriver.ConnectAsync(e),
            DatabaseKind.Postgres => await PostgresDriver.ConnectAsync(e),
            _ => await MssqlDriver.ConnectAsync(e),
        };
        await using (d) Assert.Equal("1", (await d.QueryAsync("SELECT 1")).Rows[0][0]);
    }
}
