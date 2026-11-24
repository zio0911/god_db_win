using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using GodDB.App.Services;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Ai;
using GodDB.Core.Drivers;
using GodDB.Core.Schema;

namespace GodDB.App.Tests;

/// Mac 1.4.0 ~ 1.4.3 AI 영역: 답하는 중 보내기(#177), AI 데이터 찾기, 테이블 설명(#178 · #185 · #189), 설정 > AI (로컬 전용 · 모델 확인 · 추천 모델)
public class Release143AiTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static async Task Until(Func<bool> done, int ms = 5000)
    {
        for (var i = 0; i < ms / 10 && !done(); i++)
        {
            await Task.Delay(10);
            Pump();
        }
        Pump();
        Assert.True(done());
    }

    private static readonly TableMeta Members = new("members", "회원", [
        new ColumnMeta("id", "int", false, columnKey: "PRI", position: 1),
        new ColumnMeta("grade_cd", "varchar(10)", true, position: 2),
    ]);
    private static readonly TableMeta Orders = new("orders", "", [
        new ColumnMeta("id", "int", false, columnKey: "PRI", position: 1),
        new ColumnMeta("member_id", "int", true, position: 2),
        new ColumnMeta("status", "char(1)", true, comment: "주문 상태", position: 3),
    ], foreignKeys: [new ForeignKeyMeta("fk_m", "orders", ["member_id"], "members", ["id"])]);

    private const string DescriptionsJson = """
        {"format":"goddb-table-descriptions","version":1,"database":"shop","overview":"쇼핑몰",
         "tables":[{"name":"orders","title":"주문","terms":["결제 내역"],
                    "columns":[{"name":"status","values":{"P":"결제 대기","C":"완료"}}],"notes":["취소도 남는다"]},
                   {"name":"members","columns":[{"name":"grade_cd","title":"회원 등급","terms":["등급"]}]},
                   {"name":"old_coupon"}]}
        """;

    /// 연결된 것처럼 (테이블 · 세션 객체는 있지만 실제 DB 연결은 없음) + 테이블 설명 저장소는 임시 파일
    private static (AppState App, ConnectionProfile P, TestEnv Env) ConnectedApp(bool withDescriptions = true)
    {
        var env = new TestEnv();
        var app = env.NewApp();
        app.DescriptionStore = new TableDescriptionStore(Path.Combine(env.Dir, "table-descriptions.json"));
        var p = TestEnv.Profile("dev");
        app.Profiles.Add(p);
        app.UpdateConnection(p.Id, c =>
        {
            c.State = ConnectionState.Connected;
            c.CurrentDatabase = "shop";
            c.Databases = ["shop"];
            c.Tables = [Members, Orders];
            c.Session = new DbSession(p);
        });
        app.SelectedProfileId = p.Id;
        if (withDescriptions)
        {
            app.TableDescriptions.Set(new TableDescriptionEntry(p.Id, "shop", TableDescriptionDoc.Parse(DescriptionsJson), fileName: "shop.json"));
            app.SaveTableDescriptions();
        }
        return (app, p, env);
    }

    // MARK: - #177 답하는 중에 보내기

    [Fact]
    public Task Assistant_SendWhileAnswering_StopsAnswerAndSendsNewMessage() => UiThread.Run(async () =>
    {
        var (app, _, _) = ConnectedApp(withDescriptions: false);
        var panel = new AssistantPanel { DataContext = app };
        var w = new Window { Width = 420, Height = 700, Content = panel };
        w.Show();
        var vm = panel.Vm!;
        var fake = new HangingThenAnswer();
        vm.Chat.ClientFactory = () => new OllamaClient(null, fake);
        vm.Chat.ModelSetting = () => "";

        vm.Input = "members 설명해 줘";
        vm.Send();
        await Until(() => fake.FirstStarted);
        Assert.True(vm.Chat.Running);
        // 답하는 중에 Enter: 앞 답은 멈추고(취소했어요) 새 글을 바로 보낸다
        vm.Input = "orders 도 설명해 줘";
        vm.Send();
        await Until(() => !vm.Chat.Running && vm.Chat.Messages.Count == 4);
        Assert.Equal("members 설명해 줘", vm.Chat.Messages[0].Text);
        Assert.EndsWith("(취소했어요)", vm.Chat.Messages[1].Text);
        Assert.False(vm.Chat.Messages[1].Failed);
        Assert.Equal("orders 도 설명해 줘", vm.Chat.Messages[2].Text);
        Assert.Equal("둘째 답이에요.", vm.Chat.Messages[3].Text);
        w.Close();
    });

    /// 첫 대화 요청은 한 조각만 주고 멈추고, 그다음 요청은 바로 답한다
    private sealed class HangingThenAnswer : HttpMessageHandler
    {
        private int _chats;
        public bool FirstStarted { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags") return Task.FromResult(Json("""{"models":[{"name":"test-model"}]}"""));
            if (Interlocked.Increment(ref _chats) == 1)
            {
                FirstStarted = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new HangingStream(Encoding.UTF8.GetBytes("""{"message":{"content":"첫 답"}}""" + "\n"))),
                });
            }
            return Task.FromResult(Json("""{"message":{"content":"둘째 답이에요."}}""" + "\n" + """{"done":true}""" + "\n"));
        }
    }

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

    private static HttpResponseMessage Json(string s, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(s, Encoding.UTF8, "application/json") };

    // MARK: - AI 어시스턴트 + 테이블 설명

    [Fact]
    public Task Assistant_UsesTableDescriptions_ChipTitlesAndContext() => UiThread.Run(async () =>
    {
        var (app, _, _) = ConnectedApp();
        var vm = new AssistantPanelViewModel(app);
        vm.Refresh();
        Assert.Contains(vm.Chips(), c => c.Text == "테이블 설명 · 테이블 3개");
        // 질문 속 terms 로 테이블을 찾고, 빈 코멘트에는 title
        var focus = app.AssistantFocus("결제 내역 보여줘");
        Assert.Equal(["orders"], focus.Select(t => t.Name));
        Assert.Equal("주문", focus[0].Comment);
        var messages = await app.AssistantMessagesAsync([("user", "결제 내역 보여줘")]);
        var context = string.Join("\n", messages.Select(m => m.Content));
        Assert.Contains("Table descriptions written by the team", context);
        Assert.Contains("C=완료, P=결제 대기", context);
        Assert.Contains("rule: 취소도 남는다", context);
        Assert.Contains("table descriptions the team wrote", messages[0].Content);
    });

    // MARK: - AI 데이터 찾기

    [Fact]
    public Task Finder_QuickActionAndRouting() => UiThread.Run(() =>
    {
        var (app, _, _) = ConnectedApp();
        var panel = new AssistantPanel { DataContext = app };
        var w = new Window { Width = 420, Height = 700, Content = panel };
        w.Show();
        Pump();
        var vm = panel.Vm!;
        Assert.True(vm.CanFind);
        // 처음 화면의 빠른 동작 🔎 데이터 위치 찾기 → 입력이 데이터 찾기 모드로
        var find = panel.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "FindDataButton");
        find.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.True(vm.FinderOn);
        Assert.True(panel.FindControl<Border>("FinderBadge")!.IsVisible);
        Assert.Equal("찾을 데이터 (예: 학년 정보)", panel.FindControl<TextBox>("InputBox")!.PlaceholderText);
        vm.StopFinder();
        Pump();
        Assert.False(panel.FindControl<Border>("FinderBadge")!.IsVisible);

        // "…어디 있어?" 꼴은 데이터 찾기로: 테이블 설명의 찾는 말이 맞으면 AI 없이 바로 답 카드
        vm.Input = "등급 정보는 어디 있어?";
        vm.Send();
        Pump();
        var answer = vm.Chat.Messages[^1];
        var card = app.Assistant.FinderCard(answer.Id)!;
        Assert.True(card.FromNotes);
        Assert.Equal("members.grade_cd", card.Current!.Location);
        Assert.True(card.Current.Confirmed);
        Assert.Equal("🔎 **등급** → `members.grade_cd` · 확인됨", answer.Text);
        // 카드가 말풍선 대신 그려진다
        Assert.Contains(panel.GetLogicalDescendants().OfType<Border>(), b => b.Name == "FinderCard");
        Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "📄 테이블 설명에서 찾음 (AI 호출 없음)");
        // 맞아요 · 아니에요 · 다시 찾기 단추는 벡터 아이콘 (Mac hand.thumbsup / hand.thumbsdown / arrow.clockwise)
        GodDB.App.Controls.Symbol IconOf(string name) =>
            ((Panel)panel.GetLogicalDescendants().OfType<Button>().First(b => b.Name == name).Content!).Children.OfType<GodDB.App.Controls.SymbolIcon>().Single().Symbol;
        Assert.Equal(GodDB.App.Controls.Symbol.ThumbsUp, IconOf("FinderConfirm"));
        Assert.Equal(GodDB.App.Controls.Symbol.ThumbsDown, IconOf("FinderReject"));
        Assert.Equal(GodDB.App.Controls.Symbol.Refresh, IconOf("FinderRefind"));
        // 일반 질문은 그대로 어시스턴트로 (데이터 찾기 아님)
        Assert.False(DataFinder.IsFindQuestion("이 쿼리 어디가 느려?"));
        w.Close();
    });

    [Fact]
    public Task Finder_ConfirmAndRejectEditTableDescriptions() => UiThread.Run(() =>
    {
        var (app, p, _) = ConnectedApp();
        app.FindData("등급 어디 있어?");
        Pump();
        var chat = app.Assistant.Chat(app.AssistantScope);
        var id = chat.Messages[^1].Id;
        // 테이블 설명에서 찾은 답의 [아니에요]: 그 위치에서 찾는 말을 빼고 (title 은 그대로), 다른 후보가 없으면 안내
        app.FinderReject(id);
        var card = app.Assistant.FinderCard(id)!;
        Assert.Null(card.Current);
        Assert.Equal("🔎 **등급** — 다른 후보가 없어요", chat.Messages[^1].Text);
        var saved = app.DescriptionStore.Load()!.Doc(p.Id, "shop")!;
        Assert.Null(saved.FindTable("members")!.Column("grade_cd")!.Terms);
        Assert.Equal("회원 등급", saved.FindTable("members")!.Column("grade_cd")!.Title);

        // AI 답은 [맞아요] 를 눌렀을 때만 테이블 설명의 terms 에 넣는다
        var aiCard = new DataFinderCard
        {
            Question = "회원 레벨 어디 있어?", Terms = ["회원", "레벨"], ProfileId = p.Id, Database = "shop",
            Current = new DataFinderAnswer { Table = "members", Columns = ["grade_cd"], Sql = "SELECT * FROM `members`", Source = TableNote.NoteSource.Ai },
        };
        var msg = chat.Add("assistant", "");
        app.Assistant.SetFinderCard(msg.Id, aiCard);
        Assert.Null(app.TableDescriptions.Doc(p.Id, "shop")!.FindTable("members")!.Column("grade_cd")!.Terms);
        app.FinderConfirm(msg.Id);
        Assert.True(aiCard.Current!.Confirmed);
        Assert.Equal(["회원 레벨"], app.DescriptionStore.Load()!.Doc(p.Id, "shop")!.FindTable("members")!.Column("grade_cd")!.Terms!);
        // 다음에는 AI 없이 바로
        app.FindData("회원 레벨 어디 있어?");
        Pump();
        Assert.True(app.Assistant.FinderCard(chat.Messages[^1].Id)!.FromNotes);

        // [SQL 넣기]: 같은 접속의 탭이 없으면 새 탭에
        app.FinderInsertSql(aiCard);
        Assert.Equal("SELECT * FROM `members`\n", app.SelectedTab!.Text);
        Assert.Equal("🔎 회원 레벨", app.SelectedTab.Title);
    });

    [Fact]
    public Task Finder_BrokenModel_StopsAndOffersWorkingModels() => UiThread.Run(async () =>
    {
        var (app, _, _) = ConnectedApp(withDescriptions: false);
        var before = AISettings.Model;
        AISettings.Model = "";
        try
        {
            var fake = new ProbeOllama();
            app.AiClientFactory = () => new OllamaClient(new Uri("http://127.0.0.7:11434"), fake);
            app.FindData("학년 정보는 어디 있어?");
            var chat = app.Assistant.Chat(app.AssistantScope);
            await Until(() => app.Assistant.FinderCard(chat.Messages[^1].Id) is { ModelProblem.Checking: false });
            var card = app.Assistant.FinderCard(chat.Messages[^1].Id)!;
            Assert.Equal("broken-model", card.ModelProblem!.Model);
            Assert.Equal("token repeat limit reached", card.ModelProblem.Reason);
            Assert.Equal(["good-model"], card.ModelProblem.Alternatives);
            Assert.StartsWith("🔎 **학년** — 선택한 AI 모델 ‹broken-model› 이 응답하지 않아요", chat.Messages[^1].Text);
            Assert.False(chat.Running);
            // 샘플 조회는 하지 않았다 (모델 확인에서 멈춤)
            Assert.DoesNotContain(fake.Bodies, b => b.Contains("candidates"));
            Assert.Contains(app.Logs, l => l.Message.StartsWith("AI 데이터 찾기 · 모델 broken-model 응답 없음"));
        }
        finally { AISettings.Model = before; }
    });

    /// 모델 목록: broken-model, good-model. broken-model 은 HTTP 500, good-model 은 OK
    private sealed class ProbeOllama : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags") return Json("""{"models":[{"name":"broken-model"},{"name":"good-model"}]}""");
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (Bodies) Bodies.Add(body);
            var model = JsonDocument.Parse(body).RootElement.GetProperty("model").GetString();
            return model == "broken-model"
                ? Json("""{"error":"token repeat limit reached"}""", HttpStatusCode.InternalServerError)
                : Json("""{"message":{"content":"OK"},"done":true}""");
        }
    }

    // MARK: - 아이콘 (이모지 → Mac SF Symbols 모양 벡터)

    [Fact]
    public Task AssistantPanel_ButtonsUseVectorIcons() => UiThread.Run(() =>
    {
        var (app, _, _) = ConnectedApp();
        var panel = new AssistantPanel { DataContext = app };
        var w = new Window { Width = 420, Height = 700, Content = panel };
        w.Show();
        Pump();
        GodDB.App.Controls.Symbol Icon(string name) => Assert.IsType<GodDB.App.Controls.SymbolIcon>(panel.FindControl<Button>(name)!.Content).Symbol;
        Assert.Equal(GodDB.App.Controls.Symbol.PlusBubble, Icon("NewChat"));
        Assert.Equal(GodDB.App.Controls.Symbol.History, Icon("HistoryButton"));
        Assert.Equal(GodDB.App.Controls.Symbol.Bolt, Icon("QuickMenu"));
        Assert.Equal(GodDB.App.Controls.Symbol.SendCircle, Icon("SendButton"));
        Assert.Equal(GodDB.App.Controls.Symbol.StopCircle, Icon("StopButton"));
        w.Close();
    });

    [Fact]
    public void NewScreens_NoEmojiIcons()
    {
        // 1.4.x 로 새로 들어온 화면의 단추 · 제목 · 빈 화면 아이콘 (문장 안 ⚠ 와 Mac 문장 그대로인 Tr 키는 예외)
        string[] emojis = ["📄", "⬇", "⬆", "🗑", "📋", "🔖", "🔗", "📤", "🕘", "📡", "💬", "🪄", "👍", "👎", "🛢", "👁", "📁", "📌", "🔍", "✨"];
        foreach (var rel in new[]
        {
            "src/GodDB.App/Views/TableDescriptionsWindow.cs", "src/GodDB.App/Views/SavedQueriesWindow.cs", "src/GodDB.App/Views/PreviousVersionsWindow.cs",
            "src/GodDB.App/Views/AssistantPanel.axaml", "src/GodDB.App/Views/DiagramAIPanel.cs",
            "src/GodDB.App/Views/QuickOpenWindow.cs", "src/GodDB.App/Views/ConnectionEditorWindow.axaml", "src/GodDB.App/Views/SidebarView.axaml",
        })
        {
            var src = File.ReadAllText(ToolbarSymbolTests.FindRepoFile(rel));
            foreach (var e in emojis) Assert.False(src.Contains(e), $"{rel}: {e}");
        }
        // 데이터 찾기 카드: 단추만 (보낸 내용 칩의 📄 은 글자 그대로)
        var finder = File.ReadAllText(ToolbarSymbolTests.FindRepoFile("src/GodDB.App/Views/DataFinderCardView.cs"));
        Assert.DoesNotContain("👍", finder);
        Assert.DoesNotContain("👎", finder);
        var modeling = File.ReadAllText(ToolbarSymbolTests.FindRepoFile("src/GodDB.App/Views/ModelingView.axaml.cs"));
        Assert.DoesNotContain("📤", modeling);
        Assert.DoesNotContain("\"📄 \" + Tr(\"SQL 추출\")", modeling);
    }

    // MARK: - 테이블 설명 창

    private sealed class FileDialogs : NullDialogs
    {
        public string? OpenPath { get; set; }
        public string? SavePath { get; set; }
        public bool Confirm { get; set; } = true;
        public List<string> Confirms { get; } = [];
        public override Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<FileFilter> filters, bool multiple = false, string? startFolder = null) =>
            Task.FromResult<IReadOnlyList<string>>(OpenPath == null ? [] : [OpenPath]);
        public override Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter> filters) => Task.FromResult(SavePath);
        public override Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel, bool destructive = false)
        {
            Confirms.Add(title);
            return Task.FromResult(Confirm);
        }
    }

    [Fact]
    public Task DescriptionsWindow_ImportExportFormClear() => UiThread.Run(async () =>
    {
        var (app, p, env) = ConnectedApp(withDescriptions: false);
        var dialogs = new FileDialogs();
        app.Dialogs = dialogs;
        app.OpenTableDescriptions();
        var request = app.TableDescriptionsRequest!;
        Assert.Equal((p.Id, "shop"), (request.ProfileId, request.Database));
        var w = new TableDescriptionsWindow(app, request);
        w.Show();
        Pump();
        Assert.Contains(w.StatusLines, l => l.Contains("아직 테이블 설명이 없어요"));
        Assert.False(w.ExportButton.IsEnabled);
        Assert.True(w.SkeletonButton.IsEnabled);

        // 단추 아이콘은 Mac SF Symbols 모양의 벡터 (이모지 아님)
        static GodDB.App.Controls.Symbol Icon(Button b) => Assert.IsType<GodDB.App.Controls.SymbolIcon>(((StackPanel)b.Content!).Children[0]).Symbol;
        Assert.Equal(GodDB.App.Controls.Symbol.Upload, Icon(w.ExportButton));
        Assert.Equal(GodDB.App.Controls.Symbol.Trash, Icon(w.ClearButton));
        Assert.Equal(GodDB.App.Controls.Symbol.Copy, Icon(w.CopyRequestButton));
        Assert.Contains(w.GetLogicalDescendants().OfType<Button>(), b => b.Content is StackPanel sp && sp.Children[0] is GodDB.App.Controls.SymbolIcon { Symbol: GodDB.App.Controls.Symbol.Download });
        Assert.DoesNotContain(w.GetLogicalDescendants().OfType<Button>(), b => b.Content is string t && new[] { "📄", "⬇", "⬆", "🗑", "📋" }.Any(t.Contains));

        // 요청문 샘플 복사 (양식 안 instructions 와 같은 글)
        await w.CopyRequestAsync();
        Assert.Equal(GodDB.App.Controls.Symbol.Checkmark, Icon(w.CopyRequestButton));   // 복사 뒤 2초 동안 체크 표시
        Assert.Equal(TableDescriptionDoc.RequestPrompt("shop", DatabaseKind.Mysql, false), dialogs.Clipboard);

        // 빈 양식: 이름 · 코멘트 · FK 만 (데이터 값 없음) + instructions
        dialogs.SavePath = Path.Combine(env.Dir, "form.json");
        await w.SaveSkeletonAsync();
        var form = TableDescriptionDoc.Parse(await File.ReadAllBytesAsync(dialogs.SavePath));
        Assert.NotNull(form.Instructions);
        Assert.Equal("members.id", form.FindTable("orders")!.Column("member_id")!.References);
        Assert.Equal("주문 상태", form.FindTable("orders")!.Column("status")!.Title);
        Assert.All(form.Tables, t => Assert.All(t.Columns ?? [], c => Assert.Null(c.Values)));

        // 깨진 JSON: 어디가 틀렸는지
        var broken = Path.Combine(env.Dir, "broken.json");
        await File.WriteAllTextAsync(broken, """{"tables": [{"name": "t", "columns": [{"name": "c", "values": ["P"]}]}]}""");
        dialogs.OpenPath = broken;
        await w.ImportAsync();
        Assert.Contains("tables[0].columns[0].values", w.Problem);
        Assert.Null(app.TableDescriptions.Entry(p.Id, "shop"));

        // 다른 DB 용 JSON 이면 묻는다 (취소하면 넣지 않음)
        var other = Path.Combine(env.Dir, "other.json");
        await File.WriteAllTextAsync(other, DescriptionsJson.Replace("\"database\":\"shop\"", "\"database\":\"legacy\""));
        dialogs.OpenPath = other;
        dialogs.Confirm = false;
        await w.ImportAsync();
        Assert.Equal("JSON 은 ‘legacy’ DB 용이에요. ‘shop’ 에 넣을까요?", dialogs.Confirms[^1]);
        Assert.Null(app.TableDescriptions.Entry(p.Id, "shop"));
        dialogs.Confirm = true;
        await w.ImportAsync();
        var entry = app.TableDescriptions.Entry(p.Id, "shop")!;
        Assert.Equal("shop", entry.Doc.Database);                  // 지금 DB 이름으로
        Assert.Equal("other.json", entry.FileName);
        Assert.Null(w.Problem);
        Assert.NotNull(app.DescriptionStore.Load()!.Entry(p.Id, "shop"));
        Assert.Contains(w.StatusLines, l => l.StartsWith("✓ 테이블 3개 · 컬럼 2개 · 코드 값 2개"));
        // 지금 DB 와 맞춰 보기
        Assert.Contains(w.StatusLines, l => l.Contains("DB 에 없는 테이블 1개 (old_coupon)"));
        Assert.Equal(app.CurrentDescriptions, entry.Doc);

        // 왼쪽 목록 + 오른쪽 읽기 전용 상세, 찾기
        Assert.Equal(["orders", "members", "old_coupon"], w.Filtered.Select(t => t.Name));
        Assert.Equal("orders", w.Shown?.Name);
        w.SearchBox.Text = "등급";
        Pump();
        Assert.Equal(["members"], w.Filtered.Select(t => t.Name));
        Assert.Equal("members", w.Shown?.Name);
        w.SearchBox.Text = "";
        Pump();

        // 내보내기
        dialogs.SavePath = Path.Combine(env.Dir, "export.json");
        await w.ExportAsync();
        Assert.Equal(entry.Doc, TableDescriptionDoc.Parse(await File.ReadAllBytesAsync(dialogs.SavePath)));

        // 지우기 (확인)
        await w.ConfirmClearAsync();
        Assert.Equal("이 DB 의 테이블 설명을 지울까요?", dialogs.Confirms[^1]);
        Assert.Null(app.TableDescriptions.Entry(p.Id, "shop"));
        Assert.Empty(app.DescriptionStore.Load()!.Entries);
        Assert.Null(app.CurrentDescriptions);
        w.Close();
    });

    [Fact]
    public Task DescriptionsWindow_OpensFromAppStateAsSheet() => UiThread.Run(() =>
    {
        var (app, _, _) = ConnectedApp();
        var main = new MainWindow(app);
        main.Show();
        Pump();
        // 사이드바 말풍선: 설명이 있으면 채운 말풍선, 도움말에 테이블 수
        var sidebar = main.FindControl<SidebarView>("Sidebar")!;
        Assert.True(sidebar.DescriptionsIconFilled);
        Assert.Equal("테이블 설명 (AI 참고) — 테이블 3개", app.TableDescriptionsTip);
        app.TableDescriptions.Remove(app.SelectedProfileId!.Value, "shop");
        app.SaveTableDescriptions();
        Pump();
        Assert.False(sidebar.DescriptionsIconFilled);
        Assert.Equal("테이블 설명 (AI 참고) — 아직 없어요", app.TableDescriptionsTip);

        var button = sidebar.FindControl<Button>("DescriptionsButton")!;
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.NotNull(app.TableDescriptionsRequest);
        Assert.IsType<TableDescriptionsWindow>(main.OpenSheets[nameof(AppState.TableDescriptionsRequest)]);
        main.OpenSheets[nameof(AppState.TableDescriptionsRequest)].Close();
        Pump();
        Assert.Null(app.TableDescriptionsRequest);
        main.Close();
    });

    // MARK: - 설정 > AI

    [Fact]
    public Task Settings_RemoteOllamaUrlIsNotSaved_AndModelCheck() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var beforeUrl = AISettings.Url;
        try
        {
            AISettings.Url = AISettings.DefaultUrl;
            var vm = new SettingsViewModel(app);
            vm.OllamaUrl = "http://10.0.0.5:11434";
            Assert.True(vm.IsNonLocal);
            Assert.Equal(AISettings.DefaultUrl, AISettings.Url);          // 다른 서버 주소는 저장하지 않는다
            Assert.Contains("이 주소는 저장되지 않아요", vm.NonLocalWarning);
            vm.OllamaUrl = "http://127.0.0.1:11500";
            Assert.False(vm.IsNonLocal);
            Assert.Equal("http://127.0.0.1:11500", AISettings.Url);
            vm.ResetOllamaUrlCommand.Execute(null);
            Assert.Equal(AISettings.DefaultUrl, AISettings.Url);
            // 추천 모델 문구 · 받기 명령 (MLX 는 Apple 전용이라 일반 Ollama 태그)
            Assert.Equal("추천: 메모리 48GB 이상이면 qwen3.8:27b, 24GB 면 qwen3:14b 를 추천해요. 모델을 \"자동\"으로 두면 설치된 추천 모델을 먼저 써요.", vm.RecommendNote);
            Assert.Equal("ollama pull qwen3.8:27b", vm.PullCommandText);
            Assert.Equal("ollama pull qwen3:14b", vm.PullSmallCommandText);
            Assert.Equal("자동 (추천 모델 먼저)", vm.ModelChoices[0].Label);
            Assert.False(vm.CanCheckModel);   // 모델 목록이 없으면 확인할 수 없다
        }
        finally { AISettings.Url = beforeUrl; }
    });
}
