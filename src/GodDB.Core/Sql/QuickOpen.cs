using System.Globalization;
using GodDB.Core.Schema;

namespace GodDB.Core.Sql;

/// 객체 빠른 찾기 (Ctrl+O): 이름 일부 · 첫 글자들로 테이블 · 뷰 · 프로시저 · 함수 · 컬럼을 찾는다
public static class QuickOpen
{
    /// 선언 순서가 같은 점수일 때의 정렬 순서 (Mac: Kind.allCases)
    public enum Kind { Table, View, Procedure, Function, Trigger, Column, Database }

    /// Mac 의 rawValue ("table", "view" …)
    public static string RawValue(this Kind k) => k.ToString().ToLowerInvariant();

    /// <param name="Schema">이 객체가 있는 DB(스키마)</param>
    /// <param name="Detail">컬럼이면 테이블 이름, 아니면 코멘트 등</param>
    public sealed record Item(Kind Kind, string Schema, string Name, string Detail = "")
    {
        public string Id => $"{Kind.RawValue()}:{Schema}:{Name}:{Detail}";
    }

    private static List<string> Characters(string s)
    {
        var list = new List<string>(s.Length);
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }

    private static int CharCount(string s) => new StringInfo(s).LengthInTextElements;

    /// 높을수록 잘 맞음. 맞지 않으면 null.
    /// 같음 > 앞부분 > 단어(_ . 뒤) 앞부분 > 포함 > 글자 순서대로 들어 있음(ord_it → order_items)
    public static int? Score(string query, string name)
    {
        var q = string.Concat(Characters(query.ToLowerInvariant()).Where(ch => !ch.All(char.IsWhiteSpace)));
        if (q.Length == 0) return 0;
        var n = name.ToLowerInvariant();
        var len = CharCount(n);
        if (n == q) return 1000;
        if (n.StartsWith(q, StringComparison.Ordinal)) return 800 - Math.Min(len, 200);
        var words = n.Split(['_', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(q, StringComparison.Ordinal))) return 600 - Math.Min(len, 200);
        if (n.Contains(q, StringComparison.Ordinal)) return 400 - Math.Min(len, 200);
        // 글자 순서대로 (중간을 건너뛰어도 됨)
        var nc = Characters(n);
        var it = 0;
        var gaps = 0;
        foreach (var ch in Characters(q))
        {
            var found = false;
            while (it < nc.Count)
            {
                var c = nc[it++];
                if (c == ch) { found = true; break; }
                gaps++;
            }
            if (!found) return null;
        }
        return Math.Max(1, 200 - gaps - Math.Min(len, 100));
    }

    /// 점수 순 (같으면 종류 · 이름 순), 최대 limit 개
    public static List<Item> Search(string query, IEnumerable<Item> items, int limit = 200) =>
        items.Select(item =>
            {
                // "schema.name" 으로도 찾을 수 있게
                var s = Math.Max(Score(query, item.Name) ?? -1,
                    Math.Max((Score(query, item.Schema + "." + item.Name) ?? -1) - 50,
                             item.Kind == Kind.Column ? (Score(query, item.Detail + "." + item.Name) ?? -1) - 20 : -1));
                return (item, s);
            })
            .Where(x => x.s >= 0)
            .OrderByDescending(x => x.s)
            .ThenBy(x => (int)x.item.Kind)
            .ThenBy(x => x.item.Name, StringComparer.Ordinal)
            .Take(limit)
            .Select(x => x.item)
            .ToList();

    // MARK: 화면 쪽 로직 (Mac: QuickOpenView.swift)

    public static Kind KindOf(RoutineKind k) => k switch
    {
        RoutineKind.View => Kind.View,
        RoutineKind.Procedure => Kind.Procedure,
        RoutineKind.Function => Kind.Function,
        _ => Kind.Trigger,
    };

    /// 찾을 대상: 지금 DB 의 테이블 (+ 컬럼) · 루틴, 다른 DB 이름, 다른 스키마 객체(others)
    public static List<Item> BuildItems(string currentDatabase, IEnumerable<TableMeta> tables, IEnumerable<RoutineMeta> routines,
                                        IEnumerable<string> databases, IEnumerable<Item>? others = null)
    {
        var db = currentDatabase;
        var list = new List<Item>();
        foreach (var t in tables)
        {
            list.Add(new Item(Kind.Table, db, t.Name, t.Comment));
            foreach (var c in t.Columns) list.Add(new Item(Kind.Column, db, c.Name, t.Name));
        }
        foreach (var r in routines) list.Add(new Item(KindOf(r.Kind), db, r.Name, r.Comment));
        foreach (var d in databases) if (d != db) list.Add(new Item(Kind.Database, d, d));
        if (others != null) list.AddRange(others);
        return list;
    }

    /// 목록에 보일 결과. 검색어가 없으면 컬럼은 빼고 보여준다 (너무 많음)
    public static List<Item> Results(string query, IReadOnlyList<Item> items) =>
        query.Length == 0 ? items.Where(i => i.Kind != Kind.Column).Take(200).ToList() : Search(query, items);

    /// 다른 스키마의 테이블 · 뷰 (DbSession.AllTablesAsync 결과에서 지금 DB 는 뺀다)
    public static List<Item> OtherSchemaItems(IEnumerable<(string Schema, string Name, bool IsView)> rows, string currentDatabase) =>
        rows.Where(r => r.Schema != currentDatabase)
            .Select(r => new Item(r.IsView ? Kind.View : Kind.Table, r.Schema, r.Name))
            .ToList();

    /// ↑↓ 로 고른 다음 항목 Id (Mac: QuickOpenView.move). 목록이 비면 null.
    public static string? Move(string? selectedId, int delta, IReadOnlyList<Item> list)
    {
        if (list.Count == 0) return null;
        var i = -1;
        for (var k = 0; k < list.Count; k++) if (list[k].Id == selectedId) { i = k; break; }
        if (i < 0) i = delta > 0 ? -1 : list.Count;
        return list[Math.Max(0, Math.Min(list.Count - 1, i + delta))].Id;
    }
}
