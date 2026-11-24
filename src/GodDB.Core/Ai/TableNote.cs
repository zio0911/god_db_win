namespace GodDB.Core.Ai;

/// "이 데이터는 이 테이블 · 컬럼에 있다" 는 위치 하나 (AI 데이터 찾기가 찾는 말과 맞춰 본다).
/// Mac 1.4.0~1.4.2 의 테이블 메모(TableNotes.swift)에서 데이터 찾기가 쓰는 부분만 옮겼다.
/// Mac 1.4.3 은 테이블 메모 화면 · table-notes.json 을 없애고 테이블 설명으로 합쳤고(이 위치는 TableDescriptionDoc.FinderNotes 가 만든다),
/// Windows 판은 1.3.7 다음이 1.4.3 이라 table-notes.json 이 생긴 적이 없으므로 메모 저장소 · 화면 · 옮기기 코드는 두지 않는다.
public sealed record TableNote
{
    public enum NoteSource
    {
        /// AI 데이터 찾기가 저장
        Ai,
        /// 사용자가 직접 추가 (테이블 설명에서 온 위치도 이것)
        User,
    }

    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ProfileId { get; init; }
    public string Database { get; init; } = "";
    public string Table { get; init; } = "";
    public IReadOnlyList<string> Columns { get; init; } = [];
    /// 찾는 말 (질문 핵심어)
    public IReadOnlyList<string> Terms { get; init; } = [];
    public string Memo { get; init; } = "";
    public NoteSource Source { get; init; } = NoteSource.User;
    public bool Confirmed { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    public TableNote() { }

    public TableNote(Guid profileId, string database, string table, IEnumerable<string>? columns = null,
                     IEnumerable<string>? terms = null, string memo = "", NoteSource source = NoteSource.User, bool? confirmed = null,
                     DateTime? createdAt = null, DateTime? updatedAt = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        ProfileId = profileId;
        Database = database;
        Table = table;
        Columns = columns?.ToList() ?? [];
        Terms = terms?.ToList() ?? [];
        Memo = memo;
        Source = source;
        // 직접 추가한 메모는 확인된 것으로 본다
        Confirmed = confirmed ?? (source == NoteSource.User);
        CreatedAt = createdAt ?? DateTime.UtcNow;
        UpdatedAt = updatedAt ?? CreatedAt;
    }

    /// 같은 위치(테이블 + 컬럼 묶음)인지 — 대소문자 · 컬럼 순서 무시
    public bool SameLocation(string table, IEnumerable<string> columns) =>
        string.Equals(Table, table, StringComparison.OrdinalIgnoreCase)
        && Columns.Select(c => c.ToLowerInvariant()).ToHashSet().SetEquals(columns.Select(c => c.ToLowerInvariant()));

    /// `table.col1, col2`
    public string Location => Columns.Count == 0 ? Table : $"{Table}.{string.Join(", ", Columns)}";

    /// 쉼표로 나눈 글 → 목록 (앞뒤 공백 제거, 빈 칸 · 중복 제외)
    public static List<string> Split(string text)
    {
        var seen = new HashSet<string>();
        return text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0 && seen.Add(s.ToLowerInvariant())).ToList();
    }

    /// 찾는 말 합치기: 순서 유지, 정규화(공백 제거 · 소문자)해서 같은 말은 한 번만 (Mac TableNoteLibrary.mergeTerms)
    public static List<string> MergeTerms(IEnumerable<string> a, IEnumerable<string> b)
    {
        var seen = new HashSet<string>();
        var output = new List<string>();
        foreach (var t in a.Concat(b))
        {
            var trimmed = t.Trim();
            var key = DataFinder.Normalize(trimmed);
            if (key.Length == 0 || !seen.Add(key)) continue;
            output.Add(trimmed);
        }
        return output;
    }
}
