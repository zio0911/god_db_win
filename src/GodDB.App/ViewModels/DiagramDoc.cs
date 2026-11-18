using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GodDB.App.ViewModels;

// Mac DiagramView.swift 의 DiagramDoc · DiagramNameMode · logicalName 과 DiagramView 의 화면 상태(배치 · 배율 · 강조)

/// 다이어그램 창 하나의 내용 (열 때의 스키마 스냅샷)
public sealed record DiagramDoc
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Title { get; init; }
    public required IReadOnlyList<TableMeta> Tables { get; init; }
    /// 중심 테이블 (테이블에서 열었을 때)
    public string? Focus { get; init; }
    /// 보라색으로 강조할 테이블 (사이드바에서 고른 테이블 / 중심 테이블)
    public IReadOnlySet<string> Highlighted { get; init; } = new HashSet<string>();
    /// 컬럼 이름으로 추정한 관계 (외래 키 제약이 없는 DB 용)
    public IReadOnlyList<ForeignKeyMeta> Inferred { get; init; } = [];
    /// DB 종류 (AI 분석 SQL 문법용)
    public DatabaseKind Kind { get; init; } = DatabaseKind.Mysql;
    /// AI 대화 기록을 구분하는 값 (접속 · DB · 테이블)
    public string ChatScope { get; init; } = "";
}

/// 다이어그램에 보일 이름
public enum DiagramNameMode { Physical, Logical, Both }

public static class DiagramNames
{
    public const string PrefKey = "GodDB.diagram.nameMode";

    public static string Raw(this DiagramNameMode m) => m switch
    {
        DiagramNameMode.Logical => "logical",
        DiagramNameMode.Both => "both",
        _ => "physical",
    };

    public static DiagramNameMode Parse(string? raw) => raw switch
    {
        "logical" => DiagramNameMode.Logical,
        "both" => DiagramNameMode.Both,
        _ => DiagramNameMode.Physical,
    };

    public static string Title(this DiagramNameMode m) => m switch
    {
        DiagramNameMode.Logical => Tr("논리명"),
        DiagramNameMode.Both => Tr("둘 다"),
        _ => Tr("물리명"),
    };

    /// 저장된 선택 (Mac @AppStorage("GodDB.diagram.nameMode"))
    public static DiagramNameMode Saved
    {
        get => Parse(Prefs.Get<string?>(PrefKey, null));
        set => Prefs.Set(PrefKey, value.Raw());
    }

    /// 코멘트에서 논리명 (첫 줄, 너무 길면 자름)
    public static string? LogicalName(string comment)
    {
        var first = comment.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        if (first.Length == 0) return null;
        return first.Length > 40 ? first[..40] + "…" : first;
    }

    /// 모드에 맞는 이름 (논리명이 없으면 물리명). missing 이면 흐리게(기울여) 보인다.
    public static (string Main, string? Sub, bool Missing) Label(DiagramNameMode mode, string physical, string comment)
    {
        var logical = LogicalName(comment);
        return mode switch
        {
            DiagramNameMode.Logical => (logical ?? physical, null, logical == null),
            DiagramNameMode.Both => (logical ?? physical, logical == null ? null : physical, logical == null),
            _ => (physical, null, false),
        };
    }
}

/// 다이어그램 창의 상태: 카드 위치, 배율, 이름 표시, 추정 관계, 마우스를 올린 테이블
public sealed partial class DiagramViewModel : ObservableObject
{
    public const double Margin = 40;
    public const double MinZoom = 0.3;
    public const double MaxZoom = 2;

    public DiagramDoc Doc { get; }
    /// 카드 위치 (여백 없는 좌표)
    public Dictionary<string, DiagramRect> Frames { get; private set; } = [];

    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private bool _showInferred = true;
    [ObservableProperty] private DiagramNameMode _nameMode;
    [ObservableProperty] private string? _hovered;

    /// 배치가 바뀌었다 (카드 끌기 · 자동 정렬)
    public event Action? LayoutChanged;

    /// 자동 정렬 직전 (화면이 지금 자리를 기억해 두고 새 자리로 움직이게)
    public event Action? Relayouting;

    public DiagramViewModel(DiagramDoc doc, DiagramNameMode? nameMode = null)
    {
        Doc = doc;
        _names = doc.Tables.Select(t => t.Name).ToHashSet();
        _nameMode = nameMode ?? DiagramNames.Saved;
        // 처음 그릴 때(그리고 PNG 저장 때)부터 위치가 있어야 한다
        Frames = DiagramLayout.Frames(doc.Tables, doc.Inferred, cardWidth: CardWidth);
        RealRelations = doc.Tables.SelectMany(t => t.ForeignKeys).Where(f => _names.Contains(f.ReferencedTable)).ToList();
    }

    private readonly HashSet<string> _names;

    public double CardWidth => NameMode == DiagramNameMode.Both ? 320 : DiagramLayout.CardWidth;

    /// 진짜 외래 키 (그림 안의 테이블끼리)
    public IReadOnlyList<ForeignKeyMeta> RealRelations { get; }

    /// 그릴 관계 (추정 포함 여부에 따라)
    public IReadOnlyList<ForeignKeyMeta> Relations => ShowInferred ? [.. RealRelations, .. Doc.Inferred] : RealRelations;

    public string CountText => Tr("테이블 %@개 · 관계 %@개", Doc.Tables.Count, RealRelations.Count);
    public string InferredText => Tr("관계 추정 %@개", Doc.Inferred.Count);
    public string ZoomText => $"{(int)Math.Round(Zoom * 100)}%";

    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));

    partial void OnNameModeChanged(DiagramNameMode value)
    {
        DiagramNames.Saved = value;
        Relayout();
    }

    partial void OnShowInferredChanged(bool value) => Relayout();

    public void Relayout()
    {
        Relayouting?.Invoke();
        Frames = DiagramLayout.Frames(Doc.Tables, ShowInferred ? Doc.Inferred : [], cardWidth: CardWidth);
        LayoutChanged?.Invoke();
    }

    public Size ContentSize
    {
        get
        {
            var maxX = Frames.Count > 0 ? Frames.Values.Max(f => f.MaxX) : 600;
            var maxY = Frames.Count > 0 ? Frames.Values.Max(f => f.MaxY) : 400;
            return new Size(maxX + Margin * 2, maxY + Margin * 2);
        }
    }

    /// 화면 좌표 (여백 포함)
    public Rect? Frame(string name) =>
        Frames.TryGetValue(name, out var f) ? new Rect(f.X + Margin, f.Y + Margin, f.Width, f.Height) : null;

    /// 카드를 옮긴다 (여백 없는 좌표, 0 아래로는 안 감)
    public void Move(string name, double x, double y)
    {
        if (!Frames.TryGetValue(name, out var f)) return;
        Frames[name] = f with { X = Math.Max(0, x), Y = Math.Max(0, y) };
        LayoutChanged?.Invoke();
    }

    public bool IsRelated(string a, string b) =>
        a == b || Relations.Any(f => (f.Table == a && f.ReferencedTable == b) || (f.Table == b && f.ReferencedTable == a));

    /// 마우스를 올린 테이블과 이어진 카드 (테두리 강조)
    public bool IsHighlighted(string table) => Hovered is { } h && IsRelated(table, h);

    /// 표시용 추정 관계 컬럼 (흐린 🔗)
    public HashSet<string> InferredColumns(string table) => ShowInferred
        ? Doc.Inferred.Where(f => f.Table == table).SelectMany(f => f.Columns).Select(c => c.ToLowerInvariant()).ToHashSet()
        : [];

    /// 컬럼 행의 세로 위치
    public double? RowY(string table, string column)
    {
        var t = Doc.Tables.FirstOrDefault(x => x.Name == table);
        if (t == null || Frame(table) is not { } f) return null;
        var index = t.Columns.FindIndex(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 0;
        var shown = Math.Min(index, DiagramLayout.MaxRows - 1);
        return f.Y + DiagramLayout.HeaderHeight + shown * DiagramLayout.RowHeight + DiagramLayout.RowHeight / 2 + 4;
    }

    /// 모든 테이블이 창 안에 들어오게 배율 조정 (최대 100%, 최소 30%)
    public void Fit(Size viewport)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0) return;
        var c = ContentSize;
        var z = Math.Min(viewport.Width / c.Width, viewport.Height / c.Height);
        Zoom = Math.Max(MinZoom, Math.Min(1, Math.Floor(z * 100) / 100));
    }

    public void ZoomIn() => Zoom = Math.Min(MaxZoom, Math.Round((Zoom + 0.1) * 100) / 100);
    public void ZoomOut() => Zoom = Math.Max(MinZoom, Math.Round((Zoom - 0.1) * 100) / 100);
}
