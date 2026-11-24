using GodDB.Core.Schema;
using GodDB.Core.Sql;

namespace GodDB.Core.Tests;

// Mac GodDBCoreTests.swift 의 "SQLText" 묶음
public class SqlTextTests
{
    [Fact]
    public void SplitsStatementsIgnoringQuotedSemicolons()
    {
        var sql = "SELECT 'a;b' FROM t;\n-- x; y\nSELECT `c;d` FROM u;";
        var parts = SqlText.Statements(sql).Select(s => s.Sql).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal("SELECT 'a;b' FROM t", parts[0]);
        Assert.EndsWith("SELECT `c;d` FROM u", parts[1]);
    }

    [Fact]
    public void PicksStatementAtCursor()
    {
        var sql = "SELECT 1;\nSELECT 2;\nSELECT 3;";
        var cursor = sql.IndexOf('2');
        Assert.Equal("SELECT 2", SqlText.Statement(sql, cursor));
        Assert.Equal("SELECT 3", SqlText.Statement(sql, sql.Length));
    }

    [Fact]
    public void ReadOnlyGuard()
    {
        Assert.True(SqlText.IsReadOnly("select * from t", DatabaseKind.Mysql));
        Assert.True(SqlText.IsReadOnly("/* hi */ SHOW TABLES", DatabaseKind.Mysql));
        Assert.True(SqlText.IsReadOnly("  explain select 1", DatabaseKind.Mysql));
        Assert.False(SqlText.IsReadOnly("UPDATE t SET a = 1", DatabaseKind.Mysql));
        Assert.False(SqlText.IsReadOnly("-- select\nDELETE FROM t", DatabaseKind.Mysql));
        Assert.False(SqlText.IsReadOnly("SELECT * FROM t INTO OUTFILE '/tmp/x'", DatabaseKind.Mysql));
        Assert.False(SqlText.IsReadOnly("WITH x AS (SELECT 1) DELETE FROM t", DatabaseKind.Mysql));
    }

    [Fact]
    public void AppliesLimitOnlyWhenMissing()
    {
        Assert.Equal("SELECT * FROM t\nLIMIT 500", SqlText.ApplyingLimit("SELECT * FROM t;", 500));
        Assert.Equal("SELECT * FROM t LIMIT 10", SqlText.ApplyingLimit("SELECT * FROM t LIMIT 10", 500));
        Assert.Equal("SHOW TABLES", SqlText.ApplyingLimit("SHOW TABLES", 500));
        Assert.Equal("SELECT 1", SqlText.ApplyingLimit("SELECT 1", 0));
    }
}

// Mac GodDBCoreTests.swift 의 "SchemaDiff" 묶음
public class SchemaDiffTests
{
    private readonly ColumnMeta id = new("id", "bigint", false, extra: "auto_increment", columnKey: "PRI", position: 1);
    private readonly ColumnMeta email = new("email", "varchar(100)", true, comment: "메일", position: 2);
    private readonly ColumnMeta regDt = new("reg_dt", "datetime", false, defaultValue: "CURRENT_TIMESTAMP", extra: "DEFAULT_GENERATED", position: 3);

    [Fact]
    public void IdenticalSchemasHaveNoDiff()
    {
        var t = new TableMeta("tb_member", columns: [id, email], indexes: [new IndexMeta("PRIMARY", true, ["id"])]);
        var report = SchemaDiff.Compare([t], [t]);
        Assert.Empty(report.Items);
        Assert.Equal(1, report.MatchedTables);
    }

    [Fact]
    public void DetectsMissingAndDifferentColumns()
    {
        var shortEmail = email with { ColumnType = "varchar(50)" };
        var source = new TableMeta("tb_member", columns: [id, email, regDt]);
        var target = new TableMeta("tb_member", columns: [id, shortEmail]);
        var report = SchemaDiff.Compare([source], [target]);

        var diff = report.Items.FirstOrDefault(i => i.Kind == SchemaDiffKind.DifferentColumn);
        Assert.Equal("ALTER TABLE `tb_member` MODIFY COLUMN `email` varchar(100) NULL COMMENT '메일';", diff?.FixSql);

        var missing = report.Items.FirstOrDefault(i => i.Kind == SchemaDiffKind.MissingColumn);
        Assert.Equal("ALTER TABLE `tb_member` ADD COLUMN `reg_dt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP AFTER `email`;", missing?.FixSql);
    }

    [Fact]
    public void QuotesLiteralDefaults()
    {
        var c = new ColumnMeta("status", "char(1)", false, defaultValue: "A");
        Assert.Equal("`status` char(1) NOT NULL DEFAULT 'A'", SchemaDiff.ColumnDefinition(c));
    }

    [Fact]
    public void IndexesAndTables()
    {
        TableMeta[] source =
        [
            new("a", columns: [id], indexes: [new IndexMeta("idx_x", false, ["x"]), new IndexMeta("uq_y", true, ["y", "z"])]),
            new("b", columns: [id]),
        ];
        TableMeta[] target =
        [
            new("a", columns: [id], indexes: [new IndexMeta("idx_x", false, ["x", "w"])]),
            new("c", columns: [id]),
        ];
        var report = SchemaDiff.Compare(source, target, createStatements: new Dictionary<string, string> { ["b"] = "CREATE TABLE `b` (...)" });
        Assert.Equal("ALTER TABLE `a` DROP INDEX `idx_x`, ADD INDEX `idx_x` (`x`);",
                     report.Items.FirstOrDefault(i => i.Kind == SchemaDiffKind.DifferentIndex)?.FixSql);
        Assert.Equal("ALTER TABLE `a` ADD UNIQUE INDEX `uq_y` (`y`, `z`);",
                     report.Items.FirstOrDefault(i => i.Kind == SchemaDiffKind.MissingIndex)?.FixSql);
        Assert.Equal("CREATE TABLE `b` (...);", report.Items.FirstOrDefault(i => i.Kind == SchemaDiffKind.MissingTable)?.FixSql);
        Assert.Contains(report.Items, i => i.Kind == SchemaDiffKind.ExtraTable && i.Table == "c" && i.FixSql == null);
        Assert.DoesNotContain("DROP TABLE", report.FixScript);
    }

    [Fact]
    public void BuildsSchemaFromRows()
    {
        var tables = SchemaBuilder.Build(
            tableRows: [["t", "코멘트"]],
            columnRows: [["t", "id", "bigint", "NO", null, "auto_increment", "PRI", "", "1"]],
            indexRows: [["t", "PRIMARY", "0", "id"], ["t", "idx_ab", "1", "a"], ["t", "idx_ab", "1", "b"]]);
        Assert.Single(tables);
        Assert.True(tables[0].Columns[0].IsPrimaryKey);
        Assert.Equal(["a", "b"], tables[0].Indexes.First(i => i.Name == "idx_ab").Columns);
        Assert.True(tables[0].Indexes.First(i => i.Name == "PRIMARY").Unique);
    }
}

// SSHTunnel 묶음(buildsSSHArguments, findsFreePort)은 SSH 터널을 옮길 때 그쪽 테스트로 옮긴다.
