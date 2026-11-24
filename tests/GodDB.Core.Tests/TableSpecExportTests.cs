using System.IO.Compression;
using System.Text;
using GodDB.Core.Modeling;
using GodDB.Core.Results;

namespace GodDB.Core.Tests;

/// Mac TableSpecExportTests.swift
public class TableSpecExportTests
{
    [Fact]
    public void DescriptionCombinesLogicalNameAndComment()
    {
        Assert.Equal("회원번호", TableSpecExport.Describe("회원번호", ""));
        Assert.Equal("주문한 회원", TableSpecExport.Describe("", "주문한 회원"));
        Assert.Equal("회원번호", TableSpecExport.Describe("회원번호", "회원번호"));
        Assert.Equal("회원번호 — 주문한 회원", TableSpecExport.Describe("회원번호", "주문한 회원"));
    }

    /// 회원 1 — N 주문 (비식별), 주문 1 — N 주문상품 (식별), 상품 1 — N 주문상품
    internal static DataModel Model(DatabaseKind kind = DatabaseKind.Mysql)
    {
        var m = new DataModel(name: "샵", kind: kind);
        var member = m.AddTable("회원");
        var order = m.AddTable("주문");
        var item = m.AddTable("주문상품");
        var product = m.AddTable("상품");
        void Set(Guid id, string physical, params ModelColumn[] cols)
        {
            var t = m.Table(id)!;
            t.PhysicalName = physical;
            t.Columns[0].PhysicalName = "id";
            t.Columns.AddRange(cols);
        }
        Set(member, "tb_member", new ModelColumn(logicalName: "이메일", physicalName: "email", notNull: true, unique: true, comment: "로그인 ID"));
        Set(order, "tb_order", new ModelColumn(logicalName: "금액", physicalName: "total", dataType: "decimal(12,2)", defaultValue: "0"));
        Set(item, "tb_order_item", new ModelColumn(logicalName: "수량", physicalName: "qty", dataType: "int", notNull: true));
        Set(product, "tb_product", new ModelColumn(logicalName: "상태", physicalName: "status", dataType: "enum('a','b')"));
        m.Table(member)!.Comment = "회원 기본 정보";
        m.AddRelation(member, order);
        m.AddRelation(order, item, identifying: true);
        m.AddRelation(product, item);
        var oi = m.Table(item)!;
        oi.Indexes = [new ModelIndex(name: "idx_qty", columns: [oi.Columns.First(c => c.PhysicalName == "qty").Id])];
        return m;
    }

    internal static readonly TableSpecInfo Info = new("샵 시스템", "홍길동", "2026-11-20");

    [Fact]
    public void SplitsTypeAndLength()
    {
        Assert.Equal(("varchar", "50"), TableSpecExport.SplitType("varchar(50)"));
        Assert.Equal(("decimal", "10,2"), TableSpecExport.SplitType("decimal(10,2)"));
        Assert.Equal(("enum", "'a','b'"), TableSpecExport.SplitType("enum('a','b')"));
        Assert.Equal(("bigint", ""), TableSpecExport.SplitType("bigint"));
        Assert.Equal(("datetime", "3"), TableSpecExport.SplitType("datetime(3)"));
        Assert.Equal(("int unsigned", "10"), TableSpecExport.SplitType("int(10) unsigned"));
    }

    [Fact]
    public void KeysAndReferencesFromRelations()
    {
        var m = Model();
        var item = m.Tables.First(t => t.PhysicalName == "tb_order_item");
        var rows = TableSpecExport.ColumnRows(m, item);
        var byName = rows.ToDictionary(r => r.Physical);
        Assert.Equal("PK", byName["id"].Key);
        Assert.Equal("AUTO_INCREMENT", byName["id"].DefaultValue);
        Assert.Equal("PK, FK", byName["order_id"].Key);              // 식별 관계
        Assert.Equal("tb_order.id", byName["order_id"].References);
        Assert.Equal("FK", byName["product_id"].Key);
        Assert.Equal("tb_product.id", byName["product_id"].References);
        Assert.Equal("Y", byName["product_id"].NotNull);
        Assert.Equal("", byName["qty"].Key);
        Assert.Equal("", byName["qty"].References);
        Assert.Equal(Enumerable.Range(1, rows.Count), rows.Select(r => r.No));

        var member = m.Tables.First(t => t.PhysicalName == "tb_member");
        var email = TableSpecExport.ColumnRows(m, member).First(r => r.Physical == "email");
        Assert.Equal("UQ", email.Note);
        Assert.Equal("로그인 ID", email.Comment);
        Assert.True(email.Type == "varchar" && email.Length == "100");
        var order = m.Tables.First(t => t.PhysicalName == "tb_order");
        var total = TableSpecExport.ColumnRows(m, order).First(r => r.Physical == "total");
        Assert.True(total.Type == "decimal" && total.Length == "12,2" && total.DefaultValue == "0" && total.NotNull == "");
    }

    [Fact]
    public void IndexRowsStartWithPrimaryKey()
    {
        var m = Model();
        var item = m.Tables.First(t => t.PhysicalName == "tb_order_item");
        var idx = TableSpecExport.IndexRows(m, item);
        Assert.Equal("PRIMARY", idx.First().Name);
        Assert.Equal("PK", idx.First().Kind);
        Assert.Equal("id, order_id", idx.First().Columns);
        Assert.Contains(idx, x => x.Name == "idx_qty" && x.Kind == "일반" && x.Columns == "qty");
        var member = m.Tables.First(t => t.PhysicalName == "tb_member");
        Assert.Contains(TableSpecExport.IndexRows(m, member), x => x.Kind == "유니크" && x.Columns == "email");

        var pg = Model(DatabaseKind.Postgres);
        var pgItem = pg.Tables.First(t => t.PhysicalName == "tb_order_item");
        Assert.Equal("pk_tb_order_item", TableSpecExport.IndexRows(pg, pgItem).First().Name);
    }

    [Fact]
    public void RelationRows()
    {
        var m = Model();
        var item = m.Tables.First(t => t.PhysicalName == "tb_order_item");
        var rels = TableSpecExport.RelationRows(m, item);
        Assert.Equal(2, rels.Count);
        Assert.Equal("fk_tb_order_item_tb_order", rels[0].Name);
        Assert.Equal("order_id → tb_order.id", rels[0].Mapping);
        Assert.Equal("1:N · 식별", rels[0].Relation);
        Assert.Equal("1:N · 비식별", rels[1].Relation);
        Assert.Equal("NO ACTION", rels[0].OnDelete);
        var member = m.Tables.First(t => t.PhysicalName == "tb_member");
        Assert.Empty(TableSpecExport.RelationRows(m, member));
    }

    [Fact]
    public void SheetNamesAreSanitizedAndUnique()
    {
        var @long = new string('a', 40);
        var names = TableSpecExport.SheetNames(["a/b:c", "tb_x", "TB_X", "목차", "", @long, @long, "'q'", "x[1]*?"]);
        Assert.Equal("a_b_c", names[0]);
        Assert.Equal("tb_x", names[1]);
        Assert.Equal("TB_X~2", names[2]);
        Assert.Equal("목차~2", names[3]);
        Assert.Equal("Table", names[4]);
        Assert.Equal(new string('a', 31), names[5]);
        Assert.Equal(new string('a', 29) + "~2", names[6]);
        Assert.Equal("q", names[7]);
        Assert.Equal("x_1___", names[8]);
        Assert.All(names, n => Assert.True(n.Length <= 31));
        Assert.Equal(names.Count, names.Select(n => n.ToLowerInvariant()).Distinct().Count());
    }

    /// Mac 은 /usr/bin/unzip 으로 확인한다. Windows 에는 unzip 이 없어 .NET ZipArchive 로 연다 (같은 확인: 깨지지 않은 ZIP, 파일 내용)
    [Fact]
    public void WorkbookIsValidZip()
    {
        var m = Model();
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = dir.File("spec.xlsx");
        File.WriteAllBytes(path, TableSpecExport.Data(m, Info));

        using var zip = ZipFile.OpenRead(path);
        string Read(string name)
        {
            var entry = zip.GetEntry(name) ?? throw new Xunit.Sdk.XunitException($"{name} 없음");
            using var s = entry.Open();
            using var mem = new MemoryStream();
            s.CopyTo(mem);   // CRC 가 틀리면 여기서 예외
            return Encoding.UTF8.GetString(mem.ToArray());
        }
        foreach (var e in zip.Entries) Read(e.FullName);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        var wb = Read("xl/workbook.xml");
        foreach (var name in new[] { "목차", "tb_member", "tb_order", "tb_order_item", "tb_product" }) Assert.Contains($"name=\"{name}\"", wb);
        Assert.Equal(5, wb.Split("<sheet ").Length - 1);
        var types = Read("[Content_Types].xml");
        Assert.Contains("/xl/worksheets/sheet5.xml", types);

        var toc = Read("xl/worksheets/sheet1.xml");
        Assert.Contains("테이블 명세서 — 샵 시스템", toc);
        Assert.Contains("테이블 목록 (4개)", toc);
        Assert.Contains("location=\"'tb_member'!A1\"", toc);
        Assert.Contains("location=\"'tb_order_item'!A1\"", toc);
        Assert.True(toc.Contains("홍길동") && toc.Contains("2026-11-20") && toc.Contains("MySQL"));
        Assert.Contains("state=\"frozen\"", toc);
        Assert.True(toc.Contains("orientation=\"landscape\"") && toc.Contains("fitToWidth=\"1\""));

        var sheet = Read("xl/worksheets/sheet2.xml");
        Assert.Contains("tb_member · 회원", sheet);
        Assert.Contains("location=\"'목차'!A1\"", sheet);
        Assert.Contains("<mergeCells", sheet);
        Assert.Contains(">없음<", sheet);                      // 관계 없음
        var styles = Read("xl/styles.xml");
        Assert.Contains("맑은 고딕", styles);
        Assert.Contains("FFE6F1FB", styles);
        Assert.False(styles.Contains("medium") || styles.Contains("thick"));
    }

    [Fact]
    public void CrcMatchesKnownValue() => Assert.Equal(0xCBF4_3926u, ZipWriter.Crc32("123456789"u8));
}
