using System.Numerics;

namespace GodDB.Core.Redis;

/// Redis 키를 구분자(기본 ":") 로 나눠 폴더 트리로 만든다. user:1001:name → user › 1001 › name
public sealed record RedisKeyNode(
    // 폴더는 "f:" + 접두어, 키는 "k:" + 키 이름
    string Id,
    // 화면에 보일 마지막 조각
    string Name,
    // 폴더면 접두어 (구분자 포함 전까지, 예: "user:1001"), 키면 키 전체
    string Path,
    // 키면 키 이름, 폴더면 null
    string? Key,
    // 폴더면 하위 노드 (폴더 먼저, 이름순), 키면 null
    IReadOnlyList<RedisKeyNode>? Children,
    // 폴더 아래 키 개수 (키는 1)
    int Count)
{
    public bool IsFolder => Key == null;
}

/// 화면에 보이는 한 줄 (펼친 폴더의 하위만 포함)
public sealed record RedisKeyTreeRow(RedisKeyNode Node, int Depth)
{
    public string Id => Node.Id;
}

public static class RedisKeyTree
{
    // 펼친 폴더의 하위만 평평한 목록으로 (들여쓰기 깊이 포함)
    public static List<RedisKeyTreeRow> VisibleRows(IReadOnlyList<RedisKeyNode> nodes, IReadOnlySet<string> expanded, int depth = 0)
    {
        var output = new List<RedisKeyTreeRow>();
        void Walk(IReadOnlyList<RedisKeyNode> ns, int d)
        {
            foreach (var n in ns)
            {
                output.Add(new RedisKeyTreeRow(n, d));
                if (n.Children != null && expanded.Contains(n.Id)) Walk(n.Children, d + 1);
            }
        }
        Walk(nodes, depth);
        return output;
    }

    private sealed class Builder
    {
        public readonly Dictionary<string, Builder> Folders = new(StringComparer.Ordinal);
        public readonly List<(string Name, string Key)> Keys = [];
        public int Count;
    }

    public static List<RedisKeyNode> Build(IEnumerable<string> keys, string delimiter = ":")
    {
        var root = new Builder();
        foreach (var key in keys)
        {
            var parts = delimiter.Length == 0 ? [key] : key.Split(delimiter);
            var node = root;
            node.Count += 1;
            // 마지막 조각 전까지는 폴더 (빈 조각도 이름 "" 인 폴더로 둔다: a::b)
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!node.Folders.TryGetValue(parts[i], out var next))
                {
                    next = new Builder();
                    node.Folders[parts[i]] = next;
                }
                node = next;
                node.Count += 1;
            }
            node.Keys.Add((parts.Length > 0 ? parts[^1] : key, key));
        }
        return Nodes(root, null, delimiter);
    }

    private static List<RedisKeyNode> Nodes(Builder b, string? prefix, string delimiter)
    {
        var result = new List<RedisKeyNode>();
        foreach (var name in b.Folders.Keys.Order(NaturalComparer.Instance))
        {
            var path = prefix == null ? name : prefix + delimiter + name;
            var child = b.Folders[name];
            result.Add(new RedisKeyNode("f:" + path, name, path, null, Nodes(child, path, delimiter), child.Count));
        }
        foreach (var (name, key) in b.Keys.OrderBy(k => k.Name, NaturalComparer.Instance))
            result.Add(new RedisKeyNode("k:" + key, name, key, key, null, 1));
        return result;
    }

    // 숫자는 숫자 크기로 (user:2 가 user:10 보다 먼저), 글자는 대소문자 무시
    public static int NaturalCompare(string a, string b) => NaturalComparer.Instance.Compare(a, b);

    internal sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                    while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                    var na = BigInteger.Parse(a.AsSpan(si, i - si));
                    var nb = BigInteger.Parse(b.AsSpan(sj, j - sj));
                    int c = na.CompareTo(nb);
                    if (c != 0) return c;
                    continue;
                }
                int cc = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (cc != 0) return cc;
                i++; j++;
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }

    // 폴더 아래 키 전체를 찾는 SCAN 패턴 (glob 특수문자는 이스케이프)
    public static string Pattern(string folderPath, string delimiter = ":")
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in folderPath + delimiter)
        {
            if ("*?[]\\".Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('*').ToString();
    }
}
