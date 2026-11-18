using GodDB.Core.Audit;

namespace GodDB.Core.Tests;

public class QueryHistoryTests
{
    private static string TempFile(string name) => Path.Combine(Path.GetTempPath(), "goddb-" + Guid.NewGuid(), name);

    [Fact]
    public void AppendsNewestFirstAndPersists()
    {
        var file = TempFile("history.jsonl");
        try
        {
            var p = Guid.NewGuid();
            var h = new QueryHistory(file);
            h.Append(new HistoryEntry(p, "a", "shop", "SELECT 1", 3, true));
            h.Append(new HistoryEntry(p, "a", "shop", "SELECT 2", 4, false));
            Assert.Equal(["SELECT 2", "SELECT 1"], h.All().Select(e => e.Sql));
            // 새로 읽어도 같다
            Assert.Equal(["SELECT 2", "SELECT 1"], new QueryHistory(file).All().Select(e => e.Sql));
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void TrimsToMaxEntries()
    {
        var file = TempFile("history.jsonl");
        try
        {
            var h = new QueryHistory(file, maxEntries: 10);
            for (int i = 0; i < 25; i++)
                h.Append(new HistoryEntry(Guid.NewGuid(), "a", "", $"SELECT {i}", 1, true));
            var all = new QueryHistory(file, maxEntries: 10).All();
            Assert.True(all.Count <= 10);
            Assert.Equal("SELECT 24", all.First().Sql);
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void FiltersByWordsAndProfile()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var list = new[]
        {
            new HistoryEntry(a, "a", "shop", "select * from orders where id = 1", 1, true),
            new HistoryEntry(b, "b", "crm", "UPDATE users SET x = 1", 1, true),
        };
        Assert.Single(QueryHistory.Filter(list, "ORDERS id", null));
        Assert.Equal(["b"], QueryHistory.Filter(list, "", b).Select(e => e.ProfileName));
        Assert.Single(QueryHistory.Filter(list, "crm", null));
        Assert.Empty(QueryHistory.Filter(list, "orders", b));
    }

    [Fact]
    public void TabStoreRoundTrip()
    {
        var file = TempFile("tabs.json");
        try
        {
            var store = new TabStore(file);
            var tabs = new List<SavedTab> { new(Guid.NewGuid(), "shop", 2, null, "SELECT 1;", true) };
            store.Save(tabs);
            Assert.Equal(tabs, store.Load());
        }
        finally { Cleanup(file); }
    }

    /// Windows 추가: 한 줄 형식이 Mac 판 history.jsonl 과 같다 (칸 이름, 초 단위 ISO 8601 날짜)
    [Fact]
    public void LineFormatMatchesMac()
    {
        var file = TempFile("history.jsonl");
        try
        {
            var h = new QueryHistory(file);
            var date = new DateTimeOffset(2026, 11, 13, 5, 46, 25, 398, TimeSpan.Zero).LocalDateTime;
            h.Append(new HistoryEntry(Guid.Empty, "a", "shop", "SELECT '/'", 3, true, "1행", date));
            var line = File.ReadAllLines(file).Single();
            Assert.Contains("\"date\":\"2026-11-13T05:46:25Z\"", line);
            Assert.Contains("\"profileID\":\"00000000-0000-0000-0000-000000000000\"", line);
            Assert.Contains("\"elapsedMs\":3", line);
            // Mac 이 쓴 줄 (대문자 UUID, "\/") 도 읽는다
            File.AppendAllText(file, """{"id":"B7DCA191-568C-407D-A9CA-7BCD3A995EA2","date":"2026-11-14T00:00:00Z","profileID":"558F2F4D-FF6D-41C9-8528-E11865A982B6","profileName":"m","database":"","sql":"SELECT '\/'","elapsedMs":1,"ok":false}""" + "\n");
            var all = new QueryHistory(file).All();
            Assert.Equal("SELECT '/'", all[0].Sql);
            Assert.Null(all[0].Summary);
            Assert.Equal("1행", all[1].Summary);
        }
        finally { Cleanup(file); }
    }

    private static void Cleanup(string file)
    {
        try { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); } catch { }
    }
}
