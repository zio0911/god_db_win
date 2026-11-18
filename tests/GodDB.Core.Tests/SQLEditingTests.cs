using GodDB.Core.Sql;

namespace GodDB.Core.Tests;

public class SQLEditingTests
{
    private static string Apply(string text, int location, int length)
    {
        var r = SqlEditing.ToggleLineComment(text, location, length);
        return text[..r.Start] + r.Replacement + text[(r.Start + r.Length)..];
    }

    [Fact]
    public void CommentsAndUncommentsSelectedLines()
    {
        var text = "SELECT 1;\n  SELECT 2;\nSELECT 3;";
        var once = Apply(text, 0, 15);
        Assert.Equal("-- SELECT 1;\n--   SELECT 2;\nSELECT 3;", once);
        var back = Apply(once, 0, 20);
        Assert.Equal(text, back);
    }

    [Fact]
    public void CursorOnlyTogglesItsLine()
    {
        Assert.Equal("a\n-- b\nc", Apply("a\nb\nc", 2, 0));
    }

    [Fact]
    public void KeepsIndentAndBlankLines()
    {
        Assert.Equal("    -- x\n\n    -- y\n", Apply("    x\n\n    y\n", 0, 12));
    }

    [Fact]
    public void FormatsClausesAndConditions()
    {
        var sql = "select a, b from t join u on u.id = t.uid where a = 1 and b in (1,2) order by a desc";
        Assert.Equal("SELECT a, b\nFROM t\nJOIN u ON u.id = t.uid\nWHERE a = 1\n    AND b IN (1, 2)\nORDER BY a DESC",
                     SqlEditing.Format(sql));
    }

    [Fact]
    public void KeepsStringsCommentsAndIdentifiers()
    {
        var sql = "select 'from x where' as `select`, \"Where\" from t -- where note\nwhere x = 'a and b'";
        var f = SqlEditing.Format(sql);
        Assert.Contains("'from x where'", f);
        Assert.Contains("`select`", f);
        Assert.Contains("\"Where\"", f);
        Assert.Contains("-- where note", f);
        Assert.Contains("'a and b'", f);
    }

    [Fact]
    public void BetweenStaysOnOneLineAndIsStable()
    {
        var f = SqlEditing.Format("SELECT * FROM t WHERE d BETWEEN 1 AND 5 AND x = 2");
        Assert.Contains("BETWEEN 1 AND 5\n    AND x = 2", f);
        Assert.Equal(f, SqlEditing.Format(f));
    }

    [Fact]
    public void FunctionCallsStayAttached()
    {
        var f = SqlEditing.Format("select count(*), max(x.y) from t where a in (1)");
        Assert.StartsWith("SELECT COUNT(*), MAX(x.y)", f);
        Assert.Contains("IN (1)", f);
    }

    [Fact]
    public void SubqueryIsIndented()
    {
        var f = SqlEditing.Format("select * from t where id in (select id from u where x = 1)");
        Assert.Contains("IN (\n    SELECT id\n    FROM u\n    WHERE x = 1)", f);
    }

    // Mac 에는 없는 테스트: SQLEditorView.swift 에서 옮긴 정렬 범위 · 문장 강조
    [Fact]
    public void FormatsStatementAtCursorAndHighlights()
    {
        var text = "select 1;\nselect a from t;";
        var e = SqlEditing.FormatSelectionOrStatement(text, 12, 0);
        Assert.NotNull(e);
        Assert.Equal("SELECT a\nFROM t", e.Value.Replacement.Trim());
        Assert.Equal((10, 25), SqlEditing.StatementHighlight(text, 12, 0));
        Assert.Null(SqlEditing.StatementHighlight("select 1", 3, 0));
        Assert.Null(SqlEditing.StatementHighlight(text, 12, 1));
        Assert.Null(SqlEditing.FormatSelectionOrStatement("SELECT 1", 0, 8));
    }
}
