using GodDB.Core.Schema;

namespace GodDB.Core.Modeling;

/// 모델링 다이어그램 자동 정렬 (Mac ModelLayout.swift): 관계를 따라 부모는 왼쪽 열, 자식은 오른쪽 열에 겹치지 않게 놓는다.
/// 관계 없는 테이블은 그 아래에 격자로. 같은 열 안에서는 부모 위치의 평균 순서로 놓아 선이 덜 엇갈리게 한다.
public static class ModelLayout
{
    public sealed record Options(
        double CardWidth = 260,
        /// 열 사이 간격 (관계선이 지나갈 자리)
        double ColumnGap = 120,
        /// 같은 열 카드 사이 간격
        double RowGap = 48,
        /// 왼쪽 위 여백
        double OriginX = 40,
        double OriginY = 40);

    /// 테이블 id → 카드 영역
    public static Dictionary<Guid, DiagramRect> Frames(DataModel model, Func<ModelTable, double> cardHeight, Options? options = null)
    {
        options ??= new Options();
        var ids = model.Tables.Select(t => t.Id).ToHashSet();
        var rels = model.Relations.Where(r => r.Parent != r.Child && ids.Contains(r.Parent) && ids.Contains(r.Child)).ToList();
        var parents = new Dictionary<Guid, List<Guid>>();
        var connected = new HashSet<Guid>();
        foreach (var r in rels)
        {
            if (!parents.TryGetValue(r.Child, out var list)) parents[r.Child] = list = [];
            list.Add(r.Parent);
            connected.Add(r.Parent);
            connected.Add(r.Child);
        }
        var order = model.Tables.Select(t => t.Id).Where(connected.Contains).Distinct().ToList();
        List<Guid> ParentsOf(Guid id) => parents.TryGetValue(id, out var l) ? l : [];

        // 단계 = 1 + 부모 단계의 최대값 (순환은 반복 횟수로 끊는다)
        var level = order.ToDictionary(id => id, _ => 0);
        for (var pass = 0; pass < Math.Max(order.Count, 1); pass++)
        {
            var changed = false;
            foreach (var id in order)
            {
                var ps = ParentsOf(id).Where(level.ContainsKey).Select(p => level[p]).ToList();
                var l = ps.Count == 0 ? 0 : ps.Max() + 1;
                if (l > level[id] && l < order.Count) { level[id] = l; changed = true; }
            }
            if (!changed) break;
        }
        var maxLevel = level.Count == 0 ? -1 : level.Values.Max();
        var columns = maxLevel >= 0
            ? Enumerable.Range(0, maxLevel + 1).Select(l => order.Where(id => level[id] == l).ToList()).ToList()
            : [];
        // 같은 열 안 순서: 부모들의 위치 평균 (두 번 다듬기)
        for (var pass = 0; pass < 2 && columns.Count > 1; pass++)
        {
            for (var l = 1; l < columns.Count; l++)
            {
                var pos = new Dictionary<Guid, double>();
                foreach (var prev in columns.Take(l))
                    for (var i = 0; i < prev.Count; i++) pos[prev[i]] = i;
                columns[l] = columns[l].Select((id, i) =>
                {
                    var ps = ParentsOf(id).Where(pos.ContainsKey).Select(p => pos[p]).ToList();
                    return (Id: id, Key: ps.Count == 0 ? i : ps.Sum() / ps.Count, Index: i);
                }).OrderBy(x => x.Key).ThenBy(x => x.Index).Select(x => x.Id).ToList();
            }
        }

        var byId = new Dictionary<Guid, ModelTable>();
        foreach (var t in model.Tables) byId.TryAdd(t.Id, t);
        var w = options.CardWidth;
        var result = new Dictionary<Guid, DiagramRect>();
        var bottom = options.OriginY;
        for (var l = 0; l < columns.Count; l++)
        {
            var y = options.OriginY;
            foreach (var id in columns[l])
            {
                if (!byId.TryGetValue(id, out var t)) continue;
                var h = cardHeight(t);
                result[id] = new DiagramRect(options.OriginX + l * (w + options.ColumnGap), y, w, h);
                y += h + options.RowGap;
            }
            bottom = Math.Max(bottom, y);
        }

        // 관계 없는 테이블: 격자 (관계 그림이 있으면 그 아래에 한 칸 더 띄워서)
        var loose = model.Tables.Where(t => !connected.Contains(t.Id)).ToList();
        if (loose.Count > 0)
        {
            var perRow = Math.Max(Math.Max(columns.Count, (int)Math.Ceiling(Math.Sqrt(loose.Count))), 1);
            var top = result.Count == 0 ? options.OriginY : bottom + options.RowGap;
            for (var i = 0; i < loose.Count; i += perRow)
            {
                double rowHeight = 0;
                var row = loose.Skip(i).Take(perRow).ToList();
                for (var c = 0; c < row.Count; c++)
                {
                    var h = cardHeight(row[c]);
                    result[row[c].Id] = new DiagramRect(options.OriginX + c * (w + options.ColumnGap), top, w, h);
                    rowHeight = Math.Max(rowHeight, h);
                }
                top += rowHeight + options.RowGap;
            }
        }
        return result;
    }
}
