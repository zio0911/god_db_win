using GodDB.Core.Results;
using GodDB.Core.Schema;
using N = GodDB.Core.Results.JSONNode;
using P = GodDB.Core.Results.JSONPathComponent;

namespace GodDB.Core.Tests;

/// Mac: JSONViewTests.swift
public class JSONViewTests
{
    private static bool IsJSONValue(JSONDetection d) => d.Kind == JSONDetection.Kinds.JSON;

    // MARK: - 판단

    [Fact]
    public void DetectsObjectsAndArrays()
    {
        Assert.True(IsJSONValue(JSONView.Detect("""{"a":1}""")));
        Assert.True(IsJSONValue(JSONView.Detect("  [1, 2, 3]\n")));
        Assert.True(IsJSONValue(JSONView.Detect("{}")));
        Assert.True(IsJSONValue(JSONView.Detect("[]")));
    }

    [Fact]
    public void PlainValuesAreNotJSON()
    {
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("hello"));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("123"));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("\"quoted\""));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("true"));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect(""));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect(null));
    }

    [Fact]
    public void BrokenJSONIsNotJSON()
    {
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("""{"a":1"""));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("""{"a":1,}"""));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("[1, 2] trailing"));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("{a:1}"));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("[01]"));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("[1.]"));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("[\"tab\there\"]"));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("""["\x"]"""));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("{[tag]} 로그 줄"));
    }

    [Fact]
    public void JsonTypedColumnOnlyChecksParse()
    {
        Assert.True(JSONView.IsJSONType("json"));
        Assert.True(JSONView.IsJSONType("JSONB"));
        Assert.False(JSONView.IsJSONType("varchar(100)"));
        Assert.False(JSONView.IsJSONType(null));
        Assert.Equal(JSONDetection.Json(N.String("text")), JSONView.Detect("\"text\"", "json"));
        Assert.Equal(JSONDetection.Json(N.Number("42")), JSONView.Detect("42", "jsonb"));
        Assert.Equal(JSONDetection.Json(N.Null), JSONView.Detect("null", "json"));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("not json", "json"));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("   ", "json"));
        // 타입이 json 이 아니면 시작 글자도 본다
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("42", "varchar(10)"));
    }

    [Fact]
    public void OverOneMegabyteShowsRawOnly()
    {
        const string item = """{"k":"0123456789012345678901234567890123456789"},""";
        var big = "[" + string.Concat(Enumerable.Repeat(item, JSONView.SizeLimit / item.Length + 1)) + "1]";
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(big) > JSONView.SizeLimit);
        Assert.Equal(JSONDetection.TooLarge, JSONView.Detect(big));
        Assert.Equal(JSONDetection.TooLarge, JSONView.Detect(big, "json"));
        Assert.Equal(JSONDetection.NotJSON, JSONView.Detect("x" + big));
        var small = "[" + string.Concat(Enumerable.Repeat(item, 100)) + "1]";
        Assert.True(IsJSONValue(JSONView.Detect(small)));
        Assert.True(JSONView.IsValid(big));
    }

    // MARK: - 파싱 · 정렬 출력

    [Fact]
    public void PrettyKeepsOriginalKeyOrder()
    {
        const string text = """{"z":1,"a":{"y":true,"b":null},"m":[1,"two",{"k":-0.5e3}],"e":{},"f":[]}""";
        var node = JSONView.Parse(text);
        Assert.NotNull(node);
        Assert.Equal("""
            {
              "z": 1,
              "a": {
                "y": true,
                "b": null
              },
              "m": [
                1,
                "two",
                {
                  "k": -0.5e3
                }
              ],
              "e": {},
              "f": []
            }
            """.ReplaceLineEndings("\n"), JSONView.Pretty(node));
        Assert.Equal(text, JSONView.Compact(node));
    }

    [Fact]
    public void EscapesAndUnicode()
    {
        const string text = """{"name":"홍길동","quote":"say \"hi\"","path":"C:\\temp\/x","nl":"a\nb\tc","u":"\u00e9\uAC00","emoji":"\ud83d\ude00","ctl":"\u0001"}""";
        var node = JSONView.Parse(text);
        Assert.NotNull(node);
        Assert.Equal(JSONKind.Object, node.Kind);
        var m = node.Members;
        Assert.Equal(["name", "quote", "path", "nl", "u", "emoji", "ctl"], m.Select(x => x.Key));
        Assert.Equal(N.String("홍길동"), m[0].Value);
        Assert.Equal(N.String("say \"hi\""), m[1].Value);
        Assert.Equal(N.String(@"C:\temp/x"), m[2].Value);
        Assert.Equal(N.String("a\nb\tc"), m[3].Value);
        Assert.Equal(N.String("é가"), m[4].Value);
        Assert.Equal(N.String("😀"), m[5].Value);
        Assert.Equal(N.String("\u0001"), m[6].Value);
        // 다시 출력하면 꼭 필요한 것만 이스케이프 (한글 · 이모지는 그대로)
        Assert.Equal("""{"name":"홍길동","quote":"say \"hi\"","path":"C:\\temp/x","nl":"a\nb\tc","u":"é가","emoji":"😀","ctl":"\u0001"}""",
            JSONView.Compact(node));
        // 짝 없는 서로게이트는 대체 문자
        Assert.Equal(N.Array([N.String("\uFFFD")]), JSONView.Parse("""["\ud83d"]"""));
        Assert.Equal(N.Array([N.String("\uFFFDx")]), JSONView.Parse("""["\ude00x"]"""));
        Assert.Null(JSONView.Parse("""["\u12G4"]"""));
    }

    [Fact]
    public void NumbersKeepTheirText()
    {
        Assert.Equal(N.Array([N.Number("0"), N.Number("-1"), N.Number("3.14"), N.Number("1E10"), N.Number("2e-3"),
                              N.Number("12345678901234567890")]),
            JSONView.Parse("[0, -1, 3.14, 1E10, 2e-3, 12345678901234567890]"));
        Assert.Null(JSONView.Parse("[-]"));
        Assert.Null(JSONView.Parse("[1e]"));
    }

    [Fact]
    public void DeepNestingIsRejectedNotCrashing()
    {
        var deep = new string('[', 5000) + new string(']', 5000);
        Assert.Null(JSONView.Parse(deep));
        var ok = new string('[', 100) + new string(']', 100);
        Assert.NotNull(JSONView.Parse(ok));
    }

    [Fact]
    public void CopyText()
    {
        Assert.Equal("A-77", JSONView.CopyText(N.String("A-77")));
        Assert.Equal("0.1", JSONView.CopyText(N.Number("0.1")));
        Assert.Equal("null", JSONView.CopyText(N.Null));
        Assert.Equal("[\n  1\n]", JSONView.CopyText(N.Array([N.Number("1")])));
    }

    // MARK: - 경로

    [Fact]
    public void Paths()
    {
        Assert.Equal("$", JSONView.Path([]));
        Assert.Equal("$.a.b[0].c", JSONView.Path([P.Key("a"), P.Key("b"), P.Index(0), P.Key("c")]));
        Assert.Equal("$.order.items[0].sku", JSONView.Path([P.Key("order"), P.Key("items"), P.Index(0), P.Key("sku")]));
        Assert.Equal("$[2]", JSONView.Path([P.Index(2)]));
        Assert.Equal("""$["key with space"]""", JSONView.Path([P.Key("key with space")]));
        Assert.Equal("""$["a-b"]["1st"][""]""", JSONView.Path([P.Key("a-b"), P.Key("1st"), P.Key("")]));
        Assert.Equal("""$["say \"hi\""]""", JSONView.Path([P.Key("say \"hi\"")]));
        Assert.Equal("$._id.이름.v2", JSONView.Path([P.Key("_id"), P.Key("이름"), P.Key("v2")]));
    }

    // MARK: - 편집 경고

    [Fact]
    public void InvalidEditsInJSONColumns()
    {
        var table = new TableMeta("t", columns: [
            new ColumnMeta("id", "int", false, columnKey: "PRI"),
            new ColumnMeta("meta", "json", true),
            new ColumnMeta("note", "text", true),
            new ColumnMeta("memo", "text", true),
        ]);
        var target = new EditTarget(table, ["id"], new Dictionary<int, string> { [0] = "id", [1] = "meta", [2] = "note", [3] = "memo" });
        List<QueryResult.Row> rows = [new(0, ["1", """{"a":1}""", """{"b":2}""", "plain"])];
        string[] columns = ["id", "meta", "note", "memo"];
        var edits = new PendingEdits();
        edits.Updates[0] = new() { [1] = """{"a":""", [2] = "not json", [3] = "{broken" };
        // meta: json 타입 · note: 원래 값이 JSON → 경고. memo: 원래 JSON 이 아니었으니 경고 없음
        Assert.Equal(["meta", "note"], JSONView.InvalidEdits(edits, target, columns, rows));
        edits.Updates[0] = new() { [1] = null, [2] = "[1]" };
        Assert.Empty(JSONView.InvalidEdits(edits, target, columns, rows));
        // 새 행은 json 타입만
        edits.Inserts = [new() { [1] = "oops", [2] = "x" }];
        Assert.Equal(["meta"], JSONView.InvalidEdits(edits, target, columns, rows));
        // 삭제 표시한 행은 보지 않는다
        edits = new PendingEdits();
        edits.Updates[0] = new() { [1] = "bad" };
        edits.Deletes = [0];
        Assert.Empty(JSONView.InvalidEdits(edits, target, columns, rows));
    }

    // Windows 판: 보기 방식 설정 값은 Mac 과 같은 글자 (raw · pretty · tree, 기본 트리)
    [Fact]
    public void ViewModeRawValues()
    {
        Assert.Equal("GodDB.json.viewMode", JSONViewModes.DefaultsKey);
        Assert.Equal(JSONViewMode.Tree, JSONViewModes.Default);
        foreach (var m in Enum.GetValues<JSONViewMode>()) Assert.Equal(m, JSONViewModes.FromRaw(m.RawValue()));
        Assert.Null(JSONViewModes.FromRaw("bogus"));
    }
}
