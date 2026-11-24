using GodDB.Core.Results;

namespace GodDB.Core.Modeling;

/// 테이블 명세서 머리 정보. Date: yyyy-MM-dd
public sealed record TableSpecInfo(string SystemName, string Author, string Date);

/// 모델을 테이블 명세서(엑셀)로 (Mac TableSpecExport.swift): 목차 시트 + 테이블마다 시트 (컬럼 · 인덱스 · 관계).
/// 명세서 내용은 한국어 그대로 쓴다 (화면 언어와 상관없이).
public static class TableSpecExport
{
    public const string TocSheetName = "목차";

    public sealed record ColumnRow(int No, string Physical, string Logical, string Type, string Length, string Key,
                                   string NotNull, string DefaultValue, string Comment, string References, string Note)
    {
        /// 설명 칸: 논리명과 코멘트 (같거나 하나만 있으면 하나만)
        public string Description => Describe(Logical, Comment);
    }

    public sealed record IndexRow(int No, string Name, string Kind, string Columns);

    public sealed record RelationRow(int No, string Name, string Mapping, string Relation, string OnDelete, string OnUpdate);

    // MARK: 행 만들기

    /// varchar(50) → (varchar, 50), decimal(10,2) → (decimal, 10,2), 괄호 없으면 길이는 빈 칸
    public static (string Type, string Length) SplitType(string dataType)
    {
        var t = dataType.Trim();
        int open = t.IndexOf('('), close = t.LastIndexOf(')');
        if (open < 0 || close < 0 || open >= close) return (t, "");
        var @base = t[..open].Trim();
        var inner = t[(open + 1)..close].Trim();
        // 괄호 뒤 꾸밈말 (예: int(10) unsigned) 은 타입 쪽에 붙인다
        var tail = t[(close + 1)..].Trim();
        return (tail.Length == 0 ? @base : $"{@base} {tail}", inner);
    }

    public static List<ColumnRow> ColumnRows(DataModel model, ModelTable table)
    {
        var childRelations = model.Relations.Where(r => r.Child == table.Id).ToList();
        return table.Columns.Select((c, i) =>
        {
            var refs = new List<string>();
            var isFk = false;
            foreach (var r in childRelations)
            {
                foreach (var p in r.Pairs.Where(p => p.Child == c.Id))
                {
                    isFk = true;
                    if (model.Table(r.Parent) is { } parent && parent.Columns.FirstOrDefault(x => x.Id == p.Parent) is { } pc)
                    {
                        var reference = $"{parent.PhysicalName}.{pc.PhysicalName}";
                        if (!refs.Contains(reference)) refs.Add(reference);
                    }
                }
            }
            var key = string.Join(", ", new[] { c.PrimaryKey ? "PK" : null, isFk ? "FK" : null }.OfType<string>());
            var defaults = new[] { c.DefaultValue.Length == 0 ? null : c.DefaultValue, c.AutoIncrement ? "AUTO_INCREMENT" : null };
            var (type, length) = SplitType(c.DataType);
            return new ColumnRow(i + 1, c.PhysicalName, c.LogicalName, type, length, key,
                                 c.NotNull || c.PrimaryKey ? "Y" : "",
                                 string.Join(", ", defaults.OfType<string>()),
                                 c.Comment, string.Join(", ", refs), c.Unique ? "UQ" : "");
        }).ToList();
    }

    /// 기본 키(있으면 맨 앞) + 유니크 컬럼 + 인덱스 — SQL 추출과 같은 이름
    public static List<IndexRow> IndexRows(DataModel model, ModelTable table) =>
        model.ToTableMeta(table).Indexes.Select((idx, i) =>
            new IndexRow(i + 1, idx.Name, idx.IsPrimary ? "PK" : idx.Unique ? "유니크" : "일반", string.Join(", ", idx.Columns))).ToList();

    /// 이 테이블이 자식인 관계 (외래 키)
    public static List<RelationRow> RelationRows(DataModel model, ModelTable table) =>
        model.Relations.Where(r => r.Child == table.Id).Select((r, i) =>
        {
            var parent = model.Table(r.Parent);
            var pairs = r.Pairs.Select(p =>
            {
                var cc = table.Columns.FirstOrDefault(c => c.Id == p.Child)?.PhysicalName ?? "?";
                var pc = parent?.Columns.FirstOrDefault(c => c.Id == p.Parent)?.PhysicalName ?? "?";
                return $"{cc} → {parent?.PhysicalName ?? "?"}.{pc}";
            });
            var name = r.Name.Length == 0 ? $"fk_{table.PhysicalName}_{parent?.PhysicalName ?? ""}" : r.Name;
            return new RelationRow(i + 1, name, string.Join(", ", pairs),
                                   r.Cardinality.Title() + (r.Identifying ? " · 식별" : " · 비식별"),
                                   r.OnDelete.RawValue(), r.OnUpdate.RawValue());
        }).ToList();

    // MARK: 시트 이름

    /// Excel 시트 이름 규칙: 31자, []:*?/\ 불가, 앞뒤 작은따옴표 불가, 대소문자 무시하고 겹치지 않게 (~2, ~3 …)
    public static List<string> SheetNames(IEnumerable<string> names, IEnumerable<string>? reserved = null)
    {
        var used = (reserved ?? [TocSheetName]).Select(x => x.ToLowerInvariant()).Append("history").ToHashSet();
        return names.Select(raw =>
        {
            var @base = new string(raw.Select(ch => "[]:*?/\\".Contains(ch) ? '_' : ch).ToArray()).Trim();
            @base = @base.TrimStart('\'').TrimEnd('\'');
            if (@base.Length == 0) @base = "Table";
            var name = Prefix(@base, 31);
            var n = 2;
            while (used.Contains(name.ToLowerInvariant()))
            {
                var suffix = $"~{n}";
                name = Prefix(@base, 31 - suffix.Length) + suffix;
                n++;
            }
            used.Add(name.ToLowerInvariant());
            return name;
        }).ToList();
    }

    private static string Prefix(string s, int n) => s.Length <= n ? s : s[..n];

    // MARK: 통합 문서

    private const string Border = "FFD3DCE6";
    private const string AccentFill = "FFE6F1FB";
    private const string AccentText = "FF0C447C";
    private const string LinkText = "FF0563C1";

    private static readonly XlsxStyle Title = new(Bold: true, Size: 14, Color: AccentText);
    private static readonly XlsxStyle Section = new(Bold: true, Size: 11, Color: AccentText);
    private static readonly XlsxStyle Label = new(Bold: true, Color: AccentText, Fill: AccentFill, Border: Border);
    private static readonly XlsxStyle Header = new(Bold: true, Color: AccentText, Fill: AccentFill, Border: Border, Align: XlsxAlign.Center);
    private static readonly XlsxStyle Cell = new(Border: Border);
    private static readonly XlsxStyle Center = new(Border: Border, Align: XlsxAlign.Center);
    private static readonly XlsxStyle Wrap = new(Border: Border, Wrap: true);
    private static readonly XlsxStyle Link = new(Color: LinkText, Underline: true, Border: Border);
    private static readonly XlsxStyle BackLink = new(Color: LinkText, Underline: true, Align: XlsxAlign.Right);

    public static XlsxWorkbook Workbook(DataModel model, TableSpecInfo info)
    {
        var names = SheetNames(model.Tables.Select(t => t.PhysicalName.Length == 0 ? t.LogicalName : t.PhysicalName));
        var sheets = new List<XlsxSheet> { TocSheet(model, info, names) };
        for (var i = 0; i < model.Tables.Count; i++) sheets.Add(TableSheet(model, model.Tables[i], names[i]));
        return new XlsxWorkbook(sheets, fontName: "맑은 고딕", fontCharset: 129);
    }

    public static byte[] Data(DataModel model, TableSpecInfo info) => Workbook(model, info).Data();

    private static XlsxSheet TocSheet(DataModel model, TableSpecInfo info, List<string> names)
    {
        var s = new XlsxSheet(TocSheetName) { ColumnWidths = [6, 26, 24, 56, 8] };
        s.Set(1, 1, $"테이블 명세서 — {info.SystemName}", Title);
        s.Merge(1, 1, 5, Title);
        s.SetHeight(1, 26);
        (string, string)[] rows = [("DBMS", model.Kind.Title()), ("DB / 스키마", model.Name), ("작성자", info.Author), ("작성일", info.Date)];
        for (var i = 0; i < rows.Length; i++)
        {
            var r = 3 + i;
            s.Set(r, 1, rows[i].Item1, Label); s.Merge(r, 1, 2, Label);
            s.Set(r, 3, rows[i].Item2, Cell); s.Merge(r, 3, 5, Cell);
        }
        s.Set(8, 1, $"테이블 목록 ({model.Tables.Count}개)", Section);
        s.Merge(8, 1, 5, Section);
        string[] heads = ["no", "테이블", "테이블명", "설명", "컬럼"];
        for (var c = 0; c < heads.Length; c++) s.Set(9, c + 1, heads[c], Header);
        s.FreezeRows = 9;
        for (var i = 0; i < model.Tables.Count; i++)
        {
            var t = model.Tables[i];
            var name = names[i];
            var r = 10 + i;
            var shown = t.PhysicalName.Length == 0 ? name : t.PhysicalName;
            s.Set(r, 1, XlsxValue.Of(i + 1), Center);
            s.Set(r, 2, shown, Link);
            s.AddLink(r, 2, $"{XlsxSheet.Quoted(name)}!A1", shown);
            s.Set(r, 3, t.LogicalName, Cell);
            s.Set(r, 4, t.Comment, Wrap);
            s.Set(r, 5, XlsxValue.Of(t.Columns.Count), Center);
        }
        if (model.Tables.Count == 0) EmptyRow(s, 10, [(1, 5)]);
        return s;
    }

    /// 논리명 + 코멘트 → 설명 칸 ("회원번호 — 주문한 회원")
    public static string Describe(string logical, string comment)
    {
        var l = logical.Trim();
        var c = comment.Trim();
        if (l.Length == 0) return c;
        if (c.Length == 0 || c == l) return l;
        return $"{l} — {c}";
    }

    private static XlsxSheet TableSheet(DataModel model, ModelTable t, string name)
    {
        // no, 컬럼, 타입, 길이, 키, NN, 기본값, 설명, 참조, 비고 (논리명은 설명 칸에)
        var s = new XlsxSheet(name) { ColumnWidths = [5, 22, 12, 8, 8, 5, 18, 40, 24, 8] };
        var heading = t.PhysicalName.Length == 0 ? name : t.PhysicalName;
        s.Set(1, 1, t.LogicalName.Length == 0 ? heading : $"{heading} · {t.LogicalName}", Title);
        s.Merge(1, 1, 8, Title);
        s.Set(1, 9, "← 목차", BackLink);
        s.Merge(1, 9, 10, BackLink);
        s.AddLink(1, 9, $"{XlsxSheet.Quoted(TocSheetName)}!A1", "← 목차");
        s.SetHeight(1, 26);
        (string, string)[] info = [("설명", t.Comment), ("DBMS · 스키마", $"{model.Kind.Title()} · {model.Name}")];
        for (var i = 0; i < info.Length; i++)
        {
            var row = 2 + i;
            s.Set(row, 1, info[i].Item1, Label); s.Merge(row, 1, 2, Label);
            s.Set(row, 3, info[i].Item2, Wrap); s.Merge(row, 3, 10, Wrap);
        }

        // 컬럼
        var r = 5;
        s.Set(r, 1, "컬럼", Section);
        r++;
        string[] heads = ["no", "컬럼", "타입", "길이", "키", "NN", "기본값", "설명", "참조", "비고"];
        for (var c = 0; c < heads.Length; c++) s.Set(r, c + 1, heads[c], Header);
        s.FreezeRows = r;
        r++;
        var cols = ColumnRows(model, t);
        foreach (var c in cols)
        {
            s.Set(r, 1, XlsxValue.Of(c.No), Center);
            s.Set(r, 2, c.Physical, Cell);
            s.Set(r, 3, c.Type, Cell);
            s.Set(r, 4, c.Length, Center);
            s.Set(r, 5, c.Key, Center);
            s.Set(r, 6, c.NotNull, Center);
            s.Set(r, 7, c.DefaultValue, Cell);
            s.Set(r, 8, c.Description, Wrap);
            s.Set(r, 9, c.References, Cell);
            s.Set(r, 10, c.Note, Center);
            r++;
        }
        if (cols.Count == 0) { EmptyRow(s, r, [(1, 10)]); r++; }

        // 인덱스: no | 인덱스 (B:C) | 종류 (D:E) | 컬럼 (F:J)
        r++;
        s.Set(r, 1, "인덱스", Section);
        r++;
        (int, int)[] idxSpans = [(1, 1), (2, 3), (4, 5), (6, 10)];
        HeaderRow(s, r, ["no", "인덱스", "종류", "컬럼"], idxSpans);
        r++;
        var idx = IndexRows(model, t);
        foreach (var i in idx)
        {
            SpanRow(s, r, [XlsxValue.Of(i.No), XlsxValue.Of(i.Name), XlsxValue.Of(i.Kind), XlsxValue.Of(i.Columns)], idxSpans,
                    [Center, Cell, Center, Cell]);
            r++;
        }
        if (idx.Count == 0) { EmptyRow(s, r, [(1, 10)]); r++; }

        // 관계: no | 이름 (B:C) | 컬럼 → 참조 (D:G) | 관계 (H) | ON DELETE (I) | ON UPDATE (J)
        r++;
        s.Set(r, 1, "관계 (외래 키)", Section);
        s.Merge(r, 1, 3, Section);
        r++;
        (int, int)[] relSpans = [(1, 1), (2, 3), (4, 7), (8, 8), (9, 9), (10, 10)];
        HeaderRow(s, r, ["no", "이름", "컬럼 → 참조", "관계", "ON DELETE", "ON UPDATE"], relSpans);
        r++;
        var rels = RelationRows(model, t);
        foreach (var x in rels)
        {
            SpanRow(s, r, [XlsxValue.Of(x.No), XlsxValue.Of(x.Name), XlsxValue.Of(x.Mapping), XlsxValue.Of(x.Relation),
                           XlsxValue.Of(x.OnDelete), XlsxValue.Of(x.OnUpdate)],
                    relSpans, [Center, Cell, Cell, Center, Center, Center]);
            r++;
        }
        if (rels.Count == 0) EmptyRow(s, r, [(1, 10)]);
        return s;
    }

    private static void HeaderRow(XlsxSheet s, int r, string[] titles, (int, int)[] spans) =>
        SpanRow(s, r, titles.Select(t => XlsxValue.Of(t)).ToArray(), spans, Enumerable.Repeat(Header, titles.Length).ToArray());

    private static void SpanRow(XlsxSheet s, int r, XlsxValue[] values, (int From, int To)[] spans, XlsxStyle[] styles)
    {
        for (var i = 0; i < spans.Length; i++)
        {
            s.Set(r, spans[i].From, values[i], styles[i]);
            s.Merge(r, spans[i].From, spans[i].To, styles[i]);
        }
    }

    private static void EmptyRow(XlsxSheet s, int r, (int From, int To)[] spans)
    {
        foreach (var (from, to) in spans)
        {
            s.Set(r, from, "없음", Center);
            s.Merge(r, from, to, Center);
        }
    }
}
