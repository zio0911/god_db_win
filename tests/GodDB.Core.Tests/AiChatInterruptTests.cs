using GodDB.Core.Ai;

namespace GodDB.Core.Tests;

// Mac 1.4.3 #177 (커밋 0b7ad46): 답하는 중에 보낸 글이 사라지던 문제 — 답을 멈추고 새 글 먼저
public class AiChatInterruptTests
{
    private static Task<List<OllamaMessage>> Build(IReadOnlyList<(string Role, string Content)> history, CancellationToken _) =>
        Task.FromResult(history.Select(x => new OllamaMessage(x.Role, x.Content)).ToList());

    [Fact]
    public async Task NewQuestionStopsRunningAnswerAndIsAnswered()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new DiagramChatStore(Path.Combine(Path.GetTempPath(), $"goddb-aichat-{Guid.NewGuid()}"));
        try
        {
            var firstStarted = new TaskCompletionSource();
            var calls = 0;
            var h = new FakeOllamaHandler((r, _) =>
            {
                if (r.RequestUri!.AbsolutePath == "/api/tags") return FakeOllamaHandler.Json(FakeOllamaHandler.Tags);
                if (Interlocked.Increment(ref calls) == 1)
                {
                    // 첫 답: 한 조각 보내고 멈춘 채 기다린다 (취소될 때까지)
                    firstStarted.TrySetResult();
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new HangingStream(System.Text.Encoding.UTF8.GetBytes("""{"message":{"content":"첫 답 쓰는 중"}}""" + "\n"))),
                    };
                }
                return FakeOllamaHandler.Json("""{"message":{"content":"둘째 답"}}""" + "\n" + """{"done":true}""" + "\n");
            });
            var chat = new AiChat("s", store) { ClientFactory = () => new OllamaClient(null, h), ModelSetting = () => "" };
            var first = chat.AskAsync("첫 질문", Build, "AI 어시스턴트 실패");
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.True(chat.Running);
            // 답하는 중에 새 글 → 앞 답은 멈추고 새 글을 바로 보낸다 (예전에는 새 글이 버려졌다)
            await chat.AskAsync("둘째 질문", Build, "AI 어시스턴트 실패");
            await first.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.False(chat.Running);
            Assert.Equal(4, chat.Messages.Count);
            Assert.Equal(["첫 질문", "둘째 질문", "둘째 답"], new[] { chat.Messages[0].Text, chat.Messages[2].Text, chat.Messages[3].Text });
            // 멈춘 답은 실패가 아니라 '(취소했어요)'
            Assert.EndsWith("(취소했어요)", chat.Messages[1].Text);
            Assert.False(chat.Messages[1].Failed);
            // 멈춘 작업이 늦게 끝나도 새 작업 번호는 그대로
            Assert.Equal(2, chat.Generation);
        }
        finally { try { Directory.Delete(store.Directory, true); } catch { } }
    }

    [Fact]
    public async Task StartingNewConversationWhileAnsweringDoesNotTouchNewOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new DiagramChatStore(Path.Combine(Path.GetTempPath(), $"goddb-aichat-{Guid.NewGuid()}"));
        try
        {
            var started = new TaskCompletionSource();
            var h = new FakeOllamaHandler((r, _) =>
            {
                if (r.RequestUri!.AbsolutePath == "/api/tags") return FakeOllamaHandler.Json(FakeOllamaHandler.Tags);
                started.TrySetResult();
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StreamContent(new HangingStream(System.Text.Encoding.UTF8.GetBytes("""{"message":{"content":"쓰는 중"}}""" + "\n"))),
                };
            });
            var chat = new AiChat("s2", store) { ClientFactory = () => new OllamaClient(null, h), ModelSetting = () => "" };
            var first = chat.AskAsync("질문", Build, "AI 어시스턴트 실패");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            chat.StartNew();
            await first.WaitAsync(TimeSpan.FromSeconds(10), ct);
            // 답 말풍선은 id 로 고치므로 새 대화에는 아무것도 들어가지 않는다
            Assert.Empty(chat.Messages);
            Assert.False(chat.Running);
        }
        finally { try { Directory.Delete(store.Directory, true); } catch { } }
    }

    /// 앞부분을 준 뒤 취소될 때까지 기다리는 응답 본문
    private sealed class HangingStream(byte[] head) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < head.Length)
            {
                var n = Math.Min(buffer.Length, head.Length - _pos);
                head.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
