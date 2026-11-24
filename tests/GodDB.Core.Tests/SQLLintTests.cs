using GodDB.Core.Profiles;
using GodDB.Core.Schema;
using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.Core.Tests;

/// Mac SQLLintTests.swift ("SQL lint")
public class SQLLintTests
{
    private readonly List<TableMeta> tables =
    [
        new("users", columns:
        [
            new ColumnMeta("id", "bigint", false, position: 1),
            new ColumnMeta("name", "varchar(50)", true, position: 2),
            new ColumnMeta("email", "varchar(100)", true, position: 3),
        ]),
        new("posts", columns:
        [
            new ColumnMeta("id", "bigint", false, position: 1),
            new ColumnMeta("user_id", "bigint", false, position: 2),
            new ColumnMeta("title", "varchar(200)", true, position: 3),
        ]),
    ];

    private List<(string Text, string Message)> Issues(string sql, DatabaseKind kind = DatabaseKind.Mysql, string? schema = null) =>
        SqlLint.Check(sql, tables, kind, schema).Select(i => (sql.Substring(i.Start, i.Length), i.Message)).ToList();

    [Fact]
    public void UnknownQualifiedColumnWithHint()
    {
        var sql = "SELECT a.id, a.username, b.title FROM users a JOIN posts b ON b.user_id = a.id";
        var found = SqlLint.Check(sql, tables, DatabaseKind.Mysql);
        Assert.Single(found);
        Assert.Equal("username", sql.Substring(found[0].Start, found[0].Length));
        Assert.Equal(Tr("%@ 테이블에 %@ 컬럼이 없어요.", "users", "username"), found[0].Message);
        Assert.Equal(Tr("비슷한 컬럼: %@", "name"), found[0].Hint);
    }

    [Fact]
    public void ValidQueriesHaveNoIssues()
    {
        string[] ok =
        [
            "SELECT a.id, a.name, b.title FROM users a JOIN posts b ON b.user_id = a.id WHERE a.email LIKE '%x%' ORDER BY a.id DESC, b.id",
            "SELECT id, name AS nm, COUNT(*) cnt FROM users WHERE email IS NOT NULL GROUP BY id, name ORDER BY nm",
            "SELECT TOP 10 u.id, u.name FROM [dbo].[users] u",
            "SELECT DISTINCT ON (u.id) u.id, u.name FROM public.users u ORDER BY u.id, u.name",
            "WITH t AS (SELECT 1 AS foo) SELECT t.foo FROM t",
            "SELECT x.whatever FROM (SELECT id AS whatever FROM users) x",
            "SELECT o.anything FROM other_db.orders o",
            "SELECT u.id::text, u.name || '!' FROM users u",
            "SELECT CASE WHEN u.id > 1 THEN u.name ELSE u.email END AS label FROM users u",
            "SELECT DATE_ADD(NOW(), INTERVAL 1 DAY), CURRENT_TIMESTAMP FROM users",
            "UPDATE users SET name = 'a' WHERE id = 1",
            "INSERT INTO posts (user_id, title) VALUES (1, 'it''s, fine')",
            "SELECT 'a, b', \"c\" FROM users -- trailing, comment\n",
            "SELECT u.id FROM users AS u WHERE u.id IN (SELECT p.user_id FROM posts p)",
        ];
        foreach (var sql in ok)
        {
            var kind = sql.Contains("[dbo]") || sql.Contains("TOP") ? DatabaseKind.Mssql
                : sql.Contains("::") || sql.Contains("ON (") ? DatabaseKind.Postgres : DatabaseKind.Mysql;
            var found = Issues(sql, kind, kind == DatabaseKind.Postgres ? "public" : null);
            Assert.True(found.Count == 0, $"{sql} → {string.Join(", ", found)}");
        }
    }

    [Fact]
    public void MissingComma()
    {
        var found = Issues("SELECT a.id a.name, b.title FROM users a JOIN posts b ON b.user_id = a.id");
        Assert.Equal(["a.name"], found.Select(f => f.Text));
        Assert.Equal(Tr("앞 항목과 사이에 콤마가 빠진 것 같아요."), found.First().Message);
        Assert.Equal(["a.name"], Issues("SELECT a.id, a.name FROM users a ORDER BY a.id a.name").Select(f => f.Text));
    }

    [Fact]
    public void CommasParensQuotes()
    {
        Assert.Equal([Tr("콤마 뒤에 항목이 없어요.")], Issues("SELECT id, name, FROM users").Select(f => f.Message));
        Assert.Equal([Tr("콤마가 두 번 있어요.")], Issues("SELECT id,, name FROM users").Select(f => f.Message));
        Assert.Equal([Tr("콤마 앞에 항목이 없어요.")], Issues("SELECT , id FROM users").Select(f => f.Message));
        Assert.Equal([Tr("닫는 괄호가 없어요.")], Issues("SELECT COUNT(id FROM users").Select(f => f.Message));
        Assert.Equal([Tr("여는 괄호가 없어요.")], Issues("SELECT id) FROM users").Select(f => f.Message));
        Assert.Equal([Tr("닫히지 않은 따옴표예요.")], Issues("SELECT id FROM users WHERE name = 'abc").Select(f => f.Message));
    }

    [Fact]
    public void KeywordTypos()
    {
        Assert.Equal([Tr("%@ 의 오타 같아요.", "SELECT")], Issues("SELEC id FROM users").Select(f => f.Message));
        Assert.Contains(Issues("SELECT id FORM users"), f => f.Text == "FORM" && f.Message == Tr("%@ 의 오타 같아요.", "FROM"));
        Assert.Contains(Issues("SELECT id FROM users WEHRE id = 1"), f => f.Text == "WEHRE");
    }

    [Fact]
    public void UnqualifiedColumnInSingleTable()
    {
        Assert.Equal(["usrname"], Issues("SELECT id, usrname FROM users WHERE id = 1").Select(f => f.Text));
        Assert.Equal(["emial"], Issues("SELECT id FROM users WHERE emial = 'x'").Select(f => f.Text));
        // JOIN 이 있으면 이름만 쓴 컬럼은 검사하지 않는다 (어느 테이블인지 확실하지 않음)
        Assert.Empty(Issues("SELECT usrname FROM users a JOIN posts b ON b.user_id = a.id"));
    }

    [Fact]
    public void OtherSchemaIsSkipped()
    {
        Assert.Empty(Issues("SELECT u.nope FROM sales.users u", DatabaseKind.Postgres, "public"));
        Assert.Equal(["nope"], Issues("SELECT u.nope FROM public.users u", DatabaseKind.Postgres, "public").Select(f => f.Text));
    }

    [Fact]
    public void RangesAreInFullText()
    {
        var text = "SELECT 1;\n\n  SELECT a.nope FROM users a;";
        var found = SqlLint.Check(text, tables, DatabaseKind.Mysql);
        Assert.Single(found);
        Assert.Equal("nope", text.Substring(found[0].Start, found[0].Length));
    }

    [Fact]
    public void ServerCheckHelpers()
    {
        Assert.True(SqlServerCheck.Checkable("  -- c\nselect 1", DatabaseKind.Postgres));
        Assert.False(SqlServerCheck.Checkable("CREATE TABLE x (a int)", DatabaseKind.Mysql));
        var sql = "SELECT a.username FROM users a";
        var r = SqlServerCheck.GuessRange("Unknown column 'a.username' in 'field list'", sql);
        Assert.Equal("a.username", r is { } x ? sql.Substring(x.Start, x.Length) : null);
        var r2 = SqlServerCheck.GuessRange("You have an error in your SQL syntax; check the manual near 'FORM users' at line 1", "SELECT id FORM users");
        Assert.Equal("FORM", r2 is { } y ? "SELECT id FORM users".Substring(y.Start, y.Length) : null);
        var (check, cleanup) = SqlServerCheck.Commands("SELECT 'it''s';", DatabaseKind.Mssql);
        Assert.Equal(["EXEC sp_describe_first_result_set @tsql = N'SELECT ''it''''s'''"], check);
        Assert.Empty(cleanup);
    }
}
