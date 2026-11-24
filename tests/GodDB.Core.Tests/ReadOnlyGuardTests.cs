using GodDB.Core.Audit;
using GodDB.Core.Drivers;
using GodDB.Core.Sql;

namespace GodDB.Core.Tests;

/// Mac ReadOnlyGuardTests.swift — 읽기전용 접속의 문장 검사 (#155). 우회 방법은 막고, 흔한 조회는 통과해야 한다.
public class ReadOnlyGuardTests
{
    private static readonly DatabaseKind[] All = [DatabaseKind.Mysql, DatabaseKind.Postgres, DatabaseKind.Mssql];
    private static readonly DatabaseKind[] My = [DatabaseKind.Mysql];
    private static readonly DatabaseKind[] Pg = [DatabaseKind.Postgres];
    private static readonly DatabaseKind[] Ms = [DatabaseKind.Mssql];

    private static void Blocked(string sql, DatabaseKind[]? kinds = null)
    {
        foreach (var k in kinds ?? All)
            Assert.False(SqlText.IsReadOnly(sql, k), $"{k} 에서 통과하면 안 됨: {sql}");
    }

    private static void Allowed(string sql, DatabaseKind[]? kinds = null)
    {
        foreach (var k in kinds ?? All)
        {
            var v = SqlText.ReadOnlyViolationOf(sql, k);
            Assert.True(v == null, $"{k} 에서 막히면 안 됨: {sql} → {v}");
        }
    }

    // MARK: - 우회 방법

    [Fact]
    public void MssqlBackToBackStatements()
    {
        // T-SQL 은 ; 없이 문장을 이어 쓸 수 있다
        Blocked("SELECT 1 DELETE FROM t", Ms);
        Blocked("SELECT * FROM t\nDELETE FROM t", Ms);
        Blocked("SELECT 1 UPDATE t SET a = 1", Ms);
        Blocked("SELECT 1 DROP TABLE t", Ms);
        Blocked("SELECT 1 EXEC sp_who", Ms);
        Blocked("SELECT 1DELETE FROM t", Ms);
        Blocked("SELECT 1.DELETE FROM t", Ms);
        Blocked("USE shop DELETE FROM t", Ms);
        Blocked("SELECT 1 TRUNCATE TABLE t", Ms);
        Blocked("SELECT 1 SHUTDOWN", Ms);
        Assert.Equal(ReadOnlyViolation.Keyword("DELETE"), SqlText.ReadOnlyViolationOf("SELECT 1 DELETE FROM t", DatabaseKind.Mssql));
    }

    [Fact]
    public void SelectIntoCreatesTable()
    {
        Blocked("SELECT * INTO new_tbl FROM t");
        Blocked("SELECT * INTO #copy FROM t", Ms);
        Blocked("SELECT * FROM t INTO OUTFILE '/tmp/x'", My);
        Blocked("SELECT * FROM t INTO DUMPFILE '/tmp/x'", My);
        Assert.Equal(ReadOnlyViolation.Keyword("INTO"), SqlText.ReadOnlyViolationOf("SELECT * INTO new_tbl FROM t", DatabaseKind.Mssql));
    }

    [Fact]
    public void WritesInsideCTE()
    {
        Blocked("WITH x AS (SELECT 1 AS id) MERGE INTO t USING x ON t.id = x.id WHEN MATCHED THEN DELETE");
        Blocked("WITH x AS (DELETE FROM t RETURNING *) SELECT * FROM x", Pg);
        Blocked("WITH x AS (SELECT 1) DELETE FROM t");
        Blocked("WITH x AS (SELECT 1) INSERT INTO t SELECT * FROM x");
        Blocked("WITH x AS (SELECT 1) UPDATE t SET a = 1");
    }

    [Fact]
    public void MssqlTempTableIsNotAComment()
    {
        // # 은 SQL Server 에서 임시 테이블 이름. 예전에는 줄 끝까지 주석으로 지워져 뒤의 DELETE 를 못 봤다
        Blocked("WITH a AS (SELECT * FROM #tmp) DELETE FROM t", Ms);
        Blocked("SELECT * FROM #tmp DELETE FROM t", Ms);
        Blocked("SELECT * FROM ##global DELETE FROM t", Ms);
        // PostgreSQL 의 # 은 연산자
        Blocked("SELECT 1 # 2, set_config('a', 'b', false)", Pg);
    }

    [Fact]
    public void MysqlHashIsAComment() => Allowed("SELECT 1 # DELETE FROM t", My);

    [Fact]
    public void PostgresSessionReadOnlyOff()
    {
        Blocked("SELECT set_config('default_transaction_read_only', 'off', false)", Pg);
        Blocked("SELECT pg_catalog.set_config('default_transaction_read_only', 'off', false)", Pg);
        Blocked("SELECT \"set_config\"('transaction_read_only', 'off', false)", Pg);
        Blocked("SELECT pg_catalog . set_config /* x */ ('a', 'b', false)", Pg);
        Blocked("SET SESSION CHARACTERISTICS AS TRANSACTION READ WRITE", Pg);
        Blocked("SET default_transaction_read_only = off", Pg);
        Blocked("SET transaction_read_only = off", Pg);
        Blocked("RESET default_transaction_read_only", Pg);
        Assert.Equal(ReadOnlyViolation.Function("set_config"), SqlText.ReadOnlyViolationOf("SELECT set_config('a','b',false)", DatabaseKind.Postgres));
    }

    [Fact]
    public void StateChangingFunctions()
    {
        foreach (var f in new[] { "pg_terminate_backend(123)", "pg_cancel_backend(123)", "pg_advisory_lock(1)", "pg_advisory_xact_lock(1)",
                     "pg_try_advisory_lock(1)", "pg_advisory_unlock_all()", "dblink_exec('c', 'DELETE FROM t')",
                     "dblink('c', 'select 1')", "lo_import('/etc/passwd')", "lo_export(1, '/tmp/x')", "lo_unlink(1)",
                     "nextval('seq')", "setval('seq', 1)", "pg_reload_conf()", "pg_read_file('x')", "query_to_xml('x', true, true, '')" })
            Blocked("SELECT " + f, Pg);
        foreach (var f in new[] { "GET_LOCK('a', 1)", "RELEASE_LOCK('a')", "LOAD_FILE('/etc/passwd')", "release_all_locks()" })
            Blocked("SELECT " + f, My);
        Blocked("SELECT * FROM OPENQUERY(srv, 'DELETE FROM t')", Ms);
        Blocked("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'select 1')", Ms);
        Blocked("SELECT * FROM OPENDATASOURCE('SQLNCLI', 'x').db.dbo.t", Ms);
        Blocked("EXEC sp_who", Ms);
        Blocked("EXECUTE ('DELETE FROM t')", Ms);
        Blocked("sp_configure 'show advanced options', 1", Ms);
        Blocked("SELECT * FROM t; EXEC master.dbo.xp_cmdshell 'dir'", Ms);
        Blocked("SELECT 1 xp_cmdshell 'dir'", Ms);
        // SLEEP 는 상태를 바꾸지 않는다
        Allowed("SELECT SLEEP(1)", My);
        Allowed("SELECT pg_sleep(1), currval('s')", Pg);
    }

    [Fact]
    public void RowLocks()
    {
        Blocked("SELECT * FROM t FOR UPDATE");
        Blocked("SELECT * FROM t FOR SHARE", [DatabaseKind.Mysql, DatabaseKind.Postgres]);
        Blocked("SELECT * FROM t FOR NO KEY UPDATE", Pg);
        Blocked("SELECT * FROM t FOR KEY SHARE", Pg);
        Blocked("SELECT * FROM t LOCK IN SHARE MODE", My);
        Assert.Equal(ReadOnlyViolation.Lock("FOR NO KEY UPDATE"), SqlText.ReadOnlyViolationOf("SELECT * FROM t FOR NO KEY UPDATE", DatabaseKind.Postgres));
        Assert.Equal(ReadOnlyViolation.Lock("LOCK IN SHARE MODE"), SqlText.ReadOnlyViolationOf("select * from t lock in share mode", DatabaseKind.Mysql));
    }

    [Fact]
    public void ExplainAnalyzeOfWrite()
    {
        // PostgreSQL EXPLAIN ANALYZE 는 실제로 실행한다
        Blocked("EXPLAIN ANALYZE DELETE FROM t", [DatabaseKind.Postgres, DatabaseKind.Mysql]);
        Blocked("EXPLAIN (ANALYZE, BUFFERS) UPDATE t SET a = 1", Pg);
        Blocked("EXPLAIN DELETE FROM t");
        Blocked("EXPLAIN ANALYZE SELECT nextval('s')", Pg);
        Blocked("ANALYZE t", Pg);
        Blocked("SELECT 1 ANALYZE", Ms);
        Allowed("EXPLAIN SELECT * FROM t");
        Allowed("EXPLAIN ANALYZE SELECT * FROM t", [DatabaseKind.Mysql, DatabaseKind.Postgres]);
        Allowed("EXPLAIN (ANALYZE, FORMAT JSON) SELECT * FROM t", Pg);
    }

    [Fact]
    public void WriteKeywordsAnywhere()
    {
        foreach (var w in new[] { "CREATE TABLE x (a int)", "ALTER TABLE t ADD c int", "GRANT SELECT ON t TO u", "CALL p()",
                     "DO $$ BEGIN END $$", "COPY t TO '/tmp/x'", "VACUUM t", "REINDEX TABLE t", "REFRESH MATERIALIZED VIEW v",
                     "COMMENT ON TABLE t IS 'x'", "LOCK TABLE t", "KILL 55", "FLUSH TABLES", "DECLARE @x int",
                     "BEGIN", "COMMIT", "ROLLBACK", "TRUNCATE t", "REPLACE INTO t VALUES (1)", "UPSERT INTO t VALUES (1)" })
        {
            Blocked(w);
            Blocked("SELECT 1 " + w, Ms);
        }
    }

    [Fact]
    public void MysqlExecutableComments()
    {
        // /*! … */ 는 MySQL 이 실행한다
        Blocked("SELECT * FROM t /*!50000 INTO OUTFILE '/tmp/x' */", My);
        Blocked("SELECT 1 /*! , GET_LOCK('a', 1) */", My);
        // -- 뒤에 공백이 없으면 MySQL 에선 주석이 아니다 (1--1 = 1 - -1)
        Blocked("SELECT 1--1 INTO OUTFILE '/tmp/x'", My);
        Allowed("SELECT 1 -- INTO OUTFILE\n FROM t", My);
    }

    [Fact]
    public void StringEscapesCannotHideCode()
    {
        // MySQL NO_BACKSLASH_ESCAPES 에선 '\' 가 끝난 문자열이다
        Blocked(@"SELECT '\' , GET_LOCK('a', 1) -- '", My);
        // standard_conforming_strings=off 이거나 E'…' 에서 \' 는 따옴표 글자
        Blocked(@"SELECT E'\'', set_config('a', 'b', false) -- '", Pg);
        // 겹친 블록 주석 (PostgreSQL · SQL Server)
        Blocked("SELECT 1 /* /* */ ' */ , set_config('a','b',false) -- '", Pg);
        Blocked("SELECT 1 /* /* */ ' */ DELETE FROM t -- '", Ms);
        // 유니코드 공백으로 붙여 쓰기
        Blocked("SELECT 1　DELETE FROM t", Ms);
    }

    [Fact]
    public void LeadingKeyword()
    {
        Blocked("DELETE FROM t");
        Blocked("INSERT INTO t VALUES (1)");
        Blocked("(SELECT 1)");
        Blocked("");
        Blocked("-- only comment");
        Assert.Equal(ReadOnlyViolation.Leading("DELETE"), SqlText.ReadOnlyViolationOf("delete from t", DatabaseKind.Mysql));
    }

    [Fact]
    public void MessageNamesTheTrigger()
    {
        var m = new GodDbException(ReadOnlyViolation.Keyword("DELETE")).Message;
        Assert.Contains("DELETE", m);
        Assert.Contains("문장 안에 있음", m);
        Assert.Contains("set_config()", new GodDbException(ReadOnlyViolation.Function("set_config")).Message);
        Assert.Contains("FOR UPDATE", new GodDbException(ReadOnlyViolation.Lock("FOR UPDATE")).Message);
        Assert.Contains("DELETE", new GodDbException(ReadOnlyViolation.Leading("DELETE")).Message);
        // 상태창에도 같은 문구 (TLS 추측 같은 다른 변환을 거치지 않음)
        Assert.Equal(m, ErrorText.Describe(new GodDbException(ReadOnlyViolation.Keyword("DELETE"))).Message);
    }

    // MARK: - 통과해야 하는 조회

    [Fact]
    public void LegitQueriesPass()
    {
        Allowed("SELECT * FROM t WHERE memo = 'please delete me' AND note = 'insert; update; drop table x'");
        Allowed("SELECT a -- update later\nFROM t /* delete? no */");
        Allowed("SELECT updated_at, deleted_yn, insert_dt, created_by, update_cnt FROM tb_member");
        Allowed("SELECT * FROM t WHERE x IN (1, 2, 3) AND y IN (SELECT id FROM u)");
        Allowed("WITH x AS (SELECT 1 AS id), y AS (SELECT * FROM x) SELECT * FROM y");
        Allowed("SELECT t.comment, t.lock, t.handler FROM t");
        Allowed("SELECT a AS lock, b AS comment FROM t");
        Allowed("SELECT comment FROM t");
        Allowed("SELECT \"delete\", \"update\" FROM t", [DatabaseKind.Postgres, DatabaseKind.Mssql]);
        Allowed("SELECT `delete`, `update` FROM t", My);
        Allowed("SELECT [delete], [update] FROM t", Ms);
        Allowed("SELECT * FROM information_schema.columns WHERE table_name = 'tb_member'");
        Allowed("SELECT update_rule, delete_rule FROM information_schema.referential_constraints");
        Allowed("SELECT event_manipulation FROM information_schema.triggers WHERE event_manipulation = 'DELETE'");
        Allowed("SELECT CASE WHEN a > 1 THEN 'x' ELSE 'y' END FROM t");
        Allowed("SELECT COUNT(*) FROM t GROUP BY a HAVING COUNT(*) > 1 ORDER BY a DESC");
    }

    [Fact]
    public void MysqlLegit()
    {
        Allowed("SELECT REPLACE(col, 'a', 'b') FROM t", My);
        Allowed("SELECT replace (col, 'a', 'b') FROM t", My);
        Allowed("SELECT 'it''s', 'back\\'slash' FROM t", My);
        Allowed("SHOW TABLES", My);
        Allowed("SHOW CREATE TABLE tb_member", My);
        Allowed("SHOW CHARACTER SET", My);
        Allowed("SHOW FULL PROCESSLIST", My);
        Allowed("SHOW VARIABLES LIKE 'max%'", My);
        Allowed("DESC tb_member", My);
        Allowed("DESCRIBE tb_member", My);
        Allowed("USE shop", My);
        Allowed("SELECT CAST(x AS CHAR CHARACTER SET utf8mb4) FROM t", My);
        Allowed("SELECT /*+ MAX_EXECUTION_TIME(1000) */ * FROM t", My);
        Allowed("SELECT @a := 1, @@version", My);
    }

    [Fact]
    public void PostgresLegit()
    {
        Allowed("SELECT '#' || name, '# not a comment' FROM t WHERE a # b = 0", Pg);
        Allowed("SELECT $$delete from t$$, $tag$ update $tag$", Pg);
        Allowed("SELECT E'it\\'s', 'plain' FROM t", Pg);
        Allowed("SHOW transaction_read_only", Pg);
        Allowed("SELECT * FROM pg_stat_activity WHERE state <> 'idle'", Pg);
        Allowed("SELECT currval('s'), current_setting('search_path')", Pg);
        Allowed("SELECT x::text, ARRAY[1, 2][1] FROM t", Pg);
        Allowed("SELECT * FROM t WHERE id = $1", Pg);
    }

    [Fact]
    public void MssqlLegit()
    {
        Allowed("SELECT * FROM #tmp", Ms);
        Allowed("SELECT * FROM ##global_tmp WHERE a = '#'", Ms);
        Allowed("SELECT TOP 10 * FROM dbo.tb_member WITH (NOLOCK) WHERE name LIKE N'%#%'", Ms);
        Allowed("WITH a AS (SELECT * FROM #tmp) SELECT * FROM a", Ms);
        Allowed("SELECT @@ROWCOUNT, @@VERSION", Ms);
        Allowed("USE shop", Ms);
        Allowed("SELECT * FROM t FOR JSON PATH", Ms);
        Allowed("SELECT 'back\\' FROM t", Ms);
    }

    [Fact]
    public void OtherCallSitesUseTheSameRules()
    {
        // 수동 커밋 · 운영 확인에서도 T-SQL 이어 쓰기를 쓰기로 본다
        Assert.False(SqlText.IsReadOnly("SELECT 1 DELETE FROM t", DatabaseKind.Mssql));
        // Mac 은 DataFinder.isSelectOnly(첫 단어 SELECT/WITH + isReadOnly)도 함께 본다. AI 데이터 찾기는 다른 포팅 작업이라
        // 같은 판정(첫 단어 + 읽기전용 검사)을 여기서 직접 확인한다.
        static bool SelectOnly(string sql, DatabaseKind k) =>
            SqlText.FirstKeyword(sql) is "SELECT" or "WITH" && SqlText.IsReadOnly(sql, k);
        Assert.False(SelectOnly("SELECT * FROM #tmp DELETE FROM t", DatabaseKind.Mssql));
        Assert.True(SelectOnly("SELECT * FROM #tmp", DatabaseKind.Mssql));
    }

    /// SQL Server: Service Broker RECEIVE · SEND, 시퀀스 NEXT VALUE FOR 도 막는다 (#193). 같은 이름의 열은 그대로
    [Fact]
    public void MssqlBrokerAndSequence()
    {
        (string Sql, ReadOnlyViolation Want)[] blocked =
        [
            ("RECEIVE TOP (1) * FROM dbo.q", ReadOnlyViolation.Leading("RECEIVE")),
            ("SELECT 1; RECEIVE TOP (1) * FROM dbo.q", ReadOnlyViolation.Keyword("RECEIVE")),
            ("SELECT 1; SEND ON CONVERSATION @h MESSAGE TYPE m (N'x')", ReadOnlyViolation.Keyword("SEND")),
            ("SELECT NEXT VALUE FOR dbo.order_seq", ReadOnlyViolation.Keyword("NEXT VALUE FOR")),
            ("select next value for order_seq as n", ReadOnlyViolation.Keyword("NEXT VALUE FOR")),
        ];
        foreach (var (sql, want) in blocked)
            Assert.True(want == SqlText.ReadOnlyViolationOf(sql, DatabaseKind.Mssql), sql);
        foreach (var sql in new[] { "SELECT send, receive, next, value FROM t", "SELECT t.send FROM t WHERE receive = 1",
                     "SELECT [next] FROM t ORDER BY value" })
            Assert.True(SqlText.ReadOnlyViolationOf(sql, DatabaseKind.Mssql) == null, sql);
    }
}
