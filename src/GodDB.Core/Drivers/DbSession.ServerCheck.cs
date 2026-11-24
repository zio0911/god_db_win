using GodDB.Core.Profiles;
using GodDB.Core.Sql;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace GodDB.Core.Drivers;

/// 쿼리 편집기 빨간 밑줄의 "DB 검사" (Mac SQLServerCheck.swift 의 DBSession.serverCheck · runCheck)
public sealed partial class DbSession
{
    /// 검사했다는 표시. Failure 가 null 이면 문제 없음.
    public sealed record ServerCheckOutcome(SqlServerCheck.Failure? Failure);

    /// 구문을 실행하지 않고 DB 에 검사만 맡긴다. 검사할 수 없거나 바쁘면 null (오류 없음과 구분).
    /// 실행 기록 · 감사 로그 · 유휴 시간에 남기지 않는다.
    public async Task<ServerCheckOutcome?> ServerCheckAsync(string sql, CancellationToken ct = default)
    {
        var kind = Profile.Kind;
        if (kind == DatabaseKind.Redis || !SqlServerCheck.Checkable(sql, kind) || IsRunning) return null;
        // PostgreSQL 은 열린 트랜잭션 안에서 오류가 나면 트랜잭션 전체가 실패 상태가 된다 → 그때는 묻지 않음
        if (kind == DatabaseKind.Postgres && InTransaction) return null;
        if (_driver is not { IsOpen: true }) return null;
        // Mac 은 actor 라 다른 작업과 겹치지 않는다. 여기서는 연결을 쓰는 중이면 기다리지 않고 묻지 않는다.
        if (!await _gate.WaitAsync(0, ct)) return null;
        try
        {
            if (_driver is not { IsOpen: true } driver) return null;
            var (check, cleanup) = SqlServerCheck.Commands(sql, kind);
            SqlServerCheck.Failure? failure = null;
            foreach (var command in check)
            {
                try { await driver.QueryAsync(command, TimeoutSeconds, ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    var (message, position) = ServerMessage(e);
                    // 무시할 오류(검사 방식의 한계)면 null → 문제 없음으로 본다 (Mac 과 같음)
                    failure = SqlServerCheck.FailureFrom(message, position, sql, kind);
                    break;
                }
            }
            await CleanupAsync(driver, cleanup);
            return new ServerCheckOutcome(failure);
        }
        finally { _gate.Release(); }
    }

    private async Task CleanupAsync(IDbDriver driver, List<string> cleanup)
    {
        foreach (var command in cleanup)
        {
            try { await driver.QueryAsync(command, TimeoutSeconds); } catch { }
        }
    }

    /// 서버 오류 메시지와 PostgreSQL 오류 위치 (1부터)
    private static (string Message, int? Position) ServerMessage(Exception e) => e switch
    {
        PostgresException pg => (pg.MessageText, pg.Position > 0 ? pg.Position : null),
        MySqlException my => (my.Message, null),
        SqlException ms => (ms.Message, null),
        _ => (e.Message, null),
    };
}
