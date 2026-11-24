using System.Text.Json;
using GodDB.Core.Dialects;
using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.Core.Tests;

/// Mac IDSearchTests.swift ("IDSearch")
public class IDSearchTests
{
    private const string Log = """
        2026-11-20 10:01:02 ERROR [main] memberNo=1023 failed (code 500)
        2026-11-20 10:01:03.120 WARN memberNo=2044, orderId=A-77 retry
        2026-11-20T10:01:04Z INFO memberNo=1023 done v1.2 user42
        """;

    [Fact]
    public void ExtractsNumbersFromThreeLogLinesInOrderWithoutDuplicates()
    {
        var r = IdSearch.Extract(Log, numeric: true);
        // 날짜 · 시각 · 버전(1.2) · 단어에 붙은 숫자(user42, A-77)는 빼고
        Assert.Equal(["1023", "500", "2044"], r.Values);
        Assert.Equal(1, r.Duplicates);
        Assert.Equal(0, r.Overflow);
        Assert.Equal(3, r.UniqueCount);
    }

    [Fact]
    public void NumericKeepsNegativeAndDedupesLeadingZeros()
    {
        var r = IdSearch.Extract("id=-5 id=007 id=7 id=8", numeric: true);
        Assert.Equal(["-5", "007", "8"], r.Values);
        Assert.Equal(1, r.Duplicates);
    }

    [Fact]
    public void TextModeExcludesDatesTimesAndLevels()
    {
        var r = IdSearch.Extract(Log, numeric: false);
        Assert.DoesNotContain(r.Values, v => v.StartsWith("2026-11-20"));
        Assert.DoesNotContain("10:01:02", r.Values);
        Assert.DoesNotContain("10:01:03.120", r.Values);
        Assert.True(!r.Values.Contains("ERROR") && !r.Values.Contains("WARN") && !r.Values.Contains("INFO"));
        Assert.Contains("A-77", r.Values);
        Assert.True(r.Values.Contains("1023") && r.Values.Contains("2044"));
        Assert.Single(r.Values, v => v == "memberNo");
        // 문장 끝 마침표는 떼기
        Assert.Equal(["see", "ORD-1", "and"], IdSearch.Extract("see ORD-1. and debug", numeric: false).Values);
    }

    [Fact]
    public void TextModeKeepsUUIDs()
    {
        var uuid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
        Assert.Equal(["req", uuid], IdSearch.Extract($"req={uuid} INFO", numeric: false).Values);
    }

    [Fact]
    public void CapsAtOneThousand()
    {
        var text = string.Join("\n", Enumerable.Range(1, 1500).Select(i => $"id={i}")) + "\nid=1";
        var r = IdSearch.Extract(text, numeric: true);
        Assert.Equal(1000, r.Values.Count);
        Assert.True(r.Values[0] == "1" && r.Values[^1] == "1000");
        Assert.Equal(500, r.Overflow);
        Assert.Equal(1, r.Duplicates);
        Assert.Equal(1500, r.UniqueCount);
    }

    [Fact]
    public void PreviewShowsFirstTwenty()
    {
        var values = Enumerable.Range(1, 25).Select(i => i.ToString()).ToList();
        var p = IdSearch.Preview(values);
        Assert.StartsWith("1, 2, 3", p);
        Assert.True(p.Contains("20") && !p.Contains("21,"));
        Assert.EndsWith(Tr("…외 %@개", "5"), p);
        Assert.Equal("a, b", IdSearch.Preview(["a", "b"]));
    }

    [Fact]
    public void NumericTypes()
    {
        foreach (var t in new[] { "int", "bigint(20) unsigned", "INT UNSIGNED", "numeric(10,2)", "decimal(12,2)", "integer", "int4", "bigserial", "double precision", "smallint" })
            Assert.True(IdSearch.IsNumericType(t), t);
        foreach (var t in new[] { "varchar(50)", "uuid", "char(36)", "nvarchar(100)", "text", "datetime", "bit", "uniqueidentifier" })
            Assert.False(IdSearch.IsNumericType(t), t);
    }

    [Fact]
    public void SqlPerDialect()
    {
        Assert.Equal("SELECT * FROM `ui_orders` WHERE `id` IN (1, 2)",
            IdSearch.Sql("ui_orders", "id", ["1", "2"], true, new MySqlDialect()));
        Assert.Equal("SELECT * FROM `orders` WHERE `code` IN ('A-77', 'O''B')",
            IdSearch.Sql("orders", "code", ["A-77", "O'B"], false, new MySqlDialect()));
        Assert.Equal("""SELECT * FROM "ui_orders" WHERE "id" IN (1, -2)""",
            IdSearch.Sql("ui_orders", "id", ["1", "-2"], true, new PostgresDialect()));
        Assert.Equal("""SELECT * FROM "orders" WHERE "code" IN ('A-77')""",
            IdSearch.Sql("orders", "code", ["A-77"], false, new PostgresDialect()));
        Assert.Equal("SELECT * FROM [sales].[orders] WHERE [id] IN (10)",
            IdSearch.Sql("sales.orders", "id", ["10"], true, new MssqlDialect()));
        Assert.Equal("SELECT * FROM [orders] WHERE [code] IN (N'A-77', N'가')",
            IdSearch.Sql("orders", "code", ["A-77", "가"], false, new MssqlDialect()));
        // 숫자 컬럼이어도 숫자가 아닌 값은 인용
        Assert.Equal("""SELECT * FROM "t" WHERE "id" IN (1, 'x')""",
            IdSearch.Sql("t", "id", ["1", "x"], true, new PostgresDialect()));
    }

    [Fact]
    public void TitleAndRowLimit()
    {
        Assert.Equal("ID: ui_orders.id (3)", IdSearch.TabTitle("ui_orders", "id", 3));
        Assert.Equal(500, IdSearch.RowLimit(500, 3));
        Assert.Equal(1000, IdSearch.RowLimit(500, 1000));
        Assert.Equal(5000, IdSearch.RowLimit(100, 9000));
        Assert.Equal(5000, IdSearch.RowLimit(5000, 1000));
        Assert.Equal(0, IdSearch.RowLimit(0, 1000));
    }

    [Fact]
    public void MissingComparison()
    {
        var c = IdSearch.Compare(["1023", "007", "2044", "99", "ABC-1", "1.50"], ["1023", "7", null, "2044", "abc-1", "1.5000"]);
        Assert.Equal(6, c.Requested);
        Assert.Equal(5, c.Found);
        Assert.Equal(["99"], c.Missing);
        Assert.Equal("0", IdSearch.Normalize("000"));
        Assert.Equal("-7", IdSearch.Normalize("-007"));
        Assert.Equal("10", IdSearch.Normalize("10.0"));
    }

    [Fact]
    public void MemoryRemembersPerConnectionAndDatabase()
    {
        Guid p = Guid.NewGuid(), other = Guid.NewGuid();
        var m = new IdSearchMemory();
        m.Remember(new(p, "shop", "orders", "id", DateTimeOffset.FromUnixTimeSeconds(100)));
        m.Remember(new(p, "shop", "members", "member_no", DateTimeOffset.FromUnixTimeSeconds(200)));
        m.Remember(new(other, "shop", "x", "y", DateTimeOffset.FromUnixTimeSeconds(300)));
        m.Remember(new(p, "shop", "orders", "code", DateTimeOffset.FromUnixTimeSeconds(150)));
        Assert.Equal("members", m.LastTable(p, "shop"));
        Assert.Null(m.LastTable(p, "other"));
        Assert.Equal("code", m.LastColumn(p, "shop", "orders"));
        Assert.Equal(3, m.Last.Count);

        var file = Path.Combine(Path.GetTempPath(), $"id-search-{Guid.NewGuid()}.json");
        try
        {
            var store = new IdSearchStore(file);
            store.Save(m);
            Assert.Equal(m, store.Load());
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
            var first = json.RootElement.GetProperty("last")[0];
            Assert.Equal(new HashSet<string> { "profileID", "database", "table", "column", "updatedAt" },
                first.EnumerateObject().Select(o => o.Name).ToHashSet());
            Assert.Equal(new IdSearchMemory(), new IdSearchStore(file + ".none").Load());
        }
        finally { File.Delete(file); }
    }
}
