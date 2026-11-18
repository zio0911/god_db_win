using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GodDB.Core.Dialects;
using GodDB.Core.Results;
using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

/// Mac: ResultExportTests.swift
public class ResultExportTests
{
    private readonly List<string> cols = ["id", "name", "memo"];
    private readonly List<string?[]> rows = [["1", "김철수", "a,b"], ["2", null, "줄\n바꿈|파이프"], ["007", "x\"y", "1e5"]];

    private (string Path, string Text) Export(ExportFormat f, ExportOptions? opts = null, ISqlDialect? dialect = null, int pages = 1)
    {
        var path = Path.Combine(Path.GetTempPath(), $"goddb-test-{Guid.NewGuid()}.{f.FileExtension()}");
        var w = new ResultExportWriter(f, cols, path, opts ?? new ExportOptions(), dialect);
        for (int i = 0; i < pages; i++) w.Write(rows);
        w.Finish();
        Assert.Equal(rows.Count * pages, w.RowsWritten);
        // String(contentsOf:) 처럼 BOM 은 빼고 읽는다
        var text = f == ExportFormat.Xlsx ? "" : File.ReadAllText(path, Encoding.UTF8);
        return (path, text);
    }

    [Fact]
    public void CsvAndTsv()
    {
        var (path, csv) = Export(ExportFormat.Csv);
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path)[..3]);   // BOM
        Assert.StartsWith("id,name,memo\r\n1,김철수,\"a,b\"\r\n", csv);
        Assert.Contains("2,,\"줄\n바꿈|파이프\"\r\n", csv);
        var (_, tsv) = Export(ExportFormat.Tsv, new ExportOptions { ExcelBom = false });
        Assert.StartsWith("id\tname\tmemo\r\n", tsv);
        Assert.Contains("2\t\t줄 바꿈|파이프\r\n", tsv);
    }

    [Fact]
    public void JsonKeepsNumbersAndNulls()
    {
        var (_, json) = Export(ExportFormat.Json);
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement;
        Assert.Equal(3, data.GetArrayLength());
        Assert.Equal(1, data[0].GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, data[1].GetProperty("name").ValueKind);
        Assert.Equal("007", data[2].GetProperty("id").GetString());     // 앞자리 0 은 문자열
        Assert.Equal("1e5", data[2].GetProperty("memo").GetString());   // 지수 표기도 문자열
    }

    [Fact]
    public void SqlInsertBatches()
    {
        var (_, sql) = Export(ExportFormat.Sql, new ExportOptions { TableName = "users", InsertBatch = 2 }, new MySqlDialect(), pages: 2);
        Assert.Equal(3, sql.Split("INSERT INTO `users` (`id`, `name`, `memo`) VALUES").Length - 1);
        Assert.True(sql.Contains("('2', NULL, '줄\\n바꿈|파이프')") || sql.Contains("('2', NULL, '줄\n바꿈|파이프')"));
    }

    [Fact]
    public void MarkdownEscapes()
    {
        var (_, md) = Export(ExportFormat.Markdown);
        Assert.StartsWith("| id | name | memo |\n| --- | --- | --- |\n", md);
        Assert.Contains("| 2 | NULL | 줄<br>바꿈\\|파이프 |", md);
    }

    [Fact]
    public void XlsxIsValidZip()
    {
        var (path, _) = Export(ExportFormat.Xlsx, new ExportOptions { TableName = "a/b:c" }, pages: 3);
        using var zip = ZipFile.OpenRead(path);
        string Read(string name)
        {
            using var r = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
            return r.ReadToEnd();
        }
        var sheet = Read("xl/worksheets/sheet1.xml");
        Assert.Equal(1 + 9, sheet.Split("<row>").Length - 1);   // 머리글 + 3행 × 3페이지
        Assert.Contains("<c><v>1</v></c>", sheet);
        Assert.Contains("<t xml:space=\"preserve\">007</t>", sheet);
        Assert.Contains("<c/>", sheet);
        Assert.Contains("state=\"frozen\"", sheet);                         // 머리글 고정
        Assert.Contains("<c t=\"inlineStr\" s=\"1\">", sheet);              // 머리글 굵게
        var wb = Read("xl/workbook.xml");
        Assert.Contains("name=\"a_b_c\"", wb);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.Contains("<b/>", Read("xl/styles.xml"));
        File.Delete(path);
    }

    [Fact]
    public void CancelRemovesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"goddb-cancel-{Guid.NewGuid()}.csv");
        var w = new ResultExportWriter(ExportFormat.Csv, cols, path);
        w.Write(rows);
        w.Cancel();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void NumberRule()
    {
        Assert.True(ResultExportWriter.IsPlainNumber("12345"));
        Assert.True(ResultExportWriter.IsPlainNumber("-3.25"));
        Assert.False(ResultExportWriter.IsPlainNumber("0012"));
        Assert.False(ResultExportWriter.IsPlainNumber("1234567890123456"));   // 16자리
        Assert.False(ResultExportWriter.IsPlainNumber("1e5"));
        Assert.False(ResultExportWriter.IsPlainNumber(""));
    }

    [Fact]
    public void StableSortForPaging()
    {
        List<string> c = ["id", "name"];
        Assert.Equal((null, true), ResultExportPlan.StableSort("SELECT * FROM t ORDER BY name", null, c, ["id"]));
        var r = ResultExportPlan.StableSort("SELECT * FROM t", null, c, ["ID"]);
        Assert.True(r.Sort == new ResultSort("id", true) && r.Stable);
        Assert.False(ResultExportPlan.StableSort("SELECT name FROM t", null, ["name"], ["id"]).Stable);
        var s = new ResultSort("name", false);
        Assert.Equal(s, ResultExportPlan.StableSort("SELECT * FROM t", s, c, null).Sort);
    }

    // ── Mac ResultExportUI.swift 의 화면과 상관없는 로직 (Windows 에서 Core 로 옮김) ──

    [Fact]
    public void PrepareRequestFromResult()
    {
        var table = new TableMeta("tb_member", columns: [new ColumnMeta("member_id", "int", false)],
                                  indexes: [new IndexMeta("PRIMARY", true, ["member_id"])]);
        var result = new QueryResult(["member_id"], [new string?[] { "1" }], 0)
        {
            Sql = "SELECT member_id FROM `shop`.`tb_member`", PageSize = 1, HasMore = true,
        };
        var now = new DateTimeOffset(2026, 11, 18, 14, 30, 5, TimeSpan.FromHours(9));
        var req = ExportRequest.Prepare(result, DatabaseKind.Mysql, [table], now)!;
        Assert.True(req.CanFetchAll);
        Assert.True(req.Stable);
        Assert.Equal(new ResultSort("member_id", true), req.Sort);
        Assert.Equal("tb_member", req.DefaultTable);
        Assert.Equal("tb_member_20261118_143005", req.BaseName);   // KST
        Assert.Equal("tb_member_20261118_143005.md", req.FileName(ExportFormat.Markdown));

        result.HasMore = false;
        result.Sql = "SELECT 1 AS a FROM x JOIN y";
        var single = ExportRequest.Prepare(result, DatabaseKind.Mysql, [table], now)!;
        Assert.False(single.CanFetchAll);
        Assert.Equal("table", single.DefaultTable);
        Assert.StartsWith("query_", single.BaseName);
        Assert.Null(ExportRequest.Prepare(new QueryResult([], [], 0), DatabaseKind.Mysql, [], now));
    }

    [Fact]
    public void ChoiceRemembersFormatAndValidatesTable()
    {
        // Prefs 는 정적이라 다른 테스트(PrefsTests)와 겹치지 않게 저장값을 직접 넘긴다
        var result = new QueryResult(["a"], [new string?[] { "1" }], 0);
        var req = ExportRequest.Prepare(result, DatabaseKind.Postgres, [], DateTimeOffset.UtcNow)!;
        var c = ExportChoice.Initial(req, null);
        Assert.Equal(ExportFormat.Csv, c.Format);
        Assert.False(c.FetchAll);
        Assert.Equal(ExportFormat.Xlsx, ExportChoice.Initial(req, "xlsx").Format);
        Assert.Equal(ExportFormat.Markdown, ExportChoice.Initial(req, "markdown").Format);
        c.Format = ExportFormat.Sql;
        c.TableName = "  ";
        Assert.False(c.CanContinue);
        Assert.Equal("table", c.Options().TableName);
        c.TableName = "users";
        Assert.True(c.CanContinue);
        Assert.Equal("SQL INSERT · 보이는 1,234행", c.PanelMessage(1234));
    }

    [Fact]
    public async Task RunnerWritesVisibleRowsAndReports()
    {
        var result = new QueryResult(cols, rows, 0) { Sql = "SELECT * FROM t" };
        var req = ExportRequest.Prepare(result, DatabaseKind.Mysql, [], DateTimeOffset.UtcNow)!;
        var choice = new ExportChoice { Format = ExportFormat.Csv, FetchAll = false, ExcelBom = false };
        var path = Path.Combine(Path.GetTempPath(), $"goddb-run-{Guid.NewGuid():N}.csv");
        try
        {
            var o = await ResultExportRunner.RunAsync(null, null, req, choice, path, ct: TestContext.Current.CancellationToken);
            Assert.Equal(ExportOutcomeKind.Success, o.Kind);
            Assert.Equal(3, o.Written);
            Assert.Equal("CSV · 보이는 행 3행", o.AuditResult);
            Assert.StartsWith("id,name,memo\r\n", File.ReadAllText(path));

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var cancelled = await ResultExportRunner.RunAsync(null, null, req, choice, path, ct: cts.Token);
            Assert.Equal(ExportOutcomeKind.Cancelled, cancelled.Kind);
            Assert.False(File.Exists(path));   // 취소하면 쓰던 파일 삭제
        }
        finally { File.Delete(path); }
    }
}

public class ExportFileNameStampTests
{
    [Fact]
    public void StampIsKst()
    {
        // UTC 2026-11-13 05:46:25 = KST 14:46:25
        Assert.Equal("20261113_144625", GodDB.Core.Results.ExportRequest.Stamp(new DateTimeOffset(2026, 11, 13, 5, 46, 25, TimeSpan.Zero)));
        // 날짜가 넘어가는 경우: UTC 11-13 16:00 = KST 11-14 01:00
        Assert.Equal("20261114_010000", GodDB.Core.Results.ExportRequest.Stamp(new DateTimeOffset(2026, 11, 13, 16, 0, 0, TimeSpan.Zero)));
    }
}
