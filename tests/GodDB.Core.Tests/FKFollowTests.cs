using GodDB.Core.Dialects;
using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

/// Mac: FKFollowTests.swift
public class FKFollowTests
{
    private static ColumnMeta Col(string name, string type, bool pk = false) =>
        new(name, type, nullable: !pk, columnKey: pk ? "PRI" : "");

    private static TableMeta Table(string name, ColumnMeta[] cols, string[] pk, ForeignKeyMeta[]? fks = null) =>
        new(name, columns: cols, indexes: [new IndexMeta("PRIMARY", true, pk)], foreignKeys: fks ?? []);

    /// ui_customers(id) ← ui_orders.customer_id (진짜 FK), tb_member(member_no) ← tb_point.member_no (추정),
    /// 복합 키 tb_line(order_no, line_no) ← tb_ship(order_no, line_no)
    private static readonly TableMeta Customers = Table("ui_customers", [Col("id", "int", pk: true), Col("name", "varchar(50)")], ["id"]);
    private static readonly TableMeta Orders = Table("ui_orders", [Col("id", "int", pk: true), Col("customer_id", "int"), Col("code", "varchar(20)")],
        ["id"], [new ForeignKeyMeta("fk_ui_orders_customer", "ui_orders", ["customer_id"], "ui_customers", ["id"])]);
    private static readonly TableMeta Member = Table("tb_member", [Col("member_no", "bigint", pk: true), Col("name", "varchar(50)")], ["member_no"]);
    private static readonly TableMeta Point = Table("tb_point", [Col("id", "int", pk: true), Col("MEMBER_NO", "bigint"), Col("note", "varchar(10)")], ["id"]);
    private static readonly TableMeta Line = Table("tb_line", [Col("order_no", "varchar(20)", pk: true), Col("line_no", "int", pk: true)],
        ["order_no", "line_no"]);
    private static readonly TableMeta Ship = Table("tb_ship", [Col("id", "int", pk: true), Col("order_no", "varchar(20)"), Col("line_no", "int")], ["id"],
        [new ForeignKeyMeta("fk_ship_line", "tb_ship", ["order_no", "line_no"], "tb_line", ["order_no", "line_no"])]);
    private static readonly TableMeta[] All = [Customers, Orders, Member, Point, Line, Ship];
    private static readonly List<ForeignKeyMeta> Inferred = FKFollow.InferredRelations(All);

    // MARK: - 원본 테이블

    [Fact]
    public void SourceTableOnlyForSingleTableSelect()
    {
        Assert.Equal("ui_orders", FKFollow.SourceTable("SELECT * FROM ui_orders WHERE id > 1", All)?.Name);
        Assert.Equal("ui_orders", FKFollow.SourceTable("select id, customer_id from `UI_ORDERS`", All)?.Name);
        Assert.Null(FKFollow.SourceTable("SELECT * FROM ui_orders o JOIN ui_customers c ON c.id = o.customer_id", All));
        Assert.Null(FKFollow.SourceTable("SELECT customer_id, COUNT(*) FROM ui_orders GROUP BY customer_id", All));
        Assert.Null(FKFollow.SourceTable("SELECT * FROM nope", All));
    }

    // MARK: - 관계

    [Fact]
    public void RealForeignKeyLookup()
    {
        var output = FKFollow.Outgoing(Orders, Inferred);
        Assert.Equal([new FKRelation("ui_orders", ["customer_id"], "ui_customers", ["id"], inferred: false)], output);
        Assert.Single(FKFollow.RelationsForColumn("CUSTOMER_ID", output));
        // 관계가 없는 컬럼
        Assert.Empty(FKFollow.RelationsForColumn("code", output));
        Assert.Empty(FKFollow.RelationsForColumn("id", output));
        Assert.Empty(FKFollow.Outgoing(Customers, Inferred));
    }

    [Fact]
    public void CompositeForeignKey()
    {
        var output = FKFollow.Outgoing(Ship, Inferred);
        Assert.Single(output);
        Assert.Equal(output, FKFollow.RelationsForColumn("order_no", output));
        Assert.Equal(output, FKFollow.RelationsForColumn("line_no", output));
        // 복합 기본 키 테이블은 "참조하는 행" 이 없다
        Assert.Empty(FKFollow.Incoming(Line, All, Inferred));
    }

    [Fact]
    public void InferredRelationByUniquePrimaryKeyName()
    {
        var output = FKFollow.Outgoing(Point, Inferred);
        Assert.Equal([new FKRelation("tb_point", ["MEMBER_NO"], "tb_member", ["member_no"], inferred: true)], output);
        var inc = FKFollow.Incoming(Member, All, Inferred);
        Assert.Equal(["tb_point"], inc.Select(r => r.Table));
        Assert.True(inc.First().Inferred);
    }

    [Fact]
    public void InferredSkippedWhenAmbiguous()
    {
        // 기본 키 이름이 user_key 인 테이블이 하나면 추정, 둘이고 이름으로도 고를 수 없으면 추정하지 않는다
        var a = Table("tb_alpha", [Col("user_key", "varchar(20)", pk: true)], ["user_key"]);
        var b = Table("tb_beta", [Col("user_key", "varchar(20)", pk: true)], ["user_key"]);
        var child = Table("tb_log", [Col("id", "int", pk: true), Col("user_key", "varchar(20)")], ["id"]);
        TableMeta[] one = [a, child];
        Assert.Equal(["tb_alpha"], FKFollow.Outgoing(child, FKFollow.InferredRelations(one)).Select(r => r.ReferencedTable));
        TableMeta[] two = [a, b, child];
        var inferred = FKFollow.InferredRelations(two);
        Assert.Empty(FKFollow.Outgoing(child, inferred));
        Assert.Empty(FKFollow.Incoming(a, two, inferred));
    }

    [Fact]
    public void RealForeignKeyIsNotAlsoInferred()
    {
        // 진짜 FK 컬럼은 추정에서 빠지고, 진짜 FK 는 표시가 "추정" 이 아니다
        var inc = FKFollow.Incoming(Customers, All, Inferred);
        Assert.Equal([new FKRelation("ui_orders", ["customer_id"], "ui_customers", ["id"], inferred: false)], inc);
    }

    // MARK: - SQL

    [Fact]
    public void FollowSqlPerDialect()
    {
        var r = FKFollow.Outgoing(Orders, Inferred)[0];
        Assert.Equal("SELECT * FROM `ui_customers` WHERE `id` = 10", FKFollow.FollowSql(r, ["10"], All, new MySqlDialect()));
        Assert.Equal("""SELECT * FROM "ui_customers" WHERE "id" = 10""", FKFollow.FollowSql(r, ["10"], All, new PostgresDialect()));
        Assert.Equal("SELECT * FROM [ui_customers] WHERE [id] = 10", FKFollow.FollowSql(r, ["10"], All, new MssqlDialect()));
        // 숫자 컬럼이어도 숫자가 아닌 값은 인용
        Assert.Equal("SELECT * FROM `ui_customers` WHERE `id` = 'x''1'", FKFollow.FollowSql(r, ["x'1"], All, new MySqlDialect()));
    }

    [Fact]
    public void FollowSqlQuotesStringColumnsAndCompositeUsesAnd()
    {
        var r = FKFollow.Outgoing(Ship, Inferred)[0];
        Assert.Equal("SELECT * FROM `tb_line` WHERE `order_no` = '0042' AND `line_no` = 3",
            FKFollow.FollowSql(r, ["0042", "3"], All, new MySqlDialect()));
        Assert.Equal("""SELECT * FROM "tb_line" WHERE "order_no" = 'A-1' AND "line_no" = 3""",
            FKFollow.FollowSql(r, ["A-1", "3"], All, new PostgresDialect()));
        Assert.Equal("SELECT * FROM [tb_line] WHERE [order_no] = N'가-1' AND [line_no] = 3",
            FKFollow.FollowSql(r, ["가-1", "3"], All, new MssqlDialect()));
        // MSSQL 스키마 이름이 붙은 테이블
        var s = new FKRelation("sales.orders", ["code"], "sales.codes", ["code"], inferred: false);
        Assert.Equal("SELECT * FROM [sales].[codes] WHERE [code] = N'A'", FKFollow.FollowSql(s, ["A"], [], new MssqlDialect()));
    }

    [Fact]
    public void NullValueDisablesFollow()
    {
        var r = FKFollow.Outgoing(Orders, Inferred)[0];
        Assert.Null(FKFollow.FollowSql(r, [null], All, new MySqlDialect()));
        var c = FKFollow.Outgoing(Ship, Inferred)[0];
        Assert.Null(FKFollow.FollowSql(c, ["A-1", null], All, new MySqlDialect()));
        // 결과에 FK 컬럼 일부가 없으면 비활성
        var values = FKFollow.Values(c, ["id", "order_no"], ["1", "A-1"]);
        Assert.Equal(["A-1", null], values);
        Assert.Null(FKFollow.FollowSql(c, values, All, new MySqlDialect()));
        // 참조하는 행도 기본 키 값이 NULL 이면 비활성
        var inc = FKFollow.Incoming(Customers, All, Inferred)[0];
        Assert.Null(FKFollow.ReferencingSql(inc, null, All, new MySqlDialect()));
    }

    [Fact]
    public void ValuesFromResultRowIgnoreCase()
    {
        var r = FKFollow.Outgoing(Point, Inferred)[0];
        Assert.Equal(["1002"], FKFollow.Values(r, ["id", "member_no", "note"], ["1", "1002", "x"]));
        Assert.Equal("7", FKFollow.Value("ID", ["id", "member_no"], ["7", null]));
        Assert.Null(FKFollow.Value("member_no", ["id", "member_no"], ["7", null]));
    }

    [Fact]
    public void ReferencingSqlPerDialect()
    {
        var inc = FKFollow.Incoming(Customers, All, Inferred)[0];
        Assert.Equal("SELECT * FROM `ui_orders` WHERE `customer_id` = 20", FKFollow.ReferencingSql(inc, "20", All, new MySqlDialect()));
        Assert.Equal("""SELECT * FROM "ui_orders" WHERE "customer_id" = 20""", FKFollow.ReferencingSql(inc, "20", All, new PostgresDialect()));
        Assert.Equal("SELECT * FROM [ui_orders] WHERE [customer_id] = 20", FKFollow.ReferencingSql(inc, "20", All, new MssqlDialect()));
        // 문자 키
        var codes = Table("tb_code", [Col("code_cd", "varchar(10)", pk: true)], ["code_cd"]);
        var usage = Table("tb_usage", [Col("id", "int", pk: true), Col("code_cd", "varchar(10)")], ["id"]);
        TableMeta[] tables = [codes, usage];
        var rel = FKFollow.Incoming(codes, tables, FKFollow.InferredRelations(tables));
        Assert.True(rel.Count == 1 && rel[0].Inferred);
        Assert.Equal("SELECT * FROM [tb_usage] WHERE [code_cd] = N'A''1'", FKFollow.ReferencingSql(rel[0], "A'1", tables, new MssqlDialect()));
        Assert.Equal("""SELECT * FROM "tb_usage" WHERE "code_cd" = '007'""", FKFollow.ReferencingSql(rel[0], "007", tables, new PostgresDialect()));
    }

    [Fact]
    public void Titles()
    {
        var r = FKFollow.Outgoing(Orders, Inferred)[0];
        Assert.Equal("↗ ui_customers", FKFollow.FollowTitle(r));
        Assert.Equal("↙ ui_orders", FKFollow.ReferencingTitle(r));
    }

    [Fact]
    public void SinglePrimaryKey()
    {
        Assert.Equal("id", FKFollow.SinglePrimaryKey(Customers));
        Assert.Null(FKFollow.SinglePrimaryKey(Line));
        Assert.Null(FKFollow.SinglePrimaryKey(new TableMeta("t", columns: [Col("a", "int")])));
    }
}
