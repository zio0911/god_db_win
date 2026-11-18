using System.Text;
using GodDB.Core.Dialects;
using GodDB.Core.Schema;
using static GodDB.Core.L10n;

namespace GodDB.Core.Results;

/// CSV / TSV 를 읽어 테이블에 넣는 INSERT 문을 만든다 (Mac: CSVImport.swift)
public static class CsvImport
{
    /// 첫 줄을 보고 구분자를 고른다 (쉼표 · 탭 · 세미콜론 중 가장 많은 것)
    /// Mac 은 Swift Character 로 나눠 CRLF 파일이면 전체 글을 셌다. 여기서는 실제 첫 줄만 센다.
    public static char DetectDelimiter(string text)
    {
        var nl = text.IndexOf('\n');
        var first = nl < 0 ? text : text[..nl];
        var best = ',';
        var bestCount = 0;
        foreach (var d in new[] { ',', '\t', ';' })
        {
            var n = first.Count(c => c == d);
            if (n > bestCount) { best = d; bestCount = n; }   // 같으면 앞의 것 (Swift max(by:) 와 같음)
        }
        return bestCount > 0 ? best : ',';
    }

    /// RFC 4180: 따옴표 안의 구분자 · 줄바꿈 · "" 를 처리한다. BOM 은 뗀다. 빈 줄은 건너뛴다.
    public static List<List<string>> Parse(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        void EndField() { row.Add(field.ToString()); field.Clear(); }
        void EndRow()
        {
            EndField();
            if (!(row.Count == 1 && row[0].Length == 0)) rows.Add(row);
            row = [];
        }
        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) inQuotes = true;
            else if (c == delimiter) EndField();
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { EndRow(); i += 2; continue; }   // Swift 는 \r\n 이 한 글자
            else if (c == '\n' || c == '\r') EndRow();
            else field.Append(c);
            i++;
        }
        if (field.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }

    /// CSV 머리글을 테이블 컬럼에 맞춘다 (대소문자 · 공백 · _ 무시). 머리글이 없으면 순서대로.
    /// 결과[i] = CSV i번째 열이 들어갈 테이블 컬럼 (null 은 건너뜀)
    public static List<string?> AutoMap(IReadOnlyList<string>? header, int csvColumns, IReadOnlyList<string> tableColumns)
    {
        static string Norm(string s) => new([.. s.ToLowerInvariant().Where(c => char.IsLetter(c) || char.IsNumber(c))]);
        if (header == null)
            return [.. Enumerable.Range(0, csvColumns).Select(i => i < tableColumns.Count ? tableColumns[i] : null)];
        var byName = new Dictionary<string, string>();
        foreach (var c in tableColumns) byName.TryAdd(Norm(c), c);
        return [.. Enumerable.Range(0, csvColumns).Select(i => i < header.Count && byName.TryGetValue(Norm(header[i]), out var c) ? c : null)];
    }

    /// 여러 행 INSERT 문들 (batch 행씩). emptyAsNull 이면 빈 칸은 NULL.
    public static List<string> Inserts(string table, IReadOnlyList<string?> mapping, IReadOnlyList<IReadOnlyList<string>> rows,
        ISqlDialect dialect, bool emptyAsNull = true, int batch = 200)
    {
        var picked = mapping.Select((c, i) => (i, c)).Where(p => p.c != null).Select(p => (p.i, Col: p.c!)).ToList();
        if (picked.Count == 0 || rows.Count == 0) return [];
        var head = $"INSERT INTO {dialect.QuoteTable(table)} (" + string.Join(", ", picked.Select(p => dialect.QuoteIdent(p.Col))) + ") VALUES\n";
        var size = Math.Max(1, Math.Min(batch, 1000));   // SQL Server 는 VALUES 1000행까지
        var output = new List<string>();
        for (int start = 0; start < rows.Count; start += size)
        {
            var values = rows.Skip(start).Take(size).Select(row =>
                "(" + string.Join(", ", picked.Select(p =>
                {
                    var v = p.i < row.Count ? row[p.i] : "";
                    return emptyAsNull && v.Length == 0 ? "NULL" : dialect.QuoteString(v);
                })) + ")");
            output.Add(head + string.Join(",\n", values));
        }
        return output;
    }

    /// 파일을 글로 읽는다. BOM 이 있으면 그 인코딩, 없으면 UTF-8.
    /// UTF-8 이 아니면 CP949 로 읽는다 (Windows 한글 엑셀은 CSV 를 ANSI=CP949 로 저장한다. Mac 은 UTF-8 실패 시 오류).
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }
}

/// CSV 가져오기 창의 상태와 계산 (Mac: CSVImportView.swift 의 화면과 상관없는 부분)
public sealed class CsvImportDocument(TableMeta table)
{
    public TableMeta Table { get; } = table;
    public string? FilePath { get; private set; }
    public string Text { get; private set; } = "";
    public char Delimiter { get; private set; } = ',';
    public bool HasHeader { get; private set; } = true;
    public bool EmptyAsNull { get; set; } = true;
    /// CSV 열마다 넣을 테이블 컬럼 (null = 건너뛰기)
    public List<string?> Mapping { get; private set; } = [];

    private List<List<string>>? _rows;
    public List<List<string>> Rows => _rows ??= CsvImport.Parse(Text, Delimiter);
    public List<string>? Header => HasHeader ? Rows.FirstOrDefault() : null;
    public List<List<string>> DataRows => HasHeader ? [.. Rows.Skip(1)] : Rows;
    public int CsvColumns => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);

    /// 미리 보기 앞 20행
    public IEnumerable<List<string>> PreviewRows => DataRows.Take(20);

    /// 미리 보기 칸 글자 (빈 칸 + 빈 칸은 NULL → "NULL")
    public string PreviewText(string v) => v.Length == 0 && EmptyAsNull ? "NULL" : v;

    public string Title => Tr("CSV 가져오기 → %@", Table.Name);
    public string Summary => Tr("%@행 · %@열", DataRows.Count.ToString(), CsvColumns.ToString());
    public string ImportButtonTitle => Tr("%@행 가져오기", DataRows.Count.ToString());

    /// [가져오기] 를 누를 수 있는지
    public bool CanImport => FilePath != null && DataRows.Count > 0 && Mapping.Any(m => m != null);

    /// 파일을 읽고 구분자를 고른 뒤 컬럼을 자동으로 맞춘다
    public void Load(string path)
    {
        var text = CsvImport.ReadText(path);
        LoadText(text, path);
    }

    /// 글을 직접 넣는다 (테스트 · 붙여넣기)
    public void LoadText(string text, string? path = null)
    {
        Text = text;
        FilePath = path ?? "";
        _rows = null;
        Delimiter = CsvImport.DetectDelimiter(text);
        Remap();
    }

    public void SetDelimiter(char d) { Delimiter = d; _rows = null; Remap(); }
    public void SetHasHeader(bool on) { HasHeader = on; Remap(); }

    public void SetMapping(int column, string? tableColumn)
    {
        if (column >= 0 && column < Mapping.Count) Mapping[column] = tableColumn;
    }

    public void Remap() => Mapping = CsvImport.AutoMap(Header, CsvColumns, [.. Table.Columns.Select(c => c.Name)]);

    /// 가져오기 INSERT 문들 (한 트랜잭션으로 실행: DbSession.ExecuteInTransactionAsync)
    public List<string> Statements(ISqlDialect dialect) =>
        CsvImport.Inserts(Table.Name, Mapping, [.. DataRows], dialect, EmptyAsNull);
}
