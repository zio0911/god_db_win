using System.Text.RegularExpressions;

namespace GodDB.Core.Ai;

/// AI 답변 화면에서 쓰는 글 처리 (Mac 판 DiagramAIPanel.swift 의 firstSQL · MarkdownBlocks.blocks)
/// 와 Swift 문자열 동작을 맞추는 작은 도우미들.
public static partial class AiText
{
    /// 간단한 마크다운 블록: 제목(#), 코드 블록(```), 구분선(---), 나머지는 문단 (인라인 마크다운은 화면에서)
    public abstract record Block
    {
        public sealed record Heading(string Text, int Level) : Block;
        public sealed record Code(string Text) : Block;
        public sealed record Rule : Block;
        public sealed record Paragraph(string Text) : Block;
    }

    [GeneratedRegex(@"```(?:sql|SQL)\s*\n([\s\S]*?)```")] private static partial Regex SqlBlock();
    [GeneratedRegex(@"^(\s*)[-*] ")] private static partial Regex Bullet();

    /// 답변의 첫 번째 ```sql 블록 (SQL 편집기에 넣기). 없거나 비었으면 null
    public static string? FirstSql(string text)
    {
        var m = SqlBlock().Match(text);
        if (!m.Success) return null;
        var s = m.Groups[1].Value.Trim();
        return s.Length == 0 ? null : s;
    }

    public static List<Block> Blocks(string text)
    {
        var out_ = new List<Block>();
        var para = new List<string>();
        List<string>? code = null;
        void Flush()
        {
            var p = string.Join("\n", para).Trim();
            if (p.Length > 0) out_.Add(new Block.Paragraph(p));
            para.Clear();
        }
        foreach (var line in text.Split('\n'))
        {
            var t = TrimSpaces(line);
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                if (code != null) { out_.Add(new Block.Code(string.Join("\n", code))); code = null; }
                else { Flush(); code = []; }
                continue;
            }
            if (code != null) { code.Add(line); continue; }
            if (t.StartsWith('#'))
            {
                Flush();
                var level = t.TakeWhile(c => c == '#').Count();
                out_.Add(new Block.Heading(TrimSpaces(t[level..]), level));
            }
            else if (t is "---" or "***")
            {
                Flush(); out_.Add(new Block.Rule());
            }
            else if (t.Length == 0)
            {
                Flush();
            }
            else
            {
                para.Add(Bullet().Replace(line, "$1• ", 1));
            }
        }
        if (code != null) out_.Add(new Block.Code(string.Join("\n", code)));
        Flush();
        return out_;
    }

    // MARK: Swift 문자열 동작 맞추기

    /// Swift `trimmingCharacters(in: .whitespaces)` — 줄바꿈은 남긴다
    internal static string TrimSpaces(string s) => s.Trim(SpaceChars);
    private static readonly char[] SpaceChars = [' ', '\t', '\u00A0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005',
                                                  '\u2006', '\u2007', '\u2008', '\u2009', '\u200A', '\u202F', '\u205F', '\u3000'];

    private static readonly char[] NewLines = ['\n', '\r', '\u000B', '\u000C', '\u0085', '\u2028', '\u2029'];

    /// Swift `split(whereSeparator: \.isNewline).first` — 빈 줄은 건너뛴 첫 줄 (없으면 null)
    internal static string? FirstLine(string s) => s.Split(NewLines, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    /// 앞에서 n 글자 (서로게이트 쌍을 자르지 않음)
    internal static string Prefix(string s, int n)
    {
        if (n <= 0) return "";
        if (s.Length <= n) return s;
        if (char.IsHighSurrogate(s[n - 1])) n--;
        return s[..n];
    }

    /// 겹치지 않게 센 등장 횟수 (Swift `components(separatedBy:).count - 1`)
    internal static int Count(string s, string w)
    {
        if (w.Length == 0) return 0;
        int n = 0, i = 0;
        while ((i = s.IndexOf(w, i, StringComparison.Ordinal)) >= 0) { n++; i += w.Length; }
        return n;
    }
}
