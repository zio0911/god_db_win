namespace GodDB.Core.Sql;

/// 최근 연 .sql 파일 목록 (최대 10개). Mac: SQLFiles.swift 의 recentSQLFiles / noteRecent / clearRecentSQLFiles
public static class RecentSqlFiles
{
    /// Mac 의 AppState.recentFilesKey 와 같은 키
    public const string Key = "recentSQLFiles";
    public const int Max = 10;

    /// 아직 있는 파일만, 최근 순
    public static List<string> Paths() =>
        Prefs.Get<List<string>?>(Key, null)?.Where(File.Exists).ToList() ?? [];

    /// 맨 앞에 넣는다 (이미 있으면 옮김).
    /// Windows 경로는 대소문자를 가리지 않으므로 같은 파일 비교는 대소문자 무시 (Mac 은 그대로 비교)
    public static void Note(string path)
    {
        var list = Prefs.Get<List<string>?>(Key, null) ?? [];
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        Prefs.Set(Key, list.Take(Max).ToList());
    }

    public static void Clear() => Prefs.Remove(Key);
}
