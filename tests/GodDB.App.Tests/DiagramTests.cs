using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using LogLevel = GodDB.Core.Audit.LogLevel;
using GodDB.App.Services;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Ai;
using GodDB.Core.Schema;

namespace GodDB.App.Tests;

/// ER 다이어그램 창 (Mac DiagramView · DiagramAIPanel) — 화면은 띄우지 않고(headless) 상태와 동작만 본다
public class DiagramTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static ColumnMeta Col(string n, string t, string comment = "", bool pk = false) =>
        new(n, t, !pk, columnKey: pk ? "PRI" : "", comment: comment);

    private static TableMeta Table(string name, string comment, IEnumerable<ColumnMeta> cols, params ForeignKeyMeta[] fks)
    {
        var list = cols.ToList();
        return new TableMeta(name, comment, list, [new IndexMeta("PRIMARY", true, [list[0].Name])], fks);
    }

    /// 회원 ← 주문 ← 주문상품 (진짜 FK), 주문상품.product_id → 상품 (추정), 공지 (관계 없음)
    internal static List<TableMeta> Shop() =>
    [
        Table("tb_member", "회원", [Col("member_id", "bigint", "회원 ID", pk: true), Col("email", "varchar(100)")]),
        Table("tb_order", "주문", [Col("order_id", "bigint", "주문 ID", pk: true), Col("member_id", "bigint", "회원 ID")],
              new ForeignKeyMeta("fk_order_member", "tb_order", ["member_id"], "tb_member", ["member_id"])),
        Table("tb_order_item", "", [Col("item_id", "bigint", pk: true), Col("order_id", "bigint"), Col("product_id", "bigint"), Col("parent_item_id", "bigint")],
              new ForeignKeyMeta("fk_item_order", "tb_order_item", ["order_id"], "tb_order", ["order_id"]),
              new ForeignKeyMeta("fk_item_parent", "tb_order_item", ["parent_item_id"], "tb_order_item", ["item_id"])),
        Table("tb_product", "상품", [Col("product_id", "bigint", "상품 ID", pk: true), Col("name", "varchar(200)", "상품명")]),
        Table("tb_notice", "", [Col("notice_no", "int", pk: true), Col("title", "varchar(200)")]),
    ];

    private static (AppState App, ConnectionProfile Profile) ConnectedApp(List<TableMeta>? tables = null)
    {
        var app = new TestEnv().NewApp();
        var p = TestEnv.Profile("local");
        app.Profiles.Add(p);
        TestEnv.FakeConnect(app, p.Id);
        app.UpdateConnection(p.Id, c => c.Tables = tables ?? Shop());
        app.SelectedProfileId = p.Id;
        return (app, p);
    }

    private static string[] Names(DiagramDoc d) => [.. d.Tables.Select(t => t.Name)];

    [Fact]
    public Task MakeDiagram_FocusMultiAndWholeSchema() => UiThread.Run(() =>
    {
        var (app, p) = ConnectedApp();

        // 테이블에서 열기: 그 테이블 + 바로 이어진 테이블 (부모 · 자식)
        var focus = app.Diagrams[app.MakeDiagram("tb_order")!.Value];
        Assert.Equal(["tb_order", "tb_member", "tb_order_item"], Names(focus));
        Assert.Equal(["tb_order"], focus.Highlighted);
        Assert.Equal("tb_order", focus.Focus);
        Assert.Equal("local / shop / tb_order", focus.Title);
        Assert.Empty(focus.Inferred);   // tb_product 가 없어서 추정 관계는 그리지 않는다
        Assert.Equal(DiagramChatStore.Scope(p.Id, "shop", Names(focus)), focus.ChatScope);

        // 여러 개 고르기: 고른 테이블은 관계 없어도 모두, 이어진 테이블(진짜 FK + 추정)도 함께
        var multi = app.Diagrams[app.MakeDiagram(new HashSet<string> { "tb_order_item", "tb_notice" })!.Value];
        Assert.Equal(["tb_order_item", "tb_notice", "tb_order", "tb_product"], Names(multi));
        Assert.Equal(new HashSet<string> { "tb_order_item", "tb_notice" }, multi.Highlighted.ToHashSet());
        Assert.Null(multi.Focus);
        Assert.Equal("local / shop / 선택 2개", multi.Title);
        var inferred = Assert.Single(multi.Inferred);
        Assert.Equal(("tb_order_item", "tb_product", "~tb_order_item.product_id"), (inferred.Table, inferred.ReferencedTable, inferred.Name));

        // 스키마 전체
        var whole = app.Diagrams[app.MakeDiagram((string?)null)!.Value];
        Assert.Equal(5, whole.Tables.Count);
        Assert.Empty(whole.Highlighted);
        Assert.Equal("local / shop", whole.Title);
        Assert.Single(whole.Inferred);
        Assert.Equal(3, app.Diagrams.Count);   // 창마다 따로 (여러 개 동시에)

        // 연결 전이면 만들지 않는다
        var empty = new TestEnv().NewApp();
        Assert.Null(empty.MakeDiagram((string?)null));
    });

    [Fact]
    public Task MakeDiagram_LimitsTo200Tables() => UiThread.Run(() =>
    {
        var many = Enumerable.Range(0, 230).Select(i => Table($"t{i:000}", "", [Col($"c{i}_pk", "int", pk: true)])).ToList();
        var (app, _) = ConnectedApp(many);
        var doc = app.Diagrams[app.MakeDiagram((string?)null)!.Value];
        Assert.Equal(200, doc.Tables.Count);
    });

    [Fact]
    public void NameModes_LogicalNameAndLabels()
    {
        Assert.Null(DiagramNames.LogicalName(""));
        Assert.Equal("회원", DiagramNames.LogicalName("  회원  \n두 번째 줄"));
        Assert.Equal(new string('가', 40) + "…", DiagramNames.LogicalName(new string('가', 50)));
        Assert.Equal(("tb_member", null, false), DiagramNames.Label(DiagramNameMode.Physical, "tb_member", "회원"));
        Assert.Equal(("회원", null, false), DiagramNames.Label(DiagramNameMode.Logical, "tb_member", "회원"));
        Assert.Equal(("tb_x", null, true), DiagramNames.Label(DiagramNameMode.Logical, "tb_x", ""));
        Assert.Equal(("회원", "tb_member", false), DiagramNames.Label(DiagramNameMode.Both, "tb_member", "회원"));
        Assert.Equal(("tb_x", null, true), DiagramNames.Label(DiagramNameMode.Both, "tb_x", ""));
        Assert.Equal(DiagramNameMode.Both, DiagramNames.Parse("both"));
        Assert.Equal(DiagramNameMode.Physical, DiagramNames.Parse("??"));
    }

    private static DiagramDoc Doc(IReadOnlyList<TableMeta> tables, IReadOnlySet<string>? highlighted = null) => new()
    {
        Title = "sample / shop",
        Tables = tables,
        Highlighted = highlighted ?? new HashSet<string>(),
        Inferred = DiagramLayout.InferredRelations(tables),
        ChatScope = "test-" + Guid.NewGuid(),
    };

    [Fact]
    public void ViewModel_RelationsLayoutZoomAndDrag()
    {
        var vm = new DiagramViewModel(Doc(Shop()), DiagramNameMode.Physical);
        // 진짜 FK 3개 (자기 참조 포함), 추정 1개
        Assert.Equal(3, vm.RealRelations.Count);
        Assert.Equal(4, vm.Relations.Count);
        Assert.Equal("테이블 5개 · 관계 3개", vm.CountText);

        // 부모는 왼쪽, 자식은 오른쪽, 관계 없는 테이블은 아래
        Assert.True(vm.Frames["tb_member"].X < vm.Frames["tb_order"].X);
        Assert.True(vm.Frames["tb_order"].X < vm.Frames["tb_order_item"].X);
        var graphBottom = new[] { "tb_member", "tb_order", "tb_order_item", "tb_product" }.Max(n => vm.Frames[n].MaxY);
        Assert.True(vm.Frames["tb_notice"].Y >= graphBottom);

        // 추정 끄기 → tb_product 는 관계 없는 테이블로 아래로
        vm.ShowInferred = false;
        Assert.Equal(3, vm.Relations.Count);
        Assert.Empty(vm.InferredColumns("tb_order_item"));
        Assert.True(vm.Frames["tb_product"].Y >= vm.Frames["tb_order_item"].MaxY);
        vm.ShowInferred = true;
        Assert.Contains("product_id", vm.InferredColumns("tb_order_item"));

        // 마우스 올리기: 그 테이블과 이어진 카드만
        vm.Hovered = "tb_order";
        Assert.True(vm.IsHighlighted("tb_member"));
        Assert.True(vm.IsHighlighted("tb_order_item"));
        Assert.False(vm.IsHighlighted("tb_notice"));
        vm.Hovered = null;
        Assert.False(vm.IsHighlighted("tb_order"));

        // 컬럼 행 위치: 머리 + 행 가운데 (화면 좌표는 여백 40 포함)
        var f = vm.Frame("tb_order")!.Value;
        Assert.Equal(f.Y + DiagramLayout.HeaderHeight + DiagramLayout.RowHeight * 1.5 + 4, vm.RowY("tb_order", "MEMBER_ID"));

        // 확대/축소 30~200%
        for (var i = 0; i < 30; i++) vm.ZoomIn();
        Assert.Equal(2, vm.Zoom);
        for (var i = 0; i < 30; i++) vm.ZoomOut();
        Assert.Equal(0.3, vm.Zoom);
        Assert.Equal("30%", vm.ZoomText);

        // 화면에 맞추기: 최대 100%, 최소 30%, 1% 단위 내림
        vm.Fit(new Size(10000, 10000));
        Assert.Equal(1, vm.Zoom);
        var c = vm.ContentSize;
        vm.Fit(new Size(c.Width / 2, c.Height));
        Assert.Equal(Math.Floor(50.0) / 100, vm.Zoom, 2);
        vm.Fit(new Size(10, 10));
        Assert.Equal(0.3, vm.Zoom);

        // 끌기: 0 아래로는 안 감, 자동 정렬로 되돌림
        var moved = 0;
        vm.LayoutChanged += () => moved++;
        vm.Move("tb_notice", -50, 12);
        Assert.Equal((0d, 12d), (vm.Frames["tb_notice"].X, vm.Frames["tb_notice"].Y));
        vm.Relayout();
        Assert.True(vm.Frames["tb_notice"].Y >= graphBottom);
        Assert.Equal(2, moved);

        // 둘 다 모드는 카드가 넓다 (선택은 기억된다)
        vm.NameMode = DiagramNameMode.Both;
        Assert.Equal(320, vm.Frames["tb_member"].Width);
        Assert.Equal(DiagramNameMode.Both, DiagramNames.Saved);
        vm.NameMode = DiagramNameMode.Physical;
        Assert.Equal(DiagramNameMode.Physical, DiagramNames.Saved);
    }

    [Fact]
    public Task Window_ConstructsHeadlessly() => UiThread.Run(() =>
    {
        var (app, _) = ConnectedApp();
        var many = Shop();
        many[4] = Table("tb_notice", "", Enumerable.Range(0, 30).Select(i => Col(i == 0 ? "notice_no" : $"c{i}", "int", pk: i == 0)));
        app.UpdateConnection(app.SelectedProfileId!.Value, c => c.Tables = many);
        var id = app.MakeDiagram(new HashSet<string> { "tb_order_item", "tb_notice" })!.Value;

        // [WindowFor("diagram")] 로 메인 창이 찾는다
        var w = Assert.IsType<DiagramWindow>(SheetRegistry.CreateWindow(WindowKinds.Diagram, app, id));
        w.Show();
        Pump();
        Assert.Equal("ER 다이어그램 · local / shop / 선택 2개", w.Title);
        Assert.NotNull(w.Canvas);
        Assert.Equal(4, w.Canvas!.Cards.Count);
        Assert.True(w.Canvas.Cards["tb_order_item"].IsFocusCard);
        Assert.True(w.Canvas.Cards["tb_notice"].IsFocusCard);
        Assert.False(w.Canvas.Cards["tb_order"].IsFocusCard);
        Assert.True(w.Canvas.Bounds.Width > 0);
        // 열 때 화면에 맞춘다 (최대 100%)
        Assert.InRange(w.Vm!.Zoom, 0.3, 1);
        // 이전 대화가 없으면 AI 패널은 닫혀 있다
        Assert.False(w.AiVisible);
        w.SetAiVisible(true);
        Pump();
        Assert.True(w.AiPanel!.IsVisible);
        Assert.True(w.AiPanel.Bounds.Width >= 300);

        // 24개 넘는 컬럼은 "… 컬럼 N개 더"
        var texts = w.Canvas.Cards["tb_notice"].GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("… 컬럼 6개 더", texts);

        // 마우스 올리기 → 이어진 카드 테두리 강조
        w.Vm.Hovered = "tb_order";
        Assert.True(w.Canvas.Cards["tb_order_item"].IsHighlightedCard);
        Assert.False(w.Canvas.Cards["tb_notice"].IsHighlightedCard);
        w.Vm.Hovered = null;

        // 이름 모드 바꾸기 · 확대 · 자동 정렬이 다시 그려진다
        w.Vm.NameMode = DiagramNameMode.Both;
        w.Vm.ZoomIn();
        w.Vm.Relayout();
        Pump();
        Assert.Equal(320, w.Canvas.Cards["tb_order"].Bounds.Width);
        w.Vm.NameMode = DiagramNameMode.Physical;
        w.Close();

        // 없는 다이어그램
        var missing = new DiagramWindow(app, Guid.NewGuid());
        missing.Show();
        Pump();
        Assert.Null(missing.Canvas);
        missing.Close();
    });

    private sealed class SaveTo(string path) : NullDialogs
    {
        public override Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter> filters) =>
            Task.FromResult<string?>(path);
    }

    [Fact]
    public Task ExportPng_WritesPngFile() => UiThread.Run(async () =>
    {
        var (app, _) = ConnectedApp();
        var w = new DiagramWindow(app, app.MakeDiagram((string?)null)!.Value);
        w.Show();
        Pump();
        var path = Path.Combine(TestSetup.Root, $"erd-{Guid.NewGuid():N}.png");
        w.Dialogs = new SaveTo(path);
        Assert.Equal(path, await w.ExportPngAsync());
        Assert.True(File.Exists(path));
        // 2배 해상도 비트맵
        using (var bitmap = w.Canvas!.RenderBitmap(2))
        {
            var size = w.Vm!.ContentSize;
            Assert.Equal(new PixelSize((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2)), bitmap.PixelSize);
            Assert.Equal(192, bitmap.Dpi.X);
        }
        // headless 그리기(테스트 플랫폼)는 PNG 내용을 만들지 않는다. 실제 렌더러(Skia)에서 내용이 있으면 PNG 서명인지 본다.
        var bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Length > 0) Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        Assert.Contains(app.Logs, l => l.Message == $"{Path.GetFileName(path)} 에 저장했어요");
        // 취소하면 쓰지 않는다
        w.Dialogs = new NullDialogs();
        Assert.Null(await w.ExportPngAsync());
        w.Close();
    });

    /// 요청을 기록하고 정해 둔 답을 흘려보내는 가짜 Ollama
    private sealed class FakeOllama(string answer) : HttpMessageHandler
    {
        public List<(string Path, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.AbsolutePath, body));
            if (request.RequestUri.AbsolutePath == "/api/tags")
                return Json("""{"models":[{"name":"qwen3:8b"}]}""");
            var lines = JsonSerializer.Serialize(new { message = new { thinking = "음" } }) + "\n"
                        + JsonSerializer.Serialize(new { message = new { content = answer } }) + "\n"
                        + """{"done":true}""" + "\n";
            return Json(lines);
        }

        private static HttpResponseMessage Json(string s) =>
            new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };
    }

    private static async Task WaitIdle(AiChat chat)
    {
        for (var i = 0; i < 500 && chat.Running; i++) await Task.Delay(10);
        Pump();
        Assert.False(chat.Running);
    }

    [Fact]
    public Task AiPanel_ChatFlowWithFakeOllama() => UiThread.Run(async () =>
    {
        var (app, p) = ConnectedApp();
        app.AddTab("select 1;");
        var w = new DiagramWindow(app, app.MakeDiagram((string?)null)!.Value);
        w.Show();
        Pump();
        var panel = w.AiPanel!;
        var fake = new FakeOllama("### 관계\n- `tb_order` → `tb_member`\n```sql\nSELECT o.*, m.email FROM tb_order o JOIN tb_member m ON o.member_id = m.member_id\n```");
        panel.Chat.ClientFactory = () => new OllamaClient(new Uri("http://127.0.0.1:11434/"), fake);
        panel.Chat.ModelSetting = () => "";
        try
        {
            // 처음에는 소개 + "관계 분석 시작" + 예시 질문
            var buttons = panel.GetLogicalDescendants().OfType<Button>().ToList();
            Assert.Contains(buttons, b => (b.Content as string)?.Contains("관계 분석 시작") == true);
            Assert.Equal(4, buttons.Count(b => b.Content is TextBlock t && AiChat.DiagramExampleQuestions.Contains(t.Text!)));

            // Enter 로 보내기
            panel.Input.Text = "핵심 테이블은?";
            panel.Send();
            Assert.Equal("", panel.Input.Text);
            await WaitIdle(panel.Chat);
            Assert.Equal(["user", "assistant"], panel.Chat.Messages.Select(m => m.Role));
            Assert.Equal("핵심 테이블은?", panel.Chat.Messages[0].Text);
            Assert.Contains("SELECT o.*", panel.Chat.Messages[1].Text);
            Assert.False(panel.Chat.Messages[1].Failed);
            Assert.Contains(app.Logs, l => l.Message == "다이어그램 AI 분석 · qwen3:8b · 테이블 5개");

            // 보낸 것: 구조와 관계만 (스키마 텍스트에 테이블 이름, 질문), 모델
            var chatReq = fake.Requests.Single(r => r.Path == "/api/chat").Body!;
            using var json = JsonDocument.Parse(chatReq);
            Assert.Equal("qwen3:8b", json.RootElement.GetProperty("model").GetString());
            var sent = string.Join("\n", json.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()));
            Assert.Contains("tb_order_item", sent);
            Assert.Contains("핵심 테이블은?", sent);

            // 답변 아래: 복사 · SQL 편집기에 넣기
            panel.Rebuild();
            var actions = panel.GetLogicalDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
            Assert.Contains("복사", actions);
            Assert.Contains("SQL 편집기에 넣기", actions);
            Assert.True(panel.InsertSql(AiText.FirstSql(panel.Chat.Messages[1].Text)!));
            Assert.Contains("-- AI 관계 분석\nSELECT o.*, m.email FROM tb_order o JOIN tb_member m ON o.member_id = m.member_id;", app.SelectedTab!.Text);

            // 대화 기록: 저장되고, 다시 열면 마지막 대화가 보인다 (AI 패널도 열린 채로)
            Assert.Single(panel.Chat.History);
            w.Close();
            var again = new DiagramWindow(app, app.Diagrams.Keys.Last());
            Assert.True(again.AiVisible);
            Assert.Equal(2, again.AiPanel!.Chat.Messages.Count);
            var chat = again.AiPanel.Chat;
            chat.ClientFactory = () => new OllamaClient(new Uri("http://127.0.0.1:11434/"), fake);

            // 새 대화 → 예시 질문으로 두 번째 대화
            chat.StartNew();
            Assert.Empty(chat.Messages);
            again.AiPanel.Ask(AiChat.DiagramExampleQuestions[0]);
            await WaitIdle(chat);
            Assert.Equal(2, chat.History.Count);

            // 이전 대화 열기 · 삭제 · 모두 삭제
            var first = chat.History.Single(c => c.Id != chat.ConversationId);
            chat.Open(first);
            Assert.Equal("핵심 테이블은?", chat.Messages[0].Text);
            chat.Delete(first.Id);
            Assert.Empty(chat.Messages);
            Assert.Single(chat.History);
            chat.DeleteAll();
            Assert.Empty(chat.History);
            Assert.Empty(new AiChat(chat.Scope).History);
        }
        finally
        {
            panel.Chat.DeleteAll();
        }
    });

    [Fact]
    public Task AiPanel_OllamaNotRunningShowsFailure() => UiThread.Run(async () =>
    {
        var (app, _) = ConnectedApp();
        var w = new DiagramWindow(app, app.MakeDiagram("tb_order")!.Value);
        var panel = w.AiPanel!;
        var down = new DownHandler();
        panel.Chat.ClientFactory = () => new OllamaClient(new Uri("http://127.0.0.1:11434/"), down);
        try
        {
            panel.Ask(DiagramAI.AnalysisQuestion(false));
            await WaitIdle(panel.Chat);
            Assert.True(panel.Chat.Messages[1].Failed);
            Assert.Contains(app.Logs, l => l.Level == LogLevel.Error);
        }
        finally { panel.Chat.DeleteAll(); }
    });

    private sealed class DownHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }

    [Fact]
    public Task Markdown_RendersBlocks() => UiThread.Run(() =>
    {
        var md = DiagramAIPanel.Markdown("## 제목\n**굵게** 와 `code`\n\n---\n```sql\nSELECT 1\n```");
        Assert.IsType<StackPanel>(md);
        Assert.Equal(4, ((StackPanel)md).Children.Count);
    });
}
