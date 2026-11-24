using GodDB.Core.Ai;
using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

public class DiagramAITests
{
    internal readonly TableMeta member = new("member", comment: "회원",
        columns: [new ColumnMeta("member_id", "bigint", false, null, "", "PRI", "회원 번호", 1),
                  new ColumnMeta("name", "varchar(50)", true, null, "", "", "", 2)],
        indexes: [new IndexMeta("PRIMARY", true, ["member_id"], primary: true)]);

    internal TableMeta Order => new("orders", comment: "",
        columns: [new ColumnMeta("order_id", "bigint", false, null, "", "PRI", "", 1),
                  new ColumnMeta("member_id", "bigint", false, null, "", "", "", 2)],
        indexes: [new IndexMeta("PRIMARY", true, ["order_id"], primary: true)],
        foreignKeys: [new ForeignKeyMeta("fk_member", "orders", ["member_id"], "member", ["member_id"])]);

    [Fact]
    public void SchemaTextHasStructureAndRelations()
    {
        var guess = new ForeignKeyMeta("guess", "orders", ["x"], "member", ["member_id"]);
        var text = DiagramAI.SchemaText([member, Order], [guess]);
        Assert.Contains("TABLE member  -- 회원", text);
        Assert.Contains("member_id bigint PK NOT NULL  -- 회원 번호", text);
        Assert.Contains("orders(member_id) -> member(member_id)", text);
        Assert.Contains("guessed", text);
        Assert.Contains("orders(x) -> member(member_id)", text);
    }

    [Fact]
    public void SchemaTextShrinksWhenHuge()
    {
        var cols = Enumerable.Range(1, 80).Select(i => new ColumnMeta($"column_{i}_with_a_long_name", "varchar(255)", true, null, "", "",
                                                                       new string('c', 60), i)).ToList();
        var tables = Enumerable.Range(1, 60).Select(i => new TableMeta($"t{i}", columns: cols)).ToList();
        var text = DiagramAI.SchemaText(tables, []);
        Assert.True(text.Length < DiagramAI.MaxSchemaChars + 2_000);
        Assert.Contains("more columns", text);
    }

    [Fact]
    public void MessagesPutSchemaFirstAndCleanThink()
    {
        var m = DiagramAI.Messages(DatabaseKind.Postgres, "S", [("user", "Q")], english: false);
        Assert.Equal("system", m.First().Role);
        Assert.EndsWith("S", m[1].Content);
        Assert.Equal("Q", m.Last().Content);
        Assert.Equal("답", DiagramAI.Clean("<think>abc</think>\n답"));
        Assert.Equal("", DiagramAI.Clean("<think>아직 생각"));
    }
}

/// 실제 로컬 Ollama 로 관계 분석 + 이어지는 질문 (GODDB_OLLAMA_URL 이 있을 때만, 수십 초 걸림)
/// 모델은 GODDB_OLLAMA_MODEL, 없으면 설치된 첫 모델.
public class DiagramAILiveTests
{
    [Fact]
    public async Task AnalyzesAndAnswersFollowUp()
    {
        var (client, model) = await LiveOllama.ClientAsync();
        var b = new DiagramAITests();
        var schema = DiagramAI.SchemaText([b.member, b.Order], []);
        async Task<string> Ask(List<(string Role, string Content)> history) =>
            DiagramAI.Clean(await client.ChatTextAsync(model, DiagramAI.Messages(DatabaseKind.Mysql, schema, history, english: false),
                                                       ct: TestContext.Current.CancellationToken));
        var history = new List<(string Role, string Content)> { ("user", DiagramAI.AnalysisQuestion(english: false)) };
        var first = await Ask(history);
        TestContext.Current.TestOutputHelper?.WriteLine("ANALYSIS:\n" + first);
        Assert.True(first.Contains("member") && first.Contains("orders"));
        history.Add(("assistant", first));
        history.Add(("user", "회원별 주문 수를 구하는 SQL 만 짧게 줘"));
        var second = await Ask(history);
        TestContext.Current.TestOutputHelper?.WriteLine("FOLLOW-UP:\n" + second);
        Assert.True(second.ToUpperInvariant().Contains("JOIN") || second.ToUpperInvariant().Contains("GROUP BY"));
    }
}

/// 실제 Ollama 테스트 공통: GODDB_OLLAMA_URL 이 없으면 건너뛴다.
internal static class LiveOllama
{
    public static async Task<(OllamaClient Client, string Model)> ClientAsync()
    {
        var url = Environment.GetEnvironmentVariable("GODDB_OLLAMA_URL");
        if (string.IsNullOrEmpty(url)) Assert.Skip("GODDB_OLLAMA_URL 없음 — 로컬 Ollama 테스트는 건너뜀");
        var client = new OllamaClient(new Uri(url!));
        var status = await client.StatusAsync(TestContext.Current.CancellationToken);
        Assert.True(status.IsRunning, "Ollama 가 실행 중이 아님");
        var model = Environment.GetEnvironmentVariable("GODDB_OLLAMA_MODEL") is { Length: > 0 } m ? m : status.RunningModels.FirstOrDefault()?.Name ?? "";
        if (model.Length == 0) Assert.Skip("설치된 Ollama 모델 없음");
        return (client, model);
    }
}

public class DiagramChatStoreTests
{
    /// 실제 키 저장소 대신 메모리 키 (Mac 1.4.1 부터 대화 파일은 GDBENC1 암호화)
    private static readonly GodDB.Core.Platform.LocalDataCrypto Crypto = new(new GodDB.Core.Platform.StaticLocalDataKeyProvider());

    private static DiagramChatStore Store() =>
        new(Path.Combine(Path.GetTempPath(), $"goddb-chat-{Guid.NewGuid()}"), crypto: Crypto);

    [Fact]
    public void SavesLoadsAndDeletes()
    {
        var s = Store();
        try
        {
            var scope = DiagramChatStore.Scope(Guid.NewGuid(), "shop", ["b", "A"]);
            Assert.EndsWith("|shop|a,b", scope);
            var c1 = new DiagramConversation(created: DateTime.UtcNow.AddSeconds(-100))
            {
                Messages = [new("user", "관계 분석\n자세히"), new("assistant", "답")],
                Updated = DateTime.UtcNow.AddSeconds(-100),
            };
            var c2 = new DiagramConversation { Messages = [new("user", "두 번째")] };
            s.Save(c1, scope);
            s.Save(c2, scope);
            s.Save(new DiagramConversation(), scope);   // 빈 대화는 저장 안 함
            var list = s.Load(scope);
            Assert.Equal([c2.Id, c1.Id], list.Select(c => c.Id));
            Assert.Equal("관계 분석", list[1].Title);
            Assert.Empty(s.Load("other"));
            s.Delete(c2.Id, scope);
            Assert.Equal([c1.Id], s.Load(scope).Select(c => c.Id));
            s.DeleteAll(scope);
            Assert.Empty(s.Load(scope));
        }
        finally { try { Directory.Delete(s.Directory, true); } catch { } }
    }

    [Fact]
    public void DropsConversationsOlderThan90Days()
    {
        var s = Store();
        try
        {
            var old = new DiagramConversation(created: DateTime.UtcNow.AddDays(-91))
            {
                Messages = [new("user", "old")],
                Updated = DateTime.UtcNow.AddDays(-91),
            };
            var recent = new DiagramConversation(created: DateTime.UtcNow.AddDays(-89))
            {
                Messages = [new("user", "recent")],
                Updated = DateTime.UtcNow.AddDays(-89),
            };
            s.Save(old, "x");
            s.Save(recent, "x");
            s.Save(old, "y");
            s.PruneAll();
            Assert.Equal(["recent"], s.Load("x").Select(c => c.Title));
            Assert.Empty(s.Load("y"));
        }
        finally { try { Directory.Delete(s.Directory, true); } catch { } }
    }

    // Windows 판 추가: 파일 권한(본인만)과 Mac 과 같은 JSON 모양 (암호화 봉투 안)
    [Fact]
    public void FileIsUserOnlyAndUsesMacJsonShape()
    {
        var s = Store();
        try
        {
            var c = new DiagramConversation { Messages = [new("user", "q")] };
            s.Save(c, "scope");
            var path = s.FilePath("scope");
            Assert.Matches(@"[0-9a-f]{24}\.json$", path);
            Assert.True(GodDB.Core.Platform.LocalDataCrypto.IsEncrypted(File.ReadAllBytes(path)));
            var json = Crypto.ReadText(path)!;
            Assert.Matches(@"^\[\{""created"":""\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ"",""id"":""[0-9a-f-]{36}"",""messages"":\[\{""failed"":false,""role"":""user"",""text"":""q""\}\],""updated"":", json);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { try { Directory.Delete(s.Directory, true); } catch { } }
    }
}
