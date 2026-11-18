using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using GodDB.Core.Dialects;
using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.Core.Results;

/// 결과 내보내기 형식
public enum ExportFormat { Csv, Tsv, Xlsx, Json, Sql, Markdown }

public static class ExportFormats
{
    public static readonly IReadOnlyList<ExportFormat> All =
        [ExportFormat.Csv, ExportFormat.Tsv, ExportFormat.Xlsx, ExportFormat.Json, ExportFormat.Sql, ExportFormat.Markdown];

    /// Mac 의 rawValue (설정 저장값과 같다)
    public static string RawValue(this ExportFormat f) => f switch
    {
        ExportFormat.Csv => "csv",
        ExportFormat.Tsv => "tsv",
        ExportFormat.Xlsx => "xlsx",
        ExportFormat.Json => "json",
        ExportFormat.Sql => "sql",
        _ => "markdown",
    };

    public static ExportFormat? FromRawValue(string? raw) => All.Where(f => f.RawValue() == raw).Select(f => (ExportFormat?)f).FirstOrDefault();

    public static string FileExtension(this ExportFormat f) => f switch
    {
        ExportFormat.Markdown => "md",
        _ => f.RawValue(),
    };

    public static string Title(this ExportFormat f) => f switch
    {
        ExportFormat.Csv => "CSV",
        ExportFormat.Tsv => "TSV",
        ExportFormat.Xlsx => "Excel (.xlsx)",
        ExportFormat.Json => "JSON",
        ExportFormat.Sql => "SQL INSERT",
        _ => "Markdown",
    };

    /// 한 파일에 담을 수 있는 최대 데이터 행 (Excel 시트는 머리글 포함 1,048,576행)
    public static int? MaxRows(this ExportFormat f) => f == ExportFormat.Xlsx ? 1_048_575 : null;

    /// 설정 창 카드 설명 (Mac: ResultExportUI.swift ExportSheet.note)
    public static string Note(this ExportFormat f) => f switch
    {
        ExportFormat.Csv => Tr("쉼표 구분 · 엑셀 호환"),
        ExportFormat.Tsv => Tr("탭 구분 · 엑셀에 붙여넣기"),
        ExportFormat.Xlsx => Tr("엑셀 파일 · 숫자 셀 유지"),
        ExportFormat.Json => Tr("행마다 {컬럼: 값}"),
        ExportFormat.Sql => Tr("INSERT 문 (다른 DB 로 옮기기)"),
        _ => Tr("Confluence · Jira 붙여넣기"),
    };

    /// 설정 창 형식별 안내 (CSV/TSV 는 BOM 체크박스, SQL 은 대상 테이블 입력 아래 안내)
    public static string? Hint(this ExportFormat f) => f switch
    {
        ExportFormat.Sql => Tr("INSERT 문 하나에 100행씩. 값은 모두 문자열 리터럴로 써요 (NULL 은 NULL)."),
        ExportFormat.Xlsx => Tr("시트 하나에 최대 1,048,575행. 넘으면 그 앞까지만 저장하고 알려줘요. 숫자로 바꿔도 값이 그대로인 것만 숫자 셀로 써요 (앞자리 0 은 문자)."),
        ExportFormat.Json => Tr("행마다 {컬럼: 값} 객체. NULL 은 null."),
        ExportFormat.Markdown => Tr("Confluence · Jira · 슬랙에 붙여 넣기 좋은 표. 큰 결과에는 맞지 않아요."),
        _ => null,
    };
}

public sealed class ExportOptions
{
    /// CSV / TSV 앞에 UTF-8 BOM (엑셀에서 한글 깨짐 방지)
    public bool ExcelBom { get; set; } = true;
    /// SQL INSERT 대상 테이블, Excel 시트 이름
    public string TableName { get; set; } = "table";
    /// INSERT 한 문장에 넣을 행 수
    public int InsertBatch { get; set; } = 100;
}

public enum ResultExportError { CannotWrite, ZipFailed }

public sealed class ResultExportException(ResultExportError kind, string detail, Exception? inner = null)
    : Exception(kind == ResultExportError.CannotWrite ? Tr("파일을 쓸 수 없어요: %@", detail) : Tr("Excel 파일을 만들지 못했어요: %@", detail), inner)
{
    public ResultExportError Kind { get; } = kind;
}

/// 결과를 파일로 조금씩 쓴다. 전체 결과는 페이지 단위로 받아 Write 를 여러 번 부르고, 마지막에 Finish.
/// 행을 메모리에 모아 두지 않는다.
/// Excel: Mac 은 시트 XML 을 임시 폴더에 쓰고 /usr/bin/zip 으로 묶었다. Windows 에는 zip 명령이 없어
/// System.IO.Compression 으로 임시 .xlsx 에 시트를 바로 압축해 쓰고, Finish 때 나머지 부품을 넣어 저장 위치로 옮긴다.
public sealed partial class ResultExportWriter : IDisposable
{
    public ExportFormat Format { get; }
    /// 저장 파일 경로 (클립보드용 작성기는 null)
    public string? Path { get; }
    public int RowsWritten { get; private set; }

    private readonly IReadOnlyList<string> _columns;
    private readonly ExportOptions _options;
    private readonly ISqlDialect? _dialect;
    private TextWriter _out;
    private readonly bool _ownsWriter;
    private FileStream? _zipFile;
    private ZipArchive? _zip;
    private string? _tempZip;
    private readonly List<string> _pendingInsert = [];
    private List<string> _jsonKeys = [];
    private bool _finished;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public ResultExportWriter(ExportFormat format, IReadOnlyList<string> columns, string path, ExportOptions? options = null,
        ISqlDialect? dialect = null)
    {
        Format = format;
        _columns = columns;
        Path = path;
        _options = options ?? new ExportOptions();
        _dialect = dialect;
        _ownsWriter = true;
        try
        {
            if (format == ExportFormat.Xlsx)
            {
                _tempZip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"goddb-xlsx-{Guid.NewGuid()}.xlsx");
                _zipFile = new FileStream(_tempZip, FileMode.Create, FileAccess.ReadWrite);
                _zip = new ZipArchive(_zipFile, ZipArchiveMode.Create, leaveOpen: false);
                var entry = _zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
                _out = new StreamWriter(entry.Open(), Utf8NoBom, 1 << 16);
            }
            else
            {
                _out = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), Utf8NoBom, 1 << 16);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            CleanupTemp();
            throw new ResultExportException(ResultExportError.CannotWrite, format == ExportFormat.Xlsx ? _tempZip ?? path : path, e);
        }
        WriteHeader();
    }

    /// 파일 없이 글자로 쓴다 (클립보드 복사용, Excel 제외). Mac 은 임시 파일을 썼다.
    internal ResultExportWriter(ExportFormat format, IReadOnlyList<string> columns, TextWriter output, ExportOptions options,
        ISqlDialect? dialect)
    {
        if (format == ExportFormat.Xlsx) throw new ArgumentException("xlsx needs a file", nameof(format));
        Format = format;
        _columns = columns;
        _options = options;
        _dialect = dialect;
        _out = output;
        _ownsWriter = false;
        WriteHeader();
    }

    // MARK: 쓰기

    public void Write(IEnumerable<IReadOnlyList<string?>> rows)
    {
        foreach (var r in rows)
        {
            var values = new string?[_columns.Count];
            for (int i = 0; i < values.Length; i++) values[i] = i < r.Count ? r[i] : null;
            switch (Format)
            {
                case ExportFormat.Csv: Emit(string.Join(",", values.Select(CsvField)) + "\r\n"); break;
                case ExportFormat.Tsv: Emit(string.Join("\t", values.Select(TsvField)) + "\r\n"); break;
                case ExportFormat.Markdown: Emit("| " + string.Join(" | ", values.Select(MarkdownField)) + " |\n"); break;
                case ExportFormat.Json:
                    var obj = string.Join(", ", _jsonKeys.Zip(values).Select(p => $"{JsonString(p.First)}: {JsonValue(p.Second)}"));
                    Emit((RowsWritten == 0 ? "\n  {" : ",\n  {") + obj + "}");
                    break;
                case ExportFormat.Sql:
                    _pendingInsert.Add("(" + string.Join(", ", values.Select(SqlLiteral)) + ")");
                    if (_pendingInsert.Count >= Math.Max(1, _options.InsertBatch)) FlushInsert();
                    break;
                case ExportFormat.Xlsx:
                    Emit("<row>" + string.Concat(values.Select(XlsxCell)) + "</row>\n");
                    break;
            }
            RowsWritten++;
        }
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        switch (Format)
        {
            case ExportFormat.Json: Emit(RowsWritten == 0 ? "]\n" : "\n]\n"); break;
            case ExportFormat.Sql: FlushInsert(); break;
            case ExportFormat.Xlsx: Emit("</sheetData></worksheet>\n"); break;
        }
        if (!_ownsWriter) { _out.Flush(); return; }
        if (Format != ExportFormat.Xlsx) { _out.Dispose(); return; }
        try
        {
            _out.Dispose();   // 시트 항목 닫기
            WriteXlsxParts(_zip!);
            _zip!.Dispose();
            _zip = null;
            _zipFile = null;
            File.Move(_tempZip!, Path!, overwrite: true);
            _tempZip = null;
        }
        catch (Exception e)
        {
            CleanupTemp();
            throw new ResultExportException(ResultExportError.ZipFailed, e.Message, e);
        }
    }

    /// 취소: 쓰던 파일을 지운다
    public void Cancel()
    {
        _finished = true;
        if (!_ownsWriter) return;
        try { _out.Dispose(); } catch { }
        CleanupTemp();
        if (Path != null) { try { File.Delete(Path); } catch { } }
    }

    /// Finish 하지 않고 버리면 Cancel 과 같다 (쓰던 파일 삭제)
    public void Dispose()
    {
        if (!_finished) Cancel();
    }

    private void CleanupTemp()
    {
        try { _zip?.Dispose(); } catch { }
        try { _zipFile?.Dispose(); } catch { }
        _zip = null;
        _zipFile = null;
        if (_tempZip != null) { try { File.Delete(_tempZip); } catch { } }
        _tempZip = null;
    }

    private void Emit(string s) => _out.Write(s);

    private void WriteHeader()
    {
        switch (Format)
        {
            case ExportFormat.Csv:
                Emit((_options.ExcelBom ? "\uFEFF" : "") + string.Join(",", _columns.Select(CsvField)) + "\r\n");
                break;
            case ExportFormat.Tsv:
                Emit((_options.ExcelBom ? "\uFEFF" : "") + string.Join("\t", _columns.Select(TsvField)) + "\r\n");
                break;
            case ExportFormat.Markdown:
                Emit("| " + string.Join(" | ", _columns.Select(MarkdownField)) + " |\n");
                Emit("|" + string.Concat(Enumerable.Repeat(" --- |", _columns.Count)) + "\n");
                break;
            case ExportFormat.Json:
                _jsonKeys = UniqueNames(_columns);
                Emit("[");
                break;
            case ExportFormat.Sql:
                Emit($"-- GodDB export · {_columns.Count} columns\n");
                break;
            case ExportFormat.Xlsx:
                Emit("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
                Emit("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                // 머리글 고정
                Emit("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
                Emit("<sheetData>\n<row>" + string.Concat(_columns.Select(c => XlsxString(c, 1))) + "</row>\n");
                break;
        }
    }

    // MARK: 형식별 값

    /// Mac 은 Swift Character 비교라 "\r\n" 한 덩어리만 있는 값은 따옴표로 감싸지 않았다 (CSV 가 깨짐).
    /// 여기서는 \r · \n 이 하나라도 있으면 감싼다 (QueryResult.Csv 와 같은 규칙).
    internal static string CsvField(string? v)
    {
        if (v == null) return "";
        if (v.IndexOfAny([',', '"', '\n', '\r']) >= 0) return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    /// TSV 는 따옴표 규칙이 없어 탭·줄바꿈을 공백으로 바꾼다 (엑셀 붙여넣기용)
    internal static string TsvField(string? v)
    {
        if (v == null) return "";
        return v.Replace("\r\n", " ").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }

    internal static string MarkdownField(string? v)
    {
        if (v == null) return "NULL";
        return v.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");
    }

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$")]
    private static partial Regex PlainNumber();

    /// 숫자로 써도 값이 바뀌지 않는 문자열만 숫자로 (앞자리 0, 15자리 넘는 수, 지수 표기는 문자열로 둔다)
    public static bool IsPlainNumber(string v)
    {
        if (v.Length == 0 || v.Length > 17) return false;
        if (!PlainNumber().IsMatch(v)) return false;
        return v.Count(char.IsAsciiDigit) <= 15;
    }

    internal static string JsonString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    internal static string JsonValue(string? v) => v == null ? "null" : IsPlainNumber(v) ? v : JsonString(v);

    /// 같은 이름 컬럼이 있으면 뒤에 _2, _3 (JSON 키 중복 방지)
    internal static List<string> UniqueNames(IReadOnlyList<string> names)
    {
        var seen = new Dictionary<string, int>();
        return [.. names.Select(n =>
        {
            var k = n.ToLowerInvariant();
            seen[k] = seen.GetValueOrDefault(k) + 1;
            return seen[k] == 1 ? n : $"{n}_{seen[k]}";
        })];
    }

    private string SqlLiteral(string? v) => v == null ? "NULL" : _dialect?.QuoteString(v) ?? SqlText.QuoteString(v);

    private void FlushInsert()
    {
        if (_pendingInsert.Count == 0) return;
        var table = _dialect?.QuoteTable(_options.TableName) ?? SqlText.QuoteIdent(_options.TableName);
        var cols = string.Join(", ", _columns.Select(c => _dialect?.QuoteIdent(c) ?? SqlText.QuoteIdent(c)));
        Emit($"INSERT INTO {table} ({cols}) VALUES\n  " + string.Join(",\n  ", _pendingInsert) + ";\n");
        _pendingInsert.Clear();
    }

    // MARK: Excel

    internal static string XmlEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    // XML 1.0 에서 허용하지 않는 제어 문자는 뺀다
                    if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue;
                    if (c == '\uFFFE' || c == '\uFFFF') continue;
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    internal static string XlsxString(string s, int style = 0)
    {
        // 셀 한 칸 최대 32,767자 (Excel 은 UTF-16 글자 수로 센다. 서로게이트 쌍은 자르지 않는다)
        var text = s;
        if (s.Length > 32_767)
        {
            var n = char.IsHighSurrogate(s[32_766]) ? 32_766 : 32_767;
            text = s[..n];
        }
        return $"<c t=\"inlineStr\"{(style > 0 ? $" s=\"{style}\"" : "")}><is><t xml:space=\"preserve\">{XmlEscape(text)}</t></is></c>";
    }

    internal static string XlsxCell(string? v)
    {
        if (v == null) return "<c/>";
        return IsPlainNumber(v) ? $"<c><v>{v}</v></c>" : XlsxString(v);
    }

    /// 시트 이름: 31자, []:*?/\ 불가
    internal static string SheetName(string s)
    {
        var cleaned = new string([.. s.Select(c => "[]:*?/\\".Contains(c) ? '_' : c)]).Trim(' ', '\t');
        var name = cleaned.Length == 0 ? "Result" : cleaned;
        return name.Length > 31 ? name[..31] : name;
    }

    private void WriteXlsxParts(ZipArchive zip)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
        var files = new (string Path, string Text)[]
        {
            ("[Content_Types].xml", head +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                "</Types>"),
            ("_rels/.rels", head +
                $"<Relationships xmlns=\"{pkg}\"><Relationship Id=\"rId1\" Type=\"{rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", head +
                $"<workbook xmlns=\"{main}\" xmlns:r=\"{rel}\"><sheets><sheet name=\"{XmlEscape(SheetName(_options.TableName))}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", head +
                $"<Relationships xmlns=\"{pkg}\">" +
                $"<Relationship Id=\"rId1\" Type=\"{rel}/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                $"<Relationship Id=\"rId2\" Type=\"{rel}/styles\" Target=\"styles.xml\"/>" +
                "</Relationships>"),
            ("xl/styles.xml", head +
                $"<styleSheet xmlns=\"{main}\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
                "</styleSheet>"),
        };
        foreach (var (path, text) in files)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open(), Utf8NoBom);
            w.Write(text);
        }
    }
}

// MARK: - 전체 결과를 페이지로 나눠 받을 때의 정렬

public static class ResultExportPlan
{
    /// 페이지 사이에 행이 빠지거나 겹치지 않도록 쓸 정렬.
    /// - 이미 DB 정렬(serverSort)이나 SQL 의 ORDER BY 가 있으면 그대로 (null, Stable: true)
    /// - 없으면 단일 컬럼 기본 키가 결과에 있을 때 그 컬럼으로 정렬
    /// - 둘 다 아니면 (null, Stable: false) → 화면에서 경고
    public static (ResultSort? Sort, bool Stable) StableSort(string sql, ResultSort? serverSort, IReadOnlyList<string> columns,
        IReadOnlyList<string>? primaryKey)
    {
        if (serverSort != null) return (serverSort, true);
        if (SqlText.HasTopLevelOrderBy(SqlText.StripComments(sql).ToUpperInvariant())) return (null, true);
        if (primaryKey is { Count: 1 } pk && columns.FirstOrDefault(c => string.Equals(c, pk[0], StringComparison.OrdinalIgnoreCase)) is { } col)
            return (new ResultSort(col, true), true);
        return (null, false);
    }
}
