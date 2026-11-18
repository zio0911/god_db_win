using static GodDB.Core.L10n;

namespace GodDB.Core.Drivers;

public enum DbErrorKind { ReadOnlyBlocked, NotConnected, TransactionOpen, Cancelled }

/// 앱이 직접 내는 DB 오류 (드라이버 오류는 각 드라이버의 예외 그대로)
public sealed class GodDbException(DbErrorKind kind) : Exception(Describe(kind))
{
    public DbErrorKind Kind { get; } = kind;

    public static string Describe(DbErrorKind kind) => kind switch
    {
        DbErrorKind.ReadOnlyBlocked => Tr("읽기전용 접속이라 이 쿼리는 실행할 수 없어요. (SELECT / SHOW / DESC / EXPLAIN 만 허용)"),
        DbErrorKind.NotConnected => Tr("DB 연결이 끊어졌어요. 다시 연결해 주세요."),
        DbErrorKind.TransactionOpen => Tr("커밋하지 않은 트랜잭션이 있어요. 먼저 커밋하거나 롤백해 주세요."),
        _ => Tr("쿼리를 취소했어요."),
    };
}
