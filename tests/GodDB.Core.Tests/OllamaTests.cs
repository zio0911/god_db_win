using System.Net;
using System.Text;
using GodDB.Core.Ai;

namespace GodDB.Core.Tests;

// Windows 판 추가: Ollama 클라이언트 (가짜 HTTP 응답으로 NDJSON 스트림 · 모델 목록 · 오류 확인)

/// 요청을 기록하고 정해 둔 응답을 돌려주는 가짜 HTTP
internal sealed class FakeOllamaHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// 한 바이트씩 흘려보내는 응답 (줄 · UTF-8 글자가 중간에 끊겨 와도 맞게 읽는지)
    public static HttpResponseMessage Trickle(string text, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StreamContent(new TrickleStream(Encoding.UTF8.GetBytes(text))) };

    public const string Tags = """{"models":[{"name":"qwen3:30b","size":18000000000,"details":{"parameter_size":"30.5B"}},{"name":"tiny"}]}""";
}

internal sealed class TrickleStream(byte[] data) : Stream
{
    private int _pos;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos >= data.Length || count == 0) return 0;
        buffer[offset] = data[_pos++];
        return 1;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public class OllamaClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<ChatChunk>> Collect(OllamaClient c, CancellationToken ct = default)
    {
        var list = new List<ChatChunk>();
        await foreach (var chunk in c.ChatAsync("m", [new("user", "hi")], ct: ct)) list.Add(chunk);
        return list;
    }

    [Fact]
    public async Task StreamsThinkingAndContentSeparately()
    {
        var ndjson = string.Join("\n",
            """{"model":"m","message":{"role":"assistant","content":"","thinking":"생각"},"done":false}""",
            "",
            "not json",
            """{"message":{"role":"assistant","content":"안녕"},"done":false}""",
            """{"message":{"role":"assistant","content":"하세요"},"done":false}""",
            """{"message":{"role":"assistant","content":""},"done":true,"total_duration":1}""",
            """{"message":{"role":"assistant","content":"done 뒤는 안 읽음"},"done":false}""") + "\n";
        var h = new FakeOllamaHandler((_, _) => FakeOllamaHandler.Trickle(ndjson));
        var chunks = await Collect(new OllamaClient(new Uri("http://localhost:11434"), h), Ct);
        Assert.Equal<ChatChunk>([new ChatChunk.Thinking("생각"), new ChatChunk.Content("안녕"), new ChatChunk.Content("하세요")], chunks);

        var (req, body) = h.Requests.Single();
        Assert.Equal("http://localhost:11434/api/chat", req.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, req.Method);
        // think 옵션은 보내지 않는다
        Assert.Equal("""{"model":"m","messages":[{"role":"user","content":"hi"}],"stream":true,"options":{"temperature":0.2}}""", body);
    }

    [Fact]
    public async Task StreamWithoutDoneEndsAtEof()
    {
        var h = new FakeOllamaHandler((_, _) => FakeOllamaHandler.Json("""{"message":{"content":"a"}}""" + "\n" + """{"message":{"content":"b"}}"""));
        var text = await new OllamaClient(new Uri("http://127.0.0.1:11434/"), h).ChatTextAsync("m", [new("user", "q")], ct: Ct);
        Assert.Equal("ab", text);
    }

    [Fact]
    public async Task ErrorLineAndHttpErrorThrow()
    {
        var h1 = new FakeOllamaHandler((_, _) => FakeOllamaHandler.Json("""{"message":{"content":"x"}}""" + "\n" + """{"error":"model not found"}"""));
        var e1 = await Assert.ThrowsAsync<OllamaException>(() => Collect(new OllamaClient(null, h1), Ct));
        Assert.Equal(OllamaErrorKind.Http, e1.Kind);
        Assert.Equal(500, e1.StatusCode);
        Assert.Equal("Ollama 오류 (HTTP 500): model not found", e1.Message);

        var h2 = new FakeOllamaHandler((_, _) => FakeOllamaHandler.Json("{\"error\":\n\"no such model\"}", HttpStatusCode.NotFound));
        var e2 = await Assert.ThrowsAsync<OllamaException>(() => Collect(new OllamaClient(null, h2), Ct));
        Assert.Equal(404, e2.StatusCode);
        Assert.Equal("{\"error\":\"no such model\"}", e2.Body);
    }

    [Fact]
    public async Task ModelsAndStatus()
    {
        var h = new FakeOllamaHandler((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/api/tags")
            ? FakeOllamaHandler.Json(FakeOllamaHandler.Tags) : new HttpResponseMessage(HttpStatusCode.NotFound));
        var c = new OllamaClient(new Uri("http://localhost:11434/sub"), h);
        var models = await c.ModelsAsync(Ct);
        Assert.Equal(["qwen3:30b", "tiny"], models.Select(m => m.Name));
        Assert.Equal(18.0, models[0].SizeGB, 3);
        Assert.Equal("30.5B", models[0].ParameterSize);
        Assert.Equal("", models[1].ParameterSize);
        Assert.Equal("http://localhost:11434/sub/api/tags", h.Requests[0].Request.RequestUri!.ToString());
        var status = await c.StatusAsync(Ct);
        Assert.True(status.IsRunning);
        Assert.Equal(2, status.RunningModels.Count);
        Assert.Equal("qwen3:30b", TuningSettings.ResolveModel(status.RunningModels, ""));
        Assert.Equal("mine", TuningSettings.ResolveModel(status.RunningModels, "mine"));
        Assert.Equal("", TuningSettings.ResolveModel([], ""));
    }

    [Fact]
    public async Task StatusWhenServerDown()
    {
        var h = new FakeOllamaHandler((_, _) => throw new HttpRequestException("connection refused"));
        var status = await new OllamaClient(null, h).StatusAsync(Ct);
        Assert.False(status.IsRunning);
        Assert.Equal(OllamaClient.IsInstalled, status is OllamaStatus.InstalledNotRunning);
    }

    [Fact]
    public void ParseLineIgnoresGarbage()
    {
        Assert.Null(OllamaClient.ParseLine(""));
        Assert.Null(OllamaClient.ParseLine("[1,2]"));
        Assert.Null(OllamaClient.ParseLine("{bad"));
        var l = OllamaClient.ParseLine("""{"message":{"content":"a","thinking":"t"},"done":true}""")!;
        Assert.Equal(("a", "t", true, (string?)null), (l.Content, l.Thinking, l.Done, l.Error));
    }

    [Fact]
    public void LocalUrlCheck()
    {
        Assert.True(TuningSettings.IsLocalUrl("http://localhost:11434"));
        Assert.True(TuningSettings.IsLocalUrl("http://127.0.0.1:11434/"));
        Assert.True(TuningSettings.IsLocalUrl("http://[::1]:11434"));
        Assert.False(TuningSettings.IsLocalUrl("http://10.0.0.5:11434"));
        Assert.False(TuningSettings.IsLocalUrl("http://ollama.example.com"));
        Assert.False(TuningSettings.IsLocalUrl("not a url"));
        Assert.Contains("이 PC 밖의 주소", TuningSettings.NonLocalWarning);
    }
}

public class AiChatTests
{
    private static DiagramChatStore Store() => new(Path.Combine(Path.GetTempPath(), $"goddb-aichat-{Guid.NewGuid()}"));

    [Fact]
    public async Task AsksStreamsAndPersists()
    {
        var store = Store();
        try
        {
            var h = new FakeOllamaHandler((r, _) => r.RequestUri!.AbsolutePath == "/api/tags"
                ? FakeOllamaHandler.Json(FakeOllamaHandler.Tags)
                : FakeOllamaHandler.Trickle("""{"message":{"thinking":"음"}}""" + "\n" + """{"message":{"content":"<think>x</think>답"}}""" + "\n" + """{"done":true}""" + "\n"));
            var chat = new AiChat("scope", store) { ClientFactory = () => new OllamaClient(null, h), ModelSetting = () => "" };
            string? logged = null;
            (string, int, string)? answered = null;
            var hooks = new AiChat.Hooks
            {
                LogMessage = (model, first) => first ? $"start {model}" : null,
                Log = (line, _) => logged = line,
                Answered = (f, s, m) => answered = (f, s, m),
            };
            await chat.AskAsync("  질문  ", (history, _) => Task.FromResult(DiagramAI.Messages(DatabaseKind.Mysql, "S", history, false)),
                                "다이어그램 AI 분석 실패", hooks);
            Assert.False(chat.Running);
            Assert.Equal(["질문", "답"], chat.Messages.Select(m => m.Text));
            Assert.Equal(1, chat.ThinkingChars);
            Assert.Equal("start qwen3:30b", logged);
            Assert.Equal("다이어그램 AI 분석", answered?.Item1);
            Assert.Contains("\"model\":\"qwen3:30b\"", h.Requests.Last().Body);

            // 다시 열면 마지막 대화가 보인다
            var reopened = new AiChat("scope", store);
            Assert.Equal(["질문", "답"], reopened.Messages.Select(m => m.Text));
            Assert.Single(reopened.History);
            reopened.StartNew();
            Assert.Empty(reopened.Messages);
            reopened.DeleteAll();
            Assert.Empty(new AiChat("scope", store).History);
        }
        finally { try { Directory.Delete(store.Directory, true); } catch { } }
    }

    [Fact]
    public async Task FailureMarksAnswerAndIsExcludedFromHistory()
    {
        var store = Store();
        try
        {
            var h = new FakeOllamaHandler((_, _) => throw new HttpRequestException("down"));
            var chat = new AiChat("s", store) { ClientFactory = () => new OllamaClient(null, h) };
            await chat.AskAsync("q", (_, _) => Task.FromResult(new List<OllamaMessage>()), "AI 어시스턴트 실패",
                                new AiChat.Hooks { Failed = (e, ctx, feature) => $"{feature}: {e.Message}" });
            Assert.True(chat.Messages[1].Failed);
            Assert.Equal("AI 어시스턴트: Ollama 가 실행 중이 아니에요. 설정 > AI 에서 확인해 주세요.", chat.Messages[1].Text);
            Assert.Equal("AI 어시스턴트", AiChat.FeatureName("AI 어시스턴트 실패"));
        }
        finally { try { Directory.Delete(store.Directory, true); } catch { } }
    }
}

public class AiTextTests
{
    [Fact]
    public void FirstSqlAndBlocks()
    {
        var answer = "## 제목\n설명\n- 항목\n\n```sql\nSELECT 1;\n```\n---\n```\nplain\n```";
        Assert.Equal("SELECT 1;", AiText.FirstSql(answer));
        Assert.Null(AiText.FirstSql("```sql\n   \n```"));
        Assert.Null(AiText.FirstSql("no sql"));
        Assert.Equal<AiText.Block>(
            [new AiText.Block.Heading("제목", 2), new AiText.Block.Paragraph("설명\n• 항목"), new AiText.Block.Code("SELECT 1;"),
             new AiText.Block.Rule(), new AiText.Block.Code("plain")],
            AiText.Blocks(answer));
    }
}
