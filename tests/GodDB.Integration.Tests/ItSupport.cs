using GodDB.Core;
using GodDB.Core.Drivers;

namespace GodDB.Integration.Tests;

/// Mac 통합 테스트의 접속 도우미 (IntegrationTests.profile, MultiDB.profile, ExportIntegrationTests.session).
/// 다른 테스트가 같은 Docker DB 를 동시에 쓰므로, 만드는 테이블·스키마·DB·프로시저 이름에는 매번 Suffix() 를 붙이고 finally 에서 지운다.
internal static class It
{
    /// 실행마다 다른 이름 꼬리 (소문자 + 숫자)
    public static string Suffix() => Guid.NewGuid().ToString("N")[..10];

    public static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// Mac IntegrationTests.profile (MySQL, TLS 사용)
    public static ConnectionProfile MySql(string database = "shop") => new()
    {
        Kind = DatabaseKind.Mysql,
        Name = "it",
        Host = "127.0.0.1",
        Port = TestDb.Port(DatabaseKind.Mysql),
        Username = "root",
        Database = database,
        UseTLS = true,
        SavePassword = false,
    };

    public static string MySqlPassword => Env("GODDB_IT_PASSWORD") ?? "";

    /// Mac MultiDB.profile (PostgreSQL / SQL Server, host localhost)
    public static (ConnectionProfile Profile, string Password) Multi(DatabaseKind kind, string database) => kind switch
    {
        DatabaseKind.Postgres => (new ConnectionProfile
        {
            Kind = DatabaseKind.Postgres, Name = "pg", Host = "localhost", Port = TestDb.Port(kind),
            Username = "postgres", Database = database, UseTLS = false, SavePassword = false,
        }, Env("GODDB_PG_PASSWORD") ?? "goddbtest"),
        DatabaseKind.Mssql => (new ConnectionProfile
        {
            Kind = DatabaseKind.Mssql, Name = "ms", Host = "localhost", Port = TestDb.Port(kind),
            Username = "sa", Database = database, UseTLS = false, SavePassword = false,
        }, Env("GODDB_MSSQL_PASSWORD") ?? "GodDb_test1!"),
        DatabaseKind.Mysql => (MySql(database), MySqlPassword),
        _ => throw new ArgumentException("SQL DB 만"),
    };

    public static Task<DbSession> ConnectAsync(DatabaseKind kind, string database)
    {
        var (p, pw) = Multi(kind, database);
        return DbSession.ConnectAsync(p, pw);
    }

    /// Mac ExportIntegrationTests.session: 세 DB 의 shop (SQL Server 는 없으면 만든다)
    public static async Task<DbSession> SessionAsync(DatabaseKind kind)
    {
        if (kind == DatabaseKind.Mssql)
        {
            await using var admin = await ConnectAsync(DatabaseKind.Mssql, "master");
            // 다른 테스트가 동시에 만들 수 있다 (이미 있으면 무시)
            try { await admin.RawAsync("IF DB_ID('shop') IS NULL CREATE DATABASE shop"); } catch { }
        }
        return await ConnectAsync(kind, "shop");
    }

    /// 정리용: 새 연결로 문장들을 실행한다 (실패는 무시)
    public static async Task CleanupAsync(DatabaseKind kind, string database, params string[] sql)
    {
        try
        {
            await using var s = await ConnectAsync(kind, database);
            foreach (var q in sql) { try { await s.RawAsync(q); } catch { } }
        }
        catch { }
    }

    /// SQL Server DB 삭제 (남은 연결은 끊는다)
    public static string[] DropMssqlDatabase(string name) =>
    [
        $"IF DB_ID('{name}') IS NOT NULL ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE",
        $"IF DB_ID('{name}') IS NOT NULL DROP DATABASE [{name}]",
    ];
}
