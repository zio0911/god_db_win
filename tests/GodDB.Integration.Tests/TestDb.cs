using GodDB.Core;
using GodDB.Core.Drivers;

namespace GodDB.Integration.Tests;

/// Docker 테스트 DB (scripts/test-dbs.sh) 접속 정보. 환경변수가 없으면 테스트를 건너뛴다.
internal static class TestDb
{
    public static string Password => Environment.GetEnvironmentVariable("GODDB_IT_PASSWORD") ?? "goddbtest";
    public const string MssqlPassword = "GodDb_test1!";

    public static int Port(DatabaseKind kind)
    {
        var name = kind switch
        {
            DatabaseKind.Mysql => "GODDB_IT_PORT",
            DatabaseKind.Postgres => "GODDB_PG_PORT",
            DatabaseKind.Mssql => "GODDB_MSSQL_PORT",
            _ => "GODDB_REDIS_PORT",
        };
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(v)) Assert.Skip($"{name} 없음 — scripts/test-dbs.sh test 로 실행");
        return int.Parse(v!);
    }

    public static ConnectionProfile Profile(DatabaseKind kind, string? database = null) => new()
    {
        Kind = kind,
        Name = "it-" + kind.Badge(),
        Host = "127.0.0.1",
        Port = Port(kind),
        Username = kind switch { DatabaseKind.Mysql => "root", DatabaseKind.Postgres => "postgres", DatabaseKind.Mssql => "sa", _ => "" },
        Database = database ?? kind switch { DatabaseKind.Mssql => "master", _ => "shop" },
        UseTLS = false,
        SavePassword = false,
    };

    public static string PasswordFor(DatabaseKind kind) => kind == DatabaseKind.Mssql ? MssqlPassword : Password;

    public static DriverEndpoint Endpoint(DatabaseKind kind, string? database = null)
    {
        var p = Profile(kind, database);
        return new DriverEndpoint { Host = p.Host, Port = p.Port, Username = p.Username, Password = PasswordFor(kind), Database = p.Database, UseTLS = false };
    }

    public static async Task<IDbDriver> DriverAsync(DatabaseKind kind, string? database = null) => kind switch
    {
        DatabaseKind.Mysql => await MySqlDriver.ConnectAsync(Endpoint(kind, database)),
        DatabaseKind.Postgres => await PostgresDriver.ConnectAsync(Endpoint(kind, database)),
        _ => await MssqlDriver.ConnectAsync(Endpoint(kind, database)),
    };
}
