using GodDB.Core.Dialects;
using GodDB.Core.Results;
using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

/// Mac: ResultEditingTests.swift (Result editing · Binary text). DB 를 쓰는 편집 검사는 통합 테스트 ResultEditingIntegrationTests 로.
public class ResultEditingTests
{
    private readonly TableMeta table = new("tb_member", columns: [
        new ColumnMeta("member_id", "bigint", false, columnKey: "PRI", position: 1),
        new ColumnMeta("email", "varchar(100)", true, position: 2),
        new ColumnMeta("status", "char(1)", false, position: 3),
    ], indexes: [new IndexMeta("PRIMARY", true, ["member_id"])]);

    [Fact]
    public void DetectsSingleTable()
    {
        Assert.Equal("tb_member", ResultEditing.SingleTable("SELECT * FROM tb_member WHERE status = 'A'"));
        Assert.Equal("shop.tb_member", ResultEditing.SingleTable("select m.* from `shop`.`tb_member` m order by 1"));
        Assert.Equal("dbo.tb_member", ResultEditing.SingleTable("SELECT * FROM [dbo].[tb_member]"));
        Assert.Null(ResultEditing.SingleTable("SELECT * FROM a JOIN b ON a.id = b.id"));
        Assert.Null(ResultEditing.SingleTable("SELECT * FROM a, b"));
        Assert.Null(ResultEditing.SingleTable("SELECT status, COUNT(*) FROM a GROUP BY status"));
        Assert.Null(ResultEditing.SingleTable("SELECT * FROM a WHERE id IN (SELECT id FROM b)"));
        Assert.Null(ResultEditing.SingleTable("UPDATE a SET x = 1"));
    }

    [Fact]
    public void TargetNeedsPrimaryKey()
    {
        var t = ResultEditing.Target("SELECT member_id, email FROM tb_member", ["member_id", "email"], [table]);
        Assert.Equal([0], t.PrimaryKeyIndexes);
        Assert.Equal(new Dictionary<int, string> { [0] = "member_id", [1] = "email" }, t.ColumnMap);
        var e = Assert.Throws<ResultEditingException>(() => ResultEditing.Target("SELECT email FROM tb_member", ["email"], [table]));
        Assert.Equal(ResultEditingError.PrimaryKeyNotSelected, e.Kind);
        Assert.Equal("member_id", e.Detail);
        var e2 = Assert.Throws<ResultEditingException>(() => ResultEditing.Target("SELECT * FROM tb_member m JOIN x ON 1=1", [], [table]));
        Assert.Equal(ResultEditingError.NotSingleTable, e2.Kind);
    }

    [Fact]
    public void GeneratesStatements()
    {
        var t = ResultEditing.Target("SELECT * FROM tb_member", ["member_id", "email", "status"], [table]);
        List<QueryResult.Row> rows = [new(0, ["1", "a@x.com", "A"]), new(1, ["2", null, "A"])];
        var e = new PendingEdits();
        e.Updates[0] = new() { [1] = "it's@x.com", [2] = null };
        e.Deletes = [1];
        e.Inserts = [new() { [0] = "3", [1] = "c@x.com" }];
        Assert.Equal(4, e.Count);
        var sql = ResultEditing.Statements(e, t, rows, new MySqlDialect());
        Assert.Equal([
            "DELETE FROM `tb_member` WHERE `member_id` = '2';",
            "UPDATE `tb_member` SET `email` = 'it''s@x.com', `status` = NULL WHERE `member_id` = '1';",
            "INSERT INTO `tb_member` (`member_id`, `email`) VALUES ('3', 'c@x.com');",
        ], sql);
        var ms = ResultEditing.Statements(e, t, rows, new MssqlDialect());
        Assert.Equal("DELETE FROM [tb_member] WHERE [member_id] = N'2';", ms[0]);
    }

    [Fact]
    public void HexAndLiterals()
    {
        Assert.Equal("0xCD01FF", GodDB.Core.Drivers.BinaryText.Hex([0xCD, 0x01, 0xFF]));
        Assert.True(BinaryColumn.IsHex("0xcd01") && !BinaryColumn.IsHex("0xC") && !BinaryColumn.IsHex("abc"));
        Assert.Equal("X'CD01'", new MySqlDialect().BinaryLiteral("CD01"));
        Assert.Equal("decode('CD01', 'hex')", new PostgresDialect().BinaryLiteral("CD01"));
        Assert.Equal("0xCD01", new MssqlDialect().BinaryLiteral("CD01"));
    }

    // ── Mac ResultEditingUI.swift 의 편집 상태 로직 (Windows 에서 Core 로 옮김) ──

    [Fact]
    public void PendingEditsCellLogic()
    {
        var r = new QueryResult(["member_id", "email"], [new string?[] { "1", "a" }, new string?[] { "2", "b" }], 0);
        var e = new PendingEdits();
        e.SetCell(r, 0, 1, "z");
        Assert.Equal(CellState.Modified, e.State(0, 1));
        e.SetCell(r, 0, 1, "a");   // 원래 값으로 돌아가면 변경에서 빠진다
        Assert.True(e.IsEmpty);
        var id = e.AddRow();
        Assert.Equal(PendingEdits.NewRowBase, id);
        Assert.Equal(CellState.InsertedDefault, e.State(id, 0));
        e.SetCell(r, id, 0, "9");
        Assert.Equal(CellState.Inserted, e.State(id, 0));
        e.ResetInsertedCell(id, 0);
        Assert.Equal(CellState.InsertedDefault, e.State(id, 0));
        e.ToggleDelete([1, id]);
        Assert.Equal(CellState.Deleted, e.State(1, 0));
        Assert.Empty(e.Inserts);
        e.ToggleDelete([1]);
        Assert.Equal(CellState.Normal, e.State(1, 0));
        e.SetCell(r, 1, 1, null);
        e.AddRow();
        var shown = e.DisplayRows(r);
        Assert.Equal(3, shown.Count);
        Assert.Null(shown[1].Values[1]);
        Assert.Equal([null, null], shown[2].Values);
        var copy = e.Clone();
        Assert.Equal(e, copy);
        copy.Deletes.Add(0);
        Assert.NotEqual(e, copy);
    }

    [Fact]
    public void CheckExplainsWhyNotEditable()
    {
        var r = new QueryResult(["member_id"], [new string?[] { "1" }], 0) { Sql = "SELECT member_id FROM tb_member" };
        Assert.True(ResultEditing.Check(r, false, false, [table])!.CanEdit);
        Assert.Null(ResultEditing.Check(r, true, false, [table]));
        Assert.IsType<GodDB.Core.Drivers.GodDbException>(ResultEditing.Check(r, false, true, [table])!.Error);
        r.TruncatedCells = 1;
        Assert.Equal(ResultEditingError.ValuesTruncated, Assert.IsType<ResultEditingException>(ResultEditing.Check(r, false, false, [table])!.Error).Kind);
    }
}
