using GodDB.Core.Platform;
using System.Text;
using System.Text.Json;
using GodDB.Core.Dialects;
using GodDB.Core.Profiles;
using GodDB.Core.Sql;

namespace GodDB.Core.Tests;

/// Mac SavedQueriesTests.swift ("SavedQueries")
public class SavedQueriesTests
{
    private static Dictionary<string, SqlParameters.Input> In(params (string Name, string Value)[] values) =>
        values.ToDictionary(v => v.Name, v => new SqlParameters.Input(v.Value));

    // MARK: - 빈칸 찾기

    [Fact]
    public void SpecExampleFindsOnlyX()
    {
        // 명세 확인 기준: 캐스트 · 문자열 · 주석 안은 빈칸이 아니다
        var sql = "SELECT * FROM t WHERE a = :x AND b::text = ':y' -- :z";
        Assert.Equal(["x"], SqlParameters.Names(sql, DatabaseKind.Postgres));
        Assert.Equal(["x"], SqlParameters.Names(sql, DatabaseKind.Mysql));
    }

    [Fact]
    public void NamesFollowIdentifierRule()
    {
        var sql = "SELECT :a, :_b2, :Member_No, :1, : c, :é";
        Assert.Equal(["a", "_b2", "Member_No"], SqlParameters.Names(sql, DatabaseKind.Mysql));
    }

    [Fact]
    public void IgnoresStringLiterals()
    {
        var sql = """SELECT ':a', ":b", `:c`, 'it''s :d', :e""";
        Assert.Equal(["e"], SqlParameters.Names(sql, DatabaseKind.Mysql));
        Assert.Equal(["e"], SqlParameters.Names(sql, DatabaseKind.Postgres));
        // MySQL 은 \' 가 문자열 안의 따옴표
        Assert.Equal(["y"], SqlParameters.Names("""SELECT 'a\' :x', :y""", DatabaseKind.Mysql));
    }

    [Fact]
    public void IgnoresEachCommentStyle()
    {
        Assert.Equal(["a", "c"], SqlParameters.Names("SELECT :a -- :b\nFROM t WHERE c = :c", DatabaseKind.Postgres));
        Assert.Equal(["a"], SqlParameters.Names("SELECT /* :b\n :c */ :a", DatabaseKind.Mssql));
        Assert.Equal(["a", "c"], SqlParameters.Names("SELECT :a # :b\n, :c", DatabaseKind.Mysql));
    }

    [Fact]
    public void HashIsCommentOnlyForMySQL()
    {
        var sql = "SELECT :a # :b";
        Assert.Equal(["a"], SqlParameters.Names(sql, DatabaseKind.Mysql));
        Assert.Equal(["a", "b"], SqlParameters.Names(sql, DatabaseKind.Postgres));
        Assert.Equal(["a", "b"], SqlParameters.Names(sql, DatabaseKind.Mssql));
    }

    [Fact]
    public void IgnoresCastAndAssignment()
    {
        Assert.Equal(["d"], SqlParameters.Names("SELECT created::date, :d::int FROM t", DatabaseKind.Postgres));
        Assert.Equal(["n"], SqlParameters.Names("SET @n := 1; SELECT :n", DatabaseKind.Mysql));
        Assert.Empty(SqlParameters.Names("SELECT a:=b", DatabaseKind.Mysql));
    }

    [Fact]
    public void IgnoresPostgresDollarQuotes()
    {
        var sql = "DO $$ BEGIN PERFORM :inner; END $$; SELECT :outer";
        Assert.Equal(["outer"], SqlParameters.Names(sql, DatabaseKind.Postgres));
    }

    [Fact]
    public void DedupesRepeatedNamesInFirstOrder()
    {
        var sql = "SELECT * FROM t WHERE a = :id OR b = :from OR c = :id";
        Assert.Equal(["id", "from"], SqlParameters.Names(sql, DatabaseKind.Mysql));
        Assert.Equal(3, SqlParameters.Occurrences(sql, DatabaseKind.Mysql).Count);
    }

    // MARK: - 값 변환

    [Fact]
    public void ConvertsNumbersAndStrings()
    {
        var my = new MySqlDialect();
        Assert.Equal("1023", SqlParameters.Render(new("1023"), my));
        Assert.Equal("-3.5", SqlParameters.Render(new("-3.5"), my));
        Assert.Equal("'2026-11-01'", SqlParameters.Render(new("2026-11-01"), my));
        Assert.Equal("'O''Neil'", SqlParameters.Render(new("O'Neil"), new PostgresDialect()));
        Assert.Equal("'1e5'", SqlParameters.Render(new("1e5"), my));
    }

    [Fact]
    public void NullAndRaw()
    {
        var my = new MySqlDialect();
        Assert.Equal("NULL", SqlParameters.Render(new("abc", IsNull: true), my));
        Assert.Equal("NOW()", SqlParameters.Render(new("NOW()", Raw: true), my));
        // 그대로 넣기면 쉼표도 그대로
        Assert.Equal("1,2,3", SqlParameters.Render(new("1,2,3", Raw: true), my));
        // NULL 이 그대로 넣기보다 먼저
        Assert.Equal("NULL", SqlParameters.Render(new("x", IsNull: true, Raw: true), my));
    }

    [Fact]
    public void CommaExpandsToList()
    {
        var my = new MySqlDialect();
        Assert.Equal("1, 2, 3", SqlParameters.Render(new("1,2,3"), my));
        Assert.Equal("'a', 'b', 7", SqlParameters.Render(new("a, b ,7"), my));
        var sql = SqlParameters.Fill("SELECT * FROM t WHERE id IN (:ids)", In(("ids", "1,2,3")), my);
        Assert.Equal("SELECT * FROM t WHERE id IN (1, 2, 3)", sql);
    }

    [Fact]
    public void MssqlUsesNPrefix()
    {
        var ms = new MssqlDialect();
        Assert.Equal("N'kim'", SqlParameters.Render(new("kim"), ms));
        Assert.Equal("N'kim', N'lee'", SqlParameters.Render(new("kim,lee"), ms));
        Assert.Equal("10", SqlParameters.Render(new("10"), ms));
    }

    [Fact]
    public void FillReplacesEveryOccurrenceAndKeepsOthers()
    {
        var sql = "SELECT ':id', a::text FROM t WHERE a = :id AND b = :name OR c = :id -- :id";
        var output = SqlParameters.Fill(sql, In(("id", "7"), ("name", "kim")), new PostgresDialect());
        Assert.Equal("SELECT ':id', a::text FROM t WHERE a = 7 AND b = 'kim' OR c = 7 -- :id", output);
        // 입력이 없는 빈칸은 그대로
        Assert.Equal("SELECT 1, :b", SqlParameters.Substitute("SELECT :a, :b", new Dictionary<string, string> { ["a"] = "1" }, DatabaseKind.Mysql));
    }

    // MARK: - 이름 · 목록

    [Fact]
    public void DefaultNameIsFirstLine40Chars()
    {
        Assert.Equal("SELECT * FROM member WHERE member_no = :memberNo AND x = 1"[..40],
            SavedQuery.DefaultName("\n  SELECT * FROM member WHERE member_no = :memberNo AND x = 1\nORDER BY 1"));
        Assert.Equal("SELECT 1", SavedQuery.DefaultName("SELECT 1"));
    }

    [Fact]
    public void SearchGroupsAndVisibility()
    {
        Guid p1 = Guid.NewGuid(), p2 = Guid.NewGuid();
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_000);
        var a = new SavedQuery("회원 조회", "SELECT * FROM member WHERE no = :no", "회원", createdAt: t0, lastUsedAt: t0.AddSeconds(50));
        var b = new SavedQuery("주문", "SELECT * FROM orders", "", createdAt: t0.AddSeconds(10));
        var c = new SavedQuery("탈퇴 회원", "SELECT 1", "회원", SavedQuery.QueryScope.Profile, p1, createdAt: t0, lastUsedAt: t0.AddSeconds(100));
        var lib = new SavedQueryLibrary([a, b, c]);

        Assert.Equal(3, lib.Search("", p1).Count);
        Assert.Equal([a.Id, b.Id], lib.Search("", p2).Select(q => q.Id));
        Assert.Equal([a.Id, b.Id], lib.Search("", null).Select(q => q.Id));
        // 이름 · SQL · 폴더, 대소문자 무시
        Assert.Equal([b.Id], lib.Search("ORDERS", p1).Select(q => q.Id));
        Assert.Equal([a.Id], lib.Search("회원", p2).Select(q => q.Id));
        Assert.Equal([a.Id], lib.Search("member :no", p2).Select(q => q.Id));
        Assert.Equal(["회원"], lib.Folders);

        var groups = SavedQueryLibrary.Grouped(lib.Search("", p1));
        Assert.Equal(["회원", ""], groups.Select(g => g.Folder));
        Assert.Equal([c.Id, a.Id], groups[0].Queries.Select(q => q.Id));
    }

    [Fact]
    public void LibraryEdits()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_000);
        var lib = new SavedQueryLibrary([new SavedQuery("q", "SELECT :a", createdAt: t0)]);
        var id = lib.Queries[0].Id;
        lib.Rename(id, "  새 이름 ", t0.AddSeconds(1));
        lib.Rename(id, "   ");
        Assert.Equal("새 이름", lib[id]?.Name);
        Assert.Equal(t0.AddSeconds(1), lib[id]?.UpdatedAt);
        lib.SetFolder(id, " 보고서 ");
        Assert.Equal("보고서", lib[id]?.Folder);
        lib.MarkUsed(id, new Dictionary<string, string> { ["a"] = "5" }, t0.AddSeconds(9));
        Assert.Equal(new Dictionary<string, string> { ["a"] = "5" }, lib[id]?.LastValues);
        Assert.Equal(t0.AddSeconds(9), lib[id]?.LastUsedAt);
        var copy = lib.Duplicate(id, " 사본");
        Assert.Equal("새 이름 사본", copy?.Name);
        Assert.True(copy?.LastUsedAt == null && copy!.LastValues["a"] == "5" && copy.LastValues.Count == 1 && copy.Id != id);
        lib.Delete(id);
        Assert.Equal([copy!.Id], lib.Queries.Select(q => q.Id));
    }

    [Fact]
    public void ProfileIDOnlyKeptForProfileScope()
    {
        var q = new SavedQuery("a", "SELECT 1", scope: SavedQuery.QueryScope.All, profileId: Guid.NewGuid());
        Assert.Null(q.ProfileId);
    }

    // MARK: - 저장 형식

    [Fact]
    public void StoreRoundTripAndJSONKeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), "goddb-saved-" + Guid.NewGuid());
        try
        {
            var crypto = new LocalDataCrypto(new StaticLocalDataKeyProvider());
            var store = new SavedQueryStore(Path.Combine(dir, "saved-queries.json"), crypto);
            Assert.Equal(new SavedQueryLibrary(), store.Load());

            var profile = Guid.NewGuid();
            var t0 = DateTimeOffset.FromUnixTimeSeconds(1_763_600_000);
            var a = new SavedQuery("회원", "SELECT * FROM member WHERE no = :memberNo", "업무", SavedQuery.QueryScope.Profile, profile,
                                   new Dictionary<string, string> { ["memberNo"] = "1023" }, t0, lastUsedAt: t0);
            var b = new SavedQuery("전체", "SELECT 1", createdAt: t0);
            store.Save(new SavedQueryLibrary([a, b]));
            Assert.Equal(new SavedQueryLibrary([a, b]), store.Load());

            // 디스크엔 GDBENC1 로 암호화돼 있고 (SQL 평문 없음), 풀면 Mac 과 같은 JSON
            var raw = File.ReadAllText(store.File);
            Assert.StartsWith("GDBENC1", raw);
            Assert.DoesNotContain("member", raw);
            using var json = JsonDocument.Parse(crypto.ReadDocumentData(store.File)!);
            Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
            var queries = json.RootElement.GetProperty("queries");
            var q0 = queries[0];
            Assert.Equal(new HashSet<string> { "id", "name", "folder", "sql", "scope", "profileID", "lastValues", "createdAt", "updatedAt", "lastUsedAt" },
                q0.EnumerateObject().Select(o => o.Name).ToHashSet());
            Assert.Equal("profile", q0.GetProperty("scope").GetString());
            Assert.Equal(profile.ToString().ToUpperInvariant(), q0.GetProperty("profileID").GetString());
            Assert.Equal("1023", q0.GetProperty("lastValues").GetProperty("memberNo").GetString());
            // ISO 8601 날짜
            Assert.Equal("2025-11-20T00:53:20Z", q0.GetProperty("createdAt").GetString());
            var q1 = queries[1];
            Assert.Equal("all", q1.GetProperty("scope").GetString());
            Assert.Equal(JsonValueKind.Null, q1.GetProperty("profileID").ValueKind);
            Assert.Equal(JsonValueKind.Null, q1.GetProperty("lastUsedAt").ValueKind);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadsMinimalFileFromOtherApps()
    {
        // Mac 판이 빠뜨린 칸이 있어도 읽는다
        var json = """
            {"version":1,"queries":[{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","name":"q","sql":"SELECT :a",
              "scope":"all","createdAt":"2026-11-20T01:02:03Z"}]}
            """;
        var lib = SavedQueryStore.Decode(Encoding.UTF8.GetBytes(json));
        Assert.Single(lib.Queries);
        Assert.Equal("", lib.Queries[0].Folder);
        Assert.Empty(lib.Queries[0].LastValues);
        Assert.Equal(lib.Queries[0].CreatedAt, lib.Queries[0].UpdatedAt);
        Assert.Null(lib.Queries[0].LastUsedAt);
    }
}
