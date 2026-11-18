namespace GodDB.App.ViewModels;

// 트랜잭션: 자동 커밋 켜기/끄기, 커밋/롤백, 커밋하지 않은 변경 확인
public sealed partial class AppState
{
    public TransactionState TxState(Guid id) =>
        Connections.TryGetValue(id, out var c) ? c.Tx : new TransactionState();

    public async Task RefreshTxAsync(Guid id)
    {
        if (!Connections.TryGetValue(id, out var info) || info.Session is not { } s) return;
        var st = s.TransactionState;
        if (info.Tx != st) UpdateConnection(id, c => c.Tx = st);
        await Task.CompletedTask;
    }

    private string TxTarget(Guid id)
    {
        var name = Profile(id)?.Name ?? "?";
        var db = Connections.TryGetValue(id, out var c) ? c.CurrentDatabase : "";
        return db.Length == 0 ? name : $"{name}/{db}";
    }

    /// 자동 커밋 켜기/끄기. 켤 때 열린 트랜잭션이 있으면 먼저 묻는다.
    public async Task SetAutoCommitAsync(Guid id, bool on)
    {
        if (!Connections.TryGetValue(id, out var info) || info.Session is not { } s) return;
        if (on && TxState(id).Open)
        {
            TxPrompt = new TxPrompt(id, TxPromptReason.EnableAutoCommit);
            return;
        }
        try
        {
            s.SetAutoCommit(on);
            await RefreshTxAsync(id);
            Log(LogLevel.Info, LogSource.Query, on ? Tr("[%@] 자동 커밋 켬", TxTarget(id)) : Tr("[%@] 자동 커밋 끔 · 변경은 커밋할 때 반영돼요", TxTarget(id)),
                audit: new AuditRecord(AuditAction.Settings, TxTarget(id), on ? "자동 커밋 켬" : "자동 커밋 끔 (수동 커밋)"));
        }
        catch (Exception e)
        {
            LogError(e, LogSource.Query, Tr("자동 커밋 전환 실패"));
        }
    }

    public async Task CommitTxAsync(Guid id)
    {
        if (!Connections.TryGetValue(id, out var info) || info.Session is not { } s) return;
        try
        {
            var n = await Bg(() => s.CommitAsync());
            await RefreshTxAsync(id);
            Log(LogLevel.Success, LogSource.Query, Tr("[%@] 커밋 · 변경 %@건", TxTarget(id), n),
                audit: new AuditRecord(AuditAction.Execute, TxTarget(id), $"커밋 · 변경 {n}건", s.Dialect.CommitSql()));
        }
        catch (Exception e)
        {
            await RefreshTxAsync(id);
            LogError(e, LogSource.Query, Tr("커밋 실패"), audit: new AuditRecord(AuditAction.Execute, TxTarget(id), Statement: s.Dialect.CommitSql()));
        }
    }

    public async Task RollbackTxAsync(Guid id)
    {
        if (!Connections.TryGetValue(id, out var info) || info.Session is not { } s) return;
        try
        {
            var n = await Bg(() => s.RollbackAsync());
            await RefreshTxAsync(id);
            Log(LogLevel.Success, LogSource.Query, Tr("[%@] 롤백 · 변경 %@건 되돌림", TxTarget(id), n),
                audit: new AuditRecord(AuditAction.Execute, TxTarget(id), $"롤백 · 변경 {n}건 되돌림", s.Dialect.RollbackSql()));
            // 되돌린 데이터가 보이도록 열린 결과를 다시 읽는다
            foreach (var tab in Tabs.Where(t => t.ProfileId == id && t.Result?.IsPaged == true).ToList())
                if (tab.Result is { } r) await LoadPageAsync(r.Page, tab.Id);
        }
        catch (Exception e)
        {
            await RefreshTxAsync(id);
            LogError(e, LogSource.Query, Tr("롤백 실패"), audit: new AuditRecord(AuditAction.Execute, TxTarget(id), Statement: s.Dialect.RollbackSql()));
        }
    }

    /// 확인 창에서 커밋/롤백을 고른 뒤 원래 하려던 일을 이어서 한다
    public async Task ResolveTxPromptAsync(TxPrompt prompt, bool commit)
    {
        TxPrompt = null;
        if (commit) await CommitTxAsync(prompt.ProfileId);
        else await RollbackTxAsync(prompt.ProfileId);
        if (TxState(prompt.ProfileId).Open) return;   // 실패했으면 멈춘다
        switch (prompt.Reason)
        {
            case TxPromptReason.EnableAutoCommit: await SetAutoCommitAsync(prompt.ProfileId, true); break;
            case TxPromptReason.Disconnect: await DisconnectAsync(prompt.ProfileId, force: true); break;
            case TxPromptReason.Reconnect: await ReconnectAsync(prompt.ProfileId); break;
            case TxPromptReason.Delete:
                if (Profile(prompt.ProfileId) is { } p) await DeleteProfileAsync(p);
                break;
        }
    }

    /// 앱 종료 전: 열린 트랜잭션이 있는 접속들 (접속 id, 변경 수)
    public List<(Guid Id, int Changes)> OpenTransactions =>
        Connections.Where(kv => kv.Value.Tx.Open).Select(kv => (kv.Key, kv.Value.Tx.Changes)).ToList();
}
