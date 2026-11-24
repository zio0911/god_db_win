namespace GodDB.Core.Results;

/// 결과 표 칸을 그 자리에서 고칠 때의 규칙 (화면과 상관없는 부분). (Mac: InlineCellEdit.swift)
public static class InlineCellEdit
{
    /// 편집을 끝낸 방법
    public enum Finish
    {
        /// Enter 또는 다른 곳 클릭: 값을 넣고 끝
        Commit,
        /// Tab: 값을 넣고 같은 행 다음 칸으로
        Next,
        /// Shift+Tab: 값을 넣고 같은 행 앞 칸으로
        Previous,
        /// Esc: 넣지 않고 끝
        Cancel,
    }

    /// 편집을 시작할 때 칸에 넣을 글자. NULL · (기본값) 칸은 빈 글자로 시작한다.
    public static string InitialText(string? value) => value ?? "";

    // Swift Character.isNewline 과 같은 글자들
    private static readonly char[] Newlines = ['\n', '\r', (char)0x0B, (char)0x0C, (char)0x85, (char)0x2028, (char)0x2029];

    /// 그 자리에서 고칠 수 있는 값인지 (여러 줄 값은 한 줄 입력란에서 망가지므로 오른쪽 패널에서 고친다)
    public static bool CanEditInline(string? value) => value == null || value.IndexOfAny(Newlines) < 0;

    /// 끝낼 때 기록할 값. 바꾸지 않았으면 null (변경을 남기지 않는다).
    /// 빈 글자는 NULL 이 아니라 빈 문자열이다 (NULL 은 우클릭 "NULL 로 설정" 으로).
    public static string? CommittedValue(string? original, string text, Finish finish)
    {
        if (finish == Finish.Cancel || text == InitialText(original)) return null;
        return text;
    }

    /// Tab / Shift+Tab 으로 옮겨 갈 칸 (화면 순서의 편집 가능한 컬럼 중). 끝이면 null.
    public static int? Neighbor(int column, IEnumerable<int> editable, Finish finish)
    {
        var sorted = editable.Order().ToList();
        return finish switch
        {
            Finish.Next => sorted.Where(c => c > column).Select(c => (int?)c).FirstOrDefault(),
            Finish.Previous => sorted.Where(c => c < column).Select(c => (int?)c).LastOrDefault(),
            _ => null,
        };
    }
}
