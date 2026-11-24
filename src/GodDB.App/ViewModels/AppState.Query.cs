using System.Diagnostics;
using GodDB.App.Services;

namespace GodDB.App.ViewModels;

// 쿼리 실행 · 여러 문장 · 페이지 · 정렬 · 전체 행 수 · 생성문 새로고침 · 테이블 이름 변경/삭제 · ALTER ·
// 취소/시간 제한 · 결과 편집 · 내보내기 · CSV 가져오기
public sealed partial class AppState
{
    private static readonly string[] SchemaKeywords = ["CREATE", "ALTER", "DROP", "RENAME", "TRUNCATE"];

    /// 탭이 쓰는 연결. 연결이 끊겼으면 null. 탭의 DB 가 세션의 현재 DB 와 다르면 바꾼다.
    private async Task<DbSession?> SessionForAsync(QueryTab tab)
    {
        if (!Connections.TryGetValue(tab.ProfileId, out var info) || info.Session is not { } s) return null;
        if (tab.Database.Length > 0 && s.Current != tab.Database)
        {
            var db = tab.Database;
            await Bg(() => s.UseAsync(db));
        }
        return s;
    }

    private string NotConnectedMessage(QueryTab tab) =>
        Tr("%@ 이(가) 연결돼 있지 않아요. 왼쪽에서 접속을 눌러 다시 연결해 주세요.", Profile(tab.ProfileId)?.Name ?? Tr("이 탭의 접속"));

    private static string Summary(QueryResult r) =>
        r.Columns.Count == 0 ? (r.Message ?? Tr("실행 완료")) : Tr("%@행", r.Rows.Count) + (r.HasMore ? "+" : "");

    /// 선택된 탭에서 sql 실행. explain 이면 실행 계획을 보여준다.
    /// rowLimitOverride: 이번 실행만 다른 행 수 제한 (ID 로 찾기 — 값 개수 이상)
    public async Task RunAsync(string sql, bool explain = false, int? rowLimitOverride = null)
    {
        if (SelectedTab is not { } tab) return;
        var tabId = tab.Id;
        var trimmed = sql.Trim();
        while (trimmed.EndsWith(';')) trimmed = trimmed[..^1];
        if (trimmed.Length == 0) return;
        if (explain && SqlText.FirstKeyword(trimmed) == "EXPLAIN")
            trimmed = trimmed["EXPLAIN".Length..].Trim();
        tab.Running = true;
        ArmRunTimeout(tabId);
        tab.Error = null;
        var effectiveLimit = rowLimitOverride ?? RowLimit;
        var streaming = effectiveLimit == 0;
        var limit = streaming ? StreamPageSize : effectiveLimit;
        try
        {
            if (await SessionForAsync(tab) is not { } session)
            {
                Update(tabId, t => { t.Error = NotConnectedMessage(tab); t.Running = false; });
                Log(LogLevel.Warning, LogSource.Query, NotConnectedMessage(tab));
                return;
            }
            var finalSql = explain ? session.Dialect.Explain(trimmed).Main : trimmed;
            Update(tabId, t => t.LastSql = finalSql);
            QueryResult result;
            string? tree = null;
            if (explain)
                (result, tree) = await Bg(() => session.ExplainAsync(trimmed));
            else
                result = await Bg(() => session.ExecuteAsync(finalSql, limit));
            var shown = result;
            shown.Sql = trimmed;
            if (streaming && shown.IsPaged) shown.AppendMode = true;
            Update(tabId, t =>
            {
                t.Edits = new PendingEdits();
                t.Results = [shown];
                t.ResultIndex = 0;
                t.Result = shown;
                t.Running = false;
                t.IsPlan = explain;
                t.AppendCapped = false;
                t.PlanTree = tree;
            });
            var ms = (int)(result.Elapsed * 1000);
            var label = $"[{session.Profile.Name}] ";
            if (explain)
            {
                var warnings = PlanAnalyzer.Warnings(result);
                Log(warnings.Count == 0 ? LogLevel.Success : LogLevel.Warning, LogSource.Query,
                    label + Tr("실행 계획 · 주의 %@건 · %@", warnings.Count, OneLine(trimmed)),
                    detail: string.Join("\n", new[] { finalSql }.Concat(warnings.Select(w => $"[{w.Table}] {w.Message}"))),
                    audit: new AuditRecord(AuditAction.Read, AuditTarget(tab), $"실행 계획 · {ms}ms", finalSql));
            }
            else
            {
                var summary = Summary(result);
                RecordHistory(tab, trimmed, ms, true, summary);
                Log(LogLevel.Success, LogSource.Query, label + $"{summary} · {ms}ms · {OneLine(trimmed)}", detail: trimmed,
                    audit: new AuditRecord(AuditRecord.Classify(trimmed), AuditTarget(tab), $"{summary} · {ms}ms", trimmed));
            }
            var kw = SqlText.FirstKeyword(trimmed);
            await AfterStatementsAsync(session, tabId, !explain && SchemaKeywords.Contains(kw), kw == "USE");
        }
        catch (Exception e)
        {
            await RefreshTxAsync(tab.ProfileId);
            if (!explain) RecordHistory(tab, trimmed, 0, false, null);
            var message = LogError(e, LogSource.Query, explain ? Tr("실행 계획 실패") : Tr("쿼리 실패"), trimmed,
                                   new AuditRecord(explain ? AuditAction.Read : AuditRecord.Classify(trimmed), AuditTarget(tab), Statement: trimmed));
            Update(tabId, t => { t.Error = message; t.Running = false; });
        }
    }

    /// 확인 창을 거친 뒤(또는 확인이 필요 없을 때) 실행을 시작한다
    public Task StartAsync(RunConfirm confirm) =>
        confirm.Explain ? RunAsync(confirm.Statements[0], explain: true) : RunScriptAsync(confirm.Statements);

    /// 여러 문장을 위에서부터 차례로 실행한다. 실패하면 그 문장에서 멈춘다.
    public async Task RunScriptAsync(IReadOnlyList<string> statements)
    {
        var list = statements.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (list.Count == 0) return;
        if (list.Count == 1) { await RunAsync(list[0]); return; }
        if (SelectedTab is not { } tab) return;
        var tabId = tab.Id;
        tab.Running = true;
        ArmRunTimeout(tabId);
        tab.Error = null;
        tab.IsPlan = false;
        tab.PlanTree = null;
        tab.LastSql = string.Join(";\n", list);
        var streaming = RowLimit == 0;
        var limit = streaming ? StreamPageSize : RowLimit;
        var started = Stopwatch.StartNew();
        var results = new List<QueryResult>();
        var schemaChanged = false;
        var usedUse = false;

        void Finish(string? error)
        {
            // 결과 집합이 있는 마지막 결과를 먼저 보여준다.
            var index = results.FindLastIndex(r => r.Columns.Count > 0);
            if (index < 0) index = Math.Max(0, results.Count - 1);
            Update(tabId, t =>
            {
                t.Edits = new PendingEdits();
                t.Results = results;
                t.ResultIndex = index;
                t.Result = results.Count == 0 ? null : results[index];
                t.Error = error;
                t.Running = false;
            });
        }

        DbSession session;
        try
        {
            if (await SessionForAsync(tab) is not { } s)
            {
                Finish(NotConnectedMessage(tab));
                return;
            }
            session = s;
        }
        catch (Exception e)
        {
            Finish(LogError(e, LogSource.Query, Tr("DB 전환 실패")));
            return;
        }

        for (var n = 0; n < list.Count; n++)
        {
            var statement = list[n];
            try
            {
                var r = await Bg(() => session.ExecuteAsync(statement, limit));
                r.Sql = statement;
                if (streaming && r.IsPaged) r.AppendMode = true;
                results.Add(r);
                var summary = Summary(r);
                var ms = (int)(r.Elapsed * 1000);
                RecordHistory(tab, statement, ms, true, summary);
                AuditOnly(new AuditRecord(AuditRecord.Classify(statement), AuditTarget(tab),
                                          $"{n + 1}/{list.Count} · {summary} · {ms}ms", statement));
                var kw = SqlText.FirstKeyword(statement);
                if (SchemaKeywords.Contains(kw)) schemaChanged = true;
                if (kw == "USE") usedUse = true;
            }
            catch (Exception e)
            {
                RecordHistory(tab, statement, 0, false, null);
                var message = LogError(e, LogSource.Query, Tr("%@/%@번째 문장 실패", n + 1, list.Count), statement,
                                       new AuditRecord(AuditRecord.Classify(statement), AuditTarget(tab), Statement: statement));
                Finish(Tr("%@번째 문장에서 멈췄어요. (앞의 %@개는 실행됨)\n\n%@", n + 1, n, message));
                await AfterStatementsAsync(session, tabId, schemaChanged, usedUse);
                return;
            }
        }
        Finish(null);
        var total = (int)started.Elapsed.TotalMilliseconds;
        var withRows = results.Count(r => r.Columns.Count > 0);
        Log(LogLevel.Success, LogSource.Query, Tr("[%@] %@개 문장 실행 · 결과 %@개 · %@ms", session.Profile.Name, list.Count, withRows, total),
            detail: string.Join(";\n\n", list));
        await AfterStatementsAsync(session, tabId, schemaChanged, usedUse);
    }

    /// USE 로 DB 를 바꿨으면 탭의 DB 도 바꾸고, 구조가 바뀌었으면 사이드바 스키마를 새로 읽는다.
    private async Task AfterStatementsAsync(DbSession session, Guid tabId, bool schemaChanged, bool usedUse)
    {
        var id = session.Profile.Id;
        await RefreshTxAsync(id);
        if (usedUse)
        {
            var db = session.Current;
            if (db.Length > 0)
            {
                Update(tabId, t => t.Database = db);
                if (SelectedTabId == tabId) await SelectDatabaseAsync(db, id);
                return;
            }
        }
        if (schemaChanged) await RefreshSchemaAsync(id);
    }

    /// "제한 없음" 결과: 스크롤이 끝에 닿으면 다음 페이지를 받아 이어 붙인다 (메모리 상한까지)
    public async Task LoadMoreAsync(Guid tabId)
    {
        if (Tab(tabId) is not { } t || t.Result is not { } current || !current.AppendMode || !current.HasMore
            || t.PageLoading || current.Sql is not { } sql) return;
        if (current.Rows.Count >= MaxRowsInMemory)
        {
            if (!t.AppendCapped)
            {
                t.AppendCapped = true;
                Log(LogLevel.Warning, LogSource.Query, Tr("화면에는 %@행까지만 보여요. 전체 데이터는 [내보내기] 로 저장하세요.", MaxRowsInMemory.ToString("N0")));
            }
            return;
        }
        var index = t.ResultIndex;
        t.PageLoading = true;
        try
        {
            if (await SessionForAsync(t) is not { } session) return;
            var next = current.Page + 1;
            var r = await Bg(() => session.ExecuteAsync(sql, current.PageSize, next, current.ServerSort));
            if (!ReferenceEquals(t.Result, current)) return;
            var start = current.Rows.Count;
            current.Rows.AddRange(r.Rows.Select((row, i) => new QueryResult.Row(start + i, row.Values)));
            current.Page = next;
            current.HasMore = r.HasMore;
            current.TruncatedCells += r.TruncatedCells;
            if (index < t.Results.Count && !ReferenceEquals(t.Results[index], current))
            {
                var list = t.Results.ToList();
                list[index] = current;
                t.Results = list;
            }
            t.RaiseRowsAppended(start, r.Rows.Count);
            t.NotifyResultChanged();
        }
        catch (Exception e)
        {
            var message = LogError(e, LogSource.Query, Tr("다음 행 불러오기 실패"), sql);
            t.Error = message;
        }
        finally
        {
            t.PageLoading = false;
        }
    }

    /// 보고 있는 결과의 다른 페이지를 가져온다. (같은 쿼리를 OFFSET 만 바꿔 다시 실행)
    public async Task LoadPageAsync(int page, Guid tabId)
    {
        if (Tab(tabId) is not { } t || t.Result is not { } current || !current.IsPaged || current.Sql is not { } sql || page < 0) return;
        var index = t.ResultIndex;
        t.PageLoading = true;
        t.Error = null;
        try
        {
            if (await SessionForAsync(t) is not { } session)
            {
                t.Error = NotConnectedMessage(t);
                t.PageLoading = false;
                return;
            }
            var r = await Bg(() => session.ExecuteAsync(sql, current.PageSize, page, current.ServerSort));
            r.Sql = sql;
            r.TotalCount = current.TotalCount;
            // 마지막 페이지를 넘어가면 (데이터가 줄어든 경우) 빈 페이지 대신 그대로 둔다.
            if (r.Rows.Count == 0 && page > 0)
            {
                t.PageLoading = false;
                Log(LogLevel.Info, LogSource.Query, Tr("더 이상 행이 없어요."));
                return;
            }
            t.Edits = new PendingEdits();
            SetResultAt(t, index, r);
            t.PageLoading = false;
            Log(LogLevel.Info, LogSource.Query, Tr("%@–%@행 (%@페이지) · %@ms", r.RowOffset + 1, r.RowOffset + r.Rows.Count, page + 1, (int)(r.Elapsed * 1000)));
        }
        catch (Exception e)
        {
            var message = LogError(e, LogSource.Query, Tr("페이지 불러오기 실패"), sql);
            t.Error = message;
            t.PageLoading = false;
        }
    }

    /// 보고 있는 결과를 바꾸고 문장별 결과 목록에도 넣는다
    private static void SetResultAt(QueryTab t, int index, QueryResult r)
    {
        t.Result = r;
        if (index >= 0 && index < t.Results.Count)
        {
            var list = t.Results.ToList();
            list[index] = r;
            t.Results = list;
        }
    }

    /// 헤더 클릭 정렬: 원래 쿼리를 감싸 ORDER BY 로 다시 실행하고 첫 페이지부터 보여준다.
    /// (SHOW 처럼 감쌀 수 없는 결과만 받아 온 행을 앱에서 정렬)
    public async Task SortResultAsync(Guid tabId, string column, bool ascending)
    {
        if (Tab(tabId) is not { } t || t.Result is not { } current || current.Sql is not { } sql) return;
        var columnIndex = current.Columns.FindIndex(c => c.Name == column);
        if (columnIndex < 0) return;
        if (!t.Edits.IsEmpty)
        {
            ConfirmDiscardingEdits([tabId], () =>
            {
                Update(tabId, x => x.Edits = new PendingEdits());
                Fire(() => SortResultAsync(tabId, column, ascending));
            });
            return;
        }
        var index = t.ResultIndex;
        var sort = new ResultSort(column, ascending);
        if (!Connections.TryGetValue(t.ProfileId, out var info) || info.Session is not { } s0 || s0.Dialect.SortedSql(sql, sort) == null)
        {
            current.Rows = ResultSorting.Sorted(current.Rows, columnIndex, ascending);
            current.ClientSort = sort;
            SetResultAt(t, index, current);
            t.NotifyResultChanged();
            return;
        }
        t.PageLoading = true;
        t.Error = null;
        try
        {
            if (await SessionForAsync(t) is not { } session)
            {
                t.Error = NotConnectedMessage(t);
                t.PageLoading = false;
                return;
            }
            var size = current.PageSize > 0 ? current.PageSize : RowLimit;
            var r = await Bg(() => session.ExecuteAsync(sql, size, 0, sort));
            r.Sql = sql;
            r.AppendMode = current.AppendMode && r.IsPaged;
            r.TotalCount = current.TotalCount;
            SetResultAt(t, index, r);
            t.PageLoading = false;
            Log(LogLevel.Info, LogSource.Query, Tr("정렬: %@ %@ · %@ms", column, ascending ? "↑" : "↓", (int)(r.Elapsed * 1000)),
                detail: session.Dialect.SortedSql(sql, sort));
        }
        catch (Exception e)
        {
            var message = LogError(e, LogSource.Query, Tr("정렬 실패"), sql);
            t.Error = message;
            t.PageLoading = false;
        }
    }

    /// 보고 있는 결과의 전체 행 수를 센다.
    public async Task CountRowsAsync(Guid tabId)
    {
        if (Tab(tabId) is not { } t || t.Result?.Sql is not { } sql) return;
        var index = t.ResultIndex;
        t.PageLoading = true;
        try
        {
            if (await SessionForAsync(t) is not { } session) return;
            if (await Bg(() => session.CountAsync(sql)) is not { } total) return;
            if (t.Result != null) t.Result.TotalCount = total;
            if (index >= 0 && index < t.Results.Count) t.Results[index].TotalCount = total;
            t.NotifyResultChanged();
            Log(LogLevel.Info, LogSource.Query, Tr("전체 %@행", total.ToString("N0")));
        }
        catch (Exception e)
        {
            LogError(e, LogSource.Query, Tr("전체 행 수 세기 실패"), sql);
        }
        finally
        {
            t.PageLoading = false;
        }
    }

    // MARK: - 생성문 새로고침 / 테이블 이름 변경 / 삭제

    /// DDL·정의 탭의 내용을 DB 에서 다시 읽어 바꾼다.
    public async Task RefreshSourceAsync(Guid tabId)
    {
        if (Tab(tabId) is not { } tab || tab.Source is not { } source) return;
        try
        {
            if (await SessionForAsync(tab) is not { } session)
            {
                tab.Error = NotConnectedMessage(tab);
                return;
            }
            string text;
            switch (source)
            {
                case TabSource.Table tbl:
                    // 구조가 바뀌었을 수 있으니 스키마를 새로 읽고 DDL 을 만든다
                    var db = tab.Database;
                    var tables = await Bg(() => session.LoadSchemaAsync(db));
                    UpdateConnection(tab.ProfileId, c => c.Tables = tables);
                    if (tables.FirstOrDefault(m => m.Name == tbl.Name) is not { } meta)
                    {
                        tab.Error = Tr("테이블 %@ 이(가) 더 이상 없어요. (이름이 바뀌었거나 삭제됨)", tbl.Name);
                        return;
                    }
                    text = await Bg(() => session.TableDdlAsync(meta));
                    break;
                case TabSource.Routine r:
                    text = await Bg(() => session.DefinitionAsync(r.Meta)) ?? "";
                    break;
                default:
                    return;
            }
            tab.Text = text + "\n";
            tab.Error = null;
            Log(LogLevel.Info, LogSource.Schema, Tr("%@ 생성문 새로고침", tab.Title));
        }
        catch (Exception e)
        {
            var message = LogError(e, LogSource.Schema, Tr("생성문 새로고침 실패"));
            tab.Error = message;
        }
    }

    public void RequestRename(string table)
    {
        if (SelectedProfileId is not { } id) return;
        TableAction = new TableActionRequest(TableActionKind.Rename, id, table);
    }

    public void RequestDrop(string table)
    {
        if (SelectedProfileId is not { } id) return;
        TableAction = new TableActionRequest(TableActionKind.Drop, id, table);
    }

    /// 테이블 이름 변경 / 삭제 실행. 성공하면 사이드바를 새로 읽고, 열린 DDL 탭의 이름도 맞춘다.
    public async Task PerformTableActionAsync(TableActionRequest request, string newName = "")
    {
        if (!Connections.TryGetValue(request.ProfileId, out var info) || info.Session is not { } session)
            throw new GodDbException(DbErrorKind.NotConnected);
        var db = info.CurrentDatabase;
        if (db.Length > 0 && session.Current != db) await Bg(() => session.UseAsync(db));
        var sql = request.Kind == TableActionKind.Rename
            ? session.Dialect.RenameTableSql(request.Table, newName)
            : session.Dialect.DropTableSql(request.Table);
        var dbTarget = (Profile(request.ProfileId)?.Name ?? "?") + (db.Length == 0 ? "" : $"/{db}");
        try
        {
            await Bg(() => session.ExecuteAsync(sql, 0));
        }
        catch (Exception e)
        {
            LogError(e, LogSource.Schema, request.Kind == TableActionKind.Rename ? Tr("테이블 이름 변경 실패") : Tr("테이블 삭제 실패"), sql,
                     new AuditRecord(AuditAction.Ddl, dbTarget, Statement: sql));
            throw;
        }
        switch (request.Kind)
        {
            case TableActionKind.Rename:
                Log(LogLevel.Success, LogSource.Schema, Tr("테이블 이름 변경: %@ → %@", request.Table, newName), detail: sql,
                    audit: new AuditRecord(AuditAction.Ddl, dbTarget, "테이블 이름 변경", sql));
                foreach (var t in Tabs.Where(t => t.ProfileId == request.ProfileId && t.Source == new TabSource.Table(request.Table)))
                {
                    t.Source = new TabSource.Table(newName);
                    t.CustomTitle = $"DDL · {newName}";
                }
                break;
            case TableActionKind.Drop:
                Log(LogLevel.Success, LogSource.Schema, Tr("테이블 삭제: %@", request.Table), detail: sql,
                    audit: new AuditRecord(AuditAction.Ddl, dbTarget, "테이블 삭제", sql));
                break;
        }
        await RefreshSchemaAsync(request.ProfileId);
    }

    /// AI 가 만든 SQL 등을 탭 편집기 끝에 덧붙인다.
    public void InsertIntoTab(Guid tabId, string sql, string label)
    {
        Update(tabId, t =>
        {
            var text = t.Text;
            if (text.Length > 0 && !text.EndsWith('\n')) text += "\n";
            text += $"\n-- {label}\n{sql}" + (sql.EndsWith(';') ? "" : ";") + "\n";
            t.Text = text;
        });
        SelectTab(tabId);
    }

    public void SelectResult(int index, Guid tabId)
    {
        Update(tabId, t =>
        {
            if (index < 0 || index >= t.Results.Count) return;
            t.Edits = new PendingEdits();
            t.ResultIndex = index;
            t.Result = t.Results[index];
        });
    }

    // MARK: - 테이블 수정 (ALTER) — 사이드바에서 보고 있는 접속 / DB

    public async Task ExecuteAlterAsync(string sql, string table)
    {
        if (Session is not { } session) throw new GodDbException(DbErrorKind.NotConnected);
        var db = CurrentDatabase;
        var alterTarget = session.Profile.Name + (db.Length == 0 ? "" : $"/{db}");
        try
        {
            if (db.Length > 0 && session.Current != db) await Bg(() => session.UseAsync(db));
            await Bg(() => session.ExecuteAsync(sql, 0));
            Log(LogLevel.Success, LogSource.Schema, Tr("%@ 테이블 수정 완료", table), detail: sql,
                audit: new AuditRecord(AuditAction.Ddl, alterTarget, "테이블 수정", sql));
            await RefreshSchemaAsync();
        }
        catch (Exception e)
        {
            LogError(e, LogSource.Schema, Tr("%@ 테이블 수정 실패", table), sql, new AuditRecord(AuditAction.Ddl, alterTarget, Statement: sql));
            throw;
        }
    }

    // MARK: - 쿼리 취소 · 실행 시간 제한

    /// 설정 > 쿼리 > 실행 시간 제한 (초, 0 이면 없음)
    public const string QueryTimeoutKey = "queryTimeoutSeconds";

    /// 실행 시작을 기록하고, 제한 시간이 있으면 넘었을 때 취소한다
    public void ArmRunTimeout(Guid tabId)
    {
        var started = DateTime.Now;
        Update(tabId, t => t.RunStarted = started);
        var seconds = Prefs.Get(QueryTimeoutKey, 0);
        if (seconds <= 0) return;
        Fire(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            if (Tab(tabId) is not { Running: true } t || t.RunStarted != started) return;
            Log(LogLevel.Warning, LogSource.Query, Tr("실행 시간 제한 %@초를 넘어 취소해요.", seconds));
            await CancelQueryAsync(tabId);
        });
    }

    /// 탭에서 실행 중인 쿼리를 멈춘다
    public async Task CancelQueryAsync(Guid tabId)
    {
        if (Tab(tabId) is not { Running: true } t || !Connections.TryGetValue(t.ProfileId, out var info) || info.Session is not { } session) return;
        try
        {
            if (await Bg(() => session.CancelRunningAsync()))
                Log(LogLevel.Info, LogSource.Query, session.Profile.Kind == DatabaseKind.Mssql
                    ? Tr("쿼리 취소를 요청했어요. SQL Server 는 세션을 다시 연결해요 (열린 트랜잭션은 롤백돼요).")
                    : Tr("쿼리 취소를 요청했어요."));
        }
        catch (Exception e)
        {
            LogError(e, LogSource.Query, Tr("쿼리 취소 실패"));
        }
        await RefreshTxAsync(t.ProfileId);
    }

    // MARK: - 결과 편집

    /// 결과를 고칠 수 있으면 대상, 못 고치면 이유(예외). 편집과 상관없는 결과(빈 결과, 실행 계획)는 null.
    public (EditTarget? Target, Exception? Error)? EditTarget(QueryTab tab)
    {
        if (tab.Result is not { } result || result.Columns.Count == 0 || tab.IsPlan || result.Sql is not { } sql) return null;
        if (Profile(tab.ProfileId)?.ReadOnly == true) return (null, new GodDbException(DbErrorKind.ReadOnlyBlocked));
        if (result.TruncatedCells > 0) return (null, new ResultEditingException(ResultEditingError.ValuesTruncated));
        var tables = Connections.TryGetValue(tab.ProfileId, out var c) ? c.Tables : [];
        try { return (ResultEditing.Target(sql, [.. result.Columns.Select(x => x.Name)], tables), null); }
        catch (Exception e) { return (null, e); }
    }

    /// 셀 값 바꾸기 (null = NULL). 원래 값과 같아지면 변경에서 뺀다.
    public void SetCell(Guid tabId, int row, int column, string? value) =>
        Update(tabId, t => { t.Edits.SetCell(t.Result, row, column, value); t.NotifyEditsChanged(); });

    /// 입력하지 않은 새 행 셀을 DB 기본값으로 되돌린다.
    public void ResetInsertedCell(Guid tabId, int row, int column) =>
        Update(tabId, t => { t.Edits.ResetInsertedCell(row, column); t.NotifyEditsChanged(); });

    public int? AddRow(Guid tabId)
    {
        int? id = null;
        Update(tabId, t => { id = t.Edits.AddRow(); t.NotifyEditsChanged(); });
        return id;
    }

    /// 선택한 행을 삭제 표시 (이미 표시돼 있으면 해제). 새 행은 바로 뺀다.
    public void ToggleDelete(Guid tabId, IEnumerable<int> rows) =>
        Update(tabId, t => { t.Edits.ToggleDelete(rows); t.NotifyEditsChanged(); });

    public void RevertEdits(Guid tabId) => Update(tabId, t => t.Edits = new PendingEdits());

    public void RequestApplyEdits(Guid tabId)
    {
        if (Tab(tabId) is not { } tab || tab.Result is not { } result || EditTarget(tab) is not { Target: { } target }
            || !Connections.TryGetValue(tab.ProfileId, out var info) || info.Session is not { } session) return;
        var statements = ResultEditing.Statements(tab.Edits, target, result.Rows, session.Dialect);
        if (statements.Count == 0) return;
        EditApply = new EditApplyRequest(tabId, statements);
    }

    /// 트랜잭션으로 적용하고, 성공하면 같은 페이지를 다시 읽는다.
    public async Task ApplyEditsAsync(EditApplyRequest request)
    {
        if (Tab(request.TabId) is not { } tab || !Connections.TryGetValue(tab.ProfileId, out var info) || info.Session is not { } session)
            throw new GodDbException(DbErrorKind.NotConnected);
        if (tab.Database.Length > 0 && session.Current != tab.Database)
        {
            var db = tab.Database;
            await Bg(() => session.UseAsync(db));
        }
        try
        {
            await Bg(() => session.ExecuteInTransactionAsync(request.Statements));
        }
        catch (Exception e)
        {
            LogError(e, LogSource.Query, Tr("데이터 변경 실패 (모두 되돌림)"), string.Join("\n", request.Statements),
                     new AuditRecord(AuditRecord.Classify(request.Statements[0]), AuditTarget(tab), Statement: string.Join(" ; ", request.Statements)));
            throw;
        }
        Log(LogLevel.Success, LogSource.Query, Tr("데이터 변경 %@건 적용", request.Statements.Count), detail: string.Join("\n", request.Statements));
        // 감사 로그: 문장마다 한 줄 (추가/수정/삭제 구분)
        for (var i = 0; i < request.Statements.Count; i++)
            AuditOnly(new AuditRecord(AuditRecord.Classify(request.Statements[i]), AuditTarget(tab),
                                      $"결과 편집 {i + 1}/{request.Statements.Count} (한 트랜잭션)", request.Statements[i]));
        await RefreshTxAsync(tab.ProfileId);
        RevertEdits(request.TabId);
        if (tab.Result is { AppendMode: true, Sql: { } appendSql })
        {
            // 이어 붙인 결과는 처음부터 다시 읽는다
            SelectTab(request.TabId);
            await RunAsync(appendSql);
        }
        else if (tab.Result is { IsPaged: true } r)
        {
            await LoadPageAsync(r.Page, request.TabId);
        }
        else if (tab.Result?.Sql is { } sql)
        {
            SelectTab(request.TabId);
            await RunAsync(sql);
        }
    }

    // MARK: - 결과 내보내기

    /// 결과 내보내기: 먼저 설정 창(형식·범위)을 띄운다
    public void ExportResult(Guid tabId)
    {
        if (Tab(tabId) is not { } tab || tab.Result is not { } result || result.Columns.Count == 0 || tab.ExportProgress != null) return;
        var kind = Profile(tab.ProfileId)?.Kind ?? DatabaseKind.Mysql;
        var tables = Connections.TryGetValue(tab.ProfileId, out var c) ? c.Tables : [];
        if (GodDB.Core.Results.ExportRequest.Prepare(result, kind, tables, DateTimeOffset.Now) is { } request)
            ExportRequest = new TabExportRequest(tabId, request);
    }

    /// 설정 창에서 [저장 위치 선택…] → 저장 창 → 내보내기 시작
    public async Task ContinueExportAsync(TabExportRequest request, ExportChoice choice)
    {
        ExportRequest = null;
        choice.RememberFormat();
        var f = choice.Format;
        var path = await Dialogs.SaveFileAsync(choice.PanelMessage(request.Request.VisibleRows), request.Request.FileName(f),
                                               [new FileFilter(f.Title(), [$"*.{f.FileExtension()}"])]);
        if (path == null) return;
        var tabId = request.TabId;
        var cts = new CancellationTokenSource();
        ExportTasks[tabId] = cts;
        await RunExportAsync(tabId, request.Request, choice, path, cts.Token);
    }

    public void CancelExport(Guid tabId)
    {
        if (ExportTasks.TryGetValue(tabId, out var cts)) cts.Cancel();
    }

    private async Task RunExportAsync(Guid tabId, GodDB.Core.Results.ExportRequest request, ExportChoice choice, string path, CancellationToken ct)
    {
        Update(tabId, t => t.ExportProgress = new ExportProgress(choice.Format));
        try
        {
            var tab = Tab(tabId);
            var target = tab != null ? AuditTarget(tab) : "";
            DbSession? session = tab != null && Connections.TryGetValue(tab.ProfileId, out var info) ? info.Session : null;
            var progress = new Progress<ExportProgress>(p => Update(tabId, t => t.ExportProgress = p));
            var outcome = await Bg(() => ResultExportRunner.RunAsync(session, tab?.Database, request, choice, path, progress, ct));
            if (outcome.UnstableWarning is { } w) Log(LogLevel.Warning, LogSource.Query, w);
            switch (outcome.Kind)
            {
                case ExportOutcomeKind.Cancelled:
                    Log(LogLevel.Info, LogSource.Query, outcome.Message);
                    break;
                case ExportOutcomeKind.Failed:
                    LogError(outcome.Error ?? new Exception(outcome.Message), LogSource.Query, Tr("내보내기 실패"),
                             audit: new AuditRecord(AuditAction.Export, target, Statement: outcome.AuditStatement));
                    break;
                default:
                    LastExportPath = path;
                    if (outcome.AuditResult is { } ar) AuditOnly(new AuditRecord(AuditAction.Export, target, ar, outcome.AuditStatement));
                    Log(outcome.Kind == ExportOutcomeKind.Warning ? LogLevel.Warning : LogLevel.Success, LogSource.Query, outcome.Message, detail: path);
                    break;
            }
        }
        finally
        {
            Update(tabId, t => t.ExportProgress = null);
            ExportTasks.Remove(tabId);
        }
    }

    // MARK: - CSV 가져오기

    /// 가져오기 INSERT 들을 한 트랜잭션으로 실행 (수동 커밋 모드면 열린 트랜잭션에 합류)
    public async Task ImportRowsAsync(string table, IReadOnlyList<string> statements, int rowCount)
    {
        if (SelectedProfileId is not { } id || !Connections.TryGetValue(id, out var info) || info.Session is not { } session)
            throw new GodDbException(DbErrorKind.NotConnected);
        var target = (Profile(id)?.Name ?? "?") + (CurrentDatabase.Length == 0 ? "" : $"/{CurrentDatabase}");
        try
        {
            await Bg(() => session.ExecuteInTransactionAsync(statements));
        }
        catch (Exception e)
        {
            LogError(e, LogSource.Query, Tr("CSV 가져오기 실패 (모두 되돌림)"),
                     audit: new AuditRecord(AuditAction.Insert, target, Statement: $"CSV → {table}"));
            await RefreshTxAsync(id);
            throw;
        }
        await RefreshTxAsync(id);
        Log(LogLevel.Success, LogSource.Query, Tr("%@ 에 %@행 가져옴", table, rowCount),
            audit: new AuditRecord(AuditAction.Insert, target, $"CSV 가져오기 {rowCount}행", $"CSV → {table}"));
    }
}
