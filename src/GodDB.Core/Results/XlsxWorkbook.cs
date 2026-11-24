using System.Globalization;
using System.Text;

namespace GodDB.Core.Results;

// Mac XLSXWorkbook.swift — 작은 여러 시트 Excel(.xlsx) 작성기: 글꼴·채우기·테두리 스타일, 병합, 열 너비, 틀 고정, 문서 안 링크, 인쇄 설정.
// 메모리에서 만들어 byte[] 로 돌려준다 (압축 없는 ZIP). 결과 내보내기(ResultExportWriter)는 큰 결과를 스트리밍하는 한 시트 전용이라
// 따로 두고, XML 이스케이프만 같이 쓴다.

public enum XlsxAlign { General, Left, Center, Right }

public sealed record XlsxStyle(
    bool Bold = false,
    double Size = 10,
    /// ARGB 16진 (예: FF0C447C). null 이면 기본 글자색
    string? Color = null,
    bool Underline = false,
    /// 단색 채우기 ARGB. null 이면 채우기 없음
    string? Fill = null,
    /// 얇은 테두리 색 ARGB. null 이면 테두리 없음
    string? Border = null,
    XlsxAlign Align = XlsxAlign.General,
    bool Wrap = false);

public abstract record XlsxValue
{
    public sealed record Text(string Value) : XlsxValue;
    public sealed record Number(int Value) : XlsxValue;
    public sealed record Empty : XlsxValue;

    public static readonly XlsxValue None = new Empty();
    public static XlsxValue Of(string text) => text.Length == 0 ? None : new Text(text);
    public static XlsxValue Of(int n) => new Number(n);
}

public sealed class XlsxSheet(string name)
{
    public sealed record Cell(XlsxValue Value, XlsxStyle Style);
    /// Location: 문서 안 위치 (예: 'tb_member'!A1)
    public sealed record Link(string Ref, string Location, string Display);

    public string Name { get; set; } = name;
    /// 열 너비 (문자 수 단위), A 열부터
    public List<double> ColumnWidths { get; set; } = [];
    /// 위에서 몇 행을 고정할지 (0 이면 고정 없음)
    public int FreezeRows { get; set; }
    /// 인쇄: A4 가로, 너비 한 페이지에 맞춤
    public bool PrintLandscapeFitWidth { get; set; } = true;
    public SortedDictionary<int, SortedDictionary<int, Cell>> Cells { get; } = [];
    public Dictionary<int, double> RowHeights { get; } = [];
    public List<string> Merges { get; } = [];
    public List<Link> Links { get; } = [];

    /// row, col 은 1부터
    public void Set(int row, int col, XlsxValue value, XlsxStyle style)
    {
        if (!Cells.TryGetValue(row, out var r)) Cells[row] = r = [];
        r[col] = new Cell(value, style);
    }

    public void Set(int row, int col, string text, XlsxStyle style) => Set(row, col, XlsxValue.Of(text), style);

    /// 같은 행의 from...to 열을 병합. 덮이는 칸도 같은 스타일로 채워 테두리가 이어지게 한다
    public void Merge(int row, int from, int to, XlsxStyle style)
    {
        if (to <= from) return;
        for (var c = from + 1; c <= to; c++) Set(row, c, XlsxValue.None, style);
        Merges.Add($"{Ref(row, from)}:{Ref(row, to)}");
    }

    public void AddLink(int row, int col, string location, string display) => Links.Add(new Link(Ref(row, col), location, display));

    public void SetHeight(int row, double height) => RowHeights[row] = height;

    public int MaxRow => Cells.Count == 0 ? 0 : Cells.Keys.Max();
    public int MaxColumn => Cells.Values.SelectMany(r => r.Keys).DefaultIfEmpty(0).Max();

    /// 1 → A, 27 → AA
    public static string Column(int c)
    {
        var n = c;
        var s = "";
        while (n > 0)
        {
            var r = (n - 1) % 26;
            s = (char)('A' + r) + s;
            n = (n - 1) / 26;
        }
        return s;
    }

    public static string Ref(int row, int col) => $"{Column(col)}{row}";

    /// 시트 이름을 수식·링크에서 쓰는 꼴로: '이름' (작은따옴표는 두 번)
    public static string Quoted(string name) => "'" + name.Replace("'", "''") + "'";
}

public sealed class XlsxWorkbook(IReadOnlyList<XlsxSheet> sheets, string fontName = "Calibri", int? fontCharset = null)
{
    public IReadOnlyList<XlsxSheet> Sheets { get; } = sheets;
    /// 기본 글꼴 이름 (모든 스타일에 쓴다)
    public string FontName { get; } = fontName;
    /// 글꼴 문자 집합 (129 = 한글). null 이면 쓰지 않음
    public int? FontCharset { get; } = fontCharset;

    private static string Num(double d) => d.ToString(CultureInfo.InvariantCulture);

    // MARK: 직렬화

    public byte[] Data()
    {
        var styles = new StyleTable(Sheets);
        var files = new List<(string, string)>();
        const string head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string sheetType = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
        var overrides = string.Concat(Sheets.Select((_, i) =>
            $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"{sheetType}\"/>"));
        files.Add(("[Content_Types].xml", head +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            overrides +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>"));
        files.Add(("_rels/.rels", head +
            $"<Relationships xmlns=\"{pkg}\"><Relationship Id=\"rId1\" Type=\"{rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"));
        var sheetList = string.Concat(Sheets.Select((s, i) => $"<sheet name=\"{Esc(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>"));
        files.Add(("xl/workbook.xml", head +
            $"<workbook xmlns=\"{main}\" xmlns:r=\"{rel}\"><bookViews><workbookView activeTab=\"0\"/></bookViews>" +
            $"<sheets>{sheetList}</sheets></workbook>"));
        var sheetRels = string.Concat(Sheets.Select((_, i) =>
            $"<Relationship Id=\"rId{i + 1}\" Type=\"{rel}/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>"));
        files.Add(("xl/_rels/workbook.xml.rels", head +
            $"<Relationships xmlns=\"{pkg}\">{sheetRels}" +
            $"<Relationship Id=\"rId{Sheets.Count + 1}\" Type=\"{rel}/styles\" Target=\"styles.xml\"/></Relationships>"));
        files.Add(("xl/styles.xml", head + styles.Xml(FontName, FontCharset, main)));
        for (var i = 0; i < Sheets.Count; i++)
            files.Add(($"xl/worksheets/sheet{i + 1}.xml", head + SheetXml(Sheets[i], i, styles, main, rel)));
        return ZipWriter.Stored(files.Select(f => (f.Item1, Encoding.UTF8.GetBytes(f.Item2))).ToList());
    }

    private static string SheetXml(XlsxSheet s, int index, StyleTable styles, string ns, string rel)
    {
        var x = new StringBuilder($"<worksheet xmlns=\"{ns}\" xmlns:r=\"{rel}\">");
        if (s.PrintLandscapeFitWidth) x.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
        int lastRow = Math.Max(1, s.MaxRow), lastCol = Math.Max(1, s.MaxColumn);
        x.Append($"<dimension ref=\"A1:{XlsxSheet.Ref(lastRow, lastCol)}\"/>");
        x.Append($"<sheetViews><sheetView workbookViewId=\"0\"{(index == 0 ? " tabSelected=\"1\"" : "")}>");
        if (s.FreezeRows > 0)
        {
            var top = XlsxSheet.Ref(s.FreezeRows + 1, 1);
            x.Append($"<pane ySplit=\"{s.FreezeRows}\" topLeftCell=\"{top}\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            x.Append($"<selection pane=\"bottomLeft\" activeCell=\"{top}\" sqref=\"{top}\"/>");
        }
        x.Append("</sheetView></sheetViews>");
        x.Append("<sheetFormatPr defaultRowHeight=\"16.5\"/>");
        if (s.ColumnWidths.Count > 0)
            x.Append("<cols>" + string.Concat(s.ColumnWidths.Select((w, i) =>
                $"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{Num(w)}\" customWidth=\"1\"/>")) + "</cols>");
        x.Append("<sheetData>");
        foreach (var (r, row) in s.Cells)
        {
            var height = s.RowHeights.TryGetValue(r, out var h) ? $" ht=\"{Num(h)}\" customHeight=\"1\"" : "";
            x.Append($"<row r=\"{r}\"{height}>");
            foreach (var (c, cell) in row)
            {
                var cref = XlsxSheet.Ref(r, c);
                var sid = styles.Index(cell.Style);
                var sAttr = sid > 0 ? $" s=\"{sid}\"" : "";
                switch (cell.Value)
                {
                    case XlsxValue.Number n: x.Append($"<c r=\"{cref}\"{sAttr}><v>{n.Value}</v></c>"); break;
                    case XlsxValue.Text t:
                        var text = t.Value.Length > 32_767 ? t.Value[..32_767] : t.Value;
                        x.Append($"<c r=\"{cref}\"{sAttr} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>");
                        break;
                    default: x.Append($"<c r=\"{cref}\"{sAttr}/>"); break;
                }
            }
            x.Append("</row>");
        }
        x.Append("</sheetData>");
        if (s.Merges.Count > 0)
            x.Append($"<mergeCells count=\"{s.Merges.Count}\">" + string.Concat(s.Merges.Select(m => $"<mergeCell ref=\"{m}\"/>")) + "</mergeCells>");
        if (s.Links.Count > 0)
            x.Append("<hyperlinks>" + string.Concat(s.Links.Select(l =>
                $"<hyperlink ref=\"{l.Ref}\" location=\"{Esc(l.Location)}\" display=\"{Esc(l.Display)}\"/>")) + "</hyperlinks>");
        x.Append("<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.6\" bottom=\"0.6\" header=\"0.3\" footer=\"0.3\"/>");
        if (s.PrintLandscapeFitWidth)
            x.Append("<pageSetup paperSize=\"9\" orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"0\"/>");
        x.Append("</worksheet>");
        return x.ToString();
    }

    internal static string Esc(string s) => ResultExportWriter.XmlEscape(s);

    /// 시트에 쓰인 스타일을 모아 styles.xml 의 글꼴·채우기·테두리·셀 서식 번호로 바꾼다
    private sealed class StyleTable
    {
        private sealed record Font(bool Bold, double Size, string? Color, bool Underline);

        private readonly List<XlsxStyle> _xfs = [new XlsxStyle()];
        private readonly Dictionary<XlsxStyle, int> _xfIndex = new() { [new XlsxStyle()] = 0 };
        private readonly List<Font> _fonts = [];
        private readonly List<string?> _fills = [null, null];   // 0: none, 1: gray125 (Excel 예약)
        private readonly List<string?> _borders = [null];

        public StyleTable(IEnumerable<XlsxSheet> sheets)
        {
            foreach (var s in sheets)
                foreach (var row in s.Cells.Values)
                    foreach (var cell in row.Values)
                        if (_xfIndex.TryAdd(cell.Style, _xfs.Count)) _xfs.Add(cell.Style);
            foreach (var st in _xfs)
            {
                var f = new Font(st.Bold, st.Size, st.Color, st.Underline);
                if (!_fonts.Contains(f)) _fonts.Add(f);
                if (st.Fill is { } fill && !_fills.Contains(fill)) _fills.Add(fill);
                if (st.Border is { } b && !_borders.Contains(b)) _borders.Add(b);
            }
        }

        public int Index(XlsxStyle s) => _xfIndex.TryGetValue(s, out var i) ? i : 0;

        public string Xml(string fontName, int? charset, string ns)
        {
            var name = Esc(fontName);
            var fontXml = string.Concat(_fonts.Select(f =>
            {
                var x = "<font>";
                if (f.Bold) x += "<b/>";
                if (f.Underline) x += "<u/>";
                x += $"<sz val=\"{Num(f.Size)}\"/>";
                if (f.Color is { } c) x += $"<color rgb=\"{c}\"/>";
                x += $"<name val=\"{name}\"/><family val=\"3\"/>";
                if (charset is { } cs) x += $"<charset val=\"{cs}\"/>";
                return x + "</font>";
            }));
            var fillXml = string.Concat(_fills.Select((f, i) => i switch
            {
                0 => "<fill><patternFill patternType=\"none\"/></fill>",
                1 => "<fill><patternFill patternType=\"gray125\"/></fill>",
                _ => $"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"{f}\"/><bgColor indexed=\"64\"/></patternFill></fill>",
            }));
            var borderXml = string.Concat(_borders.Select(b =>
            {
                if (b == null) return "<border><left/><right/><top/><bottom/><diagonal/></border>";
                string Side(string n) => $"<{n} style=\"thin\"><color rgb=\"{b}\"/></{n}>";
                return "<border>" + Side("left") + Side("right") + Side("top") + Side("bottom") + "<diagonal/></border>";
            }));
            var xfXml = string.Concat(_xfs.Select(s =>
            {
                var fontId = Math.Max(0, _fonts.IndexOf(new Font(s.Bold, s.Size, s.Color, s.Underline)));
                var fillId = s.Fill is { } f ? Math.Max(0, _fills.IndexOf(f)) : 0;
                var borderId = s.Border is { } b ? Math.Max(0, _borders.IndexOf(b)) : 0;
                var x = $"<xf numFmtId=\"0\" fontId=\"{fontId}\" fillId=\"{fillId}\" borderId=\"{borderId}\" xfId=\"0\"";
                x += " applyFont=\"1\"" + (fillId > 0 ? " applyFill=\"1\"" : "") + (borderId > 0 ? " applyBorder=\"1\"" : "");
                if (s.Align != XlsxAlign.General || s.Wrap)
                {
                    x += " applyAlignment=\"1\"><alignment";
                    if (s.Align != XlsxAlign.General) x += $" horizontal=\"{s.Align.ToString().ToLowerInvariant()}\"";
                    x += " vertical=\"center\"";
                    if (s.Wrap) x += " wrapText=\"1\"";
                    x += "/></xf>";
                }
                else x += "><alignment vertical=\"center\"/></xf>";
                return x;
            }));
            return $"<styleSheet xmlns=\"{ns}\">"
                + $"<fonts count=\"{_fonts.Count}\">{fontXml}</fonts>"
                + $"<fills count=\"{_fills.Count}\">{fillXml}</fills>"
                + $"<borders count=\"{_borders.Count}\">{borderXml}</borders>"
                + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                + $"<cellXfs count=\"{_xfs.Count}\">{xfXml}</cellXfs>"
                + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>"
                + "</styleSheet>";
        }
    }
}

/// 압축 없는(stored) ZIP 작성기 — xlsx 처럼 작은 묶음용 (Mac ZipWriter)
public static class ZipWriter
{
    public static byte[] Stored(IReadOnlyList<(string Name, byte[] Body)> entries)
    {
        using var output = new MemoryStream();
        using var central = new MemoryStream();
        // 1980-01-01 00:00 (DOS 날짜·시간)
        const ushort dosTime = 0, dosDate = (0 << 9) | (1 << 5) | 1;
        foreach (var (name, body) in entries)
        {
            var nameData = Encoding.UTF8.GetBytes(name);
            var crc = Crc32(body);
            var offset = (uint)output.Length;
            Le32(output, 0x0403_4B50);
            Le16(output, 20); Le16(output, 0x0800); Le16(output, 0);   // 버전, 플래그(UTF-8 이름), 저장(압축 없음)
            Le16(output, dosTime); Le16(output, dosDate);
            Le32(output, crc); Le32(output, (uint)body.Length); Le32(output, (uint)body.Length);
            Le16(output, (ushort)nameData.Length); Le16(output, 0);
            output.Write(nameData); output.Write(body);

            Le32(central, 0x0201_4B50);
            Le16(central, 20); Le16(central, 20); Le16(central, 0x0800); Le16(central, 0);
            Le16(central, dosTime); Le16(central, dosDate);
            Le32(central, crc); Le32(central, (uint)body.Length); Le32(central, (uint)body.Length);
            Le16(central, (ushort)nameData.Length); Le16(central, 0); Le16(central, 0);
            Le16(central, 0); Le16(central, 0); Le32(central, 0); Le32(central, offset);
            central.Write(nameData);
        }
        var centralOffset = (uint)output.Length;
        var centralBytes = central.ToArray();
        output.Write(centralBytes);
        Le32(output, 0x0605_4B50);
        Le16(output, 0); Le16(output, 0);
        Le16(output, (ushort)entries.Count); Le16(output, (ushort)entries.Count);
        Le32(output, (uint)centralBytes.Length); Le32(output, centralOffset);
        Le16(output, 0);
        return output.ToArray();
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var c = (uint)i;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB8_8320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFF_FFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFF_FFFF;
    }

    private static void Le16(Stream s, ushort v) { s.WriteByte((byte)(v & 0xFF)); s.WriteByte((byte)(v >> 8)); }
    private static void Le32(Stream s, uint v) { for (var i = 0; i < 4; i++) s.WriteByte((byte)((v >> (8 * i)) & 0xFF)); }
}
