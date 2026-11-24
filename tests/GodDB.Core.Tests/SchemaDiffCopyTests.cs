using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

/// Mac ConnectTimeoutTests.swift 의 SchemaDiffCopyTests — 고른 항목 보정 SQL 복사
public class SchemaDiffCopyTests
{
    private readonly SchemaDiffItem[] items =
    [
        new(SchemaDiffKind.MissingTable, "a", null, "", "CREATE TABLE a (id int);"),
        new(SchemaDiffKind.ExtraTable, "b", null, "", null),
        new(SchemaDiffKind.MissingColumn, "c", "x", "", "ALTER TABLE c ADD COLUMN x int;"),
        new(SchemaDiffKind.DifferentIndex, "d", "ix", "", "ALTER TABLE d DROP INDEX ix, ADD INDEX ix (y);"),
    ];

    [Fact]
    public void JoinsSelectedInListOrderWithBlankLines()
    {
        // 고른 순서가 아니라 목록 순서, 테이블마다 "-- 테이블" 머리줄 + 테이블 사이 빈 줄
        var selected = new HashSet<string> { items[3].Id, items[0].Id };
        Assert.Equal("-- a\nCREATE TABLE a (id int);\n\n-- d\nALTER TABLE d DROP INDEX ix, ADD INDEX ix (y);",
                     SchemaDiffReport.FixSql(items, selected));
    }

    [Fact]
    public void SameTableStaysTogetherOnConsecutiveLines()
    {
        var more = items.Append(new SchemaDiffItem(SchemaDiffKind.MissingColumn, "c", "z", "", "ALTER TABLE c ADD COLUMN z int;")).ToArray();
        Assert.Equal("-- c\nALTER TABLE c ADD COLUMN x int;\nALTER TABLE c ADD COLUMN z int;",
                     SchemaDiffReport.FixSql(more, new HashSet<string> { more[2].Id, more[4].Id }));
    }

    [Fact]
    public void SingleSelectionMatchesItsSQL() =>
        Assert.Equal("-- c\n" + items[2].FixSql, SchemaDiffReport.FixSql(items, new HashSet<string> { items[2].Id }));

    [Fact]
    public void SkipsReferenceOnlyItems()
    {
        Assert.Equal("", SchemaDiffReport.FixSql(items, new HashSet<string> { items[1].Id }));
        Assert.Equal("-- a\nCREATE TABLE a (id int);\n\n-- c\nALTER TABLE c ADD COLUMN x int;\n\n-- d\nALTER TABLE d DROP INDEX ix, ADD INDEX ix (y);",
                     SchemaDiffReport.FixSql(items, items.Select(i => i.Id).ToHashSet()));
        Assert.Equal("", SchemaDiffReport.FixSql(items, new HashSet<string>()));
    }
}
