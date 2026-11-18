using GodDB.Core.Dialects;
using GodDB.Core.Results;
using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

/// Mac: CSVImportTests.swift
public class CSVImportTests
{
    [Fact]
    public void DetectsDelimiter()
    {
        Assert.Equal(',', CsvImport.DetectDelimiter("a,b,c\n1,2,3"));
        Assert.Equal('\t', CsvImport.DetectDelimiter("a\tb\n1\t2"));
        Assert.Equal(';', CsvImport.DetectDelimiter("a;b;c\n1;2;3"));
        Assert.Equal(',', CsvImport.DetectDelimiter("single"));
    }

    [Fact]
    public void ParsesQuotesNewlinesAndBOM()
    {
        var text = "﻿id,name,note\r\n1,\"Kim, A\",\"line1\nline2\"\r\n2,\"say \"\"hi\"\"\",\n\n";
        var rows = CsvImport.Parse(text, ',');
        Assert.Equal(3, rows.Count);
        Assert.Equal(["id", "name", "note"], rows[0]);
        Assert.Equal(["1", "Kim, A", "line1\nline2"], rows[1]);
        Assert.Equal(["2", "say \"hi\"", ""], rows[2]);
    }

    [Fact]
    public void AutoMapsByNameOrPosition()
    {
        List<string> cols = ["id", "user_name", "created_at"];
        Assert.Equal(["id", "user_name", null], CsvImport.AutoMap(["ID", "User Name", "unknown"], 3, cols));
        Assert.Equal(["id", "user_name", "created_at", null], CsvImport.AutoMap(null, 4, cols));
    }

    [Fact]
    public void BuildsBatchedInserts()
    {
        var rows = Enumerable.Range(1, 5).Select(i => (IReadOnlyList<string>)[$"{i}", i == 2 ? "" : $"n'{i}", "skip"]).ToList();
        var sql = CsvImport.Inserts("t", ["id", "name", null], rows, new MySqlDialect(), batch: 2);
        Assert.Equal(3, sql.Count);
        Assert.StartsWith("INSERT INTO `t` (`id`, `name`) VALUES", sql[0]);
        Assert.Contains("('2', NULL)", sql[0]);
        Assert.True(sql[0].Contains("'n''1'") || sql[0].Contains("'n\\'1'"));
        Assert.DoesNotContain("skip", sql[0]);
    }

    // ── Mac CSVImportView.swift 의 화면 상태 로직 (Windows 에서 Core 로 옮김) ──

    [Fact]
    public void DocumentMapsAndBuildsStatements()
    {
        var table = new TableMeta("tb_member", columns: [new ColumnMeta("member_id", "int", false), new ColumnMeta("email", "varchar(50)", true)]);
        var doc = new CsvImportDocument(table);
        doc.LoadText("Member ID;e-mail;x\r\n1;a@x.com;q\r\n2;;r\r\n");
        Assert.Equal(';', doc.Delimiter);
        Assert.Equal(["member_id", "email", null], doc.Mapping);   // 공백 · 기호 무시
        Assert.Equal(2, doc.DataRows.Count);
        Assert.Equal(3, doc.CsvColumns);
        doc.SetMapping(0, null);
        doc.SetMapping(1, null);
        Assert.False(doc.CanImport);
        doc.SetMapping(0, "member_id");
        doc.SetMapping(1, "email");
        Assert.True(doc.CanImport);
        Assert.Equal("NULL", doc.PreviewText(""));
        var sql = doc.Statements(new MySqlDialect());
        Assert.Single(sql);
        Assert.Equal("INSERT INTO `tb_member` (`member_id`, `email`) VALUES\n('1', 'a@x.com'),\n('2', NULL)", sql[0]);
        doc.SetHasHeader(false);
        Assert.Equal(3, doc.DataRows.Count);
        Assert.Equal(["member_id", "email", null], doc.Mapping);
    }

    [Fact]
    public void ReadsCp949WhenNotUtf8()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"goddb-csv-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllBytes(path, System.Text.Encoding.GetEncoding(949).GetBytes("이름\n홍길동\n"));
            Assert.Equal("이름\n홍길동\n", CsvImport.ReadText(path));
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("a,b")]);
            Assert.Equal("a,b", CsvImport.ReadText(path));
        }
        finally { File.Delete(path); }
    }
}
