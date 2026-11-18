namespace GodDB.Core.Tests;

public class PrefsTests
{
    [Fact]
    public void RoundTripsAndPersists()
    {
        var file = Path.Combine(Path.GetTempPath(), $"goddb-prefs-{Guid.NewGuid():N}.json");
        try
        {
            Prefs.UseFile(file);
            Assert.Equal(5, Prefs.Get("GodDB.redis.autoRefreshSeconds", 5));
            Prefs.Set("GodDB.redis.autoRefreshSeconds", 12);
            Prefs.Set("GodDB.language", "en");
            Prefs.Set("GodDB.recentFiles", new[] { "a.sql", "b.sql" });
            Prefs.UseFile(file); // 다시 읽기
            Assert.Equal(12, Prefs.Get("GodDB.redis.autoRefreshSeconds", 5));
            Assert.Equal("en", Prefs.GetString("GodDB.language"));
            Assert.Equal(["a.sql", "b.sql"], Prefs.Get<string[]>("GodDB.recentFiles", []));
            Assert.True(Prefs.Get("GodDB.missing", true));
            Prefs.Remove("GodDB.language");
            Assert.Null(Prefs.GetString("GodDB.language"));
        }
        finally { Prefs.UseFile(null); File.Delete(file); }
    }
}
