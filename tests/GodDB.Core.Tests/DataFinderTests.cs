using GodDB.Core.Ai;
using GodDB.Core.Dialects;
using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

// Mac Tests/GodDBCoreTests/DataFinderTests.swift

/// 정해 둔 답을 차례로 돌려주는 가짜 Ollama
internal sealed class FakeFinderTransport(IEnumerable<string> replies)
{
    private readonly Lock _lock = new();
    private readonly Queue<string> _replies = new(replies);
    public List<List<OllamaMessage>> Sent { get; } = [];

    public string Complete(IReadOnlyList<OllamaMessage> messages)
    {
        lock (_lock)
        {
            Sent.Add(messages.ToList());
            return _replies.Count == 0 ? "" : _replies.Dequeue();
        }
    }

    public int Calls { get { lock (_lock) return Sent.Count; } }
}

public class DataFinderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static readonly TableMeta Students = new("ui_students", "학생", [
        new ColumnMeta("id", "int", false, columnKey: "PRI"),
        new ColumnMeta("name", "varchar(50)", true, comment: "학생 이름"),
        new ColumnMeta("school_id", "int", true, comment: "학교 번호"),
        new ColumnMeta("grade_level", "int", true, comment: "학년"),
    ]);
    internal static readonly TableMeta Schools = new("ui_schools", "학교", [
        new ColumnMeta("id", "int", false, columnKey: "PRI"),
        new ColumnMeta("name", "varchar(50)", true, comment: "학교 이름"),
    ]);
    internal static readonly TableMeta Customers = new("ui_customers", columns: [
        new ColumnMeta("id", "int", false),
        new ColumnMeta("grade", "varchar(10)", true),
    ]);
    internal static readonly List<TableMeta> Tables = [Customers, Schools, Students];

    // MARK: - 질문 판단

    [Theory]
    [InlineData("학년 정보는 어디 있어?")]
    [InlineData("학년 정보는 어딨어?")]
    [InlineData("학교 정보 어디야")]
    [InlineData("회원 주소는 어디에 저장돼?")]
    [InlineData("학년이 어디 들어있어")]
    [InlineData("where is the student grade?")]
    [InlineData("Which table has the school name?")]
    public void DetectsFindQuestions(string q) => Assert.True(DataFinder.IsFindQuestion(q), q);

    [Theory]
    [InlineData("이 쿼리 어디가 느려?")]
    [InlineData("지금 어디 붙어있어?")]
    [InlineData("에러 원인이 어디야?")]
    [InlineData("어디야?")]
    [InlineData("SELECT * FROM t WHERE x = '어디 있어'")]
    [InlineData("인덱스가 빠진 테이블을 찾아 줘")]
    [InlineData("which table has the most rows?")]
    [InlineData("학년 테이블을 설명해 줘")]
    [InlineData("where clause 쓰는 법")]
    public void AmbiguousGoesToNormalQuestion(string q) => Assert.False(DataFinder.IsFindQuestion(q), q);

    // MARK: - 핵심어

    [Fact]
    public void ExtractsKeyTerms()
    {
        Assert.Equal(["학년"], DataFinder.KeyTerms("학년 정보는 어딨어?"));
        Assert.Equal(["학년"], DataFinder.KeyTerms("학년 정보는 어디 있어?"));
        Assert.Equal(["학교"], DataFinder.KeyTerms("학교 정보 어디야"));
        Assert.Equal(["회원", "주소"], DataFinder.KeyTerms("회원 주소는 어디에 저장돼?"));
        Assert.Equal(["학년"], DataFinder.KeyTerms("학년이 어디 들어있어"));
        Assert.Equal(["student", "grade"], DataFinder.KeyTerms("where is the student grade stored?"));
        // 두 글자 말은 조사처럼 보여도 자르지 않는다
        Assert.Equal(["나이"], DataFinder.KeyTerms("나이 데이터 어디 있어"));
    }

    [Fact]
    public void Normalizes()
    {
        Assert.Equal("학년코드", DataFinder.Normalize(" 학년 코드 "));
        Assert.Equal("gradecd", DataFinder.Normalize("Grade CD"));
    }

    // MARK: - 메모 일치

    [Fact]
    public void MatchesNotes()
    {
        var p = Guid.NewGuid();
        var old = DateTime.UnixEpoch.AddSeconds(1_000);
        var @new = DateTime.UnixEpoch.AddSeconds(2_000);
        var a = new TableNote(p, "school", "ui_students", ["grade_level"], ["학년"], source: TableNote.NoteSource.Ai, confirmed: false, createdAt: @new);
        var b = new TableNote(p, "school", "tb_class", ["grade"], ["학년", "반"], source: TableNote.NoteSource.User, createdAt: old);
        var c = new TableNote(p, "school", "ui_members", ["addr"], ["회원 주소"], source: TableNote.NoteSource.Ai, confirmed: false);
        List<TableNote> notes = [a, b, c];
        // 확인된 것 먼저
        Assert.Equal(["tb_class", "ui_students"], DataFinder.MatchNotes(["학년"], notes).Select(n => n.Table));
        // 공백 · 대소문자 무시: "회원 주소" ↔ ["회원", "주소"]
        Assert.Equal(["ui_members"], DataFinder.MatchNotes(["회원", "주소"], notes).Select(n => n.Table));
        Assert.Equal(["ui_members"], DataFinder.MatchNotes(["회원주소"], notes).Select(n => n.Table));
        // 핵심어가 모두 있어야
        Assert.Empty(DataFinder.MatchNotes(["학년", "코드"], notes));
        Assert.Empty(DataFinder.MatchNotes([], notes));
    }

    // MARK: - 후보 요청 (예산)

    [Fact]
    public void BudgetPrefersTablesOverlappingTerms()
    {
        // 관계 없는 큰 테이블을 앞에 많이 두고, 예산을 작게
        var filler = Enumerable.Range(0, 30).Select(i => new TableMeta($"zz_log_{i}", "로그",
            Enumerable.Range(0, 10).Select(j => new ColumnMeta($"col_{j}", "varchar(255)", true, comment: "기타 값")))).ToList();
        List<TableMeta> all = [.. filler, Students];
        var r = DataFinder.SchemaText(all, ["학년"], budget: 600);
        Assert.StartsWith("ui_students — 학생\n", r.Text);
        Assert.Contains("grade_level int — 학년", r.Text);
        Assert.True(r.Count < all.Count);
        Assert.True(r.Text.Length <= 600 + 60);   // 예산 + "… (N more tables not shown)" 한 줄
        // 예산이 넉넉하면 전부
        Assert.Equal(all.Count, DataFinder.SchemaText(all, ["학년"], budget: 1_000_000).Count);
    }

    [Fact]
    public void CandidatePromptIncludesNotesAndCount()
    {
        var note = new TableNote(Guid.NewGuid(), "s", "ui_students", ["grade_level"], ["학년"], source: TableNote.NoteSource.Ai, confirmed: false);
        var m = DataFinder.CandidateMessages("학년 정보는 어디 있어?", ["학년"], DatabaseKind.Mysql, Tables, [note], english: false);
        Assert.Equal(3, m.TablesSent);
        Assert.Equal(2, m.Messages.Count);
        Assert.Contains("{\"candidates\"", m.Messages[0].Content);
        Assert.Contains("학년 → ui_students.grade_level (unconfirmed)", m.Messages[1].Content);
    }

    // MARK: - 답 읽기

    [Fact]
    public void ParsesGoodCandidates()
    {
        var reply = """
            Here you go:
            ```json
            {"candidates":[{"table":"UI_STUDENTS","columns":["Grade_Level"],"reason":"코멘트가 학년"},
                           {"table":"ui_customers","columns":["grade"],"reason":"이름이 grade"}]}
            ```
            """;
        var c = DataFinder.ParseCandidates(reply, Tables);
        Assert.Equal([new DataFinder.Candidate("ui_students", ["grade_level"], "코멘트가 학년"),
                      new DataFinder.Candidate("ui_customers", ["grade"], "이름이 grade")], c!);
    }

    [Fact]
    public void BadFormatIsNull()
    {
        Assert.Null(DataFinder.ParseCandidates("학년은 ui_students 에 있어요", Tables));
        Assert.Null(DataFinder.ParseCandidates("{\"answer\": 1}", Tables));
        Assert.Null(DataFinder.ParseCandidates("{\"candidates\": [", Tables));
    }

    [Fact]
    public void DropsHallucinatedTablesAndColumns()
    {
        var reply = """
            {"candidates":[{"table":"tb_student","columns":["grade_cd"]},
                           {"table":"ui_students","columns":["grade_cd","grade_level","ui_students.name"]},
                           {"table":"ui_schools","columns":["nope"]}]}
            """;
        Assert.Equal([new DataFinder.Candidate("ui_students", ["grade_level", "name"])], DataFinder.ParseCandidates(reply, Tables)!);
        // 모두 없는 것이면 빈 목록 (형식은 맞음)
        Assert.Empty(DataFinder.ParseCandidates("{\"candidates\":[{\"table\":\"x\",\"columns\":[\"y\"]}]}", Tables)!);
    }

    [Fact]
    public void CapsAtFiveCandidates()
    {
        var many = Enumerable.Range(0, 8).Select(i => new TableMeta($"t{i}", columns: [new ColumnMeta("c", "int", true)])).ToList();
        var json = "{\"candidates\":[" + string.Join(",", Enumerable.Range(0, 8).Select(i => $"{{\"table\":\"t{i}\",\"columns\":[\"c\"]}}")) + "]}";
        Assert.Equal(5, DataFinder.ParseCandidates(json, many)?.Count);
    }

    [Fact]
    public void FallbackRanksByKeyword()
    {
        var c = DataFinder.FallbackCandidates(["학년"], Tables);
        // 코멘트 '학년' 이 먼저, 약어 사전(학년 → grade)으로 ui_customers.grade 도 후보
        Assert.Equal(["ui_students.grade_level", "ui_customers.grade"], c.Select(x => x.Location));
        var school = DataFinder.FallbackCandidates(["학교"], Tables);
        // 코멘트 일치(학교 이름 · 학교 번호), 테이블 코멘트도 맞는 ui_schools 가 먼저
        Assert.Equal(["ui_schools", "ui_students"], school.Select(x => x.Table));
        Assert.Equal(["ui_customers.grade", "ui_students.grade_level"], DataFinder.FallbackCandidates(["grade"], Tables).Select(x => x.Location));
        Assert.Empty(DataFinder.FallbackCandidates(["없는말"], Tables));
    }

    // MARK: - 샘플

    [Fact]
    public void SampleSqlPerDialect()
    {
        Assert.Equal("SELECT `grade_level` FROM `ui_students` WHERE `grade_level` IS NOT NULL LIMIT 200",
                     DataFinder.SampleSql("ui_students", "grade_level", new MySqlDialect()));
        Assert.Equal("SELECT \"grade_level\" FROM \"ui_students\" WHERE \"grade_level\" IS NOT NULL LIMIT 200",
                     DataFinder.SampleSql("ui_students", "grade_level", new PostgresDialect()));
        Assert.Equal("SELECT TOP 200 [grade] FROM [sales].[orders] WITH (NOLOCK) WHERE [grade] IS NOT NULL",
                     DataFinder.SampleSql("sales.orders", "grade", new MssqlDialect()));
    }

    [Fact]
    public void CleansSamples()
    {
        var @long = new string('가', 80);
        List<string?> values = ["1", "2", null, "1", "  ", @long, @long + "나", .. Enumerable.Range(0, 40).Select(i => $"v{i}")];
        var output = DataFinder.CleanSamples(values);
        Assert.Equal(20, output.Count);
        Assert.Equal(["1", "2", new string('가', 50)], output.Take(3));
        Assert.All(output, s => Assert.True(s.Length <= 50));
        Assert.Equal(output.Count, output.Distinct().Count());
    }

    [Fact]
    public async Task TimeoutReturnsNull()
    {
        var slow = await DataFinder.WithTimeout(0.05, async () =>
        {
            await Task.Delay(2_000);
            return 1;
        });
        Assert.Null(slow);
        var fast = await DataFinder.WithTimeout(2, () => Task.FromResult(7));
        Assert.Equal(7, fast);
    }

    [Fact]
    public void DisplaySamples()
    {
        var c = new DataFinder.Candidate("t", ["a", "b"]);
        Assert.Null(new DataFinder.SampledCandidate(c).DisplaySamples);
        Assert.Empty(new DataFinder.SampledCandidate(c, new() { ["a"] = null, ["b"] = null }).DisplaySamples!);
        Assert.Equal(["x"], new DataFinder.SampledCandidate(c, new() { ["a"] = [], ["b"] = ["x"] }).DisplaySamples!);
    }

    // MARK: - 판단 · SELECT 만

    [Fact]
    public void SelectOnly()
    {
        Assert.True(DataFinder.IsSelectOnly("SELECT grade_level FROM ui_students", DatabaseKind.Mysql));
        Assert.True(DataFinder.IsSelectOnly("  select * from t;  ", DatabaseKind.Mysql));
        Assert.True(DataFinder.IsSelectOnly("WITH x AS (SELECT 1) SELECT * FROM x", DatabaseKind.Mysql));
        Assert.False(DataFinder.IsSelectOnly("DELETE FROM ui_students", DatabaseKind.Mysql));
        Assert.False(DataFinder.IsSelectOnly("SELECT 1; DROP TABLE t", DatabaseKind.Mysql));
        Assert.False(DataFinder.IsSelectOnly("SELECT * INTO backup FROM t", DatabaseKind.Mysql));
        Assert.False(DataFinder.IsSelectOnly("SELECT * FROM t FOR UPDATE", DatabaseKind.Mysql));
        Assert.False(DataFinder.IsSelectOnly("WITH x AS (DELETE FROM t RETURNING *) SELECT * FROM x", DatabaseKind.Mysql));
        Assert.False(DataFinder.IsSelectOnly("", DatabaseKind.Mysql));
    }

    [Fact]
    public void ParsesVerdict()
    {
        List<DataFinder.Candidate> cands = [new("ui_students", ["grade_level"])];
        var good = """
            {"table":"ui_students","columns":["grade_level"],"confidence":"high","explain":"학년 코멘트, 값 1~6","sql":"SELECT grade_level FROM ui_students"}
            """;
        var v = DataFinder.ParseVerdict(good, Tables, cands, new MySqlDialect());
        Assert.Equal(new DataFinder.Verdict("ui_students", ["grade_level"], DataFinder.Confidence.High, "학년 코멘트, 값 1~6",
                                            "SELECT grade_level FROM ui_students"), v);
        // SELECT 가 아니면 SELECT * FROM 으로, 신뢰도가 틀리면 낮음, 컬럼이 없으면 후보 컬럼
        var bad = """
            {"table":"ui_students","columns":["nope"],"confidence":"very","explain":"x","sql":"UPDATE ui_students SET grade_level = 1"}
            """;
        var w = DataFinder.ParseVerdict(bad, Tables, cands, new MySqlDialect());
        Assert.Equal("SELECT * FROM `ui_students`", w?.Sql);
        Assert.Equal(DataFinder.Confidence.Low, w?.Confidence);
        Assert.Equal(["grade_level"], w!.Columns);
        // 없는 테이블 · 형식 틀림
        Assert.Null(DataFinder.ParseVerdict("{\"table\":\"tb_student\"}", Tables, cands, new MySqlDialect()));
        Assert.Null(DataFinder.ParseVerdict("모르겠어요", Tables, cands, new MySqlDialect()));
    }

    [Fact]
    public void AuditLineHasNoQuestionOrValues() =>
        Assert.Equal("데이터 찾기 · 후보 3 · 샘플 조회 4", DataFinder.AuditResult(3, 4));

    // MARK: - 가짜 전송으로 두 번의 AI 호출

    private static DataFinderAI Ai(FakeFinderTransport fake) =>
        new(DatabaseKind.Mysql, false, (m, _) => Task.FromResult(fake.Complete(m)));

    [Fact]
    public async Task PicksCandidatesFirstTry()
    {
        var fake = new FakeFinderTransport(["{\"candidates\":[{\"table\":\"ui_students\",\"columns\":[\"grade_level\"],\"reason\":\"r\"}]}"]);
        var p = await Ai(fake).PickCandidatesAsync("학년 정보는 어디 있어?", ["학년"], Tables, [], ct: Ct);
        Assert.Equal(["ui_students.grade_level"], p.Candidates.Select(c => c.Location));
        Assert.False(p.Fallback);
        Assert.Equal(3, p.TablesSent);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task RetriesOnceThenSucceeds()
    {
        var fake = new FakeFinderTransport(["학년은 학생 테이블에 있어요.",
                                            "{\"candidates\":[{\"table\":\"ui_students\",\"columns\":[\"grade_level\"]}]}"]);
        var p = await Ai(fake).PickCandidatesAsync("q", ["학년"], Tables, [], ct: Ct);
        Assert.Equal(["ui_students.grade_level"], p.Candidates.Select(c => c.Location));
        Assert.False(p.Fallback);
        Assert.Equal(2, fake.Calls);
        // 다시 요청할 때 앞 답과 형식 안내를 함께 보낸다
        Assert.Equal(4, fake.Sent[1].Count);
        Assert.Equal(DataFinder.RetryPrompt, fake.Sent[1][3].Content);
    }

    [Fact]
    public async Task FallsBackAfterSecondBadReply()
    {
        var fake = new FakeFinderTransport(["몰라요", "정말 몰라요"]);
        var p = await Ai(fake).PickCandidatesAsync("q", ["학년"], Tables, [], ct: Ct);
        Assert.True(p.Fallback);
        Assert.Equal("ui_students.grade_level", p.Candidates.FirstOrDefault()?.Location);
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task FallsBackWhenAllHallucinated()
    {
        var fake = new FakeFinderTransport(["{\"candidates\":[{\"table\":\"tb_student\",\"columns\":[\"grade_cd\"]}]}"]);
        var p = await Ai(fake).PickCandidatesAsync("q", ["학년"], Tables, [], ct: Ct);
        Assert.True(p.Fallback);
        Assert.Equal("ui_students", p.Candidates.FirstOrDefault()?.Table);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task OllamaErrorFallsBackWithReason()
    {
        // 모델이 답을 만들다 실패(HTTP 500)하면 다시 묻지 않고 사전 점수로, 이유를 남긴다
        var fake = new FakeFinderTransport([]);
        var ai = new DataFinderAI(DatabaseKind.Mysql, false, (m, _) =>
        {
            fake.Complete(m);
            throw new OllamaException(OllamaErrorKind.Http, 500, "{\"error\":\"prediction aborted, token repeat limit reached\"}");
        });
        var p = await ai.PickCandidatesAsync("q", ["학년"], Tables, [], ct: Ct);
        Assert.True(p.Fallback);
        Assert.Equal("prediction aborted, token repeat limit reached", p.AiError);
        Assert.Equal(1, fake.Calls);
        Assert.Equal("ui_students.grade_level", p.Candidates.FirstOrDefault()?.Location);
        var v = await ai.JudgeAsync("q", [new DataFinder.SampledCandidate(p.Candidates[0])], Tables, ct: Ct);
        Assert.Equal(DataFinder.Confidence.Low, v?.Confidence);
        Assert.True(v?.Fallback);
        // Ollama 가 꺼져 있으면 그대로 오류
        var off = new DataFinderAI(DatabaseKind.Mysql, false, (_, _) => throw new OllamaException(OllamaErrorKind.NotRunning));
        await Assert.ThrowsAsync<OllamaException>(() => off.PickCandidatesAsync("q", ["학년"], Tables, [], ct: Ct));
    }

    [Fact]
    public async Task JudgeSendsSamplesAndFallsBackToFirstCandidate()
    {
        var c = new DataFinder.Candidate("ui_students", ["grade_level"], "학년 코멘트");
        List<DataFinder.SampledCandidate> sampled = [new(c, new() { ["grade_level"] = ["1", "2", "3"] })];
        var fake = new FakeFinderTransport(["형식 없는 답"]);
        var v = await Ai(fake).JudgeAsync("학년 정보는 어디 있어?", sampled, Tables, ct: Ct);
        Assert.Contains("samples: 1 | 2 | 3", fake.Sent[0][1].Content);
        Assert.Equal(new DataFinder.Verdict("ui_students", ["grade_level"], DataFinder.Confidence.Low, "학년 코멘트",
                                            "SELECT * FROM `ui_students`", Fallback: true), v);
    }
}
