using GodDB.Core.Results;
using GodDB.Core.Schema;

namespace GodDB.Core.Dialects;

/// 실행 계획을 보기 위해 실행할 SQL 들
public sealed record ExplainSteps
{
    public List<string> Before { get; init; } = [];
    public required string Main { get; init; }
    public List<string> After { get; init; } = [];
    /// 트리 형태 계획을 얻는 별도 SQL (MySQL)
    public string? TreeSql { get; init; }
    /// main 결과에서 이 컬럼을 줄바꿈으로 이어 트리로 쓴다 (PostgreSQL, SQL Server)
    public string? TreeColumn { get; init; }
}

/// 한 페이지를 가져오는 SQL. Skip 은 받아온 뒤 앞에서 버릴 행 수 (SQL Server 에서 ORDER BY 가 없을 때)
public readonly record struct PagedSql(string Sql, int Skip);

/// DB 별 SQL 문법 차이를 모은 곳. (Swift SQLDialect 프로토콜 + 확장. 구현은 SqlDialect 기반 클래스)
/// 메타데이터 SQL 은 SchemaBuilder 가 읽는 정규화된 컬럼 순서로 돌려줘야 한다.
/// - tables:  [테이블, 코멘트]
/// - columns: [테이블, 컬럼, 타입, YES/NO, 기본값, extra, PRI/'', 코멘트, 순서]
/// - indexes: [테이블, 인덱스, NON_UNIQUE 0/1, 컬럼, PRIMARY 1/0, 제약조건 1/0]
/// - routines: [VIEW/PROCEDURE/FUNCTION/TRIGGER, 이름, 코멘트]
public interface ISqlDialect
{
    DatabaseKind Kind { get; }

    string QuoteIdent(string name);
    /// 테이블 이름 (SQL Server 는 `schema.table` 형태일 수 있음)
    string QuoteTable(string name);
    string QuoteString(string value);

    string ApplyingLimit(string sql, int limit);
    /// 읽기전용 접속일 때 연결 직후 실행할 SQL (서버 측 차단)
    string? ReadOnlySessionSql { get; }
    /// 연결 직후 항상 실행할 세션 설정 (실패해도 접속은 계속)
    string? SessionSetupSql { get; }
    /// 서버 세션의 실행 시간 제한 (밀리초). 지원하지 않으면 null
    string? StatementTimeoutSql(int milliseconds);
    /// 이 연결의 서버 쪽 세션 번호 (실행 중인 쿼리 취소에 쓴다)
    string SessionIdSql { get; }
    /// 다른 연결에서 실행해 그 세션의 쿼리를 멈추는 SQL (id 는 숫자만)
    string CancelSql(int sessionId);

    string DatabasesSql { get; }
    string UseSql(string name);
    /// 메타데이터를 읽기 전에 USE 가 필요한지 (SQL Server)
    bool MetadataNeedsUse { get; }
    string InitialContainer(ConnectionProfile profile, IReadOnlyList<string> available);

    string TablesSql(string schema);
    string ColumnsSql(string schema);
    string IndexesSql(string schema);
    string RoutinesSql(string schema);
    string DefinitionSql(RoutineMeta routine, string schema);
    string FormatDefinition(RoutineMeta routine, string schema, string body);

    /// 서버가 DDL 을 직접 주는 경우 (MySQL SHOW CREATE TABLE). null 이면 메타데이터로 만든다.
    string? ShowCreateTableSql(string table);
    string CreateTableSql(TableMeta table);

    ExplainSteps Explain(string sql);

    string ColumnDefinition(ColumnMeta c);
    string DefaultLiteral(string value, string extra);
    string Render(IReadOnlyList<SchemaChange> changes, string table);
    /// 빠른 찾기용: 모든 DB(스키마)의 (스키마, 테이블, 종류) — 지원하지 않으면 null (SQL Server 는 지금 DB 만)
    string? AllTablesSql { get; }

    // ── 화면에서 쓰는 DB 별 SQL ──
    /// 테이블 데이터 보기
    string SelectAllSql(string table);
    /// 인덱스 상세 (카디널리티/정의 등)
    string IndexDetailSql(string table, string schema);
    /// 서버 버전 한 줄
    string VersionSql { get; }
    /// 프로시저 호출 문 틀
    string CallSql(string name);
    string RenameTableSql(string table, string newName);
    string DropTableSql(string table);
    /// [테이블, 외래키 이름, 컬럼, 참조 테이블, 참조 컬럼, 순서] — 같은 DB/스키마 안의 참조만
    string ForeignKeysSql(string schema);

    // ── 결과 페이징 · 건수 · 정렬 ──
    /// SELECT 를 페이지 단위로 가져오는 SQL. 다음 페이지가 있는지 알기 위해 limit + 1 행을 요청한다.
    /// 이미 LIMIT/TOP/OFFSET/INTO/FOR UPDATE 가 있거나 SELECT 가 아니면 null (그대로 실행).
    PagedSql? PagedSql(string sql, int limit, int offset);
    /// 쿼리 결과의 전체 행 수
    string? CountSql(string sql);
    /// 원래 쿼리를 감싸 정렬한 SQL. SELECT 가 아니면 null.
    string? SortedSql(string sql, ResultSort sort);

    /// DB 종류에 맞는 문법 (Redis 는 MySQL 문법). `Dialects.Of` 와 같다.
    /// GodDB.Core.* 네임스페이스 안에서는 `Dialects` 가 네임스페이스로 먼저 잡히므로 이쪽을 쓴다.
    static ISqlDialect Of(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Postgres => new PostgresDialect(),
        DatabaseKind.Mssql => new MssqlDialect(),
        _ => new MySqlDialect(),
    };
}

public static class Dialects
{
    public static ISqlDialect Of(DatabaseKind kind) => ISqlDialect.Of(kind);
}
