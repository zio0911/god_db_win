using GodDB.Core.Dialects;
using GodDB.Core.Results;

namespace GodDB.Core.Tests;

/// Mac: ResultCopyTests.swift
public class ResultCopyTests
{
    private readonly List<string> cols = ["id", "name"];
    private readonly List<string?[]> rows = [["1", "Kim, A"], ["2", null]];

    [Fact]
    public void TsvAndCsv()
    {
        var tsv = ResultCopy.Text(ExportFormat.Tsv, cols, rows);
        Assert.StartsWith("id\tname", tsv);
        Assert.Contains("1\tKim, A", tsv);
        var csv = ResultCopy.Text(ExportFormat.Csv, cols, rows);
        Assert.Contains("1,\"Kim, A\"", csv);
        Assert.False(csv.StartsWith('﻿'));
    }

    [Fact]
    public void InsertUsesTableName()
    {
        var sql = ResultCopy.Text(ExportFormat.Sql, cols, rows, tableName: "users", dialect: new MySqlDialect());
        Assert.Contains("INSERT INTO", sql);
        Assert.Contains("users", sql);
        Assert.Contains("NULL", sql);
    }

    [Fact]
    public void InListQuotesAndDedupes()
    {
        Assert.Equal("(1, 2)", ResultCopy.InList(["1", "2", "2", null]));
        Assert.Equal("('a', 'b''c', '007')", ResultCopy.InList(["a", "b'c", "007"]));
    }

    [Fact]
    public void QuickFilter()
    {
        var w = ResultCopy.Words("kim  A");
        Assert.True(ResultCopy.Matches(["1", "Kim, A"], w));
        Assert.False(ResultCopy.Matches(["2", null], w));
        Assert.True(ResultCopy.Matches(["2", null], ["null"]));
    }
}
