using static GodDB.Core.L10n;

namespace GodDB.Core.Schema;

public enum RoutineKind { View, Procedure, Function, Trigger }

public static class RoutineKindExtensions
{
    /// SQL 에 쓰는 이름 (VIEW / PROCEDURE / FUNCTION / TRIGGER)
    public static string RawValue(this RoutineKind k) => k switch
    {
        RoutineKind.View => "VIEW",
        RoutineKind.Procedure => "PROCEDURE",
        RoutineKind.Function => "FUNCTION",
        _ => "TRIGGER",
    };

    public static RoutineKind? FromRawValue(string raw) => raw switch
    {
        "VIEW" => RoutineKind.View,
        "PROCEDURE" => RoutineKind.Procedure,
        "FUNCTION" => RoutineKind.Function,
        "TRIGGER" => RoutineKind.Trigger,
        _ => null,
    };

    public static string Title(this RoutineKind k) => k switch
    {
        RoutineKind.View => Tr("뷰"),
        RoutineKind.Procedure => Tr("프로시저"),
        RoutineKind.Function => Tr("함수"),
        _ => Tr("트리거"),
    };
}

/// 뷰 / 프로시저 / 함수 / 트리거
public sealed record RoutineMeta(RoutineKind Kind, string Name, string Comment = "")
{
    public string Id => $"{Kind.RawValue()}:{Name}";
}
