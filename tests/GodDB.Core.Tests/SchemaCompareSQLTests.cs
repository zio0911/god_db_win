using GodDB.Core.Dialects;
using GodDB.Core.Schema;
using GodDB.Core.Sql;

namespace GodDB.Core.Tests;

/// Mac SchemaCompareIntegrationTests.swift 의 "SchemaCompare SQL" (DB 없이 보는 보정 SQL 모양).
/// 실제 DB 로 보는 부분은 GodDB.Integration.Tests/SchemaCompareIntegrationTests.cs
public class SchemaCompareSQLTests
{
    [Fact]
    public void TableCommentDifference()
    {
        var s = new TableMeta("t", "회원", [new ColumnMeta("id", "int", false)]);
        var t = s with { Comment = "회원 테이블" };
        var item = SchemaDiff.Compare([s], [t]).Items.FirstOrDefault();
        Assert.True(item?.Kind == SchemaDiffKind.DifferentTable && item.Severity == SchemaDiffSeverity.Different);
        Assert.Equal("ALTER TABLE `t` COMMENT = '회원';", item!.FixSql);
        Assert.Equal("COMMENT ON TABLE \"t\" IS '회원';", SchemaDiff.Compare([s], [t], dialect: new PostgresDialect()).Items.FirstOrDefault()?.FixSql);
        Assert.StartsWith("EXEC sys.sp_updateextendedproperty", SchemaDiff.Compare([s], [t], dialect: new MssqlDialect()).Items.FirstOrDefault()?.FixSql);
        Assert.Empty(SchemaDiff.Compare([s], [s]).Items);
    }

    [Fact]
    public void PostgresSerialCreatesSequence()
    {
        var id = new ColumnMeta("id", "integer", false, defaultValue: "nextval('t_id_seq'::regclass)");
        Assert.Equal("t_id_seq", PostgresDialect.SequenceName(id));
        Assert.Equal("\"My'Seq\"", PostgresDialect.SequenceName(new ColumnMeta("x", "integer", true, defaultValue: "nextval('\"My''Seq\"'::regclass)")));
        Assert.Null(PostgresDialect.SequenceName(new ColumnMeta("x", "integer", true, defaultValue: "0")));
        var ddl = new PostgresDialect().CreateTableSql(new TableMeta("t", columns: [id]));
        Assert.StartsWith("CREATE SEQUENCE IF NOT EXISTS t_id_seq AS integer;\nCREATE TABLE \"t\"", ddl);
        Assert.Contains("ALTER SEQUENCE t_id_seq OWNED BY \"t\".\"id\";", ddl);
        var add = new PostgresDialect().Render([new SchemaChange.AddColumn(id, null)], "t");
        Assert.True(add.Split('\n').Length == 3 && add.StartsWith("CREATE SEQUENCE IF NOT EXISTS t_id_seq"));
    }

    /// 기존 기본값 제약조건은 이름을 찾아 지운다. 쿼리 탭이 세미콜론으로 나눠도 한 문장이어야 한다.
    [Fact]
    public void MssqlDefaultChangeDropsExistingConstraint()
    {
        var old = new ColumnMeta("status", "char(1)", false, defaultValue: "('B')");
        var @new = old with { DefaultValue = "('A')" };
        var sql = new MssqlDialect().Render([new SchemaChange.ModifyColumn(old, @new)], "member");
        var statements = SqlText.Statements(sql).Select(x => x.Sql).ToList();
        Assert.Equal(2, statements.Count);
        Assert.StartsWith("DECLARE @df_status nvarchar(300) = (SELECT QUOTENAME(dc.name)", statements[0]);
        Assert.Contains("OBJECT_ID(N'[member]') AND c.name = N'status'", statements[0]);
        Assert.EndsWith("EXEC (N'ALTER TABLE [member] DROP CONSTRAINT ' + @df_status)", statements[0]);
        Assert.Equal("ALTER TABLE [member] ADD DEFAULT ('A') FOR [status]", statements[1]);
        // 기본값이 없던 컬럼은 지우지 않고 더하기만
        var none = old with { DefaultValue = null };
        Assert.Equal("ALTER TABLE [member] ADD DEFAULT ('A') FOR [status];",
                     new MssqlDialect().Render([new SchemaChange.ModifyColumn(none, @new)], "member"));
    }

    /// 여러 개 골라 복사 (Mac CompareView 의 fixSQL(of:selected:)): 목록 순서, 테이블마다 "-- 테이블" 머리줄, 참고용은 건너뜀
    [Fact]
    public void GroupedFixSqlFollowsListOrderPerTable()
    {
        var items = new List<SchemaDiffItem>
        {
            new(SchemaDiffKind.MissingColumn, "a", "x", "", "ALTER a x;"),
            new(SchemaDiffKind.ExtraColumn, "a", "y", "", null),
            new(SchemaDiffKind.MissingIndex, "b", "i", "", "CREATE i;"),
            new(SchemaDiffKind.DifferentColumn, "a", "z", "", "ALTER a z;"),
        };
        var r = new SchemaDiffReport(items, 0);
        Assert.Equal("-- a\nALTER a x;\nALTER a z;\n\n-- b\nCREATE i;", r.FixScript);
        var selected = new HashSet<string> { items[3].Id, items[1].Id, items[0].Id };
        Assert.Equal("-- a\nALTER a x;\nALTER a z;", SchemaDiffReport.FixSql(items, selected));
        Assert.Equal("", SchemaDiffReport.FixSql(items, new HashSet<string> { items[1].Id }));
    }
}
