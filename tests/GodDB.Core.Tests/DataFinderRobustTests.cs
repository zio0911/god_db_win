using System.Text.RegularExpressions;
using GodDB.Core.Ai;
using GodDB.Core.Dialects;
using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

// Mac Tests/GodDBCoreTests/DataFinderRobustTests.swift (#151: 약어 사전 · 값 모양 · 2단계 후보 · 모델 확인 · NOLOCK)
public class DataFinderRobustTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ColumnMeta Col(string name, string type = "varchar(50)", string comment = "") => new(name, type, true, comment: comment);

    /// 코멘트 없는 영어 약어 스키마 (학년 = schyr, 회원 등급 = mbr_grd_cd 가 섞여 있음)
    private static readonly TableMeta Student = new("tb_stdnt_info", columns: [
        Col("stdnt_no", "int"), Col("stdnt_nm"), Col("schyr", "tinyint"), Col("ban_cd"), Col("schl_cd"),
    ]);
    private static readonly TableMeta School = new("tb_schl_info", columns: [Col("schl_cd"), Col("schl_nm"), Col("schl_addr")]);
    private static readonly TableMeta Member = new("tb_mbr_info", columns: [
        Col("mbr_no", "int"), Col("mbr_nm"), Col("mbr_tel_no"), Col("mbr_grd_cd"), Col("reg_dt", "datetime"),
    ]);
    private static readonly TableMeta Sales = new("tb_sales_yr_sum", columns: [Col("base_yr", "int"), Col("sale_amt", "decimal")]);
    private static TableMeta Noise(int i) => new($"tb_sys_log_{i}", columns: [Col("log_seq", "bigint"), Col("req_url", "varchar(255)"),
                                                                           Col("resp_body", "text"), Col("crt_dtm", "datetime")]);
    /// 회원 · 매출 테이블이 학생보다 앞에 오게 (같은 점수면 앞 테이블이 이기는 상황을 만든다)
    private static readonly List<TableMeta> Small = [Member, Sales, Student, School];
    private static readonly List<TableMeta> Large = [.. Enumerable.Range(0, 200).Select(Noise), .. Small];

    // MARK: - 이름 조각 · 사전

    [Fact]
    public void TokenizesNames()
    {
        Assert.Equal(["tb", "stdnt", "info"], AbbreviationDictionary.Tokens("tb_stdnt_info"));
        Assert.Equal(["stdnt", "nm"], AbbreviationDictionary.Tokens("StdntNm"));
        Assert.Equal(["http", "server", "2", "id"], AbbreviationDictionary.Tokens("HTTPServer2Id"));
        Assert.Equal(["schyr"], AbbreviationDictionary.Tokens("schyr"));
        Assert.Equal(["dbo", "tb", "mbr", "info"], AbbreviationDictionary.Tokens("dbo.TB_MBR-INFO"));
    }

    [Fact]
    public void ConceptsForTerms()
    {
        Assert.Contains("schyr", AbbreviationDictionary.Concepts("학년").First().English);
        // 사전 말과 같으면 그 항목만 (전화번호 → 전화)
        Assert.Equal(["phone"], AbbreviationDictionary.Concepts("전화번호").Select(e => e.English[0]));
        // 붙여 쓴 말은 나눠서
        Assert.Equal(["student", "name"], AbbreviationDictionary.Concepts("학생이름").Select(e => e.English[0]));
        // 영어 질문 말
        Assert.Equal("학년", AbbreviationDictionary.Concepts("grade").First().Korean[0]);
        Assert.Empty(AbbreviationDictionary.Concepts("없는말"));
    }

    [Fact]
    public void DictionaryIsDataWithReasonableSize()
    {
        var n = AbbreviationDictionary.Entries.Count;
        Assert.InRange(n, 60, 100);
        foreach (var e in AbbreviationDictionary.Entries)
        {
            Assert.True(e.Korean.Count > 0 && e.English.Count > 0);
            Assert.All(e.English, w => Assert.Equal(w.ToLowerInvariant(), w));
            if (e.ValuePattern is { } p) _ = new Regex(p);
        }
    }

    [Fact]
    public void ExpansionText()
    {
        var t = AbbreviationDictionary.ExpansionText(["학년", "없는말"]);
        Assert.StartsWith("학년 → grade, grd, schyr", t);
        Assert.DoesNotContain("없는말", t);
    }

    // MARK: - AI 없이 (사전 점수)

    [Fact]
    public void ScorerAloneFindsSchyrForGrade()
    {
        var c = DataFinder.FallbackCandidates(["학년"], Small);
        Assert.Equal("tb_stdnt_info", c.FirstOrDefault()?.Table);
        Assert.Equal("schyr", c.FirstOrDefault()?.Columns.FirstOrDefault());
        // 큰 스키마에서도
        Assert.Equal("tb_stdnt_info.schyr", DataFinder.FallbackCandidates(DataFinder.KeyTerms("학년 정보는 어디 있어?"), Large).FirstOrDefault()?.Location);
    }

    [Fact]
    public void ScorerFindsSchoolNameAndMemberPhone()
    {
        var school = DataFinder.FallbackCandidates(DataFinder.KeyTerms("학교 이름은 어디야?"), Large);
        Assert.Equal("tb_schl_info", school.FirstOrDefault()?.Table);
        Assert.Equal("schl_nm", school.FirstOrDefault()?.Columns.FirstOrDefault());
        var phone = DataFinder.FallbackCandidates(DataFinder.KeyTerms("회원 전화번호 어디 있어?"), Large);
        Assert.Equal("tb_mbr_info", phone.FirstOrDefault()?.Table);
        Assert.Equal("mbr_tel_no", phone.FirstOrDefault()?.Columns.FirstOrDefault());
    }

    [Fact]
    public void CommentsStillCount()
    {
        var t = new TableMeta("t_x", columns: [Col("c1", comment: "학년코드"), Col("c2")]);
        Assert.Equal("t_x.c1", DataFinder.FallbackCandidates(["학년"], [t]).FirstOrDefault()?.Location);
    }

    // MARK: - 값 모양

    [Fact]
    public void ValueHints()
    {
        Assert.Equal(1, AbbreviationDictionary.ValueFit(["학년"], ["1", "2", "3", "4", "5", "6"]));
        Assert.Equal(1, AbbreviationDictionary.ValueFit(["학년"], ["1학년", "2학년", "3 학년"]));
        Assert.Equal(-1, AbbreviationDictionary.ValueFit(["학년"], ["VIP", "GOLD", "SILVER"]));
        Assert.Equal(-1, AbbreviationDictionary.ValueFit(["학년"], ["2019", "2020"]));
        Assert.Equal(0, AbbreviationDictionary.ValueFit(["학교"], ["a", "b"]));   // 모양 없음
        Assert.Equal(0, AbbreviationDictionary.ValueFit(["학년"], ["1"]));        // 값이 너무 적음
        Assert.Equal(1, AbbreviationDictionary.ValueFit(["전화번호"], ["010-1234-5678", "02-123-4567"]));
        Assert.Equal("values look like 학년", AbbreviationDictionary.ValueHintText(["학년"], ["1", "2"]));
        Assert.Equal("values do not look like 학년", AbbreviationDictionary.ValueHintText(["학년"], ["VIP", "GOLD"]));
    }

    [Fact]
    public void ValuesReorderFallbackCandidates()
    {
        // 회원 등급(grd)을 먼저 둔 후보라도 값이 1~6 인 schyr 가 앞으로
        var grd = new DataFinder.SampledCandidate(new DataFinder.Candidate("tb_mbr_info", ["mbr_grd_cd"]));
        grd.Record("mbr_grd_cd", new DataFinder.SampleOutcome.Values(["VIP", "GOLD", "SILVER"]));
        var yr = new DataFinder.SampledCandidate(new DataFinder.Candidate("tb_stdnt_info", ["schyr"]));
        yr.Record("schyr", new DataFinder.SampleOutcome.Values(["1", "2", "3", "4", "5", "6"]));
        var ranked = DataFinder.RankSampled(["학년"], [grd, yr], Small);
        Assert.Equal(["tb_stdnt_info", "tb_mbr_info"], ranked.Select(s => s.Candidate.Table));
    }

    [Fact]
    public void JudgePromptCarriesValueHint()
    {
        var s = new DataFinder.SampledCandidate(new DataFinder.Candidate("tb_stdnt_info", ["schyr"]));
        s.Record("schyr", new DataFinder.SampleOutcome.Values(["1", "2", "3"]));
        var m = DataFinder.JudgeMessages("학년 정보는 어디 있어?", [s], Small, DatabaseKind.Mssql, english: false, terms: ["학년"]);
        Assert.Contains("samples: 1 | 2 | 3", m[1].Content);
        Assert.Contains("hint: values look like 학년", m[1].Content);
    }

    [Fact]
    public void SampleStatusText()
    {
        var c = new DataFinder.Candidate("t", ["a", "b"]);
        Assert.Null(new DataFinder.SampledCandidate(c).SampleStatus);
        var s = new DataFinder.SampledCandidate(c);
        s.Record("a", new DataFinder.SampleOutcome.TimedOut());
        Assert.Equal("시간 초과", s.SampleStatus);
        s.Record("b", new DataFinder.SampleOutcome.Values(["1", "2"]));
        Assert.Equal("값 2개", s.SampleStatus);
        var f = new DataFinder.SampledCandidate(c);
        f.Record("a", new DataFinder.SampleOutcome.Failed("권한 없음"));
        Assert.Equal("조회 실패: 권한 없음", f.SampleStatus);
        Assert.True(f.Samples.ContainsKey("a"));
        Assert.Null(f.Samples["a"]);
    }

    // MARK: - NOLOCK

    [Fact]
    public void MssqlSampleUsesNoLock()
    {
        Assert.Equal("SELECT TOP 200 [schyr] FROM [tb_stdnt_info] WITH (NOLOCK) WHERE [schyr] IS NOT NULL",
                     DataFinder.SampleSql("tb_stdnt_info", "schyr", new MssqlDialect()));
        Assert.DoesNotContain("NOLOCK", DataFinder.SampleSql("t", "c", new MySqlDialect()));
        Assert.Equal(10, DataFinder.SampleTimeout);
    }

    // MARK: - 2단계 (큰 스키마)

    [Fact]
    public void TwoStagePromptKeepsScoredTablesWhenTruncated()
    {
        Assert.False(DataFinder.NeedsTwoStages(Small));
        Assert.True(DataFinder.NeedsTwoStages(Large, budget: 2_000));
        // 학생 테이블은 목록 맨 끝이지만 사전 점수로 앞에, 맞은 컬럼과 함께
        var m = DataFinder.TableMessages("학년 정보는 어디 있어?", ["학년"], DatabaseKind.Mssql, Large, [], budget: 1_000);
        var user = m.Messages[1].Content;
        Assert.Contains("학년 → grade, grd, schyr", user);
        Assert.Contains("tb_stdnt_info  [matching columns: schyr", user);
        Assert.True(m.TablesSent < Large.Count);
        Assert.Contains("more tables not shown", user);
        Assert.Contains("{\"tables\"", m.Messages[0].Content);
        // 컬럼 정의는 보내지 않는다 (테이블 이름 · 코멘트만)
        Assert.DoesNotContain("varchar", user);
    }

    [Fact]
    public void ParsesStage1Tables()
    {
        Assert.Equal(["tb_stdnt_info", "tb_schl_info"], DataFinder.ParseTables("{\"tables\":[\"TB_STDNT_INFO\",\"nope\",\"tb_schl_info\"]}", Small)!);
        Assert.Equal(["tb_mbr_info"], DataFinder.ParseTables("{\"candidates\":[{\"table\":\"tb_mbr_info\"}]}", Small)!);
        Assert.Null(DataFinder.ParseTables("학생 테이블이요", Small));
        var many = "{\"tables\":[" + string.Join(",", Enumerable.Range(0, 20).Select(i => $"\"tb_sys_log_{i}\"")) + "]}";
        Assert.Equal(10, DataFinder.ParseTables(many, Large)?.Count);
    }

    [Fact]
    public void Stage2AddsTopScoredTables()
    {
        var t = DataFinder.Stage2Tables(["tb_sys_log_1"], ["학년"], Large);
        Assert.Equal("tb_sys_log_1", t.FirstOrDefault()?.Name);
        Assert.Contains(t, x => x.Name == "tb_stdnt_info");
        Assert.True(t.Count <= 1 + DataFinder.ScorerExtraTables);
    }

    /// 정해 둔 답(또는 오류)을 차례로
    private sealed class Fake(IEnumerable<Func<string>> replies)
    {
        private readonly Lock _lock = new();
        private readonly Queue<Func<string>> _replies = new(replies);
        public List<List<OllamaMessage>> Sent { get; } = [];

        public string Next(IReadOnlyList<OllamaMessage> m)
        {
            Func<string> r;
            lock (_lock)
            {
                Sent.Add(m.ToList());
                r = _replies.Count == 0 ? () => "" : _replies.Dequeue();
            }
            return r();
        }
    }

    [Fact]
    public async Task TwoStageFlowWithFakeTransport()
    {
        var fake = new Fake([() => "{\"tables\":[\"tb_stdnt_info\"]}",
                             () => "{\"candidates\":[{\"table\":\"tb_stdnt_info\",\"columns\":[\"schyr\"],\"reason\":\"학년\"}]}"]);
        var ai = new DataFinderAI(DatabaseKind.Mssql, false, (m, _) => Task.FromResult(fake.Next(m)));
        var p = await ai.PickCandidatesAsync("학년 정보는 어디 있어?", ["학년"], Large, [], budget: 2_000, ct: Ct);
        Assert.True(p.TwoStage);
        Assert.False(p.Fallback);
        Assert.Equal(["tb_stdnt_info.schyr"], p.Candidates.Select(c => c.Location));
        Assert.Equal(2, fake.Sent.Count);
        // 2단계에는 고른 테이블의 컬럼 정의만
        var stage2 = fake.Sent[1][1].Content;
        Assert.Contains("schyr tinyint", stage2);
        Assert.DoesNotContain("tb_sys_log_150", stage2);
    }

    [Fact]
    public async Task FindWithoutAIUsesValuesAndSaysSo()
    {
        // 모델 오류 → 후보는 사전 점수, 판단은 값 모양 (AI 판단 요청은 보내지 않는다)
        var fake = new Fake([() => throw new OllamaException(OllamaErrorKind.Http, 500, "{\"error\":\"prediction aborted, token repeat limit reached\"}")]);
        var ai = new DataFinderAI(DatabaseKind.Mssql, false, (m, _) => Task.FromResult(fake.Next(m)));
        var values = new Dictionary<string, List<string>> { ["mbr_grd_cd"] = ["VIP", "GOLD"], ["schyr"] = ["1", "2", "3", "4", "5", "6"] };
        var found = await ai.FindAsync("학년 정보는 어디 있어?", ["학년"], Small, [],
            (_, col) => Task.FromResult<DataFinder.SampleOutcome>(new DataFinder.SampleOutcome.Values(values.GetValueOrDefault(col) ?? ["x", "y"])), ct: Ct);
        Assert.True(found.UsedFallback);
        Assert.Equal("prediction aborted, token repeat limit reached", found.AiError);
        Assert.Equal("tb_stdnt_info", found.Verdict?.Table);
        Assert.Equal("schyr", found.Verdict?.Columns.FirstOrDefault());
        Assert.Equal("tb_stdnt_info", found.Sampled.FirstOrDefault()?.Candidate.Table);
        Assert.Single(fake.Sent);
        Assert.True(found.SampleQueries > 0);
    }

    [Fact]
    public async Task FindCancelsWhenSamplesNotAllowed()
    {
        var ai = new DataFinderAI(DatabaseKind.Mssql, false, (_, _) => Task.FromResult("{\"candidates\":[{\"table\":\"tb_stdnt_info\",\"columns\":[\"schyr\"]}]}"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ai.FindAsync("q", ["학년"], Small, [], (_, _) => Task.FromResult<DataFinder.SampleOutcome>(new DataFinder.SampleOutcome.Values([])),
                         allowSamples: () => Task.FromResult(false), ct: Ct));
    }

    // MARK: - 모델 확인

    [Fact]
    public async Task ProbeOK() => Assert.True((await ModelProbe.CheckAsync(_ => Task.CompletedTask, ct: Ct)).IsOK);

    [Fact]
    public async Task ProbeFailureGivesShortReason()
    {
        var h = await ModelProbe.CheckAsync(_ => throw new OllamaException(OllamaErrorKind.Http, 500, "{\"error\":\"prediction aborted, token repeat limit reached\"}"), ct: Ct);
        Assert.Equal(new ModelHealth.Failed("prediction aborted, token repeat limit reached"), h);
        Assert.Equal("prediction aborted, token repeat limit reached", h.Reason);
        Assert.False(h.IsOK);
        var plain = await ModelProbe.CheckAsync(_ => throw new OllamaException(OllamaErrorKind.Http, 404, "model 'x' not found"), ct: Ct);
        Assert.Equal(new ModelHealth.Failed("model 'x' not found"), plain);
    }

    [Fact]
    public async Task ProbeTimesOut()
    {
        var h = await ModelProbe.CheckAsync(c => Task.Delay(3_000, c), timeout: 0.05, ct: Ct);
        Assert.IsType<ModelHealth.TimedOut>(h);
        Assert.Equal("20초 안에 답이 없어요", h.Reason);
    }

    [Fact]
    public void ProbeCache()
    {
        var cache = new ModelHealthCache();
        Assert.Null(cache.Get("k"));
        cache.Set("k", new ModelHealth.Failed("x"));
        Assert.Equal(new ModelHealth.Failed("x"), cache.Get("k"));
    }

    [Fact]
    public async Task ProbeRefusesRemoteOllama()
    {
        var h = await ModelProbe.CheckAsync("m", new OllamaClient(new Uri("http://10.0.0.5:11434")), new ModelHealthCache(), ct: Ct);
        Assert.IsType<ModelHealth.Failed>(h);
    }

    // MARK: - 추천 모델

    [Fact]
    public void DefaultModelPrefersQwen3()
    {
        // 기본: qwen3.8:27b (48GB 이상 — Mac 은 qwen3.8:27b-mlx, MLX 는 Apple 전용이라 Windows 는 일반 태그), 다음 qwen3:14b (24GB), 그다음 예전 추천
        Assert.Equal("qwen3.8:27b", AISettings.RecommendedModels[0]);
        Assert.Equal("qwen3.8:27b", AISettings.DefaultRecommendedModel);
        Assert.Equal("qwen3.8:27b", AISettings.DefaultModel(["qwen3:14b", "qwen3.8:27b-mlx", "qwen3.8:27b"]));
        Assert.Equal("qwen3:14b", AISettings.DefaultModel(["qwen3:30b", "qwen3:14b"]));
        Assert.Equal("qwen3:30b", AISettings.DefaultModel(["laguna-xs-2.1:q8_0", "qwen3:30b"]));
        Assert.Equal("qwen2.5-coder:14b", AISettings.DefaultModel(["llama3", "qwen2.5-coder:32b", "qwen2.5-coder:14b"]));
        Assert.Equal("llama3", AISettings.DefaultModel(["llama3", "mistral"]));
        Assert.Null(AISettings.DefaultModel([]));
        Assert.Equal(["qwen3:30b", "qwen2.5-coder:14b", "a"], AISettings.RecommendedFirst(["a", "qwen2.5-coder:14b", "qwen3:30b"]));
    }
}
