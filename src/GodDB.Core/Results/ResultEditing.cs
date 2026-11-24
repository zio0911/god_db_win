using System.Text.RegularExpressions;
using GodDB.Core.Dialects;
using GodDB.Core.Drivers;
using GodDB.Core.Schema;
using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.Core.Results;

/// 바이너리 값 규칙 (Mac: ResultEditing.swift 의 BinaryText.isHex / isBinaryType).
/// BinaryText(16진수 표시)는 Drivers 에 있고 partial 이 아니라서 판별 함수만 여기 둔다.
public static partial class BinaryColumn
{
    [GeneratedRegex("^0[xX]([0-9A-Fa-f]{2})*$")]
    private static partial Regex HexPattern();

    /// "0x…" 형태의 올바른 16진수인지
    public static bool IsHex(string s) => HexPattern().IsMatch(s);

    /// 바이너리 컬럼 타입인지 (varbinary, binary, blob, bytea, image)
    public static bool IsBinaryType(string columnType)
    {
        var t = columnType.ToLowerInvariant();
        return t.Contains("binary") || t.Contains("blob") || t.StartsWith("bytea", StringComparison.Ordinal) || t == "image";
    }
}

/// 결과를 직접 고칠 수 있는 대상 테이블 정보
public sealed class EditTarget : IEquatable<EditTarget>
{
    public TableMeta Table { get; }
    /// 기본 키 컬럼 이름
    public IReadOnlyList<string> PrimaryKey { get; }
    /// 결과 컬럼 번호 → 테이블 컬럼 이름 (테이블 컬럼과 이름이 같은 결과 컬럼만)
    public IReadOnlyDictionary<int, string> ColumnMap { get; }

    public EditTarget(TableMeta table, IEnumerable<string> primaryKey, IReadOnlyDictionary<int, string> columnMap)
    {
        Table = table;
        PrimaryKey = [.. primaryKey];
        ColumnMap = new Dictionary<int, string>(columnMap);
    }

    /// 결과에서 기본 키 컬럼 번호
    public List<int> PrimaryKeyIndexes =>
        [.. PrimaryKey.Select(pk => ColumnMap.Where(e => e.Value.Equals(pk, StringComparison.OrdinalIgnoreCase))
                                             .Select(e => (int?)e.Key).FirstOrDefault())
                      .OfType<int>()];

    public bool Equals(EditTarget? other) =>
        other is not null && Table.Equals(other.Table) && PrimaryKey.SequenceEqual(other.PrimaryKey)
        && ColumnMap.Count == other.ColumnMap.Count
        && ColumnMap.All(e => other.ColumnMap.TryGetValue(e.Key, out var v) && v == e.Value);

    public override bool Equals(object? obj) => Equals(obj as EditTarget);
    public override int GetHashCode() => HashCode.Combine(Table.Name, PrimaryKey.Count, ColumnMap.Count);
}

/// 셀 표시 상태 (Mac: ResultEditingUI.swift)
public enum CellState { Normal, Modified, Inserted, InsertedDefault, Deleted }

/// 결과 편집 대기 중인 변경 (적용 전까지 화면에만 있음).
/// Swift 에서는 값 타입(struct)이라 복사가 자동이었다. 여기서는 Clone() 으로 복사한다.
public sealed class PendingEdits : IEquatable<PendingEdits>
{
    /// 새 행 id 는 기존 행과 겹치지 않게 큰 수부터
    public const int NewRowBase = 1_000_000;

    /// 기존 행 id → (결과 컬럼 번호 → 새 값, null 은 NULL)
    public Dictionary<int, Dictionary<int, string?>> Updates { get; set; } = [];
    /// 새 행 (결과 컬럼 번호 → 값). 비어 있는 컬럼은 INSERT 에서 빼서 DB 기본값을 쓴다.
    public List<Dictionary<int, string?>> Inserts { get; set; } = [];
    /// 삭제할 기존 행 id
    public HashSet<int> Deletes { get; set; } = [];

    public int Count => Updates.Values.Sum(c => c.Count) + Inserts.Count + Deletes.Count;

    public bool IsEmpty => Count == 0;

    public PendingEdits Clone() => new()
    {
        Updates = Updates.ToDictionary(e => e.Key, e => new Dictionary<int, string?>(e.Value)),
        Inserts = [.. Inserts.Select(d => new Dictionary<int, string?>(d))],
        Deletes = [.. Deletes],
    };

    // MARK: 화면 로직 (Mac: ResultEditingUI.swift 의 PendingEdits 확장과 AppState 편집 함수)

    /// 화면에 보일 행: 기존 행(수정 반영) + 새 행
    public List<QueryResult.Row> DisplayRows(QueryResult r)
    {
        var list = new List<QueryResult.Row>(r.Rows.Count + Inserts.Count);
        foreach (var row in r.Rows)
        {
            if (!Updates.TryGetValue(row.Id, out var changes)) { list.Add(row); continue; }
            var values = row.Values.ToArray();
            foreach (var (i, v) in changes) if (i >= 0 && i < values.Length) values[i] = v;
            list.Add(new QueryResult.Row(row.Id, values));
        }
        for (int i = 0; i < Inserts.Count; i++)
        {
            var values = Inserts[i];
            list.Add(new QueryResult.Row(NewRowBase + i,
                [.. r.Columns.Select((_, c) => values.TryGetValue(c, out var v) ? v : null)]));
        }
        return list;
    }

    public CellState State(int row, int column)
    {
        if (row >= NewRowBase)
        {
            var i = row - NewRowBase;
            if (i < 0 || i >= Inserts.Count) return CellState.Normal;
            return Inserts[i].ContainsKey(column) ? CellState.Inserted : CellState.InsertedDefault;
        }
        if (Deletes.Contains(row)) return CellState.Deleted;
        if (Updates.TryGetValue(row, out var c) && c.ContainsKey(column)) return CellState.Modified;
        return CellState.Normal;
    }

    /// 셀 값 바꾸기 (null = NULL). 원래 값과 같아지면 변경에서 뺀다.
    public void SetCell(QueryResult? result, int row, int column, string? value)
    {
        if (row >= NewRowBase)
        {
            var i = row - NewRowBase;
            if (i < 0 || i >= Inserts.Count) return;
            Inserts[i][column] = value;
            return;
        }
        var original = result?.Rows.FirstOrDefault(x => x.Id == row) is { } r && column < r.Values.Count ? r.Values[column] : null;
        var changes = Updates.TryGetValue(row, out var c) ? c : [];
        if (value == original) changes.Remove(column);
        else changes[column] = value;
        if (changes.Count == 0) Updates.Remove(row);
        else Updates[row] = changes;
    }

    /// 입력하지 않은 새 행 셀을 DB 기본값으로 되돌린다.
    public void ResetInsertedCell(int row, int column)
    {
        var i = row - NewRowBase;
        if (i < 0 || i >= Inserts.Count) return;
        Inserts[i].Remove(column);
    }

    /// 새 행을 추가하고 그 행 id 를 돌려준다
    public int AddRow()
    {
        Inserts.Add([]);
        return NewRowBase + Inserts.Count - 1;
    }

    /// 선택한 행을 삭제 표시 (이미 표시돼 있으면 해제). 새 행은 바로 뺀다.
    public void ToggleDelete(IEnumerable<int> rows)
    {
        foreach (var id in rows.Distinct().OrderByDescending(x => x))
        {
            if (id >= NewRowBase)
            {
                var i = id - NewRowBase;
                if (i >= 0 && i < Inserts.Count) Inserts.RemoveAt(i);
            }
            else if (!Deletes.Remove(id)) Deletes.Add(id);
        }
    }

    public bool Equals(PendingEdits? other)
    {
        if (other is null) return false;
        static bool Same(Dictionary<int, string?> a, Dictionary<int, string?> b) =>
            a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var v) && v == e.Value);
        return Deletes.SetEquals(other.Deletes)
            && Inserts.Count == other.Inserts.Count && Inserts.Zip(other.Inserts).All(p => Same(p.First, p.Second))
            && Updates.Count == other.Updates.Count
            && Updates.All(e => other.Updates.TryGetValue(e.Key, out var o) && Same(e.Value, o));
    }

    public override bool Equals(object? obj) => Equals(obj as PendingEdits);
    public override int GetHashCode() => Count;
}

public enum ResultEditingError
{
    NotSingleTable, TableNotFound, NoPrimaryKey, PrimaryKeyNotSelected, ValuesTruncated,
    /// Redshift: 기본 키가 정보용이라 행을 하나로 고를 수 없다
    KeysNotEnforced,
    /// 결과 열이 어느 컬럼인지 SELECT 목록으로 확실히 알 수 없다
    ColumnsUnclear,
}

/// 결과를 고칠 수 없는 이유. (Swift 의 associated value enum → 종류 + 값)
public sealed class ResultEditingException(ResultEditingError kind, string? detail = null) : Exception(Describe(kind, detail))
{
    public ResultEditingError Kind { get; } = kind;
    /// 테이블 이름 또는 기본 키 컬럼 목록
    public string? Detail { get; } = detail;

    public static string Describe(ResultEditingError kind, string? detail) => kind switch
    {
        ResultEditingError.NotSingleTable =>
            Tr("테이블 하나만 조회한 결과만 고칠 수 있어요. (JOIN, GROUP BY, DISTINCT, UNION, 서브쿼리가 없는 SELECT)"),
        ResultEditingError.TableNotFound =>
            Tr("테이블 %@ 을(를) 사이드바 스키마에서 찾지 못했어요. 스키마를 새로고침해 보세요.", detail),
        ResultEditingError.NoPrimaryKey =>
            Tr("테이블 %@ 에 기본 키가 없어서 행을 정확히 고를 수 없어요.", detail),
        ResultEditingError.PrimaryKeyNotSelected =>
            Tr("결과에 기본 키 컬럼(%@)이 있어야 고칠 수 있어요. SELECT 에 넣어 주세요.", detail),
        ResultEditingError.ColumnsUnclear =>
            Tr("결과 열이 테이블의 어느 컬럼인지 확실하지 않아 고치지 않아요. 별칭 · 식 없이 컬럼 이름이나 * 로 조회해 주세요."),
        ResultEditingError.KeysNotEnforced =>
            Tr("Redshift 는 기본 키를 강제하지 않아 같은 키의 행이 여러 개일 수 있어서, 결과 표에서 고치지 않아요. UPDATE 문으로 수정하세요."),
        _ => Tr("값이 너무 커서 앞부분만 불러온 칸이 있어 이 결과는 표에서 고칠 수 없어요. UPDATE 문으로 수정하세요."),
    };
}

/// 편집 가능 여부 확인 결과 (Swift Result&lt;EditTarget, Error&gt; 대응): Target 이나 Error 중 하나
public sealed record EditCheck(EditTarget? Target, Exception? Error)
{
    public bool CanEdit => Target != null;
}

public static partial class ResultEditing
{
    [GeneratedRegex(@"\bJOIN\b|\bUNION\b|\bGROUP\s+BY\b|\bDISTINCT\b|\bHAVING\b|\bINTO\b|\(\s*SELECT\b|\bINTERSECT\b|\bEXCEPT\b")]
    private static partial Regex Blocked();

    [GeneratedRegex(@"\bFROM\s+((?:`[^`]+`|""[^""]+""|\[[^\]]+\]|[A-Za-z0-9_$#@]+)(?:\.(?:`[^`]+`|""[^""]+""|\[[^\]]+\]|[A-Za-z0-9_$#@]+))*)(\s*,)?", RegexOptions.IgnoreCase)]
    private static partial Regex FromTable();

    /// SELECT 문에서 대상 테이블 이름을 찾는다. 테이블 하나짜리 단순 조회가 아니면 null.
    public static string? SingleTable(string sql)
    {
        var cleaned = SqlText.StripComments(sql).Trim();
        if (SqlText.FirstKeyword(cleaned) != "SELECT") return null;
        var upper = cleaned.ToUpperInvariant();
        if (Blocked().IsMatch(upper)) return null;
        var matches = FromTable().Matches(cleaned);
        if (matches.Count != 1 || matches[0].Groups[2].Success) return null;
        var raw = matches[0].Groups[1].Value;
        return string.Join(".", raw.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim('`', '"', '[', ']')));
    }

    /// SQL 에 쓴 테이블 이름으로 스키마의 테이블을 찾는다 (대소문자 무시, `db.t` · `dbo.t` 도)
    public static TableMeta? TableNamed(string name, IReadOnlyList<TableMeta> tables)
    {
        var lower = name.ToLowerInvariant();
        var last = name.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.ToLowerInvariant() ?? lower;
        return tables.FirstOrDefault(t => t.Name.ToLowerInvariant() == lower)
               ?? tables.FirstOrDefault(t => t.Name.ToLowerInvariant() == last)
               ?? tables.FirstOrDefault(t => t.Name.ToLowerInvariant() == "dbo." + last);
    }

    // SQL Server TOP (n) [PERCENT] [WITH TIES], ALL
    [GeneratedRegex(@"^(?i)(ALL\s+)?(TOP\s*(\(\s*\d+\s*\)|\d+)(\s+PERCENT)?(\s+WITH\s+TIES)?\s+)?")]
    private static partial Regex SelectPrefix();

    private const string Ident = @"(?:`[^`]+`|""[^""]+""|\[[^\]]+\]|[A-Za-z0-9_$#@]+)";

    [GeneratedRegex("^(?:" + Ident + @"\.)*\*$")]
    private static partial Regex StarItem();

    [GeneratedRegex("^((?:" + Ident + @"\.)*)(" + Ident + @")(?:\s+(?:(?i:AS)\s+)?(" + Ident + "))?$")]
    private static partial Regex ColumnItem();

    /// SELECT 목록의 항목 (맨 위 수준 쉼표로 나눔). TOP · ALL 같은 앞부분은 뺀다. 읽을 수 없으면 null
    public static List<string>? SelectItems(string sql)
    {
        var s = SqlText.StripComments(sql);
        int i = 0, depth = 0;
        var items = new List<string>();
        var cur = new System.Text.StringBuilder();
        int? start = null;
        string Word(int k)
        {
            var j = k;
            var w = new System.Text.StringBuilder();
            while (j < s.Length && (char.IsLetter(s[j]) || s[j] == '_')) { w.Append(s[j]); j++; }
            return w.ToString().ToUpperInvariant();
        }
        bool Boundary(int k) => k < 0 || !(char.IsLetterOrDigit(s[k]) || s[k] == '_');
        while (i < s.Length)
        {
            var ch = s[i];
            if (ch is '\'' or '"' or '`' or '[')
            {
                var close = ch == '[' ? ']' : ch;
                var j = i + 1;
                while (j < s.Length && s[j] != close) j++;
                if (start != null) cur.Append(s, i, Math.Min(j, s.Length - 1) - i + 1);
                i = j + 1;
                continue;
            }
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            if (depth == 0 && Boundary(i - 1))
            {
                var w = Word(i);
                if (start == null && w == "SELECT") { start = i + 6; i += 6; continue; }
                if (start != null && w == "FROM")
                {
                    items.Add(cur.ToString());
                    break;
                }
            }
            if (start != null)
            {
                if (depth == 0 && ch == ',') { items.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            i++;
        }
        if (start == null || items.Count == 0) return null;
        var list = items.Select(x => x.Trim()).ToList();
        list[0] = SelectPrefix().Replace(list[0], "", 1);
        return list.Any(x => x.Length == 0) ? null : list;
    }

    /// 결과 열마다 실제 컬럼 이름 (식 · 다른 이름의 별칭이면 null). SELECT 목록과 결과 열 수가 맞지 않으면 null
    internal static List<string?>? ColumnSources(string sql, IReadOnlyList<string> columns, TableMeta table)
    {
        if (SelectItems(sql) is not { } items) return null;
        static string Unquote(string s) => s.Trim('`', '"', '[', ']');
        var output = new List<string?>();
        foreach (var item in items)
        {
            if (StarItem().IsMatch(item))
            {
                output.AddRange(table.Columns.Select(c => (string?)c.Name));
            }
            else if (ColumnItem().Match(item) is { Success: true } m)
            {
                var col = Unquote(m.Groups[2].Value);
                var alias = m.Groups[3].Success ? Unquote(m.Groups[3].Value) : col;
                output.Add(alias.Equals(col, StringComparison.OrdinalIgnoreCase) ? col : null);
            }
            else
            {
                output.Add(null);  // 식 · 함수 · 상수
            }
        }
        return output.Count == columns.Count ? output : null;
    }

    /// 편집할 수 있는지 확인하고 대상 정보를 만든다.
    public static EditTarget Target(string sql, IReadOnlyList<string> columns, IReadOnlyList<TableMeta> tables)
    {
        if (SingleTable(sql) is not { } name) throw new ResultEditingException(ResultEditingError.NotSingleTable);
        var table = TableNamed(name, tables) ?? throw new ResultEditingException(ResultEditingError.TableNotFound, name);
        if (table.Indexes.FirstOrDefault(i => i.IsPrimary) is not { Columns.Count: > 0 } pk)
            throw new ResultEditingException(ResultEditingError.NoPrimaryKey, table.Name);
        // 결과 열이 실제로 어느 컬럼인지: SELECT 목록을 보고, 별칭이 다른 이름이거나 식인 열은 고치지 않는다 (#194).
        // (이름만 맞춰 보면 SELECT name AS id 의 'id' 를 기본 키로 보고 다른 행을 바꿀 수 있다)
        var sources = ColumnSources(sql, columns, table) ?? throw new ResultEditingException(ResultEditingError.ColumnsUnclear);
        var map = new Dictionary<int, string>();
        var used = new HashSet<string>();
        for (int i = 0; i < columns.Count; i++)
        {
            if (sources[i] is not { } source || !source.Equals(columns[i], StringComparison.OrdinalIgnoreCase)) continue;
            var c = table.Columns.FirstOrDefault(c => c.Name.Equals(source, StringComparison.OrdinalIgnoreCase));
            if (c != null && used.Add(c.Name.ToLowerInvariant())) map[i] = c.Name;
        }
        var target = new EditTarget(table, pk.Columns, map);
        if (target.PrimaryKeyIndexes.Count != pk.Columns.Count)
            throw new ResultEditingException(ResultEditingError.PrimaryKeyNotSelected, string.Join(", ", pk.Columns));
        return target;
    }

    /// 결과를 고칠 수 있으면 대상, 못 고치면 이유. 편집과 상관없는 결과(빈 결과, 실행 계획)는 null.
    /// (Mac: ResultEditingUI.swift 의 AppState.editTarget(for:))
    public static EditCheck? Check(QueryResult? result, bool isPlan, bool readOnly, IReadOnlyList<TableMeta> tables)
    {
        if (result is not { Columns.Count: > 0 } || isPlan || result.Sql is not { } sql) return null;
        if (readOnly) return new EditCheck(null, new GodDbException(DbErrorKind.ReadOnlyBlocked));
        if (result.TruncatedCells > 0) return new EditCheck(null, new ResultEditingException(ResultEditingError.ValuesTruncated));
        try { return new EditCheck(Target(sql, [.. result.Columns.Select(c => c.Name)], tables), null); }
        catch (Exception e) { return new EditCheck(null, e); }
    }

    /// 대기 중인 변경을 SQL 문들로 만든다. 순서: DELETE → UPDATE → INSERT
    public static List<string> Statements(PendingEdits edits, EditTarget target, IReadOnlyList<QueryResult.Row> rows, ISqlDialect dialect)
    {
        var table = dialect.QuoteTable(target.Table.Name);
        var binaryColumns = target.Table.Columns.Where(c => BinaryColumn.IsBinaryType(c.ColumnType))
                                                .Select(c => c.Name.ToLowerInvariant()).ToHashSet();
        string Literal(string? v, int column)
        {
            if (v == null) return "NULL";
            if (target.ColumnMap.TryGetValue(column, out var name) && binaryColumns.Contains(name.ToLowerInvariant()) && BinaryColumn.IsHex(v))
                return dialect.BinaryLiteral(v[2..]);
            return dialect.QuoteString(v);
        }
        string WhereClause(QueryResult.Row row) => string.Join(" AND ", target.PrimaryKeyIndexes.Select(i =>
        {
            var col = dialect.QuoteIdent(target.ColumnMap[i]);
            var v = row.Values[i];
            return v == null ? $"{col} IS NULL" : $"{col} = {Literal(v, i)}";
        }));
        var byId = new Dictionary<int, QueryResult.Row>();
        foreach (var r in rows) byId.TryAdd(r.Id, r);
        var output = new List<string>();
        foreach (var id in edits.Deletes.Order())
        {
            if (!byId.TryGetValue(id, out var row)) continue;
            output.Add($"DELETE FROM {table} WHERE {WhereClause(row)};");
        }
        foreach (var id in edits.Updates.Keys.Order())
        {
            if (edits.Deletes.Contains(id)) continue;
            if (!byId.TryGetValue(id, out var row) || edits.Updates[id] is not { Count: > 0 } changes) continue;
            var sets = changes.Keys.Order()
                .Where(target.ColumnMap.ContainsKey)
                .Select(i => $"{dialect.QuoteIdent(target.ColumnMap[i])} = {Literal(changes[i], i)}")
                .ToList();
            if (sets.Count == 0) continue;
            output.Add($"UPDATE {table} SET {string.Join(", ", sets)} WHERE {WhereClause(row)};");
        }
        foreach (var row in edits.Inserts)
        {
            var cols = row.Keys.Order().Where(target.ColumnMap.ContainsKey).ToList();
            if (cols.Count == 0)
            {
                output.Add(dialect.Kind == DatabaseKind.Mysql ? $"INSERT INTO {table} () VALUES ();" : $"INSERT INTO {table} DEFAULT VALUES;");
                continue;
            }
            var names = string.Join(", ", cols.Select(c => dialect.QuoteIdent(target.ColumnMap[c])));
            var values = string.Join(", ", cols.Select(c => Literal(row[c], c)));
            output.Add($"INSERT INTO {table} ({names}) VALUES ({values});");
        }
        return output;
    }
}

public static class BinaryLiteralSql
{
    /// 16진수 → 바이너리 값 (0x 없이 넘긴다)
    public static string BinaryLiteral(this ISqlDialect d, string hex) => d.Kind switch
    {
        DatabaseKind.Postgres => $"decode('{hex}', 'hex')",
        DatabaseKind.Mssql => hex.Length == 0 ? "0x" : "0x" + hex,
        _ => $"X'{hex}'",
    };
}
