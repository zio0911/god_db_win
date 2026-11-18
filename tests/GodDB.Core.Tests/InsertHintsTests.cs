using GodDB.Core.Schema;
using GodDB.Core.Sql;

namespace GodDB.Core.Tests;

public class InsertHintsTests
{
    private readonly List<TableMeta> tables =
    [
        new("tb_member", columns:
        [
            new ColumnMeta("member_id", "bigint", false, position: 1),
            new ColumnMeta("name", "varchar(50)", true, position: 2),
            new ColumnMeta("email", "varchar(100)", true, position: 3),
        ]),
    ];

    // 힌트 위치부터 값 앞부분 몇 글자
    private List<string> Labels(string sql) =>
        InsertHints.Hints(sql, tables).Select(h => h.Column + "=" + sql.Substring(h.Location, Math.Min(3, sql.Length - h.Location))).ToList();

    [Fact]
    public void ExplicitColumnList()
    {
        Assert.Equal(["wow='a'", "id='b'"], Labels("insert into a (wow, id) values ('a', 'b')"));
    }

    [Fact]
    public void UsesTableColumnsWhenNoList()
    {
        Assert.Equal(["member_id=1, ", "name='ki", "email=NUL"], Labels("INSERT INTO tb_member VALUES (1, 'kim', NULL)"));
    }

    [Fact]
    public void MultipleRowsQuotesAndFunctions()
    {
        var sql = "INSERT INTO `tb_member` (`name`, email) VALUES ('a,b', CONCAT('x', 'y')), ('it''s', NOW());";
        Assert.Equal(["name='a,", "email=CON", "name='it", "email=NOW"], Labels(sql));
    }

    [Fact]
    public void SchemaQualifiedAndUnknownTable()
    {
        Assert.Equal(3, Labels("insert into shop.tb_member values (7, 'a', 'b')").Count);
        Assert.Empty(Labels("insert into unknown values (1, 2)"));
    }

    [Fact]
    public void IgnoresCommentsAndStringsAndIncompleteInput()
    {
        Assert.Empty(Labels("-- insert into tb_member values (1)\nselect 'insert into tb_member values (1)'"));
        // 입력 중 (괄호 안 닫힘)
        Assert.Equal(["member_id=1, ", "name='k"], Labels("insert into tb_member values (1, 'k"));
        // 컬럼보다 값이 많으면 넘치는 값은 표시 안 함
        Assert.Equal(["name='a'"], Labels("insert into tb_member (name) values ('a', 'b')"));
    }

    [Fact]
    public void LocationsAreUTF16()
    {
        var sql = "insert into tb_member (name) values ('한글'), ('😀')";
        var hints = InsertHints.Hints(sql, tables);
        Assert.Equal(["'", "'"], hints.Select(h => sql.Substring(h.Location, 1)));
    }
}
