using System.Diagnostics;
using System.Globalization;
using GodDB.Core.Dialects;
using GodDB.Core.Drivers;
using GodDB.Core.Schema;
using static GodDB.Core.L10n;

namespace GodDB.Core.Results;

/// 내보내는 중 진행 상황 (결과 아래 줄에 표시)
public sealed record ExportProgress(ExportFormat Format, int Written = 0, int? Total = null);

/// 내보내기 설정 창에 넘길 정보 (결과를 보고 미리 계산). Mac: ResultExportUI.swift ExportRequest + AppState.exportResult
public sealed record ExportRequest
{
    public required QueryResult Result { get; init; }
    public required DatabaseKind Kind { get; init; }
    public required int VisibleRows { get; init; }
    /// 쿼리 전체를 다시 조회할 수 있는지 (여러 페이지짜리 SELECT)
    public required bool CanFetchAll { get; init; }
    public int? KnownTotal { get; init; }
    /// 페이지 사이 순서가 안정적인지 (정렬 기준 있음)
    public required bool Stable { get; init; }
    public ResultSort? Sort { get; init; }
    /// SQL INSERT 대상 / 시트 이름 기본값 (테이블 하나짜리 조회가 아니면 "table")
    public required string DefaultTable { get; init; }
    /// 저장 파일 이름 (확장자 없이): 테이블명_날짜시각
    public required string BaseName { get; init; }

    /// 설정 창 부제목
    public string Subtitle => DefaultTable == "table" ? Tr("쿼리 결과") : DefaultTable;

    /// "쿼리 전체 데이터" 카드 설명
    public string FetchAllNote => CanFetchAll
        ? (KnownTotal is int t ? Tr("전체 %@행 · 5,000행씩 다시 조회하며 바로 저장", ResultExportRunner.Number(t))
                               : Tr("COUNT 로 전체 행 수 확인 후 5,000행씩 다시 조회하며 바로 저장"))
        : Tr("결과가 한 페이지뿐이라 보이는 행이 전체예요");

    /// "지금 보이는 행만" 카드 설명
    public string VisibleNote => Tr("화면의 %@행 그대로", ResultExportRunner.Number(VisibleRows));

    /// 전체 내보내기인데 정렬 기준이 없을 때 설정 창 경고
    public static string UnstableWarning =>
        Tr("정렬 기준(ORDER BY 또는 결과에 포함된 기본 키)이 없어 페이지 사이에 행이 빠지거나 겹칠 수 있어요. ORDER BY 를 넣고 다시 실행하는 걸 권장해요.");

    /// 결과를 보고 내보내기 요청을 만든다. 컬럼이 없으면 null.
    /// tables: 사이드바 스키마 (기본 키를 찾아 안정 정렬에 쓴다), now: 파일 이름 시각
    public static ExportRequest? Prepare(QueryResult result, DatabaseKind kind, IReadOnlyList<TableMeta> tables, DateTimeOffset now)
    {
        if (result.Columns.Count == 0) return null;
        // 페이지가 하나뿐이면 지금 보이는 게 전체 결과
        var morePages = result.IsPaged && (result.HasMore || result.Page > 0);
        var dialect = ISqlDialect.Of(kind);
        var canFetchAll = morePages && result.Sql != null && dialect.PagedSql(result.Sql, 1, 0) != null;
        var tableRef = result.Sql is { } s ? ResultEditing.SingleTable(s) : null;
        var bare = tableRef?.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim('`', '"', '[', ']');
        var meta = bare == null ? null : tables.FirstOrDefault(t => string.Equals(t.Name, bare, StringComparison.OrdinalIgnoreCase));
        var pk = meta?.Indexes.FirstOrDefault(i => i.IsPrimary)?.Columns;
        var plan = ResultExportPlan.StableSort(result.Sql ?? "", result.ServerSort, [.. result.Columns.Select(c => c.Name)], pk);
        return new ExportRequest
        {
            Result = result, Kind = kind, VisibleRows = result.Rows.Count, CanFetchAll = canFetchAll,
            KnownTotal = result.TotalCount, Stable = plan.Stable, Sort = plan.Sort,
            DefaultTable = bare ?? "table", BaseName = $"{bare ?? "query"}_{Stamp(now)}",
        };
    }

    /// 파일 이름 시각 yyyyMMdd_HHmmss, 한국 표준시(KST, UTC+9 · 서머타임 없음) 기준.
    /// (Mac 판은 UTC 로 찍혀 한국 시각보다 9시간 늦게 보였다 — Windows 판은 사용자 요청으로 KST)
    public static string Stamp(DateTimeOffset now) =>
        now.ToOffset(TimeSpan.FromHours(9)).ToString("yyyyMMdd'_'HHmmss", CultureInfo.InvariantCulture);

    /// 저장 창 기본 파일 이름
    public string FileName(ExportFormat f) => BaseName + "." + f.FileExtension();
}

/// 설정 창에서 고른 값
public sealed record ExportChoice
{
    public const string LastFormatKey = "GodDB.export.format";

    public ExportFormat Format { get; set; }
    public bool FetchAll { get; set; }
    public bool ExcelBom { get; set; } = true;
    public string TableName { get; set; } = "table";

    /// 설정 창을 열 때: 마지막으로 쓴 형식(없으면 CSV), 전체 가능하면 전체
    public static ExportChoice Initial(ExportRequest request) => Initial(request, Prefs.GetString(LastFormatKey));

    /// lastFormat: 기억한 형식의 rawValue (csv, xlsx …)
    public static ExportChoice Initial(ExportRequest request, string? lastFormat) => new()
    {
        Format = ExportFormats.FromRawValue(lastFormat) ?? ExportFormat.Csv,
        FetchAll = request.CanFetchAll,
        TableName = request.DefaultTable,
    };

    /// [저장 위치 선택…] 을 누를 수 있는지 (SQL 은 대상 테이블이 있어야 함)
    public bool CanContinue => !(Format == ExportFormat.Sql && TableName.Trim(' ', '\t').Length == 0);

    /// [저장 위치 선택…] 때 형식을 기억한다
    public void RememberFormat() => Prefs.Set(LastFormatKey, Format.RawValue());

    /// 저장 창 안내 문구
    public string PanelMessage(int visibleRows) => FetchAll
        ? Tr("%@ · 쿼리 전체 데이터", Format.Title())
        : Tr("%@ · 보이는 %@행", Format.Title(), ResultExportRunner.Number(visibleRows));

    public ExportOptions Options() => new()
    {
        ExcelBom = ExcelBom,
        TableName = TableName.Trim(' ', '\t').Length == 0 ? "table" : TableName,
    };
}

public enum ExportOutcomeKind { Success, Warning, Cancelled, Failed }

/// 내보내기 결과: 로그 한 줄(수준 + 문장) + 감사 기록용 결과 문구
public sealed record ExportOutcome
{
    public required ExportOutcomeKind Kind { get; init; }
    /// 로그에 쓸 문장 (실패면 맥락 "내보내기 실패")
    public required string Message { get; init; }
    public int Written { get; init; }
    public int? Total { get; init; }
    public bool Truncated { get; init; }
    /// 시작 전 정렬 경고 (전체 내보내기 + 정렬 기준 없음). 있으면 먼저 경고 로그로 남긴다.
    public string? UnstableWarning { get; init; }
    /// 감사 로그 결과 칸 (예: "CSV · 전체 12345행 · COUNT 일치"). 실패·취소는 null
    public string? AuditResult { get; init; }
    /// 감사 로그 문장: SQL → 경로
    public required string AuditStatement { get; init; }
    public Exception? Error { get; init; }
}

/// 내보내기 실행 (Mac: ResultExportUI.swift AppState.runExport 의 화면과 상관없는 부분)
public static class ResultExportRunner
{
    internal static string Number(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    /// 보이는 행 또는 쿼리 전체를 path 에 쓴다. 취소하면 쓰던 파일을 지운다.
    /// session: 전체 내보내기 때만 필요. database 가 있으면 먼저 그 DB 로 바꾼다.
    public static async Task<ExportOutcome> RunAsync(DbSession? session, string? database, ExportRequest request, ExportChoice choice,
        string path, IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        var format = choice.Format;
        var fetchAll = choice.FetchAll && request.CanFetchAll;
        var result = request.Result;
        var auditStatement = (result.Sql ?? "") + " → " + path;
        var started = Stopwatch.StartNew();
        string? warning = null;
        ResultExportWriter? writer = null;
        try
        {
            var w = new ResultExportWriter(format, [.. result.Columns.Select(c => c.Name)], path, choice.Options(),
                                           ISqlDialect.Of(request.Kind));
            writer = w;
            int? total = null;
            var truncated = false;
            progress?.Report(new ExportProgress(format));
            if (fetchAll && result.Sql is { } sql)
            {
                if (session == null) throw new GodDbException(DbErrorKind.NotConnected);
                if (!string.IsNullOrEmpty(database) && session.Current != database) await session.UseAsync(database, ct);
                if (!request.Stable) warning = Tr("내보내기: 정렬 기준이 없어 페이지 사이에 행이 빠지거나 겹칠 수 있어요");
                // 전체 행 수 먼저 (실패해도 내보내기는 계속)
                total = result.TotalCount;
                if (total == null) { try { total = await session.CountAsync(sql, ct); } catch (Exception e) when (!ct.IsCancellationRequested && e is not OperationCanceledException) { } }
                progress?.Report(new ExportProgress(format, 0, total));
                var t = total;
                truncated = await session.ExportAllAsync(sql, request.Sort, w, 5_000,
                    progress == null ? null : new InlineProgress(n => progress.Report(new ExportProgress(format, n, t))), ct);
            }
            else
            {
                IEnumerable<IReadOnlyList<string?>> rows = result.Rows.Select(r => r.Values);
                if (format.MaxRows() is int max && result.Rows.Count > max) { rows = rows.Take(max); truncated = true; }
                w.Write(rows);
            }
            ct.ThrowIfCancellationRequested();
            w.Finish();
            var secs = started.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
            var written = w.RowsWritten;
            var file = System.IO.Path.GetFileName(path);
            var scopeText = fetchAll ? "전체" : "보이는 행";
            var verdict = truncated ? "Excel 최대 행에서 자름"
                : fetchAll && total != null && total != written ? $"COUNT {total}행과 다름"
                : fetchAll ? "COUNT 일치" : "";
            var audit = $"{format.Title()} · {scopeText} {written}행" + (verdict.Length == 0 ? "" : " · " + verdict);
            ExportOutcomeKind kind;
            string message;
            if (truncated)
            {
                kind = ExportOutcomeKind.Warning;
                message = Tr("%@ 저장 · Excel 시트 최대 행 수를 넘어 %@행까지만 저장했어요 · %@", format.Title(), Number(written), file);
            }
            else if (fetchAll && total is int tt && tt != written)
            {
                kind = ExportOutcomeKind.Warning;
                message = Tr("%@ 저장 · 전체 %@행인데 %@행을 저장했어요. 조회 중 데이터가 바뀌었거나 정렬 기준이 없어 생긴 차이일 수 있어요 · %@",
                             format.Title(), Number(tt), Number(written), file);
            }
            else if (fetchAll)
            {
                kind = ExportOutcomeKind.Success;
                message = Tr("%@ 저장 · 전체 데이터 %@행 (COUNT 와 일치) · %@초 · %@", format.Title(), Number(written), secs, file);
            }
            else
            {
                kind = ExportOutcomeKind.Success;
                message = Tr("%@ 저장 · 보이는 행 %@행 · %@", format.Title(), Number(written), file);
            }
            return new ExportOutcome
            {
                Kind = kind, Message = message, Written = written, Total = total, Truncated = truncated,
                UnstableWarning = warning, AuditResult = audit, AuditStatement = auditStatement,
            };
        }
        catch (Exception e) when (e is OperationCanceledException || (e is GodDbException { Kind: DbErrorKind.Cancelled }) || ct.IsCancellationRequested)
        {
            writer?.Cancel();
            return new ExportOutcome
            {
                Kind = ExportOutcomeKind.Cancelled, Message = Tr("내보내기를 취소했어요 (파일 삭제)"),
                Written = writer?.RowsWritten ?? 0, UnstableWarning = warning, AuditStatement = auditStatement,
            };
        }
        catch (Exception e)
        {
            writer?.Cancel();
            return new ExportOutcome
            {
                Kind = ExportOutcomeKind.Failed, Message = Tr("내보내기 실패"), Error = e,
                Written = writer?.RowsWritten ?? 0, UnstableWarning = warning, AuditStatement = auditStatement,
            };
        }
    }

    /// Progress<T> 는 UI 스레드로 늦게 보내므로, 받은 progress 에 바로 넘긴다
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
