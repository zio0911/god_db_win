using GodDB.Core.Results;
using GodDB.Core.Sql;

namespace GodDB.Core.Drivers;

/// 결과 편집 적용 · 전체 결과 내보내기 (Mac: ResultEditing.swift, ResultExport.swift 의 DBSession 확장)
public sealed partial class DbSession
{
    /// 여러 변경 문을 한 트랜잭션으로 실행한다. 하나라도 실패하면 전부 되돌린다.
    /// 수동 커밋 모드이거나 이미 열린 트랜잭션이 있으면 거기에 합류하고 커밋하지 않는다 (사용자가 커밋/롤백).
    public async Task ExecuteInTransactionAsync(IReadOnlyList<string> statements, CancellationToken ct = default)
    {
        foreach (var s in statements) CheckReadOnly(s);
        if (!AutoCommit || InTransaction)
        {
            foreach (var s in statements) await ExecuteAsync(s, 0, ct: ct);
            return;
        }
        await RawAsync(Dialect.BeginSql(), ct);
        try
        {
            foreach (var s in statements) await RawAsync(s, ct);
            await RawAsync(Dialect.CommitSql(), ct);
        }
        catch
        {
            try { await RawAsync(Dialect.RollbackSql(), CancellationToken.None); } catch { }
            throw;
        }
    }

    /// 쿼리 전체 결과를 chunk 행씩 나눠 조회하며 writer 에 바로 쓴다. 메모리에는 한 페이지만 둔다.
    /// progress 에는 지금까지 쓴 행 수를 알린다.
    /// PostgreSQL · MySQL 은 모든 페이지를 한 읽기 스냅샷 안에서 읽어, 그사이 다른 곳에서 데이터가 바뀌어도
    /// 페이지 경계에서 행이 겹치거나 빠지지 않는다 (#199).
    /// - Returns: 형식의 최대 행 수에 걸려 잘랐는지
    public async Task<bool> ExportAllAsync(string sql, ResultSort? sort, ResultExportWriter writer, int chunk = 5_000,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var snapshot = await BeginSnapshotAsync(ct);
        try { return await ExportPagesAsync(sql, sort, writer, chunk, progress, ct); }
        finally { if (snapshot) await EndSnapshotAsync(); }
    }

    private async Task<bool> ExportPagesAsync(string sql, ResultSort? sort, ResultExportWriter writer, int chunk,
        IProgress<int>? progress, CancellationToken ct)
    {
        var page = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var r = await ExecuteAsync(sql, chunk, page, sort, capCells: false, ct: ct);
            IEnumerable<IReadOnlyList<string?>> rows = r.Rows.Select(x => x.Values);
            var truncated = false;
            if (writer.Format.MaxRows() is int max && writer.RowsWritten + r.Rows.Count > max)
            {
                rows = rows.Take(max - writer.RowsWritten);
                truncated = true;
            }
            writer.Write(rows);
            progress?.Report(writer.RowsWritten);
            if (truncated) return true;
            if (!r.HasMore || r.Rows.Count == 0) return false;
            page++;
        }
    }
}
