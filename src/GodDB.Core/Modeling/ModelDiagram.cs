using GodDB.Core.Schema;
using static GodDB.Core.L10n;

namespace GodDB.Core.Modeling;

// Mac: Sources/GodDB/ModelingView.swift · ModelingEditors.swift 의 화면과 무관한 계산 (도구, 카드 크기, 관계 선, 자동 정렬, 이름 표시)

/// 다이어그램 도구 (Mac ModelTool)
public abstract record ModelTool
{
    public sealed record Select : ModelTool;
    public sealed record AddTable : ModelTool;
    public sealed record Relation(ModelCardinality Cardinality, bool Identifying) : ModelTool;

    public static ModelTool SelectTool { get; } = new Select();
    public static ModelTool AddTableTool { get; } = new AddTable();

    /// 도구 막대 버튼: 같은 도구를 다시 누르면 선택 도구로
    public static ModelTool Toggle(ModelTool current, ModelTool pressed) =>
        current == pressed && pressed is not Select ? SelectTool : pressed;
}

/// 다이어그램에서 고른 것 (Mac ModelSelection)
public abstract record ModelSelection
{
    public sealed record Table(Guid Id) : ModelSelection;
    public sealed record Relation(Guid Id) : ModelSelection;
}

/// 다이어그램 이름 표시 (Mac DiagramNameMode 중 모델링에서 쓰는 것; 설정 키 "GodDB.model.nameMode", 기본 논리명)
public enum ModelNameMode { Logical, Physical, Both }

/// 관계 선 (3차 베지어): Start = 자식 쪽, End = 부모 쪽. Out = 자식 쪽 기호가 뻗는 방향(±1), Inward = 부모 카드 안쪽 방향(±1)
public readonly record struct ModelRelationGeometry(
    (double X, double Y) Start, (double X, double Y) C1, (double X, double Y) C2, (double X, double Y) End, double Out, double Inward);

public static class ModelDiagram
{
    public const double CardWidth = 260;
    public const double HeaderHeight = 34;
    public const double RowHeight = 20;
    public const double MinZoom = 0.3, MaxZoom = 2, ZoomStep = 0.1;

    /// 카드 머리 색 이름 (파일에 저장되는 값). blue 는 앱 강조색
    public static IReadOnlyList<string> CardColors { get; } = ["blue", "purple", "green", "orange", "red", "teal", "gray"];

    public static double CardHeight(ModelTable t) => HeaderHeight + Math.Max(t.Columns.Count, 1) * RowHeight + 10;

    /// 캔버스 크기 (여유 공간 포함)
    public static (double Width, double Height) Size(DataModel m)
    {
        double maxX = m.Tables.Count > 0 ? m.Tables.Max(t => t.X + CardWidth) : 600;
        double maxY = m.Tables.Count > 0 ? m.Tables.Max(t => t.Y + CardHeight(t)) : 400;
        return (Math.Max(1200, maxX + 400), Math.Max(800, maxY + 300));
    }

    public static DiagramRect Frame(ModelTable t) => new(t.X, t.Y, CardWidth, CardHeight(t));

    /// 빈 곳을 눌러 테이블을 추가할 때 카드 왼쪽 위 (누른 곳이 카드 머리 가운데)
    public static (double X, double Y) AddTablePosition(double x, double y) => (Math.Max(0, x - CardWidth / 2), Math.Max(0, y - 20));

    /// 카드를 끌 때 새 위치 (정수로 반올림, 음수 불가)
    public static (double X, double Y) DragPosition((double X, double Y) start, double dx, double dy) =>
        (Math.Max(0, Math.Round(start.X + dx, MidpointRounding.AwayFromZero)), Math.Max(0, Math.Round(start.Y + dy, MidpointRounding.AwayFromZero)));

    public static double RowY(ModelTable t, Guid? column)
    {
        int i = column is { } c ? t.Columns.FindIndex(x => x.Id == c) : -1;
        if (i < 0) i = 0;
        return t.Y + HeaderHeight + i * RowHeight + RowHeight / 2 + 5;
    }

    public static ModelRelationGeometry? Geometry(DataModel model, ModelRelation r)
    {
        if (model.Table(r.Child) is not { } child || model.Table(r.Parent) is not { } parent) return null;
        var cf = Frame(child);
        var pf = Frame(parent);
        double cy = RowY(child, r.Pairs.FirstOrDefault()?.Child);
        double py = RowY(parent, r.Pairs.FirstOrDefault()?.Parent);
        if (r.Child == r.Parent)
        {
            (double X, double Y) s = (cf.MaxX, cy), e = (cf.MaxX, py == cy ? cy + 30 : py);
            return new(s, (s.X + 70, s.Y), (e.X + 70, e.Y), e, 1, 1);
        }
        bool childLeft = cf.X + cf.Width / 2 > pf.X + pf.Width / 2;
        (double X, double Y) st = (childLeft ? cf.MinX : cf.MaxX, cy);
        (double X, double Y) en = (childLeft ? pf.MaxX : pf.MinX, py);
        double bend = Math.Max(50, Math.Abs(en.X - st.X) * 0.45);
        return new(st, (st.X + (childLeft ? -bend : bend), st.Y), (en.X + (childLeft ? bend : -bend), en.Y), en,
                   childLeft ? -1 : 1, childLeft ? 1 : -1);
    }

    /// 관계 표시(1:N 등)를 놓을 곡선 가운데 (t = 0.5)
    public static (double X, double Y)? Midpoint(DataModel model, ModelRelation r)
    {
        if (Geometry(model, r) is not { } g) return null;
        static double B(double a, double b, double c, double d) => 0.125 * a + 0.375 * b + 0.375 * c + 0.125 * d;
        return (B(g.Start.X, g.C1.X, g.C2.X, g.End.X), B(g.Start.Y, g.C1.Y, g.C2.Y, g.End.Y));
    }

    /// 관계 표시 글자: 식별이면 "● " + 카디널리티
    public static string RelationBadge(ModelRelation r) => (r.Identifying ? "● " : "") + r.Cardinality.Title();

    /// 관계 표시에 마우스를 올렸을 때 설명
    public static string RelationHelp(DataModel model, ModelRelation r)
    {
        var p = model.Table(r.Parent)?.DisplayName ?? "?";
        var c = model.Table(r.Child)?.DisplayName ?? "?";
        return $"{p} → {c} · {r.Cardinality.Title()}" + (r.Identifying ? Tr(" · 식별") : Tr(" · 비식별"))
               + (r.CreateConstraint ? "" : Tr(" · FK 제약 없음"));
    }

    /// 편집기 관계 목록의 한 줄 꼬리 ("1:N · 식별")
    public static string RelationKindText(ModelRelation r) => r.Cardinality.Title() + (r.Identifying ? Tr(" · 식별") : Tr(" · 비식별"));

    /// 마우스를 올린 테이블과 관계로 이어진 카드인지 (강조 표시)
    public static bool IsHighlighted(DataModel model, Guid tableId, Guid? hovered) =>
        hovered is { } h && (h == tableId || model.Relations.Any(r =>
            (r.Parent == h && r.Child == tableId) || (r.Child == h && r.Parent == tableId)));

    /// 관계 선을 진하게 그릴지 (마우스가 없거나, 이어진 테이블 위거나, 선택된 관계)
    public static bool IsRelationActive(ModelRelation r, Guid? hovered, ModelSelection? selection) =>
        hovered == null || hovered == r.Child || hovered == r.Parent || selection == new ModelSelection.Relation(r.Id);

    /// 카드의 FK 컬럼 (자식 쪽 관계 연결)
    public static HashSet<Guid> ForeignKeyColumns(DataModel model, Guid tableId) =>
        [.. model.Relations.Where(r => r.Child == tableId).SelectMany(r => r.Pairs.Select(p => p.Child))];

    /// 카드에 보일 이름: (주 이름, 둘째 줄, 물리명/논리명 빠짐 표시)
    public static (string Main, string? Sub, bool Missing) Label(string logical, string physical, ModelNameMode mode) => mode switch
    {
        ModelNameMode.Logical => (logical.Length == 0 ? physical : logical, null, logical.Length == 0),
        ModelNameMode.Physical => (physical.Length == 0 ? logical : physical, null, physical.Length == 0),
        _ => (logical.Length == 0 ? physical : logical, physical.Length == 0 ? Tr("물리명 없음") : physical, physical.Length == 0),
    };

    /// 도구 사용 안내 (캔버스 위 알림)
    public static string? ToolHint(DataModel model, ModelTool tool, Guid? pendingParent)
    {
        switch (tool)
        {
            case ModelTool.AddTable:
                return Tr("빈 곳을 누르면 테이블이 생겨요. (Esc: 선택 도구)");
            case ModelTool.Relation rel:
                var kind = (rel.Identifying ? Tr("식별 ") : "") + rel.Cardinality.Title();
                if (pendingParent is { } p && model.Table(p) is { } t)
                    return Tr("%@ 관계 · 부모 %@ → 이제 자식(FK 를 받을) 테이블을 누르세요", kind, t.DisplayName);
                return Tr("%@ 관계 · 먼저 부모(참조되는) 테이블을 누르세요", kind);
            default:
                return null;
        }
    }

    /// 자동 정렬: 관계에 따라 부모는 왼쪽 열, 자식은 오른쪽 열에 겹치지 않게 (Mac 1.4.0 ModelLayout). 호출 전에 Checkpoint.
    /// 카드 높이는 화면과 같은 계산 (컬럼이 많은 카드도 겹치지 않게)
    public static void AutoLayout(DataModel model)
    {
        var frames = ModelLayout.Frames(model, CardHeight, new ModelLayout.Options(CardWidth: CardWidth));
        foreach (var t in model.Tables)
        {
            if (frames.TryGetValue(t.Id, out var f))
            {
                t.X = f.MinX;
                t.Y = f.MinY;
            }
        }
    }

    /// 모델 검사 버튼 표시: 오류 수 → 문제 수 → OK
    public static string IssueBadge(IReadOnlyList<ModelIssue> issues)
    {
        int errors = issues.Count(i => i.Level == ModelIssueLevel.Error);
        return errors > 0 ? $"{errors}" : (issues.Count == 0 ? "OK" : $"{issues.Count}");
    }
}
