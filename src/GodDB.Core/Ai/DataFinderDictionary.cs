using System.Text.RegularExpressions;
using GodDB.Core.Schema;

namespace GodDB.Core.Ai;

/// AI 데이터 찾기에 쓰는 한국어 ↔ 영어 DB 약어 사전 항목 (Mac DataFinderDictionary.swift).
/// 공공 · 기업 DB 에서 흔한 이름 짓기(예: 학년 → grade · grd · schyr)를 데이터로 둔다.
public sealed record AbbreviationEntry
{
    /// 한국어 말 (질문 · 코멘트에서 찾는다)
    public IReadOnlyList<string> Korean { get; }
    /// 영어 이름 · 약어 (이름 조각과 똑같으면 일치)
    public IReadOnlyList<string> English { get; }
    /// 흔해서 다른 뜻과 겹치기 쉬운 약어 (일치해도 점수 절반)
    public IReadOnlyList<string> Weak { get; }
    /// 번호 · 코드 · 이름처럼 어디에나 붙는 말이면 가중치를 낮춘다
    public bool Generic { get; }
    /// 값 모양 (정규식, 대소문자 무시). 샘플 값 대부분이 맞으면 그 컬럼을 더 믿는다.
    public string? ValuePattern { get; }

    public AbbreviationEntry(string[] korean, string[] english, string[]? weak = null, bool generic = false, string? values = null)
    {
        Korean = korean;
        English = english;
        Weak = weak ?? [];
        Generic = generic;
        ValuePattern = values;
    }

    /// 점수 가중치
    public double Weight => Generic ? 0.5 : 1;
}

public static class AbbreviationDictionary
{
    private const string GradeValues = @"^(0?[1-9]|1[0-2])\s*(학년)?$";
    private const string DateValues = @"^\d{4}[-./]?\d{2}[-./]?\d{2}";
    private const string YnValues = "^(y|n|0|1|true|false|t|f)$";

    private static AbbreviationEntry E(string[] k, string[] e, string[]? weak = null, bool generic = false, string? values = null) =>
        new(k, e, weak, generic, values);

    /// 사전 (약 80개). 새 말은 여기에만 더하면 된다.
    public static readonly IReadOnlyList<AbbreviationEntry> Entries =
    [
        // 학교 · 교육
        E(["학년"], ["grade", "grd", "schyr", "schlyr", "grde"], weak: ["gr", "yr"], values: GradeValues),
        E(["학교"], ["school", "sch", "schl"]),
        E(["학생"], ["student", "std", "stdnt", "stu", "pupil"]),
        E(["교사", "선생님", "선생", "강사"], ["teacher", "tchr", "tch", "instructor", "inst"]),
        E(["반", "학급"], ["class", "cls", "ban", "clss", "classroom"]),
        E(["과목", "교과"], ["subject", "subj", "sbj", "course", "crs"]),
        E(["학기"], ["semester", "sem", "term", "smstr"]),
        E(["성적", "점수"], ["score", "scr", "point", "pnt", "mark"]),
        E(["출석", "출결"], ["attend", "attendance", "atnd", "att"]),
        E(["학부모", "보호자", "부모"], ["parent", "guardian", "prnt", "grdn"]),
        E(["입학"], ["admission", "enter", "entr", "adm"]),
        E(["졸업"], ["graduate", "graduation", "grad", "grdt"]),
        // 사람 · 회원
        E(["회원", "고객", "사용자"], ["member", "mbr", "mem", "user", "usr", "cust", "customer", "client"]),
        E(["직원", "사원"], ["employee", "emp", "staff", "empl"]),
        E(["이름", "성명", "명칭", "명"], ["name", "nm"], generic: true),
        E(["아이디", "계정"], ["id", "login", "account", "acct", "userid", "loginid"], generic: true),
        E(["비밀번호", "암호"], ["pwd", "passwd", "password", "pw", "pass"]),
        E(["생년월일", "생일"], ["birth", "brth", "bday", "birthday", "birthdt", "bir"], values: DateValues),
        E(["나이", "연령"], ["age"]),
        E(["성별"], ["gender", "sex", "gndr"], values: "^(m|f|male|female|남|여|남자|여자|1|2)$"),
        E(["전화", "연락처", "휴대폰", "핸드폰", "전화번호"], ["phone", "tel", "mobile", "hp", "cell", "telno", "phn", "mbl"],
          values: @"^\+?\d{2,3}[- ]?\d{3,4}[- ]?\d{4}$"),
        E(["주소"], ["addr", "address", "adr"]),
        E(["우편번호"], ["zip", "zipcode", "post", "postcode", "zipcd"], values: @"^\d{3}-?\d{2,3}$"),
        E(["이메일", "메일"], ["email", "mail", "eml"], values: @"^[^@\s]+@[^@\s]+$"),
        E(["국적", "국가", "나라"], ["nation", "country", "ntn", "cntry"]),
        E(["직업"], ["job", "occupation", "occp"]),
        E(["부서"], ["dept", "department", "dpt"]),
        E(["직급", "직위", "직책"], ["position", "rank", "pos", "title", "jbgd"]),
        // 조직 · 지역
        E(["지역"], ["region", "area", "rgn", "zone"]),
        E(["지점", "센터", "영업소"], ["branch", "center", "brnc", "brch", "cntr", "office", "ofc"]),
        E(["회사", "업체", "거래처"], ["company", "comp", "corp", "vendor", "vndr", "partner", "cmpy"]),
        E(["본부", "사업부"], ["division", "div", "hq"]),
        // 일반 속성
        E(["번호", "순번", "일련번호"], ["no", "num", "seq", "sn", "number", "nbr"], generic: true),
        E(["코드"], ["cd", "code"], generic: true),
        E(["유형", "종류", "구분"], ["type", "typ", "kind", "gb", "div", "cls"], generic: true),
        E(["일자", "날짜", "날"], ["date", "dt", "ymd", "day"], generic: true, values: DateValues),
        E(["일시", "시각", "시간"], ["dtm", "datetime", "ts", "time", "tm", "dttm"], generic: true),
        E(["년도", "연도"], ["year", "yr", "yy", "yyyy"], values: @"^(19|20)\d{2}$"),
        E(["월"], ["month", "mon", "mm", "mth"]),
        E(["상태"], ["status", "stat", "sts", "state", "stt"]),
        E(["여부"], ["yn", "flag", "fl", "flg", "is"], generic: true, values: YnValues),
        E(["사용"], ["use", "used", "usg"]),
        E(["등록"], ["reg", "regist", "register", "registered", "rgst", "create", "created", "crt"]),
        E(["수정", "변경"], ["upd", "mod", "update", "updated", "modify", "chg", "change"]),
        E(["삭제"], ["del", "delete", "deleted"]),
        E(["시작"], ["start", "strt", "begin", "bgn", "from", "st"]),
        E(["종료", "끝"], ["end", "fin", "finish", "to", "ed", "close"]),
        E(["내용", "설명", "비고"], ["content", "cont", "desc", "description", "remark", "rmk", "note", "memo", "cn"], generic: true),
        E(["제목"], ["title", "ttl", "subject", "subj", "sj"]),
        E(["메모"], ["memo", "note", "remark", "rmk"]),
        E(["순서", "정렬"], ["order", "ord", "sort", "srt", "seq"], generic: true),
        E(["개수", "건수", "횟수"], ["count", "cnt"]),
        E(["합계", "총"], ["total", "tot", "sum"]),
        E(["비율", "율"], ["rate", "ratio", "rt", "pct", "percent"]),
        E(["이미지", "사진"], ["image", "img", "photo", "pic"]),
        E(["파일", "첨부"], ["file", "attach", "atch"]),
        E(["로그", "이력", "기록"], ["log", "hist", "history", "his"]),
        E(["권한", "역할"], ["auth", "role", "perm", "permission", "grant"]),
        E(["설정"], ["config", "conf", "setting", "set", "cfg"]),
        // 상거래 · 돈
        E(["주문"], ["order", "ord", "orders"]),
        E(["상품", "제품", "물품"], ["product", "prod", "item", "goods", "gds", "prdt", "prd"]),
        E(["결제"], ["pay", "payment", "pymt", "stl", "settle"]),
        E(["금액", "가격", "단가", "요금"], ["amt", "amount", "price", "prc", "cost", "fee"]),
        E(["수량"], ["qty", "quantity", "cnt"]),
        E(["배송", "배달"], ["delivery", "dlvr", "ship", "shipping", "dlv"]),
        E(["장바구니"], ["cart", "basket"]),
        E(["쿠폰"], ["coupon", "cpn"]),
        E(["포인트", "적립금"], ["point", "pnt", "pt", "mileage", "reward"]),
        E(["환불"], ["refund", "rfnd"]),
        E(["계좌", "은행"], ["account", "acct", "bank", "bnk"]),
        E(["카드"], ["card", "crd"]),
        E(["계약"], ["contract", "cntrc", "ctrt"]),
        E(["매출", "판매"], ["sales", "sale", "sell", "sls"]),
        E(["재고"], ["stock", "inventory", "inv", "stk"]),
        E(["게시판", "게시글", "글"], ["board", "bbs", "post", "article", "brd"]),
        E(["댓글"], ["comment", "reply", "cmt", "rply"]),
        E(["공지"], ["notice", "ntc", "announce"]),
        E(["문의"], ["inquiry", "inq", "qna", "question"]),
        E(["알림"], ["alarm", "notify", "noti", "notification", "push", "alrm"]),
    ];

    // MARK: - 이름 조각

    /// 이름을 조각으로: _ · - · . · 공백 · 숫자 경계 · camelCase 로 나누고 소문자로.
    /// tb_stdnt_info → [tb, stdnt, info], StdntNm → [stdnt, nm], schyr → [schyr]
    public static List<string> Tokens(string name)
    {
        var output = new List<string>();
        var cur = new System.Text.StringBuilder();
        char? prev = null;
        void Flush()
        {
            if (cur.Length > 0) output.Add(cur.ToString().ToLowerInvariant());
            cur.Clear();
        }
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (!(char.IsLetter(ch) || char.IsNumber(ch)) || ch == '_')
            {
                Flush();
                prev = null;
                continue;
            }
            if (prev is char p)
            {
                char? next = i + 1 < name.Length ? name[i + 1] : null;
                var lowerToUpper = char.IsLower(p) && char.IsUpper(ch);
                // "HTTPServer" → HTTP | Server
                var acronymEnd = char.IsUpper(p) && char.IsUpper(ch) && next is char n && char.IsLower(n);
                var digitEdge = char.IsNumber(p) != char.IsNumber(ch);
                if (lowerToUpper || acronymEnd || digitEdge) Flush();
            }
            cur.Append(ch);
            prev = ch;
        }
        Flush();
        return output;
    }

    // MARK: - 질문 말 → 사전 항목

    /// 질문 핵심어 하나에 맞는 사전 항목. 사전 말과 똑같으면 그것만, 아니면 핵심어 안에 든 사전 말들
    /// (긴 것부터, 겹치지 않게): "전화번호" → 전화 (사전에 그대로), "학생이름" → 학생 + 이름.
    /// 영어 핵심어는 영어 약어와 같으면 그 항목.
    public static List<AbbreviationEntry> Concepts(string term)
    {
        var t = term.ToLowerInvariant();
        if (Entries.FirstOrDefault(e => e.Korean.Contains(t)) is { } exact) return [exact];
        if (!DataFinder.IsHangul(t)) return Entries.Where(e => e.English.Contains(t)).ToList();
        // 핵심어 안의 사전 말 (긴 것부터, 이미 덮인 글자는 빼고)
        var covered = new bool[t.Length];
        var found = new List<(int Pos, AbbreviationEntry Entry)>();
        var words = Entries.SelectMany(e => e.Korean.Where(k => k.Length >= 2).Select(k => (Word: k, Entry: e)))
            .OrderByDescending(w => w.Word.Length).ToList();
        foreach (var w in words)
        {
            if (w.Word.Length > t.Length) continue;
            for (var start = 0; start <= t.Length - w.Word.Length; start++)
            {
                if (string.CompareOrdinal(t, start, w.Word, 0, w.Word.Length) != 0) continue;
                if (covered.AsSpan(start, w.Word.Length).Contains(true)) continue;
                for (var i = start; i < start + w.Word.Length; i++) covered[i] = true;
                if (!found.Any(f => f.Entry == w.Entry)) found.Add((start, w.Entry));
            }
        }
        return found.OrderBy(f => f.Pos).Select(f => f.Entry).ToList();
    }

    /// 질문 핵심어를 영어 이름으로 넓힌 글 (AI 에게 함께 보낸다): 학년 → grade, grd, schyr, schlyr, grde, gr, yr
    public static string ExpansionText(IEnumerable<string> terms) =>
        string.Join("\n", terms.Select(term =>
        {
            var words = Concepts(term).SelectMany(e => e.English.Concat(e.Weak)).ToList();
            if (words.Count == 0) return null;
            var seen = new HashSet<string>();
            return $"{term} → " + string.Join(", ", words.Where(seen.Add));
        }).OfType<string>());

    // MARK: - 일치

    /// 이름 조각이 이 항목과 맞는 정도: 1 (같음) · 0.5 (약한 약어, 또는 4글자 이상 영어 말로 시작하는 붙은 조각) · 0
    internal static double Match(AbbreviationEntry entry, IReadOnlyList<string> tokens)
    {
        var best = 0.0;
        foreach (var tok in tokens)
        {
            if (entry.English.Contains(tok)) return 1;
            if (entry.Weak.Contains(tok)) best = Math.Max(best, 0.5);
            // 붙여 쓴 이름: "gradecd", "telno" … 4글자 이상 영어 말만 (짧은 약어는 잘못 맞기 쉽다)
            if (tok.Length > 4 && entry.English.Any(e => e.Length >= 4 && tok.StartsWith(e, StringComparison.Ordinal))) best = Math.Max(best, 0.5);
        }
        return best;
    }

    /// 코멘트에 이 항목의 한국어 말 또는 영어 말이 있는지
    internal static bool MatchComment(AbbreviationEntry entry, string comment)
    {
        var c = comment.ToLowerInvariant();
        if (c.Length == 0) return false;
        if (entry.Korean.Any(k => k.Length >= 2 && c.Contains(k, StringComparison.Ordinal))) return true;
        var words = Tokens(c).ToHashSet();
        return entry.English.Any(e => e.Length >= 3 && words.Contains(e));
    }

    // MARK: - 값 모양

    /// 샘플 값이 질문 말의 값 모양과 맞는 정도 (-1…1). 모양이 없거나 값이 적으면 0.
    /// 대부분(80% 이상) 맞으면 1, 하나도 안 맞으면 -1.
    public static double ValueFit(IEnumerable<string> terms, IReadOnlyList<string> samples)
    {
        var patterns = terms.SelectMany(Concepts).Select(e => e.ValuePattern).OfType<string>().ToList();
        if (patterns.Count == 0 || samples.Count < 2) return 0;
        var best = -1.0;
        foreach (var p in patterns)
        {
            Regex re;
            try { re = new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch (ArgumentException) { continue; }
            var hits = samples.Count(s => re.IsMatch(s.Trim(' ', '\t')));
            var ratio = (double)hits / samples.Count;
            best = Math.Max(best, ratio >= 0.8 ? 1 : ratio > 0 ? 0 : -1);
        }
        return best;
    }

    /// 값 모양 안내 (AI 판단 글에 함께): 맞으면 "values look like 학년", 안 맞으면 "values do not look like 학년"
    public static string? ValueHintText(IReadOnlyList<string> terms, IReadOnlyList<string> samples)
    {
        var named = terms.Where(t => Concepts(t).Any(e => e.ValuePattern != null)).ToList();
        if (named.Count == 0) return null;
        return ValueFit(terms, samples) switch
        {
            1 => $"values look like {string.Join(", ", named)}",
            -1 => $"values do not look like {string.Join(", ", named)}",
            _ => null,
        };
    }
}

// MARK: - 점수 (AI 없이 후보 만들기 · AI 에 보낼 테이블 미리 고르기)

public static partial class DataFinder
{
    /// 컬럼 하나의 점수
    public sealed record ColumnScore(string Table, string Column, double Score);

    /// 질문 핵심어로 테이블 · 컬럼 점수 매기기. 핵심어마다:
    /// - 컬럼: 핵심어가 코멘트에 그대로 4 · 이름 조각과 같음(영어 질문) 3, 사전 항목이 이름 조각 · 코멘트와 맞음 3 × 가중치
    ///   (약한 약어 · 붙여 쓴 이름은 절반). 둘 중 큰 값.
    /// - 테이블 이름 · 코멘트가 핵심어(또는 그 사전 항목)와 맞으면 2 × 가중치 더한다.
    /// 컬럼 자체가 하나도 안 맞으면 후보가 아니다. 이름 조각 중 맞은 조각 비율 × 0.5 를 더해
    /// 같은 점수면 군더더기 없는 이름(schyr)이 mbr_grd_cd 보다 앞선다.
    public static List<ColumnScore> ScoreColumns(IEnumerable<string> terms, IReadOnlyList<TableMeta> tables)
    {
        var qs = terms.Select(t => t.ToLowerInvariant()).Where(t => t.Length > 0).ToList();
        if (qs.Count == 0) return [];
        var conceptsByTerm = qs.Select(AbbreviationDictionary.Concepts).ToList();
        var allConcepts = conceptsByTerm.SelectMany(c => c).ToList();
        var output = new List<ColumnScore>();
        foreach (var t in tables)
        {
            var tTokens = AbbreviationDictionary.Tokens(t.Name);
            var tComment = t.Comment.ToLowerInvariant();
            // 핵심어마다 테이블 자체가 맞는 점수
            var tableScores = qs.Select((q, i) =>
            {
                if ((tComment.Length > 0 && tComment.Contains(q, StringComparison.Ordinal)) || tTokens.Contains(q)) return 2.0;
                return conceptsByTerm[i].Select(e => 2 * e.Weight * Math.Max(AbbreviationDictionary.Match(e, tTokens),
                                                                               AbbreviationDictionary.MatchComment(e, tComment) ? 1 : 0))
                    .DefaultIfEmpty(0).Max();
            }).ToList();
            foreach (var col in t.Columns)
            {
                var cTokens = AbbreviationDictionary.Tokens(col.Name);
                var cComment = col.Comment.ToLowerInvariant();
                double own = 0, total = 0;
                for (var i = 0; i < qs.Count; i++)
                {
                    var q = qs[i];
                    var direct = 0.0;
                    if (cComment.Length > 0 && cComment.Contains(q, StringComparison.Ordinal))
                        direct = 4;
                    else if (!IsHangul(q) && (cTokens.Contains(q) || (q.Length >= 4 && Normalize(col.Name).Contains(q, StringComparison.Ordinal))))
                        direct = 3;
                    var concept = 0.0;
                    foreach (var e in conceptsByTerm[i])
                    {
                        var m = Math.Max(AbbreviationDictionary.Match(e, cTokens), AbbreviationDictionary.MatchComment(e, cComment) ? 1 : 0);
                        concept += 3 * e.Weight * m;
                    }
                    var termOwn = Math.Max(direct, concept);
                    own += termOwn;
                    total += termOwn + tableScores[i];
                }
                if (own <= 0) continue;
                var matched = cTokens.Count(tok => qs.Contains(tok) || allConcepts.Any(e => AbbreviationDictionary.Match(e, [tok]) > 0));
                var tidy = cTokens.Count == 0 ? 0 : 0.5 * matched / cTokens.Count;
                output.Add(new ColumnScore(t.Name, col.Name, total + tidy));
            }
        }
        return output;
    }

    /// 테이블별 최고 점수 (높은 순, 같으면 원래 순서)
    public static List<(string Table, double Score, List<string> Columns)> RankTables(IEnumerable<string> terms, IReadOnlyList<TableMeta> tables)
    {
        var scores = ScoreColumns(terms, tables);
        var order = new Dictionary<string, int>();
        for (var i = 0; i < tables.Count; i++) order.TryAdd(tables[i].Name, i);
        return scores.GroupBy(s => s.Table)
            .Select(g =>
            {
                var sorted = g.OrderByDescending(s => s.Score).ToList();
                return (Table: g.Key, Score: sorted[0].Score, Columns: sorted.Select(s => s.Column).ToList());
            })
            .OrderByDescending(r => r.Score)
            .ThenBy(r => order.GetValueOrDefault(r.Table))
            .ToList();
    }
}
