using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

// MultiDBChecks.foreignKeys 는 실제 DB 가 필요해 통합 테스트로 옮긴다.

public class DiagramLayoutTests
{
    private readonly TableMeta member = new("member", columns: [new ColumnMeta("id", "int", false)]);
    private TableMeta Order => new("orders",
        columns: [new ColumnMeta("id", "int", false), new ColumnMeta("member_id", "int", false)],
        foreignKeys: [new ForeignKeyMeta("fk_member", "orders", ["member_id"], "member", ["id"])]);
    private TableMeta Item => new("order_item",
        foreignKeys: [new ForeignKeyMeta("fk_order", "order_item", ["order_id"], "orders", ["id"])]);
    private readonly TableMeta loose = new("log");

    [Fact]
    public void RelatedTables()
    {
        TableMeta[] all = [member, Order, Item, loose];
        Assert.Equal(["orders", "member", "order_item"], DiagramLayout.Related("orders", all).Select(t => t.Name));
        Assert.Equal(["log"], DiagramLayout.Related("log", all).Select(t => t.Name));
    }

    [Fact]
    public void ParentsLeftChildrenRight()
    {
        var f = DiagramLayout.Frames([Item, Order, member, loose]);
        Assert.True(f["member"].MinX < f["orders"].MinX && f["orders"].MinX < f["order_item"].MinX);
        Assert.True(f["log"].MinY >= Math.Max(f["member"].MaxY, f["orders"].MaxY));
        // 겹치지 않는다
        var rects = f.Values.ToList();
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
                Assert.False(rects[i].Intersects(rects[j]));
    }

    [Fact]
    public void AttachesMultiColumnKeys()
    {
        TableMeta[] tables = [new("a"), new("b")];
        var output = SchemaBuilder.AttachForeignKeys(tables, [["b", "fk_ab", "x1", "a", "y1", "1"], ["b", "fk_ab", "x2", "a", "y2", "2"]]);
        Assert.Equal(["x1", "x2"], output[1].ForeignKeys.First().Columns);
        Assert.Equal(["y1", "y2"], output[1].ForeignKeys.First().ReferencedColumns);
        // 원래 테이블은 바뀌지 않는다 (Swift 값 복사와 같게)
        Assert.Empty(tables[1].ForeignKeys);
    }
}

public class InferredRelationTests
{
    private static TableMeta T(string name, string pk, string[] cols, ForeignKeyMeta[]? fks = null) =>
        new(name,
            columns: new[] { pk }.Concat(cols).Select(c => new ColumnMeta(c, "bigint", c != pk, columnKey: c == pk ? "PRI" : "")),
            indexes: [new IndexMeta("PRIMARY", true, [pk])], foreignKeys: fks);

    [Fact]
    public void MatchesPrimaryKeyNames()
    {
        var member = T("tb_member", "member_id", ["email"]);
        var order = T("tb_order", "order_id", ["member_id"]);
        var item = T("tb_order_item", "item_id", ["order_id", "id"]);
        var r = DiagramLayout.InferredRelations([member, order, item]);
        Assert.Equal(new HashSet<string> { "tb_order.member_id->tb_member", "tb_order_item.order_id->tb_order" },
                     r.Select(f => $"{f.Table}.{f.Columns[0]}->{f.ReferencedTable}").ToHashSet());
        Assert.Equal(["tb_member", "tb_order"], DiagramLayout.Related("tb_member", [member, order, item], r).Select(t => t.Name));
    }

    [Fact]
    public void SkipsRealForeignKeysAndAmbiguous()
    {
        var real = new ForeignKeyMeta("fk", "b", ["a_id"], "a", ["a_id"]);
        TableMeta a = T("a", "a_id", []), b = T("b", "b_id", ["a_id"], [real]);
        Assert.Empty(DiagramLayout.InferredRelations([a, b]));
        // 같은 기본 키 이름을 가진 테이블이 둘이고 이름으로도 못 고르면 건너뜀
        TableMeta x1 = T("hist_2024", "log_key", []), x2 = T("hist_2025", "log_key", []);
        var c = T("c", "c_id", ["log_key"]);
        Assert.Empty(DiagramLayout.InferredRelations([x1, x2, c]));
    }
}

public class SelectedWithRelatedTests
{
    [Fact]
    public void KeepsUnrelatedAndAddsNeighbors()
    {
        static TableMeta T(string n, string pk, params string[] cols) =>
            new(n, columns: new[] { pk }.Concat(cols).Select(c => new ColumnMeta(c, "int", true, columnKey: c == pk ? "PRI" : "")),
                indexes: [new IndexMeta("PRIMARY", true, [pk])]);
        TableMeta[] tables = [T("member", "member_id"), T("orders", "order_id", "member_id"), T("item", "item_id", "order_id"), T("notice", "notice_id")];
        var inferred = DiagramLayout.InferredRelations(tables);
        var list = DiagramLayout.WithRelated(new HashSet<string> { "item", "notice" }, tables, inferred).Select(t => t.Name);
        Assert.Equal(["item", "notice", "orders"], list);
    }
}
