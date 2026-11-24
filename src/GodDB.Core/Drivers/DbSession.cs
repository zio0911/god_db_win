using System.Diagnostics;
using GodDB.Core.Dialects;
using GodDB.Core.Platform;
using GodDB.Core.Results;
using GodDB.Core.Schema;
using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.Core.Drivers;

/// 트랜잭션 상태 (자동 커밋 / 열린 트랜잭션 / 변경 건수)
public readonly record struct TransactionState(bool AutoCommit = true, bool Open = false, int Changes = 0)
{
    /// C# 의 `new TransactionState()` 는 기본 인자를 쓰지 않고 모두 0(자동 커밋 꺼짐)으로 만든다.
    /// Mac `TransactionState()` 처럼 자동 커밋 켜짐이 기본이 되도록 직접 둔다 (접속 직후 체크박스가 꺼져 보이던 문제).
    public TransactionState() : this(true) { }
}

/// 시작·커밋·롤백 SQL (Mac: ResultEditing.swift 의 SQLDialect 확장)
public static class TransactionSql
{
    public static string BeginSql(this ISqlDialect d) => d.Kind switch
    {
        DatabaseKind.Postgres => "BEGIN",
        DatabaseKind.Mssql => "BEGIN TRANSACTION",
        _ => "START TRANSACTION",
    };
    public static string CommitSql(this ISqlDialect d) => d.Kind == DatabaseKind.Mssql ? "COMMIT TRANSACTION" : "COMMIT";
    public static string RollbackSql(this ISqlDialect d) => d.Kind == DatabaseKind.Mssql ? "ROLLBACK TRANSACTION" : "ROLLBACK";
}

/// 접속 하나 (필요하면 SSH 터널 포함). DB 종류에 맞는 드라이버와 문법(Dialect)을 쓴다. 쿼리는 순차 실행된다.
public sealed partial class DbSession : IAsyncDisposable
{
    public ConnectionProfile Profile { get; }
    /// 문법. PostgreSQL 접속이 Redshift 로 확인되면 연결 직후 Redshift 용으로 바뀐다 (ConnectAsync 가 돌려주기 전)
    public ISqlDialect Dialect { get; private set; }
    private SshTunnel? _tunnel;
    private IDbDriver? _driver;
    /// 다시 연결 · 취소용 연결에 쓰는 주소 (터널을 거쳤다면 로컬 주소)
    private DriverEndpoint? _endpoint;
    /// 서버 쪽 세션 번호 (MySQL CONNECTION_ID, PostgreSQL pid, SQL Server SPID)
    private int? _serverSessionId;
    private int _running;
    private volatile bool _cancelRequested;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// 현재 선택된 DB(MySQL/SQL Server) 또는 스키마(PostgreSQL)
    public string Current { get; private set; } = "";
    /// 쿼리 실행 시간 제한 (초, 0 = 없음). 설정 > 일반 > 쿼리
    public int TimeoutSeconds { get; set; }
    /// 연결의 TLS 상태 (상태창 한 줄 — F9-3)
    public TlsStatus TlsStatus { get; private set; } = TlsStatus.Off;
    /// 마지막으로 이 접속을 쓴 시각 (UTC). 쿼리 · 페이지 · 내보내기 · 스키마 읽기 모두 RawAsync 를 거친다 — 운영 접속 자동 끊기에 쓴다
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
    /// 비밀번호를 그때그때 읽어 오는 곳 (예: 자격 증명 관리자). 있으면 연결한 뒤 세션이 비밀번호를 들고 있지 않는다 (1.4.1)
    private Func<string?>? _passwordProvider;

    // MARK: 트랜잭션 (자동 커밋 / 수동 커밋)
    public bool AutoCommit { get; private set; } = true;
    public bool InTransaction { get; private set; }
    public int PendingChanges { get; private set; }
    public TransactionState TransactionState => new(AutoCommit, InTransaction, PendingChanges);

    /// SSH 터널 경로 설명 (감사 로그 "접속" 줄용). 터널이 없으면 null
    public string? TunnelDescription => Profile.Ssh is { } s ? $"ssh {s.Host}:{s.Port}" : null;

    public DbSession(ConnectionProfile profile)
    {
        Profile = profile;
        Dialect = ISqlDialect.Of(profile.Kind);
    }

    /// DB 서버 연결(로그인 · 세션 설정까지)에 기다리는 최대 시간 (초). SSH 터널은 따로 15초.
    public const double DefaultConnectTimeout = 15;
    private double _connectTimeout = DefaultConnectTimeout;

    public static Task<DbSession> ConnectAsync(ConnectionProfile profile, string? password, CancellationToken ct = default,
        double timeout = DefaultConnectTimeout) =>
        ConnectAsync(profile, password, null, ct, timeout);

    /// - passwordProvider: 비밀번호를 그때그때 읽어 오는 곳 (예: 자격 증명 관리자).
    ///   주면 연결한 뒤 세션이 비밀번호를 들고 있지 않고, 취소 · 다시 연결 때마다 여기서 읽는다 (메모리에 덜 남게).
    public static async Task<DbSession> ConnectAsync(ConnectionProfile profile, string? password, Func<string?>? passwordProvider,
        CancellationToken ct = default, double timeout = DefaultConnectTimeout)
    {
        var s = new DbSession(profile) { _passwordProvider = passwordProvider, _connectTimeout = timeout };
        await s.OpenAsync(password, ct);
        return s;
    }

    /// 새 연결을 열 때 쓸 주소: 비밀번호를 들고 있지 않으면 읽어 와서 채운다
    internal DriverEndpoint? LiveEndpoint()
    {
        if (_endpoint is not { } e) return null;
        if (e.Password == null && _passwordProvider is { } provider) e = e with { Password = provider() };
        return e;
    }

    private async Task OpenAsync(string? password, CancellationToken ct)
    {
        var host = Profile.Host;
        var port = Profile.Port;
        if (Profile.Ssh is { } ssh)
        {
            var t = new SshTunnel(ssh, Profile.Host, Profile.Port);
            await t.StartAsync(ct);
            _tunnel = t;
            host = "127.0.0.1";
            port = t.LocalPort;
        }
        _endpoint = new DriverEndpoint
        {
            Host = host,
            Port = port,
            ServerName = Profile.Ssh == null && !DriverUtil.IsIPAddress(Profile.Host) ? Profile.Host : null,
            Username = Profile.Username,
            Password = password,
            Database = Profile.Database,
            UseTLS = Profile.UseTLS,
            WindowsAuth = Profile.Kind == DatabaseKind.Mssql && Profile.WindowsAuth,
            Tls = TlsPolicy.Of(Profile),
        };
        try
        {
            await OpenDriverAsync(_endpoint, ct);
            // 비밀번호를 다시 읽어 올 곳이 있으면 세션은 비밀번호를 들고 있지 않는다
            if (_passwordProvider != null) _endpoint = _endpoint with { Password = null };
        }
        catch
        {
            await CloseAsync();
            throw;
        }
    }

    private static async Task<IDbDriver> MakeDriverAsync(DatabaseKind kind, DriverEndpoint e, CancellationToken ct) => kind switch
    {
        DatabaseKind.Mysql => await MySqlDriver.ConnectAsync(e, ct),
        DatabaseKind.Postgres => await PostgresDriver.ConnectAsync(e, ct),
        DatabaseKind.Mssql => await MssqlDriver.ConnectAsync(e, ct),
        _ => throw new GodDbException(DbErrorKind.NotConnected), // Redis 는 RedisSession 을 쓴다
    };

    /// 드라이버 연결 + 세션 설정. 서버가 응답하지 않으면 끝없이 기다리지 않도록 시간 제한을 둔다 (Mac ConnectDeadline).
    private async Task OpenDriverAsync(DriverEndpoint e, CancellationToken ct)
    {
        // 드라이버 자체 연결 시간 제한은 전체 제한보다 길게 둬서, 시간 초과는 늘 같은 안내(DbConnectTimeoutException)로 끝나게 한다
        var endpoint = e with { ConnectTimeoutSeconds = Math.Max(e.ConnectTimeoutSeconds, (int)Math.Ceiling(_connectTimeout) + 5) };
        var kind = Profile.Kind;
        var dialect = Dialect;
        var readOnly = Profile.ReadOnly;
        var opened = await ConnectDeadline.RunAsync(_connectTimeout,
            token => PrepareDriverAsync(kind, endpoint, dialect, readOnly, token),
            late: o => o.Driver.DisposeAsync().AsTask(), ct: ct);
        _driver = opened.Driver;
        _serverSessionId = opened.SessionId;
        TlsStatus = opened.Driver.TlsStatus;
        if (opened.Redshift && !Dialect.IsRedshift()) Dialect = new PostgresDialect(redshift: true);
    }

    private sealed record OpenedDriver(IDbDriver Driver, int? SessionId, bool Redshift);

    private static async Task<OpenedDriver> PrepareDriverAsync(DatabaseKind kind, DriverEndpoint e, ISqlDialect dialect,
        bool readOnly, CancellationToken ct)
    {
        var d = await MakeDriverAsync(kind, e, ct);
        var redshift = false;
        try
        {
            // PostgreSQL 접속이 실제로는 Amazon Redshift 인지 (version() 에 "Redshift 1.0.x")
            if (kind == DatabaseKind.Postgres && !dialect.IsRedshift())
            {
                try
                {
                    var v = (await d.QueryAsync("SELECT version()", 0, ct)).Rows.FirstOrDefault()?.FirstOrDefault();
                    if (v != null && PostgresDialect.RedshiftVersion(v) != null)
                    {
                        redshift = true;
                        dialect = new PostgresDialect(redshift: true);
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }
            if (dialect.SessionSetupSql is { } setup) { try { await d.QueryAsync(setup, 0, ct); } catch (Exception) when (!ct.IsCancellationRequested) { } }
            // 서버 측에서도 변경을 거부하도록 세션을 읽기전용으로 설정
            if (readOnly && dialect.ReadOnlySessionSql is { } ro) await d.QueryAsync(ro, 0, ct);
            int? sessionId = null;
            try
            {
                var v = (await d.QueryAsync(dialect.SessionIdSql, 0, ct)).Rows.FirstOrDefault()?.FirstOrDefault();
                sessionId = int.TryParse(v, out var id) ? id : null;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            ct.ThrowIfCancellationRequested();
            return new OpenedDriver(d, sessionId, redshift || dialect.IsRedshift());
        }
        catch
        {
            await d.DisposeAsync();
            throw;
        }
    }

    /// 실행 중인 쿼리가 있는지
    public bool IsRunning => Volatile.Read(ref _running) > 0;

    /// 실행 중인 쿼리를 멈춘다. 별도 연결을 잠깐 열어 서버에 취소를 보낸다.
    /// 실행 중이던 호출은 GodDbException(Cancelled) 로 끝난다. 실행 중이 아니면 false.
    public async Task<bool> CancelRunningAsync()
    {
        if (!IsRunning || LiveEndpoint() is not { } e || _serverSessionId is not int id) return false;
        _cancelRequested = true;
        await using var other = await MakeDriverAsync(Profile.Kind, e, CancellationToken.None);
        await other.QueryAsync(Dialect.CancelSql(id));
        return true;
    }

    /// SQL Server 는 취소하면 세션이 끊기므로 다시 연결하고 보던 DB 로 돌아간다. 열린 트랜잭션은 서버가 롤백했다.
    private async Task ReopenAfterKillAsync()
    {
        if (_driver != null) await _driver.DisposeAsync();
        _driver = null;
        InTransaction = false;
        PendingChanges = 0;
        if (LiveEndpoint() is not { } e) return;
        try
        {
            await OpenDriverAsync(e, CancellationToken.None);
            if (Current.Length > 0) { try { await _driver!.QueryAsync(Dialect.UseSql(Current)); } catch { } }
        }
        catch { _driver = null; }
    }

    public async Task CloseAsync()
    {
        if (_driver != null) { try { await _driver.DisposeAsync(); } catch { } }
        _driver = null;
        _tunnel?.Stop();
        _tunnel = null;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    public bool IsConnected => _driver?.IsOpen ?? false;

    // MARK: - 쿼리

    /// 결과 표에 담을 한 칸의 최대 글자 수 (긴 TEXT/BLOB 로 메모리가 넘치지 않게). 내보내기는 자르지 않는다.
    public const int MaxCellCharacters = 1_000_000;

    /// 사용자 쿼리 실행. 읽기전용 접속이면 쓰기 쿼리를 차단한다.
    /// limit: 페이지 크기 (0 이면 전부), page: 0부터, sort: DB 에서 정렬할 컬럼 (원래 쿼리를 감싸 ORDER BY)
    public async Task<QueryResult> ExecuteAsync(string sql, int limit, int page = 0, ResultSort? sort = null,
        bool capCells = true, CancellationToken ct = default)
    {
        CheckReadOnly(sql);
        await BeginIfNeededAsync(sql, ct);
        var source = (sort is { } s ? Dialect.SortedSql(sql, s) : null) ?? sql;
        var paged = Dialect.PagedSql(source, limit, Math.Max(0, page) * limit);
        var sw = Stopwatch.StartNew();
        var raw = await ReadOnlyGuardedRawAsync(paged?.Sql ?? source, ct);
        var elapsed = sw.Elapsed.TotalSeconds;
        IEnumerable<string?[]> rows = raw.Rows;
        bool hasMore = false;
        if (paged is { } p)
        {
            var list = raw.Rows.Skip(p.Skip).ToList();
            hasMore = list.Count > limit;
            rows = hasMore ? list.Take(limit) : list;
        }
        var kw = SqlText.FirstKeyword(sql);
        string? message = raw.Columns.Count == 0
            ? (kw is "SELECT" or "SHOW" or "DESC" or "DESCRIBE" or "EXPLAIN" or "WITH" ? Tr("결과 없음") : Tr("실행 완료"))
            : null;
        if (kw == "USE") { try { Current = await CurrentContainerAsync(ct) ?? Current; } catch { } }
        int truncated = 0;
        var rowList = rows.ToList();
        if (capCells)
            foreach (var r in rowList)
                for (int c = 0; c < r.Length; c++)
                    if (r[c] is { Length: > MaxCellCharacters } v) { r[c] = v[..MaxCellCharacters] + "…"; truncated++; }
        var result = new QueryResult(raw.Columns, rowList, elapsed, message) { TruncatedCells = truncated };
        if (paged != null)
        {
            result.Page = Math.Max(0, page);
            result.PageSize = limit;
            result.HasMore = hasMore;
        }
        if (source != sql) result.ServerSort = sort;
        NoteExecuted(sql);
        return result;
    }

    /// 읽기전용 접속이면 문장 전체를 검사해 쓰기 · 상태 변경을 막는다
    internal void CheckReadOnly(string sql)
    {
        if (Profile.ReadOnly && SqlText.ReadOnlyViolationOf(sql, Dialect.Kind) is { } v) throw new GodDbException(v);
    }

    /// 읽기전용 PostgreSQL: 문장마다 READ ONLY 트랜잭션 안에서 실행하고 끝나면 롤백한다.
    /// 세션의 READ ONLY 는 set_config · SET 으로 끌 수 있지만, BEGIN READ ONLY 로 연 트랜잭션은 안에서 쓰기로 바꿀 수 없다.
    /// 롤백하므로 문장 안에서 바꾼 세션 설정(set_config(…, false) 등)도 남지 않는다. 앱 쪽 검사는 그대로 먼저 한다.
    internal async Task<RawResult> ReadOnlyGuardedRawAsync(string sql, CancellationToken ct = default)
    {
        if (!Profile.ReadOnly || Dialect.Kind != DatabaseKind.Postgres || InTransaction) return await RawAsync(sql, ct);
        await RawAsync("BEGIN READ ONLY", ct);
        try
        {
            var r = await RawAsync(sql, ct);
            try { await RawAsync("ROLLBACK", CancellationToken.None); } catch { }
            return r;
        }
        catch
        {
            try { await RawAsync("ROLLBACK", CancellationToken.None); } catch { }
            throw;
        }
    }

    private static readonly HashSet<string> TransactionControl = ["BEGIN", "START", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "SET", "USE"];
    private static readonly HashSet<string> Ddl = ["CREATE", "ALTER", "DROP", "RENAME", "TRUNCATE"];

    /// 수동 커밋 모드에서 쓰기 문장이면 먼저 트랜잭션을 연다
    private async Task BeginIfNeededAsync(string sql, CancellationToken ct)
    {
        if (AutoCommit || InTransaction) return;
        var kw = SqlText.FirstKeyword(SqlText.StripComments(sql));
        if (SqlText.IsReadOnly(sql, Dialect.Kind) || TransactionControl.Contains(kw)) return;
        // MySQL 의 DDL 은 어차피 자동 커밋되므로 트랜잭션을 열지 않는다
        if (Dialect.Kind == DatabaseKind.Mysql && Ddl.Contains(kw)) return;
        await RawAsync(Dialect.BeginSql(), ct);
        InTransaction = true;
        PendingChanges = 0;
    }

    /// 실행 뒤 트랜잭션 상태 갱신 (사용자가 직접 COMMIT/ROLLBACK/BEGIN 을 쓴 경우, MySQL DDL 의 암묵적 커밋)
    private void NoteExecuted(string sql)
    {
        var kw = SqlText.FirstKeyword(SqlText.StripComments(sql));
        var upper = sql.ToUpperInvariant();
        switch (kw)
        {
            case "COMMIT" or "ROLLBACK" or "END":
                if (!upper.Contains("TO SAVEPOINT")) { InTransaction = false; PendingChanges = 0; }
                break;
            case "BEGIN" or "START":
                if (Dialect.Kind != DatabaseKind.Mssql || upper.Contains("TRAN")) { InTransaction = true; PendingChanges = 0; }
                break;
            default:
                if (Dialect.Kind == DatabaseKind.Mysql && Ddl.Contains(kw)) { InTransaction = false; PendingChanges = 0; }
                else if (InTransaction && !SqlText.IsReadOnly(sql, Dialect.Kind)) PendingChanges++;
                break;
        }
    }

    /// 자동 커밋 켜기/끄기. 열린 트랜잭션이 있으면 먼저 커밋/롤백해야 한다.
    public void SetAutoCommit(bool on)
    {
        if (on && InTransaction) throw new GodDbException(DbErrorKind.TransactionOpen);
        AutoCommit = on;
    }

    /// 열린 트랜잭션 커밋. 커밋한 변경 수를 돌려준다.
    public async Task<int> CommitAsync(CancellationToken ct = default)
    {
        var n = PendingChanges;
        await RawAsync(Dialect.CommitSql(), ct);
        InTransaction = false;
        PendingChanges = 0;
        return n;
    }

    /// 열린 트랜잭션 롤백. 되돌린 변경 수를 돌려준다.
    public async Task<int> RollbackAsync(CancellationToken ct = default)
    {
        var n = PendingChanges;
        await RawAsync(Dialect.RollbackSql(), ct);
        InTransaction = false;
        PendingChanges = 0;
        return n;
    }

    /// 결과 편집 적용 등 앱이 직접 여러 문장을 한 트랜잭션으로 실행할 때 (열린 트랜잭션이 있으면 거기에 합류)
    internal void NoteChanges(int n) { if (InTransaction) PendingChanges += n; }

    /// 쿼리 결과의 전체 행 수 (SELECT 만)
    public async Task<int?> CountAsync(string sql, CancellationToken ct = default)
    {
        CheckReadOnly(sql);
        if (Dialect.CountSql(sql) is not { } countSql) return null;
        var v = (await ReadOnlyGuardedRawAsync(countSql, ct)).Rows.FirstOrDefault()?.FirstOrDefault();
        return int.TryParse(v, out var n) ? n : null;
    }

    /// 실행 계획. 트리 형태 텍스트가 있으면 함께 돌려준다.
    public async Task<(QueryResult Result, string? Tree)> ExplainAsync(string sql, CancellationToken ct = default)
    {
        // 읽기전용: 계획만 보더라도 쓰기 문장 · EXPLAIN ANALYZE 같은 실제 실행은 막는다
        CheckReadOnly(sql);
        var steps = Dialect.Explain(sql);
        var sw = Stopwatch.StartNew();
        foreach (var s in steps.Before) await RawAsync(s, ct);
        RawResult main;
        try { main = await ReadOnlyGuardedRawAsync(steps.Main, ct); }
        catch
        {
            foreach (var s in steps.After) { try { await RawAsync(s, ct); } catch { } }
            throw;
        }
        foreach (var s in steps.After) await RawAsync(s, ct);
        var result = new QueryResult(main.Columns, main.Rows, sw.Elapsed.TotalSeconds);
        string? tree = null;
        if (steps.TreeSql is { } treeSql)
        {
            try { tree = (await RawAsync(treeSql, ct)).Rows.FirstOrDefault()?.FirstOrDefault(); } catch { }
        }
        else if (steps.TreeColumn is { } col && main.Columns.ToList().IndexOf(col) is var i && i >= 0)
            tree = string.Join("\n", main.Rows.Select(r => r[i]).Where(v => v != null));
        return (result, tree);
    }

    /// 모든 값을 문자열로 받는다.
    public async Task<RawResult> RawAsync(string sql, CancellationToken ct = default)
    {
        if (_driver is not { IsOpen: true }) throw new GodDbException(DbErrorKind.NotConnected);
        await _gate.WaitAsync(ct);
        Interlocked.Increment(ref _running);
        LastActivity = DateTime.UtcNow;
        try
        {
            var r = await _driver.QueryAsync(sql, TimeoutSeconds, ct);
            // MySQL 은 KILL QUERY 로 멈춘 SLEEP 등이 오류 없이 끝나기도 한다 → 취소를 요청했으면 취소로 본다
            if (_cancelRequested) { _cancelRequested = false; throw new GodDbException(DbErrorKind.Cancelled); }
            return r;
        }
        catch (Exception e) when (e is not GodDbException)
        {
            if (_cancelRequested || ct.IsCancellationRequested)
            {
                _cancelRequested = false;
                if (Profile.Kind == DatabaseKind.Mssql && !(_driver?.IsOpen ?? false)) await ReopenAfterKillAsync();
                throw new GodDbException(DbErrorKind.Cancelled);
            }
            throw;
        }
        finally
        {
            LastActivity = DateTime.UtcNow;
            Interlocked.Decrement(ref _running);
            _gate.Release();
        }
    }

    // MARK: - 메타데이터

    /// 서버 버전 (예: MySQL 8.0.36, PostgreSQL 16.4, SQL Server 16.0.4315.2 Developer Edition)
    public async Task<string> ServerVersionAsync(CancellationToken ct = default)
    {
        if (Dialect.IsRedshift())
        {
            // SHOW server_version 은 슈퍼유저만 → version() 에서 "Redshift 1.0.x"
            var rv = (await RawAsync("SELECT version()", ct)).Rows.FirstOrDefault()?.FirstOrDefault();
            return "Amazon " + ((rv == null ? null : PostgresDialect.RedshiftVersion(rv)) ?? "Redshift");
        }
        var v = (await RawAsync(Dialect.VersionSql, ct)).Rows.FirstOrDefault()?.FirstOrDefault();
        return $"{Profile.Kind.Title()} {v ?? ""}".Trim();
    }

    /// 빠른 찾기용 모든 스키마의 테이블 · 뷰 (지원하지 않으면 빈 목록)
    public async Task<List<(string Schema, string Name, bool IsView)>> AllTablesAsync(CancellationToken ct = default)
    {
        if (Dialect.AllTablesSql is not { } sql) return [];
        return (await RawAsync(sql, ct)).Rows
            .Where(r => r.Length >= 3 && r[0] != null && r[1] != null)
            .Select(r => (r[0]!, r[1]!, (r[2] ?? "").ToUpperInvariant().Contains("VIEW")))
            .ToList();
    }

    public async Task<List<string>> DatabasesAsync(CancellationToken ct = default) =>
        (await RawAsync(Dialect.DatabasesSql, ct)).Rows.Select(r => r.FirstOrDefault()).OfType<string>().ToList();

    public async Task UseAsync(string name, CancellationToken ct = default)
    {
        await RawAsync(Dialect.UseSql(name), ct);
        Current = name;
    }

    private async Task<string?> CurrentContainerAsync(CancellationToken ct)
    {
        var sql = Profile.Kind switch
        {
            DatabaseKind.Postgres => "SELECT current_schema()",
            DatabaseKind.Mssql => "SELECT DB_NAME()",
            _ => "SELECT DATABASE()",
        };
        return (await RawAsync(sql, ct)).Rows.FirstOrDefault()?.FirstOrDefault();
    }

    public async Task<List<TableMeta>> LoadSchemaAsync(string schema, CancellationToken ct = default)
    {
        if (Dialect.MetadataNeedsUse && Current != schema) await UseAsync(schema, ct);
        var tableRows = (await RawAsync(Dialect.TablesSql(schema), ct)).Rows;
        var columnRows = (await RawAsync(Dialect.ColumnsSql(schema), ct)).Rows;
        var indexRows = (await RawAsync(Dialect.IndexesSql(schema), ct)).Rows;
        var tables = SchemaBuilder.Build(tableRows, columnRows, indexRows);
        // 외래 키는 권한 등으로 못 읽어도 스키마 자체는 보여준다
        try
        {
            var fkRows = (await RawAsync(Dialect.ForeignKeysSql(schema), ct)).Rows;
            tables = SchemaBuilder.AttachForeignKeys(tables, fkRows);
        }
        catch (GodDbException) { throw; }
        catch { }
        return tables;
    }

    public async Task<List<RoutineMeta>> LoadRoutinesAsync(string schema, CancellationToken ct = default)
    {
        if (Dialect.MetadataNeedsUse && Current != schema) await UseAsync(schema, ct);
        var list = new List<RoutineMeta>();
        foreach (var r in (await RawAsync(Dialect.RoutinesSql(schema), ct)).Rows)
            if (r[0] is { } type && r[1] is { } name && RoutineKindExtensions.FromRawValue(type) is { } kind)
                list.Add(new RoutineMeta(kind, name, r.Length > 2 ? r[2] ?? "" : ""));
        return list;
    }

    /// 뷰/프로시저/함수/트리거 정의 전체
    public async Task<string?> DefinitionAsync(RoutineMeta routine, CancellationToken ct = default)
    {
        var raw = await RawAsync(Dialect.DefinitionSql(routine, Current), ct);
        if (raw.Rows.FirstOrDefault() is not { } row) return null;
        string[] wanted = ["Create View", "Create Procedure", "Create Function", "SQL Original Statement"];
        var cols = raw.Columns.ToList();
        var i = cols.FindIndex(wanted.Contains);
        var body = i >= 0 ? row[i] : row.Where(v => v != null).MaxBy(v => v!.Length);
        return body == null ? null : Dialect.FormatDefinition(routine, Current, body);
    }

    /// 테이블 CREATE 문. MySQL 은 서버가 주는 DDL, 나머지는 메타데이터로 만든다.
    public async Task<string> TableDdlAsync(TableMeta table, CancellationToken ct = default)
    {
        if (Dialect.ShowCreateTableSql(table.Name) is { } sql
            && (await RawAsync(sql, ct)).Rows.FirstOrDefault() is { Length: > 1 } row && row[1] is { } ddl)
            return ddl + ";";
        return Dialect.CreateTableSql(table);
    }

    /// MySQL 의 SHOW CREATE TABLE (스키마 비교에서 누락 테이블 생성용). 다른 DB 는 null.
    public async Task<string?> ShowCreateTableAsync(string table, CancellationToken ct = default)
    {
        if (Dialect.ShowCreateTableSql(table) is not { } sql) return null;
        return (await RawAsync(sql, ct)).Rows.FirstOrDefault() is { Length: > 1 } row ? row[1] : null;
    }
}
