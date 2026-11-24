using System.Text;
using GodDB.Core.Ai;
using GodDB.Core.Platform;
using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

// Mac Tests/GodDBCoreTests/TableDescriptionsTests.swift (#178)
// 옮기지 않은 것: migratesOnlyConfirmedNotesOnce · storeSavesPrivatelyAndMigratesOnFirstLoad 의 "예전 테이블 메모 옮기기" 부분.
// Windows 판은 1.3.7 다음이 1.4.3 이라 table-notes.json 이 생긴 적이 없어 옮기기 코드를 두지 않았다 (TableNote.cs 참고).
// 저장 · 권한 확인은 StoreSavesPrivately 로 옮겼다.
public class TableDescriptionsTests
{
    private static ColumnMeta Col(string name, string comment = "", int position = 0) => new(name, "varchar(20)", true, comment: comment, position: position);

    private static readonly TableMeta Orders = new("orders", columns: [Col("id", position: 1), Col("member_id", position: 2), Col("status", "주문 상태", position: 3)],
        foreignKeys: [new ForeignKeyMeta("fk_member", "orders", ["member_id"], "members", ["id"])]);
    private static readonly TableMeta Members = new("members", "회원", [Col("id"), Col("grade_cd")]);

    private const string Sample = """
        {
          "format": "goddb-table-descriptions", "version": 1, "database": "shop",
          "overview": "쇼핑몰 주문 DB",
          "tables": [
            { "name": "orders", "title": "주문", "description": "결제한 주문 한 건", "terms": ["결제 내역"],
              "columns": [
                { "name": "status", "title": "상태", "values": { "P": "결제 대기", "C": "완료" } },
                { "name": "member_id", "references": "members.id" },
                { "name": "deleted_at", "title": "삭제일시" }
              ],
              "notes": ["deleted_at IS NULL 조건을 붙인다"] },
            { "name": "members", "columns": [ { "name": "grade_cd", "title": "회원 등급", "terms": ["등급"] } ] },
            { "name": "old_coupon" }
          ]
        }
        """;

    private static TableDescriptionDoc Doc() => TableDescriptionDoc.Parse(Encoding.UTF8.GetBytes(Sample));

    [Fact]
    public void ParsesSampleAndCounts()
    {
        var d = Doc();
        Assert.Equal(3, d.Tables.Count);
        Assert.Equal(4, d.ColumnCount);
        Assert.Equal(2, d.ValueCount);
        Assert.Equal("완료", d.FindTable("public.orders")?.Column("STATUS")?.Values?["C"]);
    }

    [Fact]
    public void ParseErrorsSayWhere()
    {
        static TableDescriptionDoc.ParseException? Error(string json)
        {
            try { TableDescriptionDoc.Parse(json); return null; }
            catch (TableDescriptionDoc.ParseException e) { return e; }
        }
        Assert.Equal(TableDescriptionDoc.ParseException.ErrorKind.NotJson, Error("{ tables: ")?.Kind);
        Assert.Equal(TableDescriptionDoc.ParseException.Invalid("tables[0].name", "꼭 있어야 하는 칸이 없어요"), Error("""{"tables": [{"title": "x"}]}"""));
        Assert.Contains("tables[0].columns[0].values", Error("""{"tables": [{"name": "t", "columns": [{"name": "c", "values": ["P"]}]}]}""")?.Message);
        Assert.Equal(TableDescriptionDoc.ParseException.WrongFormat("other"), Error("""{"format": "other", "tables": [{"name": "t"}]}"""));
        Assert.Equal(TableDescriptionDoc.ParseException.NewerVersion(9), Error("""{"version": 9, "tables": [{"name": "t"}]}"""));
        Assert.Equal(TableDescriptionDoc.ParseException.Empty(), Error("""{"tables": []}"""));
        Assert.Equal(TableDescriptionDoc.ParseException.Invalid("tables[0].name", "비어 있어요"), Error("""{"tables": [{"name": " "}]}"""));
    }

    [Fact]
    public void RoundTripsThroughEncoding()
    {
        var d = Doc();
        Assert.Equal(d, TableDescriptionDoc.Parse(d.Encoded()));
    }

    [Fact]
    public void SkeletonHasNamesCommentsAndForeignKeysOnly()
    {
        var s = TableDescriptionDoc.Skeleton([Orders, Members], "shop");
        Assert.True(s.Format == TableDescriptionDoc.FormatName && s.Database == "shop");
        Assert.Equal(["id", "member_id", "status"], s.FindTable("orders")?.Columns?.Select(c => c.Name));
        Assert.Equal("주문 상태", s.FindTable("orders")?.Column("status")?.Title);
        Assert.Equal("members.id", s.FindTable("orders")?.Column("member_id")?.References);
        Assert.Equal("회원", s.FindTable("members")?.Title);
        Assert.All(s.Tables, t => Assert.All(t.Columns ?? [], c => Assert.Null(c.Values)));
        // 빈 양식도 다시 읽힌다
        Assert.Equal(2, TableDescriptionDoc.Parse(s.Encoded()).Tables.Count);
    }

    [Fact]
    public void FormCarriesInstructionsAndKeepsCurrentDescriptions()
    {
        var current = Doc();
        var f = TableDescriptionDoc.Form([Orders, Members], "shop", DatabaseKind.Mysql, english: false, current: current);
        var text = f.Instructions!;
        Assert.True(text.Contains("instructions 칸은 지우기") && text.Contains("`shop`"));
        Assert.Equal("주문", f.FindTable("orders")?.Title);                       // 이미 있는 설명 유지
        Assert.NotNull(f.FindTable("orders")?.Column("id"));                       // 새 컬럼 더함
        Assert.Equal("완료", f.FindTable("orders")?.Column("status")?.Values?["C"]);
        // 에이전트가 instructions 를 남겨 돌려줘도 읽힌다
        var back = TableDescriptionDoc.Parse(f.Encoded());
        Assert.Equal(text, back.Instructions);
        Assert.Contains("\"instructions\"", Encoding.UTF8.GetString(f.Encoded()));
        // 원래 설명은 그대로 (양식을 만들며 바꾸지 않는다)
        Assert.Null(current.FindTable("orders")?.Column("id"));
    }

    [Fact]
    public void RequestPromptNamesFormatAndDatabase()
    {
        foreach (var english in new[] { false, true })
        {
            var p = TableDescriptionDoc.RequestPrompt("shop", DatabaseKind.Postgres, english);
            Assert.Contains(TableDescriptionDoc.FormatName, p);
            Assert.Contains("`shop`", p);
            Assert.Contains("values", p);
        }
    }

    [Fact]
    public void ChecksAgainstDatabase()
    {
        var c = Doc().Check([Orders, Members, new TableMeta("carts")]);
        Assert.Equal(["old_coupon"], c.MissingTables);
        Assert.Equal(["orders.deleted_at"], c.UnknownColumns);
        Assert.Equal(["carts"], c.UndescribedTables);
        Assert.False(c.IsClean);
    }

    [Fact]
    public void TitlesFillOnlyEmptyComments()
    {
        var output = Doc().ApplyTitles([Orders, Members]);
        Assert.Equal("주문", output[0].Comment);
        Assert.Equal("주문 상태", output[0].Columns.First(c => c.Name == "status").Comment);   // 원래 코멘트 유지
        Assert.Equal("회원", output[1].Comment);
        Assert.Equal("회원 등급", output[1].Columns.First(c => c.Name == "grade_cd").Comment);
    }

    [Fact]
    public void QuestionFindsTableByTitleOrTerm()
    {
        var d = Doc();
        List<TableMeta> tables = [Orders, Members];
        Assert.Empty(AssistantAI.MentionedTables("결제 내역 보여줘", tables));
        Assert.Equal(["orders"], AssistantAI.MentionedTables("결제 내역 보여줘", tables, aliases: d.Aliases).Select(t => t.Name));
        Assert.Equal(["orders"], AssistantAI.MentionedTables("주문 상태별 건수", d.ApplyTitles(tables)).Select(t => t.Name));
    }

    [Fact]
    public void ContextHasMeaningValuesAndRules()
    {
        var text = Doc().ContextText(["orders"])!;
        Assert.Contains("쇼핑몰 주문 DB", text);
        Assert.Contains("orders — 주문: 결제한 주문 한 건", text);
        Assert.Contains("C=완료, P=결제 대기", text);
        Assert.Contains("→ members.id", text);
        Assert.Contains("rule: deleted_at IS NULL", text);
        Assert.DoesNotContain("grade_cd", text);
        Assert.Contains("truncated", Doc().ContextText(["orders"], limit: 10));
    }

    [Fact]
    public void FinderNotesMatchTitlesAndTerms()
    {
        var p = Guid.NewGuid();
        var notes = Doc().FinderNotes(p, "shop");
        Assert.Equal(["members.grade_cd"], DataFinder.MatchNotes(["등급"], notes).Select(n => n.Location));
        Assert.Equal(["orders"], DataFinder.MatchNotes(["결제", "내역"], notes).Select(n => n.Location));
        // id 는 위치마다 늘 같다
        Assert.Equal(notes.Select(n => n.Id), Doc().FinderNotes(p, "shop").Select(n => n.Id));
        Assert.Equal(notes.Count, notes.Select(n => n.Id).Distinct().Count());
    }

    [Fact]
    public void ConfirmAddsTermsAndRejectRemovesThem()
    {
        var d = Doc();
        d.AddTerms(["학년 정보"], "members", ["grade_cd"]);
        d.AddTerms(["쿠폰"], "coupons", []);
        d.AddTerms(["가입 경로"], "members", ["join_path"]);
        Assert.Equal(["등급", "학년 정보"], d.FindTable("members")?.Column("grade_cd")?.Terms!);
        Assert.Equal(["쿠폰"], d.FindTable("coupons")?.Terms!);
        Assert.Equal(["가입 경로"], d.FindTable("members")?.Column("join_path")?.Terms!);
        d.RemoveTerms(["학년정보"], "members", ["grade_cd"]);
        Assert.Equal(["등급"], d.FindTable("members")?.Column("grade_cd")?.Terms!);
        d.RemoveTerms(["쿠폰"], "coupons", []);
        Assert.Null(d.FindTable("coupons")?.Terms);
    }

    [Fact]
    public void LibraryEditStartsEmptyAndKeepsOthers()
    {
        var p = Guid.NewGuid();
        var lib = new TableDescriptionLibrary();
        lib.Edit(p, "shop", d => d.AddTerms(["학년"], "members", ["grade_cd"]), DateTime.UnixEpoch.AddSeconds(100));
        Assert.Equal("shop", lib.Doc(p, "shop")?.Database);
        Assert.Equal(["학년"], lib.Doc(p, "shop")?.FindTable("members")?.Column("grade_cd")?.Terms!);
        Assert.Null(lib.Doc(p, "other"));
        lib.Remove(p, "shop");
        Assert.Empty(lib.Entries);
    }

    [Fact]
    public void StoreSavesPrivately()
    {
        using var dir = new TempDir();
        var p = Guid.NewGuid();
        var store = new TableDescriptionStore(dir.File("table-descriptions.json"));
        Assert.Null(store.Load());
        var lib = new TableDescriptionLibrary();
        lib.Set(new TableDescriptionEntry(p, "db", Doc(), DateTime.UnixEpoch.AddSeconds(1_795_000_000), "shop.json"));
        store.Save(lib);
        var loaded = store.Load()!;
        Assert.Equal(lib, loaded);
        Assert.Equal("shop.json", loaded.Entry(p, "db")?.FileName);
        Assert.True(UserOnlyFile.IsRestricted(store.FilePath));
        // Mac 과 같은 형식: 키 정렬, ISO 8601 초 단위 날짜, 대문자 UUID
        var json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"profileID\": \"" + p.ToString().ToUpperInvariant() + "\"", json);
        Assert.Contains("\"updatedAt\": \"2026-11-18T", json);
        Assert.Contains("\"migratedNotes\": true", json);
        // 지우면 그대로 지워진 채로
        lib.Remove(p, "db");
        store.Save(lib);
        Assert.Empty(store.Load()!.Entries);
    }
}
