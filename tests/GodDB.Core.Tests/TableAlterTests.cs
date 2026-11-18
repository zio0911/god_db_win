using System.Text.Json;
using GodDB.Core.Dialects;
using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

public class TableAlterTests
{
    private readonly TableMeta table = new(
        "tb_member", comment: "회원",
        columns:
        [
            new ColumnMeta("id", "bigint", false, extra: "auto_increment", columnKey: "PRI", position: 1),
            new ColumnMeta("email", "varchar(100)", true, position: 2),
            new ColumnMeta("nick", "varchar(20)", true, position: 3),
        ],
        indexes:
        [
            new IndexMeta("PRIMARY", true, ["id"]),
            new IndexMeta("idx_email", false, ["email"]),
        ]);

    private List<EditableColumn> Cols => [.. table.Columns.Select(c => new EditableColumn(c))];
    private List<EditableIndex> Idxs => [.. table.Indexes.Select(i => new EditableIndex(i))];

    [Fact]
    public void NoChangesGivesNull()
    {
        Assert.Null(TableAlter.Statement(table, Cols, Idxs, "회원"));
    }

    [Fact]
    public void RenameModifyDropAdd()
    {
        var c = Cols;
        c[1].Name = "mail";                 // rename
        c[1].ColumnType = "varchar(200)";
        c.RemoveAt(2);                      // drop nick
        c.Add(new EditableColumn(name: "status", columnType: "char(1)", nullable: false, defaultValue: "A"));
        var i = Idxs;
        i[1].Columns = ["mail"];            // email 인덱스 컬럼 변경
        i.Add(new EditableIndex(name: "idx_status", columns: ["status"]));
        var sql = TableAlter.Statement(table, c, i, "회원 테이블");
        Assert.NotNull(sql);
        Assert.Equal("""
            ALTER TABLE `tb_member`
              DROP INDEX `idx_email`,
              DROP COLUMN `nick`,
              CHANGE COLUMN `email` `mail` varchar(200) NULL,
              ADD COLUMN `status` char(1) NOT NULL DEFAULT 'A' AFTER `mail`,
              ADD INDEX `idx_email` (`mail`),
              ADD INDEX `idx_status` (`status`),
              COMMENT = '회원 테이블';
            """, sql);
    }

    [Fact]
    public void Validation()
    {
        var c = Cols;
        c[2].Name = "EMAIL";
        var e1 = Assert.Throws<TableAlterException>(() => TableAlter.Statement(table, c, Idxs, ""));
        Assert.Equal(new TableAlterError.DuplicateColumn("EMAIL"), e1.Error);
        var i = Idxs;
        i.Add(new EditableIndex(name: "idx_x", columns: ["nope"]));
        var e2 = Assert.Throws<TableAlterException>(() => TableAlter.Statement(table, Cols, i, ""));
        Assert.Equal(new TableAlterError.UnknownIndexColumn("idx_x", "nope"), e2.Error);
    }
}

public class PlanAnalyzerTests
{
    [Fact]
    public void FlagsFullScanAndFilesort()
    {
        var r = new QueryResult(
            ["id", "select_type", "table", "type", "possible_keys", "key", "rows", "Extra"],
            [["1", "SIMPLE", "tb_member", "ALL", null, null, "12000", "Using where; Using filesort"],
             ["1", "SIMPLE", "tb_order", "ref", "idx_member", "idx_member", "3", null]],
            0);
        var w = PlanAnalyzer.Warnings(r);
        Assert.Equal(2, w.Count);
        Assert.True(w[0].Table == "tb_member" && w[0].Message.Contains("풀 테이블 스캔"));
        Assert.Contains("filesort", w[1].Message);
    }
}

public class ConnectionProfileTests
{
    [Fact]
    public void DecodesOldJsonWithoutSavePassword()
    {
        var json = """
            [{"id":"559343C0-9D63-4846-A9F4-CB47500265B9","name":"a","host":"localhost","port":3306,
              "username":"root","database":"shop","useTLS":false,"readOnly":false}]
            """;
        var p = JsonSerializer.Deserialize<List<ConnectionProfile>>(json, ProfileStore.Json)!;
        Assert.True(p[0].SavePassword);
        Assert.Null(p[0].Ssh);
    }

    [Fact]
    public void ExtraParts()
    {
        var e = new ColumnExtraParts("DEFAULT_GENERATED on update CURRENT_TIMESTAMP(3)", DatabaseKind.Mysql);
        Assert.False(e.AutoIncrement);
        Assert.Equal("CURRENT_TIMESTAMP(3)", e.OnUpdate);
        Assert.Equal("DEFAULT_GENERATED", e.Rest);
        var ai = new ColumnExtraParts("auto_increment", DatabaseKind.Mysql);
        Assert.True(ai.AutoIncrement && ai.Rest.Length == 0);
        ai.OnUpdate = "CURRENT_TIMESTAMP";
        Assert.Equal("auto_increment on update CURRENT_TIMESTAMP", ai.Compose(DatabaseKind.Mysql));
        ai.AutoIncrement = false;
        ai.OnUpdate = "";
        Assert.Equal("", ai.Compose(DatabaseKind.Mysql));
        var pg = new ColumnExtraParts("identity", DatabaseKind.Postgres);
        Assert.True(pg.AutoIncrement);
        Assert.Equal("identity", pg.Compose(DatabaseKind.Postgres));
    }
}
