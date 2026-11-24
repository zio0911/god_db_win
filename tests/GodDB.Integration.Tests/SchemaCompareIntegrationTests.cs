using GodDB.Core;
using GodDB.Core.Audit;
using GodDB.Core.Drivers;
using GodDB.Core.Schema;
using GodDB.Core.Sql;

namespace GodDB.Integration.Tests;

/// Mac SchemaCompareIntegrationTests.swift — 스키마 비교를 실제 DB 로 확인한다. 한 서버에 같은 테이블을 가진 DB(PostgreSQL 은 스키마) 두 개를 만들고,
/// 비교 창과 같은 경로(<see cref="SchemaDiff.CompareAsync"/>)로 읽는다.
/// 1) 똑같으면 차이가 없어야 한다.
/// 2) 대상(B)을 ALTER 로 바꾸면 차이마다 맞는 항목과 보정 SQL 이 나와야 한다.
/// 3) 그 보정 SQL 을 대상에 실행하고 다시 비교하면 "대상에만 있음" 외에는 차이가 없어야 한다.
/// GODDB_IT_PORT(MySQL) · GODDB_PG_PORT · GODDB_MSSQL_PORT 가 있을 때만 각각 실행된다.
/// (DB 없이 보는 보정 SQL 모양은 GodDB.Core.Tests/SchemaCompareSQLTests.cs)
public class SchemaCompareIntegrationTests
{
    [Fact] public Task MySql() => CompareFixture.RunAsync(DatabaseKind.Mysql);
    [Fact] public Task Postgres() => CompareFixture.RunAsync(DatabaseKind.Postgres);
    [Fact] public Task SqlServer() => CompareFixture.RunAsync(DatabaseKind.Mssql);
}

/// 테스트마다 새 이름으로 만들고 끝나면 지우는 비교용 DB 두 개
internal sealed class CompareFixture
{
    public required DatabaseKind Kind { get; init; }
    public required string A { get; init; }
    public required string B { get; init; }
    public required DbSession Admin { get; init; }
    public required DbSession Source { get; init; }
    public required DbSession Target { get; init; }

    public static Task<DbSession> ConnectAsync(DatabaseKind kind)
    {
        if (kind == DatabaseKind.Mysql)
        {
            // 비교만 본다 (TLS 는 따로 테스트)
            var p = It.MySql("shop") with { UseTLS = false };
            return DbSession.ConnectAsync(p, It.MySqlPassword);
        }
        return It.ConnectAsync(kind, kind == DatabaseKind.Mssql ? "master" : "shop");
    }

    public static async Task<CompareFixture> MakeAsync(DatabaseKind kind)
    {
        TestDb.Port(kind);   // 환경변수가 없으면 건너뛴다
        var suffix = Guid.NewGuid().ToString("N")[..6];
        string a = $"cmp_a_{suffix}", b = $"cmp_b_{suffix}";
        var admin = await ConnectAsync(kind);
        var d = admin.Dialect;
        foreach (var name in new[] { a, b })
            await admin.RawAsync(kind == DatabaseKind.Postgres ? $"CREATE SCHEMA {d.QuoteIdent(name)}" : $"CREATE DATABASE {d.QuoteIdent(name)}");
        return new CompareFixture
        {
            Kind = kind, A = a, B = b, Admin = admin, Source = await ConnectAsync(kind), Target = await ConnectAsync(kind),
        };
    }

    public async Task DropAsync()
    {
        await Source.CloseAsync();
        await Target.CloseAsync();
        var d = Admin.Dialect;
        foreach (var name in new[] { A, B })
        {
            async Task Try(string sql) { try { await Admin.RawAsync(sql); } catch { } }
            switch (Kind)
            {
                case DatabaseKind.Postgres: await Try($"DROP SCHEMA IF EXISTS {d.QuoteIdent(name)} CASCADE"); break;
                case DatabaseKind.Mssql:
                    await Try("USE master");
                    await Try($"ALTER DATABASE {d.QuoteIdent(name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
                    await Try($"DROP DATABASE IF EXISTS {d.QuoteIdent(name)}");
                    break;
                default: await Try($"DROP DATABASE IF EXISTS {d.QuoteIdent(name)}"); break;
            }
        }
        await Admin.CloseAsync();
    }

    /// 한 DB(스키마)에서 문장을 차례로 실행한다
    public async Task ExecAsync(string container, IEnumerable<string> statements)
    {
        await Admin.UseAsync(container);
        foreach (var sql in statements)
        {
            try { await Admin.RawAsync(sql); }
            catch (Exception e) { throw new Exception($"{container}: {sql}\n{ErrorText.Describe(e).Message}", e); }
        }
    }

    /// 비교 창과 같은 경로로 비교
    public Task<SchemaDiffReport> CompareAsync(string s, string t) => SchemaDiff.CompareAsync(Source, s, Target, t);

    public static async Task RunAsync(DatabaseKind kind)
    {
        var f = await MakeAsync(kind);
        try { await f.ScenarioAsync(); }
        finally { await f.DropAsync(); }
    }

    public async Task ScenarioAsync()
    {
        // 1) 같은 테이블 → 차이 없음 (양방향)
        await ExecAsync(A, CompareSql.Base(Kind));
        await ExecAsync(B, CompareSql.Base(Kind));
        foreach (var (s, t) in new[] { (A, B), (B, A) })
        {
            var same = await CompareAsync(s, t);
            Assert.True(same.Items.Count == 0, $"{Kind} {s}→{t} 같은데 차이: {Describe(same.Items)}");
            Assert.Equal(2, same.MatchedTables);
            Assert.Empty(same.FixScript);
        }

        // 2) 대상(B)만 바꾸고, A 에만 / B 에만 있는 테이블을 만든다
        await ExecAsync(A, CompareSql.OnlyA(Kind));
        await ExecAsync(B, CompareSql.AlterB(Kind));
        var report = await CompareAsync(A, B);
        var all = Describe(report.Items);

        SchemaDiffItem? Find(SchemaDiffKind k, string table, string? obj = null) =>
            report.Items.FirstOrDefault(i => i.Kind == k && i.Table == table && i.Object?.ToLowerInvariant() == obj?.ToLowerInvariant());
        // 컬럼
        var memo = Find(SchemaDiffKind.MissingColumn, "cmp_member", "memo");
        Assert.True(memo?.FixSql?.Contains("memo") == true, $"B 에서 지운 컬럼 → 누락: {all}");
        Assert.True(Find(SchemaDiffKind.ExtraColumn, "cmp_member", "extra_b") is { FixSql: null }, $"B 에만 추가한 컬럼 → 대상에만 있음: {all}");
        var nick = Find(SchemaDiffKind.DifferentColumn, "cmp_member", "nick");
        Assert.True(nick?.Detail.Contains("타입") == true, $"길이 변경: {all}");
        Assert.True(nick?.FixSql?.Contains("50") == true);
        Assert.True(Find(SchemaDiffKind.DifferentColumn, "cmp_member", "phone")?.Detail.Contains("NOT NULL → NULL") == true, $"NULL 허용 변경: {all}");
        Assert.True(Find(SchemaDiffKind.DifferentColumn, "cmp_member", "status")?.Detail.Contains("기본값") == true, $"기본값 변경: {all}");
        var email = Find(SchemaDiffKind.DifferentColumn, "cmp_member", "email");
        Assert.True(email?.Detail == "코멘트 변경", $"컬럼 코멘트 변경: {all}");
        Assert.True(email?.FixSql?.Contains("메일") == true);
        // 테이블 코멘트
        var tableComment = Find(SchemaDiffKind.DifferentTable, "cmp_member");
        Assert.True(tableComment?.FixSql?.Contains("회원") == true, $"테이블 코멘트 변경: {all}");
        // 인덱스
        Assert.True(Find(SchemaDiffKind.MissingIndex, "cmp_member", "idx_member_email")?.FixSql != null, $"B 에서 지운 인덱스: {all}");
        Assert.True(Find(SchemaDiffKind.ExtraIndex, "cmp_member", "idx_member_phone") is { FixSql: null }, $"B 에만 있는 인덱스: {all}");
        Assert.True(Find(SchemaDiffKind.DifferentIndex, "cmp_order", "uq_order_code")?.FixSql != null, $"UNIQUE → 일반 인덱스: {all}");
        // 테이블
        Assert.True(Find(SchemaDiffKind.MissingTable, "cmp_only_a")?.FixSql?.Contains("cmp_only_a") == true, $"A 에만 있는 테이블: {all}");
        Assert.True(Find(SchemaDiffKind.ExtraTable, "cmp_only_b") is { FixSql: null }, $"B 에만 있는 테이블: {all}");
        // 바꾸지 않은 컬럼은 나오지 않는다
        Assert.Null(Find(SchemaDiffKind.DifferentColumn, "cmp_member", "name"));
        Assert.Null(Find(SchemaDiffKind.DifferentColumn, "cmp_order", "amount"));
        Assert.True(Find(SchemaDiffKind.DifferentColumn, "cmp_order", "id") == null, all);

        // 여러 개 골라 복사: 고른 순서와 관계없이 목록 순서, 참고용 항목은 건너뜀
        if (nick != null && memo != null && Find(SchemaDiffKind.ExtraColumn, "cmp_member", "extra_b") is { } extra)
        {
            var joined = SchemaDiffReport.FixSql(report.Items, new HashSet<string> { nick.Id, memo.Id, extra.Id });
            Assert.Equal(SchemaDiffReport.Grouped(report.Items.Where(i => i.Id == nick.Id || i.Id == memo.Id)), joined);
            Assert.True(joined.Contains(nick.FixSql!) && joined.Contains(memo.FixSql!));
        }

        // 3) 보정 SQL 을 대상에 실행 (쿼리 탭에서 전체 실행하듯 문장별로) → 다시 비교하면 대상에만 있는 것만 남는다
        await Target.UseAsync(B);
        var failures = new List<string>();
        foreach (var stmt in SqlText.Statements(report.FixScript).Select(x => x.Sql))
        {
            try { await Target.RawAsync(stmt); }
            catch (Exception e) { failures.Add($"{Kind} 보정 SQL 실패: {stmt}\n{ErrorText.Describe(e).Message}"); }
        }
        var after = await CompareAsync(A, B);
        var left = after.Items.Where(i => i.Severity != SchemaDiffSeverity.Extra).ToList();
        Assert.True(left.Count == 0, $"{Kind} 보정 후에도 남은 차이: {Describe(left)}");
        var extras = after.Items.Where(i => i.Severity == SchemaDiffSeverity.Extra)
            .Select(i => $"{i.Table}.{i.Object ?? ""}".ToLowerInvariant()).ToHashSet();
        Assert.True(extras.SetEquals(["cmp_member.extra_b", "cmp_member.idx_member_phone", "cmp_only_b."]), string.Join(", ", extras));
        Assert.True(after.MatchedTables is 2 or 3);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    public static string Describe(IEnumerable<SchemaDiffItem> items) =>
        string.Join("\n", items.Select(i => $"[{SchemaDiffItem.RawValue(i.Kind)}] {i.Table}.{i.Object ?? ""} {i.Detail} :: {i.FixSql ?? "-"}"));
}

/// DB 별 비교용 DDL
internal static class CompareSql
{
    /// A · B 에 똑같이 만드는 테이블 두 개
    public static string[] Base(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Postgres =>
        [
            """
            CREATE TABLE cmp_member (id int PRIMARY KEY, name varchar(50) NOT NULL, nick varchar(50),
              email varchar(100), phone varchar(20), status char(1) NOT NULL DEFAULT 'A', memo varchar(200))
            """,
            "CREATE UNIQUE INDEX uq_member_name ON cmp_member (name)",
            "CREATE INDEX idx_member_email ON cmp_member (email)",
            "COMMENT ON TABLE cmp_member IS '회원'",
            "COMMENT ON COLUMN cmp_member.email IS '메일'",
            """
            CREATE TABLE cmp_order (id serial PRIMARY KEY, member_id int NOT NULL, code varchar(20) NOT NULL,
              amount numeric(10,2) NOT NULL DEFAULT 0)
            """,
            "CREATE UNIQUE INDEX uq_order_code ON cmp_order (code)",
            "CREATE INDEX idx_order_member ON cmp_order (member_id)",
        ],
        DatabaseKind.Mssql =>
        [
            """
            CREATE TABLE cmp_member (id int IDENTITY(1,1) CONSTRAINT pk_cmp_member PRIMARY KEY, name varchar(50) NOT NULL,
              nick varchar(50) NULL, email nvarchar(100) NULL, phone varchar(20) NULL,
              status char(1) NOT NULL CONSTRAINT df_member_status DEFAULT ('A'), memo nvarchar(200) NULL)
            """,
            "CREATE UNIQUE INDEX uq_member_name ON cmp_member (name)",
            "CREATE INDEX idx_member_email ON cmp_member (email)",
            MsComment("회원", null),
            MsComment("메일", "email"),
            """
            CREATE TABLE cmp_order (id int IDENTITY(1,1) CONSTRAINT pk_cmp_order PRIMARY KEY, member_id int NOT NULL,
              code varchar(20) NOT NULL CONSTRAINT uq_order_code UNIQUE, amount decimal(10,2) NOT NULL DEFAULT (0))
            """,
            "CREATE INDEX idx_order_member ON cmp_order (member_id)",
        ],
        _ =>
        [
            """
            CREATE TABLE cmp_member (id int NOT NULL AUTO_INCREMENT PRIMARY KEY, name varchar(50) NOT NULL,
              nick varchar(50) NULL, email varchar(100) NULL COMMENT '메일', phone varchar(20) NULL,
              status char(1) NOT NULL DEFAULT 'A', memo varchar(200) NULL,
              UNIQUE KEY uq_member_name (name), KEY idx_member_email (email)) COMMENT='회원'
            """,
            """
            CREATE TABLE cmp_order (id int NOT NULL AUTO_INCREMENT PRIMARY KEY, member_id int NOT NULL,
              code varchar(20) NOT NULL, amount decimal(10,2) NOT NULL DEFAULT 0,
              UNIQUE KEY uq_order_code (code), KEY idx_order_member (member_id))
            """,
        ],
    };

    /// A 에만 있는 테이블
    public static string[] OnlyA(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Postgres =>
        [
            "CREATE TABLE cmp_only_a (id serial PRIMARY KEY, title varchar(30) NOT NULL DEFAULT 'x')",
            "CREATE INDEX idx_only_title ON cmp_only_a (title)",
            "COMMENT ON TABLE cmp_only_a IS 'A 전용'",
        ],
        DatabaseKind.Mssql =>
        [
            "CREATE TABLE cmp_only_a (id int IDENTITY(1,1) CONSTRAINT pk_only_a PRIMARY KEY, title nvarchar(30) NOT NULL DEFAULT (N'x'))",
            "CREATE INDEX idx_only_title ON cmp_only_a (title)",
            MsComment("A 전용", null, "cmp_only_a"),
        ],
        _ => ["CREATE TABLE cmp_only_a (id int NOT NULL AUTO_INCREMENT PRIMARY KEY, title varchar(30) NOT NULL DEFAULT 'x', KEY idx_only_title (title)) COMMENT='A 전용'"],
    };

    /// 대상(B)을 기준과 다르게 바꾼다
    public static string[] AlterB(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Postgres =>
        [
            "ALTER TABLE cmp_member ADD COLUMN extra_b int",
            "ALTER TABLE cmp_member DROP COLUMN memo",
            "ALTER TABLE cmp_member ALTER COLUMN nick TYPE varchar(80)",
            "ALTER TABLE cmp_member ALTER COLUMN phone SET NOT NULL",
            "ALTER TABLE cmp_member ALTER COLUMN status SET DEFAULT 'B'",
            "COMMENT ON COLUMN cmp_member.email IS '이메일 주소'",
            "COMMENT ON TABLE cmp_member IS '회원 테이블'",
            "DROP INDEX idx_member_email",
            "CREATE INDEX idx_member_phone ON cmp_member (phone)",
            "DROP INDEX uq_order_code",
            "CREATE INDEX uq_order_code ON cmp_order (code)",
            "CREATE TABLE cmp_only_b (id int PRIMARY KEY)",
        ],
        DatabaseKind.Mssql =>
        [
            "ALTER TABLE cmp_member ADD extra_b int NULL",
            "ALTER TABLE cmp_member DROP COLUMN memo",
            "ALTER TABLE cmp_member ALTER COLUMN nick varchar(80) NULL",
            "ALTER TABLE cmp_member ALTER COLUMN phone varchar(20) NOT NULL",
            "ALTER TABLE cmp_member DROP CONSTRAINT df_member_status",
            "ALTER TABLE cmp_member ADD CONSTRAINT df_member_status DEFAULT ('B') FOR status",
            MsComment("이메일 주소", "email", update: true),
            MsComment("회원 테이블", null, update: true),
            "DROP INDEX idx_member_email ON cmp_member",
            "CREATE INDEX idx_member_phone ON cmp_member (phone)",
            "ALTER TABLE cmp_order DROP CONSTRAINT uq_order_code",
            "CREATE INDEX uq_order_code ON cmp_order (code)",
            "CREATE TABLE cmp_only_b (id int CONSTRAINT pk_only_b PRIMARY KEY)",
        ],
        _ =>
        [
            "ALTER TABLE cmp_member ADD COLUMN extra_b int NULL",
            "ALTER TABLE cmp_member DROP COLUMN memo",
            "ALTER TABLE cmp_member MODIFY nick varchar(80) NULL",
            "ALTER TABLE cmp_member MODIFY phone varchar(20) NOT NULL",
            "ALTER TABLE cmp_member ALTER COLUMN status SET DEFAULT 'B'",
            "ALTER TABLE cmp_member MODIFY email varchar(100) NULL COMMENT '이메일 주소'",
            "ALTER TABLE cmp_member COMMENT = '회원 테이블'",
            "ALTER TABLE cmp_member DROP INDEX idx_member_email, ADD INDEX idx_member_phone (phone)",
            "ALTER TABLE cmp_order DROP INDEX uq_order_code, ADD INDEX uq_order_code (code)",
            "CREATE TABLE cmp_only_b (id int PRIMARY KEY)",
        ],
    };

    public static string MsComment(string text, string? column, string table = "cmp_member", bool update = false)
    {
        var sql = $"EXEC sys.{(update ? "sp_updateextendedproperty" : "sp_addextendedproperty")} @name = N'MS_Description', @value = N'{text}', "
                + $"@level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'{table}'";
        if (column != null) sql += $", @level2type = N'COLUMN', @level2name = N'{column}'";
        return sql;
    }
}
