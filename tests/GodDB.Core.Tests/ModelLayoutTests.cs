using GodDB.Core.Modeling;
using GodDB.Core.Schema;

namespace GodDB.Core.Tests;

/// Mac ModelLayoutTests.swift
public class ModelLayoutTests
{
    private static double Height(ModelTable t) => 34 + Math.Max(t.Columns.Count, 1) * 20 + 10;

    private static DiagramRect Inset(DiagramRect r, double d) => new(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d);

    [Fact]
    public void NoOverlapAndParentsLeftOfChildren()
    {
        var m = new DataModel(name: "m");
        Guid a = m.AddTable("A"), b = m.AddTable("B"), c = m.AddTable("C");
        var d = m.AddTable("D");
        m.AddTable("E"); m.AddTable("F");   // 관계 없음
        // 모두 같은 자리에 겹쳐 놓고, 한 테이블은 컬럼을 아주 많이
        foreach (var t in m.Tables) { t.X = 40; t.Y = 40; }
        m.Table(b)!.Columns.AddRange(Enumerable.Range(0, 40).Select(i => new ModelColumn(logicalName: $"c{i}", physicalName: $"c{i}")));
        m.AddRelation(a, b);
        m.AddRelation(b, c);
        m.AddRelation(a, d);
        m.AddRelation(d, c);

        var frames = ModelLayout.Frames(m, Height);
        Assert.Equal(m.Tables.Count, frames.Count);
        var list = frames.Values.ToList();
        for (var i = 0; i < list.Count; i++)
            for (var j = i + 1; j < list.Count; j++)
                Assert.False(Inset(list[i], -10).Intersects(list[j]), $"{list[i]} {list[j]}");
        foreach (var t in m.Tables) Assert.Equal(Height(t), frames[t.Id].Height);
        foreach (var r in m.Relations) Assert.True(frames[r.Parent].MaxX < frames[r.Child].MinX);
        // 관계 없는 테이블은 관계 그림 아래
        var graph = new[] { a, b, c, d };
        var graphBottom = graph.Max(id => frames[id].MaxY);
        foreach (var t in m.Tables.Where(t => !graph.Contains(t.Id))) Assert.True(frames[t.Id].MinY > graphBottom);
    }

    [Fact]
    public void CycleAndSelfRelationStillLayOut()
    {
        var m = new DataModel(name: "m");
        Guid a = m.AddTable("A"), b = m.AddTable("B");
        m.AddRelation(a, b);
        m.AddRelation(b, a);
        m.AddRelation(a, a);
        var frames = ModelLayout.Frames(m, Height);
        Assert.Equal(2, frames.Count);
        Assert.False(frames[a].Intersects(frames[b]));
    }
}
