using System.Text.Json;
using GodDB.Core.Dialects;
using GodDB.Core.Results;
using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

/// Redshift (PostgreSQL 8.0 기반 카탈로그): 메타데이터 조회가 없는 항목을 쓰지 않는지, DDL 에 DISTKEY · SORTKEY (Mac RedshiftDialectTests)
public class RedshiftDialectTests
{
    private readonly PostgresDialect rs = new(redshift: true);
    private readonly PostgresDialect pg = new();

    [Fact]
    public void DetectsRedshiftFromVersion()
    {
        const string v = "PostgreSQL 8.0.2 on i686-pc-linux-gnu, compiled by GCC gcc (GCC) 3.4.2 20041017 (Red Hat 3.4.2-6.fc3), Redshift 1.0.123456";
        Assert.Equal("Redshift 1.0.123456", PostgresDialect.RedshiftVersion(v));
        Assert.Null(PostgresDialect.RedshiftVersion("PostgreSQL 16.4 on aarch64-unknown-linux-gnu"));
        Assert.True(rs.IsRedshift());
        Assert.False(pg.IsRedshift());
        Assert.False(new MySqlDialect().IsRedshift());
    }

    [Fact]
    public void MetadataAvoidsNewerCatalog()
    {
        string[] all = [rs.ColumnsSql("public"), rs.IndexesSql("public"), rs.ForeignKeysSql("public"),
                        rs.RoutinesSql("public"), rs.AllTablesSql ?? ""];
        foreach (var sql in all)
            foreach (var word in new[] { "attidentity", "prokind IN", "unnest", "LATERAL", "tgisinternal", "information_schema.tables" })
                Assert.False(sql.Contains(word), $"{word} in {sql[..Math.Min(60, sql.Length)]}");
        // 사용 권한 없는 스키마는 목록에서 뺀다 (PostgreSQL 은 그대로)
        Assert.Contains("has_schema_privilege(nspname, 'USAGE')", rs.DatabasesSql);
        Assert.Contains("has_schema_privilege(n.nspname, 'USAGE')", rs.AllTablesSql);
        Assert.DoesNotContain("has_schema_privilege", pg.DatabasesSql);
        Assert.Null(rs.SessionSetupSql);
        Assert.NotNull(pg.SessionSetupSql);
        // PostgreSQL 은 그대로
        Assert.Contains("attidentity", pg.ColumnsSql("public"));
        Assert.Contains("unnest", pg.ForeignKeysSql("public"));
    }

    /// Mac #175: Redshift 분기가 PostgreSQL 프로시저까지 잡아 본문만 보여 주던 문제 (Windows 에 같은 버그가 없는지)
    [Fact]
    public void RoutineDefinitionByKind()
    {
        foreach (var kind in new[] { RoutineKind.Procedure, RoutineKind.Function })
        {
            var r = new RoutineMeta(kind, "p1");
            Assert.True(pg.DefinitionSql(r, "public").Contains("pg_get_functiondef"), $"{kind}");
            Assert.True(rs.DefinitionSql(r, "public").Contains("p.prosrc"), $"{kind}");
            Assert.False(rs.DefinitionSql(r, "public").Contains("pg_get_functiondef"), $"{kind}");
        }
    }

    [Fact]
    public void DistAndSortKeysFromExtra()
    {
        Assert.Equal((true, (int?)1), PostgresDialect.RedshiftKeys(new ColumnMeta("a", "int", false, extra: "distkey sortkey 1")));
        Assert.Equal((false, (int?)2), PostgresDialect.RedshiftKeys(new ColumnMeta("b", "int", false, extra: "sortkey 2")));
        Assert.Equal((false, (int?)null), PostgresDialect.RedshiftKeys(new ColumnMeta("c", "int", false, extra: "")));
    }

    [Fact]
    public void CreateTableHasDistAndSortKeys()
    {
        var t = new TableMeta("sales", "판매", [
            new ColumnMeta("id", "bigint", false, extra: "distkey", columnKey: "PRI", position: 1),
            new ColumnMeta("sold_at", "timestamp without time zone", true, extra: "sortkey 2", position: 2),
            new ColumnMeta("shop_id", "integer", true, extra: "sortkey 1", comment: "매장", position: 3),
        ], [new IndexMeta("sales_pkey", true, ["id"], primary: true)]);
        var ddl = rs.CreateTableSql(t);
        Assert.Contains("DISTKEY (\"id\")", ddl);
        Assert.Contains("SORTKEY (\"shop_id\", \"sold_at\")", ddl);
        Assert.Contains("CONSTRAINT \"sales_pkey\" PRIMARY KEY", ddl);
        Assert.DoesNotContain("CREATE INDEX", ddl);
        Assert.DoesNotContain("SEQUENCE", ddl);
        Assert.Contains("COMMENT ON COLUMN \"sales\".\"shop_id\" IS '매장';", ddl);
    }

    [Fact]
    public void ConnectionTypeRedshift()
    {
        var p = new ConnectionProfile { Kind = DatabaseKind.Postgres, Port = 5432, UseTLS = false };
        Assert.Equal(ConnectionType.Postgres, p.Type);
        p.Type = ConnectionType.Redshift;
        Assert.True(p.Kind == DatabaseKind.Postgres && p.Redshift && p.Port == 5439 && p.UseTLS);
        Assert.Equal("Redshift", p.TypeTitle);
        p.Type = ConnectionType.Mysql;
        Assert.True(p.Kind == DatabaseKind.Mysql && !p.Redshift && p.Port == 3306);
        // 포트를 바꿔 둔 접속은 그대로
        p.Type = ConnectionType.Redshift;
        p.Port = 15439;
        p.Type = ConnectionType.Postgres;
        Assert.True(p.Port == 15439 && !p.Redshift);
        Assert.Equal(["MySQL", "PostgreSQL", "Redshift", "SQL Server", "Redis"], ConnectionTypes.All.Select(t => t.Title()));

        // 저장 · 읽기, 예전 파일(redshift 없음)은 false
        var r = new ConnectionProfile { Kind = DatabaseKind.Postgres, Redshift = true };
        var back = JsonSerializer.Deserialize<ConnectionProfile>(JsonSerializer.Serialize(r, ProfileStore.Json), ProfileStore.Json)!;
        Assert.True(back.Redshift && back.Type == ConnectionType.Redshift);
        const string old = """{"id":"6F1C2A6E-1B8B-4B6E-9C55-0C1F0E6A9A10","kind":"postgres","name":"a","host":"h","port":5432,"username":"u"}""";
        Assert.False(JsonSerializer.Deserialize<ConnectionProfile>(old, ProfileStore.Json)!.Redshift);
    }

    [Fact]
    public void EditingIsBlockedMessageIsTranslated()
    {
        var ko = ResultEditingException.Describe(ResultEditingError.KeysNotEnforced, null);
        Assert.Contains("Redshift", ko);
        Assert.True(L10n.EnglishTable.ContainsKey("Redshift 는 기본 키를 강제하지 않아 같은 키의 행이 여러 개일 수 있어서, 결과 표에서 고치지 않아요. UPDATE 문으로 수정하세요."));
        // Windows: 결과 편집 확인에서도 막는다 (Mac ResultEditingUI.editTarget)
        var table = new TableMeta("t", columns: [new ColumnMeta("id", "int", false, columnKey: "PRI", position: 1)],
            indexes: [new IndexMeta("t_pkey", true, ["id"], primary: true)]);
        var result = new QueryResult(["id"], [new string?[] { "1" }], 0) { Sql = "SELECT * FROM t" };
        Assert.True(ResultEditing.Check(result, false, false, [table])!.CanEdit);
        Assert.Equal(ResultEditingError.KeysNotEnforced,
            Assert.IsType<ResultEditingException>(ResultEditing.Check(result, false, false, [table], redshift: true)!.Error).Kind);
    }
}
