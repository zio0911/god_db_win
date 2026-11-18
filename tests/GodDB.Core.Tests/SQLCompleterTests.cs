using GodDB.Core.Schema;
using GodDB.Core.Sql;

namespace GodDB.Core.Tests;

public class SQLCompleterTests
{
    private readonly List<TableMeta> tables =
    [
        new("tb_member", columns:
        [
            new ColumnMeta("member_id", "bigint", false, position: 1),
            new ColumnMeta("email", "varchar(100)", true, position: 2),
        ]),
        new("tb_order", columns:
        [
            new ColumnMeta("order_id", "bigint", false, position: 1),
            new ColumnMeta("member_id", "bigint", false, position: 2),
        ]),
    ];

    [Fact]
    public void ResolvesAliases()
    {
        var a = SqlCompleter.Aliases("SELECT * FROM tb_member m JOIN tb_order AS o ON o.member_id = m.member_id WHERE");
        Assert.Equal("tb_member", a["m"]);
        Assert.Equal("tb_order", a["o"]);
        Assert.Equal("tb_member", a["tb_member"]);
        Assert.False(SqlCompleter.Aliases("SELECT * FROM tb_member WHERE x").ContainsKey("where"));
        Assert.Equal("tb_order", SqlCompleter.Aliases("SELECT * FROM shop.tb_member a, tb_order b")["b"]);
    }

    [Fact]
    public void ColumnsAfterAliasDot()
    {
        var s = SqlCompleter.Suggestions("", "o", "SELECT o. FROM tb_order o", tables);
        Assert.Equal(["order_id", "member_id"], s);
        var e = SqlCompleter.Suggestions("em", "m", "SELECT m.em FROM tb_member m", tables);
        Assert.Equal(["email"], e);
    }

    [Fact]
    public void TablesAfterFrom()
    {
        var s = SqlCompleter.Suggestions("tb_", null, "SELECT * FROM tb_", tables);
        Assert.Equal(["tb_member", "tb_order"], s);
    }

    [Fact]
    public void ColumnsOfUsedTablesThenKeywords()
    {
        var s = SqlCompleter.Suggestions("e", null, "SELECT * FROM tb_member WHERE e", tables);
        Assert.Equal("email", s.First());
        Assert.Contains("exists", s);   // 소문자로 입력하면 소문자 키워드
        var k = SqlCompleter.Suggestions("SEL", null, "SEL", tables);
        Assert.Equal(["SELECT"], k);
    }

    // Mac 에는 없는 테스트: SQLEditorView.swift 에서 옮긴 Tab 동작
    [Fact]
    public void TabIndentsCompletesOrLists()
    {
        Assert.Equal(new TabAction.Indent(3, "    "), SqlCompleter.Tab("ab ", 3, 0, tables));
        Assert.Null(SqlCompleter.Tab("abc", 0, 2, tables));
        Assert.Equal(new TabAction.Replace(0, 3, "SELECT"), SqlCompleter.Tab("SEL", 3, 0, tables));
        // 공통 접두어 tb_ 는 이미 입력했으므로 목록
        var list = Assert.IsType<TabAction.ShowList>(SqlCompleter.Tab("SELECT * FROM tb_", 17, 0, tables));
        Assert.Equal(["tb_member", "tb_order"], list.Candidates.Take(2));
        // tb 까지만 입력했으면 tb_ 까지 채운다
        Assert.Equal(new TabAction.Replace(14, 2, "tb_"), SqlCompleter.Tab("SELECT * FROM tb", 16, 0, tables));
        Assert.IsType<TabAction.Beep>(SqlCompleter.Tab("SELECT zzzq", 11, 0, tables));
        Assert.Equal(new CompletionContext(9, 2, "m"), SqlCompleter.Context("SELECT m.em", 11));
        Assert.Equal("Tb_", SqlCompleter.CommonPrefix(["Tb_member", "tb_order"]));
    }
}

public class DollarQuoteTests
{
    [Fact]
    public void KeepsFunctionBodyTogether()
    {
        var sql = "CREATE FUNCTION f() RETURNS int AS $$ SELECT 1; SELECT 2; $$ LANGUAGE sql; SELECT $1, 3;";
        var parts = SqlText.Statements(sql).Select(s => s.Sql).ToList();
        Assert.Equal(2, parts.Count);
        Assert.EndsWith("LANGUAGE sql", parts[0]);
        var tagged = SqlText.Statements("DO $body$ BEGIN PERFORM 1; END $body$; SELECT 1");
        Assert.Equal(2, tagged.Count);
    }
}
