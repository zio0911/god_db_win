using GodDB.Core;
using GodDB.Core.Ai;
using GodDB.Core.Drivers;

namespace GodDB.Integration.Tests;

/// DbSession.TuningContextAsync (Docker 테스트 DB) 와 Mac 판 TuningTests.swift 의 "Tuning live" (로컬 Ollama 까지).
/// 테스트마다 이름이 겹치지 않는 테이블을 만들고 지운다.
public class TuningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(DbSession Session, string Table)> SetUpAsync(DatabaseKind kind)
    {
        var s = await DbSession.ConnectAsync(TestDb.Profile(kind), TestDb.PasswordFor(kind), Ct);
        await s.UseAsync(Container(kind), Ct);
        var table = $"tb_tune_{Guid.NewGuid():N}"[..20];
        var dt = kind == DatabaseKind.Postgres ? "timestamp" : "datetime";
        await s.RawAsync($"CREATE TABLE {table} (member_id bigint PRIMARY KEY, email varchar(100), status char(1), reg_dt {dt})", Ct);
        await s.RawAsync($"INSERT INTO {table} VALUES (1, 'a@example.com', 'A', '2024-01-01 00:00:00'), (2, 'b@example.com', 'B', '2024-01-02 00:00:00')", Ct);
        return (s, table);
    }

    private static string Container(DatabaseKind kind) => kind == DatabaseKind.Postgres ? "public" : "shop";

    private static async Task TearDownAsync(DbSession s, string table)
    {
        try { await s.RawAsync($"DROP TABLE {table}"); } catch { }
        await s.DisposeAsync();
    }

    [Theory]
    [InlineData(DatabaseKind.Mysql)]
    [InlineData(DatabaseKind.Postgres)]
    public async Task CollectsTablesAndPlan(DatabaseKind kind)
    {
        TestDb.Port(kind);
        var (s, table) = await SetUpAsync(kind);
        try
        {
            var tables = await s.LoadSchemaAsync(Container(kind), Ct);
            var sql = $"SELECT m.member_id, m.email FROM {table} m WHERE m.status = 'A' ORDER BY m.reg_dt DESC";
            var ctx = await s.TuningContextAsync(sql, tables, Ct);
            Assert.Equal([table], ctx.Tables.Select(t => t.Meta.Name));
            Assert.Contains("CREATE TABLE", ctx.Tables[0].Ddl, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(ctx.Plan);
            Assert.Null(ctx.PlanError);
            Assert.StartsWith(kind.Title(), ctx.Version);
            if (kind == DatabaseKind.Mysql) Assert.Contains(ctx.Warnings, w => w.Message.Contains("풀 테이블 스캔"));
            // 프롬프트에는 데이터 행이 들어가지 않는다
            var prompt = TuningPrompt.Messages(ctx, english: false).Last().Content;
            Assert.DoesNotContain("a@example.com", prompt);

            // 실행 계획을 못 얻으면 이유를 남긴다
            var bad = await s.TuningContextAsync($"SELECT nope FROM {table}", tables, Ct);
            Assert.Null(bad.Plan);
            Assert.False(string.IsNullOrEmpty(bad.PlanError));
        }
        finally { await TearDownAsync(s, table); }
    }

    /// 실제 로컬 Ollama 로 튜닝 (GODDB_OLLAMA_URL 과 MySQL 테스트 DB 가 있을 때만, 수십 초 걸림)
    [Fact]
    public async Task TunesWithLocalModel()
    {
        var url = Environment.GetEnvironmentVariable("GODDB_OLLAMA_URL");
        if (string.IsNullOrEmpty(url)) Assert.Skip("GODDB_OLLAMA_URL 없음 — 로컬 Ollama 테스트는 건너뜀");
        TestDb.Port(DatabaseKind.Mysql);
        var client = new OllamaClient(new Uri(url!));
        var status = await client.StatusAsync(Ct);
        Assert.True(status.IsRunning);
        var model = Environment.GetEnvironmentVariable("GODDB_OLLAMA_MODEL") is { Length: > 0 } m ? m : TuningSettings.ResolveModel(status.RunningModels, "");
        if (model.Length == 0) Assert.Skip("설치된 Ollama 모델 없음");

        var (s, table) = await SetUpAsync(DatabaseKind.Mysql);
        try
        {
            var tables = await s.LoadSchemaAsync("shop", Ct);
            var sql = $"SELECT member_id, email FROM {table} WHERE status = 'A' ORDER BY reg_dt DESC";
            var ctx = await s.TuningContextAsync(sql, tables, Ct);
            Assert.Equal([table], ctx.Tables.Select(t => t.Meta.Name));
            Assert.NotNull(ctx.Plan);
            Assert.Contains(ctx.Warnings, w => w.Message.Contains("풀 테이블 스캔"));

            var text = await client.ChatTextAsync(model, TuningPrompt.Messages(ctx, english: false), ct: Ct);
            var r = TuningResult.Parse(text);
            TestContext.Current.TestOutputHelper?.WriteLine("TUNING RESULT:\n" + text);
            Assert.NotEmpty(r.Summary);
            Assert.Contains(table.ToUpperInvariant(), r.ImprovedSql?.ToUpperInvariant() ?? "");
            // 개선 SQL 은 실행 계획을 다시 떠볼 수 있어야 한다
            if (r.ImprovedSql is { } improved) await s.ExplainAsync(improved.Trim(';', '\n', ' '), Ct);

            // 같은 흐름을 TuningJob 으로 (화면이 쓰는 경로)
            var job = new TuningJob(Guid.NewGuid(), sql, model, "it");
            await job.RunAsync(s, "shop", tables, client, english: false);
            Assert.Equal(TuningPhase.Done, job.Phase);
            Assert.NotNull(job.Result);
        }
        finally { await TearDownAsync(s, table); }
    }
}
