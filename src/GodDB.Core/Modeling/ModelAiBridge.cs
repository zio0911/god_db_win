using System.Text;
using GodDB.Core.Ai;

namespace GodDB.Core.Modeling;

/// 모델링 AI 연결 (Mac: AssistantAI.swift 의 ModelAI.modelText / applyPhysicalNames(to: DataModel))
public static class ModelAiBridge
{
    /// 모델을 글로 (짧은 키 T1, T1.C2 로 물리명 추천 결과를 다시 맞춘다)
    public static string ModelText(DataModel m)
    {
        var s = new StringBuilder();
        s.Append($"Data model \"{m.Name}\" for {m.Kind.Title()}: {m.Tables.Count} tables, {m.Relations.Count} relations\n");
        for (int ti = 0; ti < m.Tables.Count; ti++)
        {
            var t = m.Tables[ti];
            s.Append($"T{ti + 1} {(t.LogicalName.Length == 0 ? "(no logical name)" : t.LogicalName)} [physical: {(t.PhysicalName.Length == 0 ? "-" : t.PhysicalName)}]");
            if (t.Comment.Length > 0) s.Append(" — ").Append(AiText.Prefix(t.Comment, 80));
            s.Append('\n');
            for (int ci = 0; ci < t.Columns.Count; ci++)
            {
                var c = t.Columns[ci];
                var line = $"  T{ti + 1}.C{ci + 1} {(c.LogicalName.Length == 0 ? "-" : c.LogicalName)} [physical: {(c.PhysicalName.Length == 0 ? "-" : c.PhysicalName)}] {c.DataType}";
                if (c.PrimaryKey) line += " PK";
                if (c.NotNull) line += " NOT NULL";
                if (c.Unique) line += " UNIQUE";
                if (c.AutoIncrement) line += " AUTO_INCREMENT";
                if (c.DefaultValue.Length > 0) line += $" DEFAULT {c.DefaultValue}";
                if (c.Comment.Length > 0) line += " — " + AiText.Prefix(c.Comment, 60);
                s.Append(line).Append('\n');
            }
        }
        s.Append("Relations:\n");
        foreach (var r in m.Relations)
        {
            s.Append($"  {m.Table(r.Parent)?.DisplayName ?? "?"} 1 — {(r.Cardinality == ModelCardinality.OneToOne ? "1" : "N")} {m.Table(r.Child)?.DisplayName ?? "?"}");
            s.Append(r.Identifying ? " (identifying)" : " (non-identifying)");
            s.Append(r.CreateConstraint ? "" : " (logical only, no FK constraint)");
            s.Append('\n');
        }
        var text = s.ToString();
        if (text.Length > AssistantAI.MaxContextChars) text = AiText.Prefix(text, AssistantAI.MaxContextChars) + "\n… (truncated)\n";
        return text;
    }

    /// AI 답의 JSON 을 모델에 적용. overwrite 가 false 면 비어 있는 물리명만. 바뀐 개수를 돌려준다.
    public static int ApplyPhysicalNames(string answer, DataModel m, bool overwrite = false) =>
        ModelAI.ApplyPhysicalNames(answer, new Target(m), overwrite);

    private sealed class Target(DataModel m) : IPhysicalNameTarget
    {
        public int TableCount => m.Tables.Count;
        public int ColumnCount(int table) => m.Tables[table].Columns.Count;
        public string TablePhysicalName(int table) => m.Tables[table].PhysicalName;
        public void SetTablePhysicalName(int table, string name) => m.Tables[table].PhysicalName = name;
        public string ColumnPhysicalName(int table, int column) => m.Tables[table].Columns[column].PhysicalName;
        public void SetColumnPhysicalName(int table, int column, string name) => m.Tables[table].Columns[column].PhysicalName = name;
        public void Touch() => m.Touch();
    }
}
