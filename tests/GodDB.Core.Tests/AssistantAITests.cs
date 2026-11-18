using GodDB.Core.Ai;
using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

// Mac 판 DataModelTests.swift 의 "Assistant AI context" · AppGuideTests · ReleaseNotesTests.
// (DataModel · ModelStore 테스트는 모델링 포팅에서 옮긴다. applyPhysicalNames 의 modelText 확인도 그쪽에서.)

public class AssistantAITests
{
    private readonly TableMeta member = new("tb_member", comment: "회원",
        columns: [new ColumnMeta("member_id", "bigint", false, columnKey: "PRI", comment: "회원 ID"),
                  new ColumnMeta("email", "varchar(100)", true)],
        indexes: [new IndexMeta("PRIMARY", true, ["member_id"])]);

    private TableMeta Order => new("tb_order", comment: "",
        columns: [new ColumnMeta("order_id", "bigint", false, columnKey: "PRI"),
                  new ColumnMeta("member_id", "bigint", true)],
        indexes: [new IndexMeta("PRIMARY", true, ["order_id"])],
        foreignKeys: [new ForeignKeyMeta("fk_order_member", "tb_order", ["member_id"], "tb_member", ["member_id"])]);

    [Fact]
    public void MentionsByPhysicalAndLogicalName()
    {
        TableMeta[] tables = [member, Order];
        Assert.Equal(["tb_order"], AssistantAI.MentionedTables("tb_order 특이사항 알려줘", tables).Select(t => t.Name));
        Assert.Equal(["tb_member"], AssistantAI.MentionedTables("회원 테이블 설명", tables).Select(t => t.Name));
        // 다른 이름의 일부는 아님
        Assert.Empty(AssistantAI.MentionedTables("tb_order_item", tables));
    }

    [Fact]
    public void NotesFindMissingIndexAndComment()
    {
        var n = AssistantAI.Notes(Order, new TableStats("tb_order", 2_000_000, null, null));
        Assert.Contains("no table comment", n);
        Assert.Contains(n, x => x.Contains("has no index"));
        Assert.Contains(n, x => x.Contains("nullable FK"));
        Assert.Contains(n, x => x.Contains("large table"));
    }

    [Fact]
    public void ContextHasNoRowsAndIncludesSql()
    {
        var ctx = new AssistantAI.Context(DatabaseKind.Mysql, "local", "shop", [member, Order], [Order],
                                          AssistantAI.ParseStats([["tb_order", "42", "16384", "0", "InnoDB", null]]),
                                          Sql: "SELECT * FROM tb_order", LastError: null);
        var text = AssistantAI.ContextText(ctx);
        Assert.Contains("TABLE tb_order", text);
        Assert.Contains("rows≈42", text);
        Assert.Contains("SELECT * FROM tb_order", text);
        Assert.DoesNotContain("Tables referencing", text);   // focus 는 tb_order, 참조하는 쪽 없음
        var msgs = AssistantAI.Messages(DatabaseKind.Mysql, text, [("user", "q1")], english: false);
        Assert.Equal("system", msgs.First().Role);
        Assert.Equal("q1", msgs.Last().Content);
    }

    /// Mac: DataModel 에 직접 적용. Windows 판은 모델링 포팅 전이라 같은 모양의 가짜 모델로 확인한다
    /// (테이블 "회원" 의 첫 컬럼은 물리명 없음, 두 번째 컬럼 "이메일" 은 물리명 "mail").
    private sealed class FakeModel : IPhysicalNameTarget
    {
        public string Table = "";
        public string[] Columns = ["", "mail"];
        public int Touched;
        public int TableCount => 1;
        public int ColumnCount(int table) => Columns.Length;
        public string TablePhysicalName(int table) => Table;
        public void SetTablePhysicalName(int table, string name) => Table = name;
        public string ColumnPhysicalName(int table, int column) => Columns[column];
        public void SetColumnPhysicalName(int table, int column, string name) => Columns[column] = name;
        public void Touch() => Touched++;
    }

    [Fact]
    public void ApplyPhysicalNamesFillsOnlyEmpty()
    {
        var m = new FakeModel();
        var answer = """
            추천이에요.
            ```json
            {"tables":[{"key":"T1","physical":"tb_member","columns":[{"key":"T1.C1","physical":"member_id"},{"key":"T1.C2","physical":"email"},{"key":"T1.C9","physical":"x"}]}]}
            ```
            """;
        var n = ModelAI.ApplyPhysicalNames(answer, m);
        Assert.Equal(2, n);
        Assert.Equal("tb_member", m.Table);
        Assert.Equal("member_id", m.Columns[0]);
        Assert.Equal("mail", m.Columns[1]);   // 이미 있던 건 그대로
        Assert.Equal(1, m.Touched);
    }

    // Windows 판 추가: 덮어쓰기 · 잘못된 이름 · 형식 오류
    [Fact]
    public void ApplyPhysicalNamesOverwriteAndRejects()
    {
        var m = new FakeModel();
        Assert.Equal(1, ModelAI.ApplyPhysicalNames("""{"tables":[{"key":"T1","columns":[{"key":"T1.C2","physical":"email"}]}]}""", m, overwrite: true));
        Assert.Equal("email", m.Columns[1]);
        Assert.Equal(0, ModelAI.ApplyPhysicalNames("""{"tables":[{"key":"T1","physical":"1bad name"}]}""", m));
        Assert.Equal(0, ModelAI.ApplyPhysicalNames("""{"tables":[{"key":"T1","columns":[{"key":"T1.C1"}]}]}""", m)); // physical 없음 → 전체 무시
        Assert.Equal(0, ModelAI.ApplyPhysicalNames("답에 JSON 이 없어요", m));
        Assert.Equal(1, m.Touched);   // 바뀐 것이 있었던 첫 번째만
    }

    [Fact]
    public void QuickActionsAndFocus()
    {
        var modeling = AssistantAI.QuickActions(modeling: true, sqlConnected: false, [], english: false);
        Assert.StartsWith("논리명을 보고", modeling[0]);
        Assert.Contains("```json", AssistantAI.ExpandQuickAction(modeling[0], modeling: true, english: false));
        Assert.Equal("tb_member 테이블을 설명하고 특이사항을 정리해 줘",
                     AssistantAI.QuickActions(false, true, ["tb_member"])[0]);
        var focus = AssistantAI.Focus("tb_order 봐줘", [member, Order], ["tb_member"]);
        Assert.Equal(["tb_order", "tb_member"], focus.Select(t => t.Name));
        Assert.Equal("assistant|general", AssistantAI.Scope(null, null, ""));
    }
}

public class AppGuideTests
{
    private const string Md = """
        # GodDB 기능 명세
        ## 목차
        - 1. 모델링
        ## 15. 단축키
        | `Ctrl+Enter` | 실행 |
        ## 27. 모델링 (MySQL Workbench 모델과 비슷)
        - 상단 모드에서 모델링을 고른다. SQL 추출, 리버스 엔지니어링.
        ```
        ## not a heading
        ```
        ## 29. 접속 그룹
        - 접속 우클릭 > 그룹으로 이동
        ## 18. 변경 이력
        - 0.9.0 모델링
        """;

    [Fact]
    public void ParseSkipsTocAndChangelog()
    {
        var g = new AppGuide(Md);
        Assert.Equal(["15. 단축키", "27. 모델링 (MySQL Workbench 모델과 비슷)", "29. 접속 그룹"], g.Sections.Select(s => s.Title));
        Assert.Contains("## not a heading", g.Sections[1].Body);
    }

    [Fact]
    public void RelevantSections()
    {
        var g = new AppGuide(Md);
        Assert.StartsWith("27.", g.Relevant("모델링에서 리버스 엔지니어링은 어떻게 해?").First().Title);
        Assert.StartsWith("29.", g.Relevant("접속 그룹을 어떻게 만들어?").First().Title);
        Assert.Empty(g.Relevant("사용방법 알려줘"));
        var ctx = g.ContextText("이거 누가 만든거야", "  Mode: 쿼리");
        Assert.Contains("zio0911", ctx);
        Assert.Contains("단축키", ctx);
    }

    // Windows 판 추가: Core 에 넣은 FEATURES.md 를 읽는다
    [Fact]
    public void BundledFeaturesHasChangelog()
    {
        Assert.NotNull(AppGuide.BundledMarkdown);
        Assert.Contains(ReleaseNotes.Bundled, e => e.Version == "1.0.0");
    }
}

public class ReleaseNotesTests
{
    private const string Md = """
        ## 17. 알려진 제약
        - 없음
        ## 18. 변경 이력

        ### 1.1.0 (개발 중)
        - **트랜잭션** 자동/수동 커밋
        - 문제 보고

        ### 1.0.0
        - 서명 + 공증

        ### 0.9.1
        - 시작 수정
        """;

    [Fact]
    public void ParsesChangelog()
    {
        var e = ReleaseNotes.Parse(Md);
        Assert.Equal(["1.1.0", "1.0.0", "0.9.1"], e.Select(x => x.Version));
        Assert.Equal(["- **트랜잭션** 자동/수동 커밋", "- 문제 보고"], e[0].Lines);
        Assert.Equal(["1.1.0", "1.0.0"], ReleaseNotes.Between(e, "0.9.1", "1.1.0").Select(x => x.Version));
        Assert.True(ReleaseNotes.Compare("1.10.0", "1.9.2") > 0);
        Assert.Equal(["1.0.0", "0.9.1"], ReleaseNotes.Released(e, "1.0.0").Select(x => x.Version));
        Assert.Equal(3, ReleaseNotes.Released(e, "dev").Count);
        Assert.Contains("<b>트랜잭션</b>", ReleaseNotes.Html(e[0]));
    }

    [Fact]
    public void AssistantSeesHistoryForVersionQuestions()
    {
        // Mac 테스트 실행 파일에는 앱 버전이 없어 "dev" 로 돈다 → 같게 맞춘다
        var g = new AppGuide(Md, currentVersion: "dev");
        Assert.True(AppGuide.AsksAboutVersions("1.0 이랑 1.1 뭐가 달라?"));
        Assert.False(AppGuide.AsksAboutVersions("cm_brand 페이징 쿼리 짜줘"));
        Assert.Contains("### 1.1.0", g.ContextText("새 버전에서 뭐가 바뀌었어", ""));
        Assert.DoesNotContain("Release history", g.ContextText("접속 그룹 만드는 법", ""));
    }

    // Windows 판 추가: 새 버전을 처음 켰을 때 창을 띄울지
    [Fact]
    public void UpdatedSince()
    {
        var e = ReleaseNotes.Parse(Md);
        Assert.True(ReleaseNotes.UpdatedSince("0.9.1", "1.0.0", e));
        Assert.False(ReleaseNotes.UpdatedSince(null, "1.0.0", e));
        Assert.False(ReleaseNotes.UpdatedSince("1.0.0", "1.0.0", e));
        Assert.False(ReleaseNotes.UpdatedSince("0.9.1", "dev", e));
    }
}
