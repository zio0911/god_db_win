using GodDB.Core.Ai;

namespace GodDB.Core.Tests;

// Mac Tests/GodDBCoreTests/AISettingsTests.swift (+ Windows 판: 모델 확인 요청 모양)
public class AISettingsTests
{
    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("https://127.5.6.7")]
    [InlineData("http://[::1]:11434")]
    [InlineData("http://LOCALHOST:11434/")]
    public void OnlyLoopbackOllamaIsAllowed(string ok) => Assert.True(AISettings.IsLocalUrl(ok), ok);

    [Theory]
    [InlineData("http://10.0.0.5:11434")]
    [InlineData("http://gpu.example.com:11434")]
    [InlineData("http://localhost.evil.com")]
    [InlineData("http://127.0.0.1.nip.io")]
    [InlineData("ftp://localhost")]
    [InlineData("localhost:11434")]
    [InlineData("http://128.0.0.1")]
    [InlineData("http://127.0.0.256")]
    [InlineData("")]
    public void RemoteOllamaIsRefused(string bad) => Assert.False(AISettings.IsLocalUrl(bad), bad);

    [Fact]
    public async Task ClientRefusesRemoteBeforeSending()
    {
        var ct = TestContext.Current.CancellationToken;
        var sent = 0;
        var h = new FakeOllamaHandler((_, _) => { sent++; return FakeOllamaHandler.Json(FakeOllamaHandler.Tags); });
        var c = new OllamaClient(new Uri("http://10.1.2.3:11434"), h);
        var e = await Assert.ThrowsAsync<OllamaException>(() => c.ModelsAsync(ct));
        Assert.Equal(OllamaErrorKind.RemoteBlocked, e.Kind);
        Assert.Contains("10.1.2.3", e.Message);
        await Assert.ThrowsAsync<OllamaException>(() => c.ChatTextAsync("m", [new("user", "hi")], ct: ct));
        await Assert.ThrowsAsync<OllamaException>(() => c.PingAsync("m", 5, ct));
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task PingSendsShortRequestAndRejectsEmptyAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new FakeOllamaHandler((_, _) => FakeOllamaHandler.Json("""{"message":{"content":"OK"},"done":true}"""));
        await new OllamaClient(null, h).PingAsync("m", 5, ct);
        var body = h.Requests[0].Body!;
        Assert.Contains("\"stream\":false", body);
        Assert.Contains("\"num_predict\":256", body);
        Assert.Contains("Reply with OK", body);
        var empty = new FakeOllamaHandler((_, _) => FakeOllamaHandler.Json("""{"message":{"content":"  "},"done":true}"""));
        var e = await Assert.ThrowsAsync<OllamaException>(() => new OllamaClient(null, empty).PingAsync("m", 5, ct));
        Assert.Contains("빈 답을 돌려줬어요", e.Message);
        var broken = new FakeOllamaHandler((_, _) => FakeOllamaHandler.Json("""{"error":"token repeat limit reached"}""", System.Net.HttpStatusCode.InternalServerError));
        var health = await ModelProbe.CheckAsync("m", new OllamaClient(null, broken), new ModelHealthCache(), ct: ct);
        Assert.Equal(new ModelHealth.Failed("token repeat limit reached"), health);
    }
}
