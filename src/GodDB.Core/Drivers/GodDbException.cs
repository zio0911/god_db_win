using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.Core.Drivers;

public enum DbErrorKind { ReadOnlyBlocked, NotConnected, TransactionOpen, Cancelled, ReadOnlyRejected }

/// 앱이 직접 내는 DB 오류 (드라이버 오류는 각 드라이버의 예외 그대로)
public sealed class GodDbException : Exception
{
    public DbErrorKind Kind { get; }
    /// ReadOnlyRejected: 읽기전용 접속에서 막은 문장의 걸린 단어 · 함수 (Mac DBError.readOnlyRejected)
    public ReadOnlyViolation? Violation { get; }

    public GodDbException(DbErrorKind kind) : base(Describe(kind)) { Kind = kind; }

    /// 읽기전용 접속에서 막은 문장 (걸린 단어 · 함수를 알려 준다)
    public GodDbException(ReadOnlyViolation violation) : base(violation.Message)
    {
        Kind = DbErrorKind.ReadOnlyRejected;
        Violation = violation;
    }

    public static string Describe(DbErrorKind kind) => kind switch
    {
        DbErrorKind.ReadOnlyBlocked or DbErrorKind.ReadOnlyRejected => Tr("읽기전용 접속이라 이 쿼리는 실행할 수 없어요. (SELECT / SHOW / DESC / EXPLAIN 만 허용)"),
        DbErrorKind.NotConnected => Tr("DB 연결이 끊어졌어요. 다시 연결해 주세요."),
        DbErrorKind.TransactionOpen => Tr("커밋하지 않은 트랜잭션이 있어요. 먼저 커밋하거나 롤백해 주세요."),
        _ => Tr("쿼리를 취소했어요."),
    };
}
