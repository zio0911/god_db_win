using System.Diagnostics;
using GodDB.Core;
using GodDB.Core.Ai;
using GodDB.Core.Dialects;
using GodDB.Core.Drivers;
using GodDB.Core.Schema;

namespace GodDB.Integration.Tests;

// Mac Tests/GodDBCoreTests/DataFinderLiveTests.swift (#151)
// AI 데이터 찾기 실제 확인: 약 150개 테이블의 약어 스키마를 임시 DB 에 만들고 로컬 Ollama 로 찾는다.
// - GODDB_DATAFINDER_LIVE=1 일 때만 (Ollama 가 없으면 건너뜀). SQL Server(GODDB_MSSQL_PORT) · MySQL(GODDB_IT_PORT) 중 있는 것으로.
// - 모델: GODDB_DATAFINDER_MODEL (기본 AISettings.DefaultRecommendedModel), 고장 난 모델: GODDB_DATAFINDER_BROKEN_MODEL (기본 laguna-xs-2.1:q8_0).
// - 임시 DB 이름은 실행마다 다르게 (다른 테스트와 같은 Docker DB 를 쓰므로), 끝나면 지운다. 사용자의 AI 설정은 건드리지 않는다.
internal static class FinderSeed
{
    public static bool Live => Environment.GetEnvironmentVariable("GODDB_DATAFINDER_LIVE") == "1";
    public static string Model => It.Env("GODDB_DATAFINDER_MODEL") ?? AISettings.DefaultRecommendedModel;
    public static string BrokenModel => It.Env("GODDB_DATAFINDER_BROKEN_MODEL") ?? "laguna-xs-2.1:q8_0";

    public static void RequireLive()
    {
        if (!Live) Assert.Skip("GODDB_DATAFINDER_LIVE=1 일 때만 (로컬 Ollama 필요)");
    }

    /// Ollama 가 이 PC 에서 돌고 있지 않으면 건너뛴다
    public static async Task RequireOllamaAsync()
    {
        if (await new OllamaClient().StatusAsync() is not OllamaStatus.Running) Assert.Skip("로컬 Ollama 가 실행 중이 아니에요");
    }

    public sealed record Table(string Name, string Comment, (string Name, string Type)[] Columns, List<string[]> Rows);

    /// 핵심 테이블 + 헷갈리게 하는 테이블 + 잡음 테이블 (약 150개). 이름은 모두 지어낸 일반적인 약어.
    public static List<Table> Tables()
    {
        var t = new List<Table>
        {
            new("tb_stdnt_info", "", [("stdnt_no", "int"), ("stdnt_nm", "nvarchar(50)"), ("schyr", "int"), ("ban_cd", "varchar(10)"),
                                      ("schl_cd", "varchar(10)"), ("brth_ymd", "char(8)")],
                Enumerable.Range(1, 30).Select(i => new[] { $"{i}", $"'학생{i}'", $"{i % 6 + 1}", $"'B{i % 4 + 1}'", $"'S{i % 5 + 1}'", $"'20{10 + i % 8}0301'" }).ToList()),
            new("tb_schl_info", "", [("schl_cd", "varchar(10)"), ("schl_nm", "nvarchar(100)"), ("schl_addr", "nvarchar(200)"), ("rgn_cd", "varchar(10)")],
                Enumerable.Range(1, 5).Select(i => new[] { $"'S{i}'", $"'한빛{new[] { "초", "중", "고", "초", "중" }[i - 1]}등학교'", $"'서울시 어느구 {i}길'", $"'R{i}'" }).ToList()),
            new("tb_mbr_info", "", [("mbr_no", "int"), ("mbr_id", "varchar(30)"), ("mbr_nm", "nvarchar(50)"), ("mbr_tel_no", "varchar(20)"),
                                    ("mbr_grd_cd", "varchar(10)"), ("email_addr", "varchar(100)"), ("reg_dt", "date")],
                Enumerable.Range(1, 20).Select(i => new[] { $"{i}", $"'user{i}'", $"'회원{i}'", $"'010-{1000 + i}-{2000 + i}'",
                                                            $"'{new[] { "VIP", "GOLD", "SILVER" }[i % 3]}'", $"'u{i}@example.com'", $"'2024-01-{10 + i}'" }).ToList()),
            // 헷갈리게: 연도(yr), 회원 등급 이력(grd), 지점 전화, 교사 이름 · 전화, 학급
            new("tb_sales_yr_sum", "", [("base_yr", "int"), ("sale_amt", "decimal(15,2)")],
                Enumerable.Range(2019, 6).Select(y => new[] { $"{y}", $"{y * 10}" }).ToList()),
            new("tb_mbr_grd_hist", "회원 등급 변경 이력", [("hist_seq", "int"), ("mbr_no", "int"), ("bf_grd_cd", "varchar(10)"), ("af_grd_cd", "varchar(10)"), ("chg_dt", "date")],
                [["1", "1", "'SILVER'", "'GOLD'", "'2024-02-01'"]]),
            new("tb_brnc_info", "", [("brnc_cd", "varchar(10)"), ("brnc_nm", "nvarchar(50)"), ("brnc_tel_no", "varchar(20)"), ("brnc_addr", "nvarchar(200)")],
                [["'B1'", "'강남센터'", "'02-555-0101'", "'서울'"]]),
            new("tb_tchr_info", "", [("tchr_no", "int"), ("tchr_nm", "nvarchar(50)"), ("schl_cd", "varchar(10)"), ("sbj_cd", "varchar(10)")],
                [["1", "'교사1'", "'S1'", "'MATH'"]]),
            new("tb_cls_info", "", [("cls_cd", "varchar(10)"), ("cls_nm", "nvarchar(50)"), ("tchr_no", "int")], [["'C1'", "'1반'", "1"]]),
        };
        // 잡음: 업무 영역 × 종류. 일부만 한국어 코멘트.
        string[] areas = ["ord", "prod", "pay", "dlv", "cpn", "bbs", "ntc", "inq", "alrm", "auth", "cfg", "stat", "rpt", "cart", "stk", "rfnd", "ctrt", "acct", "file", "menu"];
        string[] areaKo = ["주문", "상품", "결제", "배송", "쿠폰", "게시판", "공지", "문의", "알림", "권한", "설정", "통계", "보고서", "장바구니", "재고", "환불", "계약", "계좌", "첨부파일", "메뉴"];
        string[] kinds = ["mst", "dtl", "hist", "map", "tmp", "bak", "sum"];
        string[] kindKo = ["마스터", "상세", "이력", "매핑", "임시", "백업", "집계"];
        for (var ai = 0; ai < areas.Length; ai++)
            for (var ki = 0; ki < kinds.Length; ki++)
            {
                var a = areas[ai];
                var comment = (ai + ki) % 4 == 0 ? $"{areaKo[ai]} {kindKo[ki]}" : "";
                t.Add(new Table($"tb_{a}_{kinds[ki]}", comment, [
                    ($"{a}_seq", "bigint"), ($"{a}_cd", "varchar(20)"), ($"{a}_nm", "nvarchar(100)"), ($"{a}_stts_cd", "varchar(10)"),
                    ("use_yn", "char(1)"), ("rgst_id", "varchar(30)"), ("rgst_dtm", "datetime"), ("updt_id", "varchar(30)"), ("updt_dtm", "datetime"),
                ], []));
            }
        return t;
    }

    /// 임시 DB 를 만들고 그 DB 에 붙은 세션을 돌려준다
    public static async Task<DbSession> SetupAsync(DatabaseKind kind, string database)
    {
        await using (var admin = await It.ConnectAsync(kind, kind == DatabaseKind.Mssql ? "master" : "mysql"))
        {
            await admin.RawAsync(kind == DatabaseKind.Mssql ? $"CREATE DATABASE {database}" : $"CREATE DATABASE {database} DEFAULT CHARACTER SET utf8mb4");
        }
        var s = await It.ConnectAsync(kind, database);
        foreach (var t in Tables())
        {
            var cols = string.Join(", ", t.Columns.Select(c => $"{c.Name} {(kind == DatabaseKind.Mysql ? c.Type.Replace("nvarchar", "varchar") : c.Type)} NULL"));
            if (kind == DatabaseKind.Mysql)
            {
                await s.RawAsync($"CREATE TABLE {t.Name} ({cols})" + (t.Comment.Length == 0 ? "" : $" COMMENT='{t.Comment}'"));
            }
            else
            {
                await s.RawAsync($"CREATE TABLE dbo.{t.Name} ({cols})");
                if (t.Comment.Length > 0)
                    await s.RawAsync($"EXEC sp_addextendedproperty 'MS_Description', N'{t.Comment}', 'SCHEMA', 'dbo', 'TABLE', '{t.Name}'");
            }
            if (t.Rows.Count > 0)
            {
                var values = t.Rows.Select(r => "(" + string.Join(", ", r.Select(v => kind == DatabaseKind.Mssql && v.StartsWith('\'') ? "N" + v : v)) + ")");
                await s.RawAsync($"INSERT INTO {t.Name} VALUES " + string.Join(", ", values));
            }
        }
        return s;
    }

    public static Task TeardownAsync(DatabaseKind kind, string database) => kind == DatabaseKind.Mssql
        ? It.CleanupAsync(kind, "master", It.DropMssqlDatabase(database))
        : It.CleanupAsync(kind, "mysql", $"DROP DATABASE IF EXISTS {database}");

    /// 앱과 같은 길: 따로 연 연결에서 NOLOCK 샘플 조회
    public static Func<string, string, Task<DataFinder.SampleOutcome>> Sampler(DbSession session)
    {
        var side = session.SideConnection();
        var dialect = session.Dialect;
        return async (table, column) =>
        {
            var sql = DataFinder.SampleSql(table, column, dialect);
            try
            {
                if (side == null) return new DataFinder.SampleOutcome.Failed("no side connection");
                var r = await side.QueryAsync(sql, DataFinder.SampleTimeout);
                return new DataFinder.SampleOutcome.Values(DataFinder.CleanSamples(r.Rows.Select(x => x.FirstOrDefault())));
            }
            catch (SideConnection.TimedOutException) { return new DataFinder.SampleOutcome.TimedOut(); }
            catch (Exception e) { return new DataFinder.SampleOutcome.Failed(e.Message); }
        };
    }

    public static readonly (string Q, string Table, string Column)[] Questions =
    [
        ("학년 정보는 어디 있어?", "tb_stdnt_info", "schyr"),
        ("학교 이름은 어디야?", "tb_schl_info", "schl_nm"),
        ("회원 전화번호 어디 있어?", "tb_mbr_info", "mbr_tel_no"),
    ];
}

public class DataFinderLiveTests
{
    [Theory]
    [InlineData(DatabaseKind.Mssql)]
    [InlineData(DatabaseKind.Mysql)]
    public async Task FindsOnSeededSchema(DatabaseKind kind)
    {
        FinderSeed.RequireLive();
        TestDb.Port(kind);
        await FinderSeed.RequireOllamaAsync();
        var db = "goddb_finder_" + It.Suffix();
        try
        {
            await using var s = await FinderSeed.SetupAsync(kind, db);
            var tables = await s.LoadSchemaAsync(db);
            Assert.True(tables.Count >= 140);
            Assert.True(DataFinder.NeedsTwoStages(tables));
            var client = new OllamaClient();
            Assert.True((await ModelProbe.CheckAsync(FinderSeed.Model, client, useCache: false)).IsOK);
            var ai = DataFinderAI.Ollama(client, FinderSeed.Model, kind, english: false);
            foreach (var item in FinderSeed.Questions)
            {
                var terms = DataFinder.KeyTerms(item.Q);
                var sw = Stopwatch.StartNew();
                var found = await ai.FindAsync(item.Q, terms, tables, [], FinderSeed.Sampler(s));
                var v = found.Verdict;
                TestContext.Current.SendDiagnosticMessage(
                    $"LIVE[{kind.Title()}] {item.Q} → {(v == null ? "null" : $"{v.Table}.{string.Join(",", v.Columns)}")} · {v?.Confidence} · fallback={found.UsedFallback}"
                    + $" · twoStage={found.Picked.TwoStage} · candidates=[{string.Join(", ", found.Picked.Candidates.Select(c => c.Location))}] · {(int)sw.Elapsed.TotalSeconds}s");
                Assert.False(found.UsedFallback, item.Q);
                Assert.Equal(item.Table, v?.Table);
                Assert.Contains(item.Column, v!.Columns);
            }
        }
        finally { await FinderSeed.TeardownAsync(kind, db); }
    }

    [Fact]
    public async Task NoAIFallbackOnSeededSchema()
    {
        FinderSeed.RequireLive();
        TestDb.Port(DatabaseKind.Mssql);
        var db = "goddb_finder_" + It.Suffix();
        try
        {
            await using var s = await FinderSeed.SetupAsync(DatabaseKind.Mssql, db);
            var tables = await s.LoadSchemaAsync(db);
            var ai = new DataFinderAI(DatabaseKind.Mssql, false, (_, _) => throw new OllamaException(OllamaErrorKind.Http, 500, "{\"error\":\"broken\"}"));
            foreach (var item in FinderSeed.Questions)
            {
                var found = await ai.FindAsync(item.Q, DataFinder.KeyTerms(item.Q), tables, [], FinderSeed.Sampler(s));
                Assert.True(found.UsedFallback);
                Assert.Equal(item.Table, found.Verdict?.Table);
                Assert.Equal(item.Column, found.Verdict?.Columns.FirstOrDefault());
            }
        }
        finally { await FinderSeed.TeardownAsync(DatabaseKind.Mssql, db); }
    }

    [Fact]
    public async Task BrokenModelProbeFailsClearly()
    {
        FinderSeed.RequireLive();
        await FinderSeed.RequireOllamaAsync();
        var before = AISettings.Model;
        var h = await ModelProbe.CheckAsync(FinderSeed.BrokenModel, new OllamaClient(), new ModelHealthCache());
        Assert.False(h.IsOK);
        if (h is ModelHealth.Failed f) Assert.True(f.Why.Length > 0 && !f.Why.Contains('{'));
        Assert.Equal(before, AISettings.Model);   // 사용자 설정은 그대로
        var good = await ModelProbe.CheckAsync(FinderSeed.Model, new OllamaClient(), new ModelHealthCache());
        Assert.True(good.IsOK);
    }
}

/// 따로 연 연결의 샘플 조회: 잠금에 막혀도 NOLOCK 은 바로, 시간이 넘은 조회는 그 연결만 끊고 사용자 세션은 그대로 (SQL Server)
public class SideConnectionTests
{
    [Fact]
    public async Task TimeoutDoesNotBreakMainSession()
    {
        TestDb.Port(DatabaseKind.Mssql);
        var db = "goddb_side_" + It.Suffix();
        await It.CleanupAsync(DatabaseKind.Mssql, "master", $"CREATE DATABASE {db}");
        try
        {
            await using var user = await It.ConnectAsync(DatabaseKind.Mssql, db);
            await using var locker = await It.ConnectAsync(DatabaseKind.Mssql, db);
            await user.RawAsync("CREATE TABLE tb_lock_t (id int, v int)");
            await user.RawAsync("INSERT INTO tb_lock_t VALUES (1, 1), (2, 2)");
            // 다른 세션이 커밋하지 않은 변경으로 잠금을 쥐고 있다
            locker.SetAutoCommit(false);
            await locker.ExecuteAsync("UPDATE tb_lock_t SET v = 9", limit: 0);
            Assert.True(locker.TransactionState.Open);
            try
            {
                var side = user.SideConnection()!;
                Assert.NotNull(side);
                // NOLOCK 은 잠금에 막히지 않는다
                var r = await side.QueryAsync(DataFinder.SampleSql("tb_lock_t", "v", new MssqlDialect()), 5);
                Assert.Equal(2, r.Rows.Count);
                // NOLOCK 없는 조회는 막혀 시간 초과 → 그 연결만 버린다
                await Assert.ThrowsAsync<SideConnection.TimedOutException>(() => side.QueryAsync("SELECT v FROM tb_lock_t", 2));
                // 사용자 세션은 그대로 쓸 수 있고, 따로 연 연결도 새로 붙어 다시 쓸 수 있다
                Assert.Equal(db, (await user.RawAsync("SELECT DB_NAME()")).Rows.Single()[0]);
                var again = await side.QueryAsync(DataFinder.SampleSql("tb_lock_t", "id", new MssqlDialect()), 5);
                Assert.Equal(2, again.Rows.Count);
                await side.CloseAsync();
            }
            finally { try { await locker.RollbackAsync(); } catch { } }
        }
        finally { await It.CleanupAsync(DatabaseKind.Mssql, "master", It.DropMssqlDatabase(db)); }
    }

    /// MySQL: 따로 연 연결은 사용자 세션이 보던 DB 로 붙고 읽기전용 설정도 같다
    [Fact]
    public async Task SideConnectionUsesCurrentDatabaseAndReadOnly()
    {
        TestDb.Port(DatabaseKind.Mysql);
        var p = It.MySql("mysql") with { ReadOnly = true };
        await using var user = await DbSession.ConnectAsync(p, It.MySqlPassword);
        await user.UseAsync("information_schema");
        var side = user.SideConnection()!;
        try
        {
            Assert.Equal("information_schema", (await side.QueryAsync("SELECT DATABASE()", 5)).Rows.Single()[0]);
            Assert.Equal("1", (await side.QueryAsync("SELECT @@session.transaction_read_only", 5)).Rows.Single()[0]);
        }
        finally { await side.CloseAsync(); }
    }
}
