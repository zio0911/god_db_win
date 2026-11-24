using GodDB.Core;
using GodDB.Core.Dialects;
using GodDB.Core.Drivers;
using GodDB.Core.Schema;
using GodDB.Core.Sql;

namespace GodDB.Integration.Tests;

/// Mac IntegrationTests.swift — 실제 MySQL 대상. GODDB_IT_PORT 가 있을 때만 실행된다.
/// Mac 은 shop.tb_member 를 지우고 다시 넣지만, 여기서는 다른 테스트와 겹치지 않게
/// 실행마다 shop 과 같은 모양의 DB(goddb_it_…) 를 새로 만들어 쓰고 끝나면 지운다.
public class IntegrationTests
{
    /// shop 의 tb_member / tb_coupon 과 같은 모양의 테스트 전용 DB
    private static async Task<(DbSession Session, string Db)> ScratchAsync()
    {
        var db = $"goddb_it_{It.Suffix()}";
        await using (var admin = await DbSession.ConnectAsync(It.MySql(), It.MySqlPassword))
        {
            await admin.RawAsync($"CREATE DATABASE {db}");
            await admin.RawAsync($"""
                CREATE TABLE {db}.tb_member (member_id bigint NOT NULL AUTO_INCREMENT, email varchar(100) DEFAULT NULL COMMENT '메일',
                  status char(1) NOT NULL DEFAULT 'A', reg_dt datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
                  PRIMARY KEY (member_id), KEY idx_email (email)) COMMENT='회원'
                """);
            await admin.RawAsync($"CREATE TABLE {db}.tb_coupon (id int NOT NULL, code varchar(20) NOT NULL, PRIMARY KEY (id), KEY idx_tb_coupon_1 (code))");
        }
        return (await DbSession.ConnectAsync(It.MySql(db), It.MySqlPassword), db);
    }

    private static Task DropAsync(params string[] dbs) =>
        It.CleanupAsync(DatabaseKind.Mysql, "shop", [.. dbs.Select(d => $"DROP DATABASE IF EXISTS {d}")]);

    [Fact]
    public async Task QueryAndSchemaAndCompare()
    {
        TestDb.Port(DatabaseKind.Mysql);
        var (s, shop) = await ScratchAsync();
        var stg = $"goddb_it_stg_{It.Suffix()}";
        try
        {
            await s.RawAsync($"DELETE FROM {shop}.tb_member");
            await s.RawAsync($"INSERT INTO {shop}.tb_member (member_id, email) VALUES (1, 'kim@sample.com'), (2, 'lee@sample.com'), (3, NULL)");
            Assert.StartsWith("MySQL 8", await s.ServerVersionAsync());
            var dbs = await s.DatabasesAsync();
            Assert.Contains(shop, dbs);

            var r = await s.ExecuteAsync("SELECT member_id, email FROM tb_member ORDER BY member_id", limit: 2);
            Assert.Equal(["member_id", "email"], r.Columns.Select(c => c.Name));
            Assert.Equal(2, r.Rows.Count);
            Assert.Equal("kim@sample.com", r.Rows[0].Values[1]);

            var nulls = await s.ExecuteAsync("SELECT email FROM tb_member WHERE email IS NULL", limit: 500);
            Assert.NotEmpty(nulls.Rows);
            Assert.Null(nulls.Rows[0].Values[0]);

            await MultiDBChecks.Paging(s, "tb_member");
            await MultiDBChecks.ServerSort(s, "tb_member");
            await s.RawAsync("DROP TABLE IF EXISTS tb_rename_it, tb_renamed_it");
            await MultiDBChecks.RenameAndDrop(s, shop);
            // editing: 결과 편집(ResultEditing) 미포팅 — 건너뜀
            await MultiDBChecks.Binary(s, shop);
            await MultiDBChecks.ForeignKeys(s, shop);

            var empty = await s.ExecuteAsync("SELECT * FROM tb_member WHERE 1 = 0", limit: 500);
            Assert.Equal("결과 없음", empty.Message);

            // 테스트 전용 대상 스키마를 매번 새로 만든다
            await s.RawAsync($"CREATE DATABASE {stg}");
            await s.RawAsync($"CREATE TABLE {stg}.tb_member (member_id BIGINT AUTO_INCREMENT PRIMARY KEY, email VARCHAR(50), status CHAR(1) NOT NULL DEFAULT 'A')");
            var source = (await s.LoadSchemaAsync(shop)).Where(t => !t.Name.StartsWith("tb_alter_it")).ToList();
            var target = await s.LoadSchemaAsync(stg);
            Assert.Equal("회원", source.FirstOrDefault(t => t.Name == "tb_member")?.Comment);
            var create = await s.ShowCreateTableAsync("tb_coupon");
            var report = SchemaDiff.Compare(source, target, new Dictionary<string, string> { ["tb_coupon"] = create ?? "" });
            Assert.Contains(report.Items, i => i.Kind == SchemaDiffKind.MissingTable && i.Table == "tb_coupon");
            Assert.Contains(report.Items, i => i.Kind == SchemaDiffKind.DifferentColumn && i.Object == "email");
            Assert.Contains(report.Items, i => i.Kind == SchemaDiffKind.MissingColumn && i.Object == "reg_dt");
            Assert.Contains(report.Items, i => i.Kind == SchemaDiffKind.MissingIndex && i.Object == "idx_email");

            // 생성된 보정 SQL 을 stg 에 적용하면 차이가 사라져야 한다
            await s.UseAsync(stg);
            foreach (var stmt in SqlText.Statements(report.FixScript)) await s.RawAsync(stmt.Sql);
            var after = SchemaDiff.Compare(source, await s.LoadSchemaAsync(stg));
            Assert.True(after.Items.Count == 0, string.Join("; ", after.Items.Select(i => i.Id + " " + i.Detail)));
        }
        finally
        {
            await s.CloseAsync();
            await DropAsync(shop, stg);
        }
    }

    /// 회귀: localhost 가 ::1 로 먼저 풀려도 IPv4 로 연결돼야 한다. (Docker 는 127.0.0.1 만 열림)
    [Fact]
    public async Task ConnectsViaLocalhostName()
    {
        var p = It.MySql() with { Host = "localhost", UseTLS = false };
        await using var s = await DbSession.ConnectAsync(p, It.MySqlPassword);
        Assert.Equal("1", (await s.RawAsync("SELECT 1")).Rows.FirstOrDefault()?[0]);
    }

    [Fact]
    public async Task RefusedPortGivesKoreanHint()
    {
        var p = It.MySql() with { Port = 1 };
        var e = await Assert.ThrowsAnyAsync<Exception>(() => DbSession.ConnectAsync(p, It.MySqlPassword));
        var d = GodDB.Core.Audit.ErrorText.Describe(e);
        Assert.True(d.Message.Contains("거부"), $"{d.Message} / {d.Raw}");
        Assert.NotNull(d.Hint);
    }

    [Fact]
    public async Task WrongPasswordGivesHint()
    {
        var e = await Assert.ThrowsAnyAsync<Exception>(() => DbSession.ConnectAsync(It.MySql(), "wrong"));
        Assert.Equal(1045, Assert.IsType<MySqlConnector.MySqlException>(e).Number);
        var d = GodDB.Core.Audit.ErrorText.Describe(e);
        Assert.True(d.Message.Contains("1045"), d.Message);
        Assert.Contains("비밀번호", d.Hint ?? "");
        Assert.True(GodDB.Core.Audit.ErrorText.IsAuthFailure(e));
    }

    [Fact]
    public async Task ReadOnlyBlocksWrites()
    {
        TestDb.Port(DatabaseKind.Mysql);
        var (admin, db) = await ScratchAsync();
        await admin.CloseAsync();
        try
        {
            var p = It.MySql(db) with { ReadOnly = true };
            await using var s = await DbSession.ConnectAsync(p, It.MySqlPassword);
            var e = await Assert.ThrowsAsync<GodDbException>(() => s.ExecuteAsync("DELETE FROM tb_member", limit: 0));
            Assert.Equal(DbErrorKind.ReadOnlyRejected, e.Kind);
            // 가드를 우회하더라도 서버 세션이 읽기전용이라 거부된다 (MySQL 1792)
            var server = await Assert.ThrowsAnyAsync<Exception>(() => s.RawAsync("INSERT INTO tb_member (email) VALUES ('x')"));
            Assert.Equal(1792, Assert.IsType<MySqlConnector.MySqlException>(server).Number);
        }
        finally { await DropAsync(db); }
    }

    /// 생성한 ALTER 문이 실제 MySQL 에서 실행되고 결과가 의도대로인지 확인
    [Fact]
    public async Task AlterRunsOnServer()
    {
        TestDb.Port(DatabaseKind.Mysql);
        var (s, db) = await ScratchAsync();
        try
        {
            await s.RawAsync("DROP TABLE IF EXISTS tb_alter_it");
            await s.RawAsync("CREATE TABLE tb_alter_it (id INT PRIMARY KEY, email VARCHAR(100), nick VARCHAR(20), KEY idx_email (email))");
            var original = Assert.Single(await s.LoadSchemaAsync(db), t => t.Name == "tb_alter_it");
            var c = original.Columns.Select(x => new EditableColumn(x)).ToList();
            c[1].Name = "mail";
            c.RemoveAt(2);
            c.Add(new EditableColumn(name: "status", columnType: "char(1)", nullable: false, defaultValue: "A", comment: "상태"));
            var i = original.Indexes.Select(x => new EditableIndex(x)).ToList();
            foreach (var x in i) if (x.Name == "idx_email") x.Columns = ["mail"];
            var sql = TableAlter.Statement(original, c, i, "테스트");
            Assert.NotNull(sql);
            await s.ExecuteAsync(sql, limit: 0);
            var after = Assert.Single(await s.LoadSchemaAsync(db), t => t.Name == "tb_alter_it");
            Assert.Equal(["id", "mail", "status"], after.Columns.Select(x => x.Name));
            Assert.Equal("상태", after.Columns[^1].Comment);
            Assert.Equal(["mail"], after.Indexes.First(x => x.Name == "idx_email").Columns);
            Assert.Equal("테스트", after.Comment);
            await s.RawAsync("DROP TABLE tb_alter_it");
        }
        finally
        {
            await s.CloseAsync();
            await DropAsync(db);
        }
    }

    [Fact]
    public async Task RoutinesAndExplain()
    {
        TestDb.Port(DatabaseKind.Mysql);
        var (s, db) = await ScratchAsync();
        try
        {
            await s.RawAsync("INSERT INTO tb_member (email) VALUES ('kim@sample.com'), ('lee@sample.com'), (NULL)");
            await s.RawAsync("DROP PROCEDURE IF EXISTS sp_it_count");
            await s.RawAsync("CREATE PROCEDURE sp_it_count() COMMENT '회원 수' SELECT COUNT(*) FROM tb_member");
            await s.RawAsync("CREATE OR REPLACE VIEW v_it_member AS SELECT member_id, email FROM tb_member");
            var routines = await s.LoadRoutinesAsync(db);
            var sp = Assert.Single(routines, r => r.Name == "sp_it_count");
            Assert.True(sp.Kind == RoutineKind.Procedure && sp.Comment == "회원 수");
            Assert.Contains("SELECT COUNT(*)", await s.DefinitionAsync(sp) ?? "");
            var v = Assert.Single(routines, r => r.Name == "v_it_member");
            Assert.Contains("member_id", await s.DefinitionAsync(v) ?? "");
            Assert.DoesNotContain(await s.LoadSchemaAsync(db), t => t.Name == "v_it_member");

            var plan = await s.ExecuteAsync("EXPLAIN SELECT * FROM tb_member WHERE status = 'A' ORDER BY reg_dt", limit: 500);
            Assert.Contains(PlanAnalyzer.Warnings(plan), w => w.Message.Contains("풀 테이블 스캔"));
            await s.RawAsync("DROP PROCEDURE sp_it_count");
            await s.RawAsync("DROP VIEW v_it_member");
        }
        finally
        {
            await s.CloseAsync();
            await DropAsync(db);
        }
    }
}
