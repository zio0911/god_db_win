using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodDB.Core;

/// 앱 설정 (Mac 의 UserDefaults 대신). %APPDATA%\GodDB\settings.json 에 키-값으로 저장한다.
/// 키 이름은 Mac 판과 같게 쓴다 (예: "GodDB.language", "GodDB.tuning.url").
public static class Prefs
{
    private static readonly Lock Gate = new();
    private static JsonObject? _data;
    private static string? _path;

    /// 테스트에서 다른 파일을 쓰게 한다 (null 이면 기본 위치)
    public static void UseFile(string? path)
    {
        lock (Gate) { _path = path; _data = null; }
    }

    private static string FilePath => _path ?? AppPaths.SettingsFile;

    private static JsonObject Data
    {
        get
        {
            if (_data != null) return _data;
            try { _data = File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject : null; }
            catch { _data = null; }
            return _data ??= [];
        }
    }

    public static bool Contains(string key) { lock (Gate) return Data.ContainsKey(key); }

    public static T Get<T>(string key, T fallback)
    {
        lock (Gate)
        {
            if (!Data.TryGetPropertyValue(key, out var node) || node == null) return fallback;
            try { return node.Deserialize<T>() ?? fallback; } catch { return fallback; }
        }
    }

    public static string? GetString(string key) => Get<string?>(key, null);

    public static void Set<T>(string key, T value)
    {
        lock (Gate)
        {
            Data[key] = value == null ? null : JsonSerializer.SerializeToNode(value);
            Save();
        }
    }

    public static void Remove(string key)
    {
        lock (Gate) { if (Data.Remove(key)) Save(); }
    }

    private static void Save()
    {
        try
        {
            AppPaths.Ensure(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* 설정 저장 실패는 앱 동작을 막지 않는다 */ }
    }
}
