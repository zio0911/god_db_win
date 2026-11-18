using System.Globalization;
using static GodDB.Core.L10n;

namespace GodDB.Core.Redis;

/// Redis 화면에서 쓰는 글자 만들기·입력 나누기 (Mac 판 RedisModel.swift · RedisViews.swift 의 화면과 무관한 부분)
public static class RedisFormat
{
    private static string S(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// 키 정보의 남은 시간: "만료 없음", "45초", "12m 3s", "3h 20m", "2d 4h"
    public static string TtlText(long ms)
    {
        if (ms == -1) return Tr("만료 없음");
        if (ms == -2) return Tr("키 없음");
        long s = ms / 1000;
        if (s < 60) return Tr("%@초", S(s));
        if (s < 3600) return $"{S(s / 60)}m {S(s % 60)}s";
        if (s < 86400) return $"{S(s / 3600)}h {S(s % 3600 / 60)}m";
        return $"{S(s / 86400)}d {S(s % 86400 / 3600)}h";
    }

    /// 목록용 짧은 남은 시간: 45s, 12m, 3h, 2d
    public static string ShortTtl(long ms)
    {
        long s = ms / 1000;
        if (s < 60) return S(s) + "s";
        if (s < 3600) return S(s / 60) + "m";
        if (s < 86400) return S(s / 3600) + "h";
        return S(s / 86400) + "d";
    }

    /// 조회한 뒤 흐른 시간을 뺀 지금의 남은 TTL (밀리초, -1 만료 없음, -2 없음)
    public static long RemainingTtl(long ttlMillis, DateTime fetched, DateTime now)
    {
        if (ttlMillis < 0) return ttlMillis;
        return Math.Max(0, ttlMillis - (long)(now - fetched).TotalMilliseconds);
    }

    /// 명령 인자를 한 줄 글로 (공백·따옴표가 있으면 "…" 로 감싼다). 로그·감사 기록용.
    public static string Display(IEnumerable<string> args) =>
        string.Join(" ", args.Select(a =>
            a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c == '"' || c == '\'')
                ? "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\""
                : a));

    /// Pub/Sub 구독 입력 "news, evt:*" → 채널과 패턴 (*, ?, [ 가 있으면 패턴)
    public static (List<string> Channels, List<string> Patterns) SplitSubscribeTargets(string text)
    {
        var items = text.Split(c => c == ',' || char.IsWhiteSpace(c)).Where(s => s.Length > 0).ToList();
        var pats = items.Where(s => s.Contains('*') || s.Contains('?') || s.Contains('[')).ToList();
        var chans = items.Where(s => !pats.Contains(s)).ToList();
        return (chans, pats);
    }

    /// 새 키 만들기 명령. type: string/hash/list/set/zset/stream
    public static List<string> CreateKeyArgs(string type, string key, string field, string value) => type switch
    {
        "hash" => ["HSET", key, field, value],
        "list" => ["RPUSH", key, value],
        "set" => ["SADD", key, value],
        "zset" => ["ZADD", key, field.Length == 0 ? "0" : field, value],
        "stream" => ["XADD", key, "*", field.Length == 0 ? "value" : field, value],
        _ => ["SET", key, value],
    };

    /// Workbench 스크립트에 확인이 필요한 (위험한) 명령이 섞였는지
    public static bool ScriptHasDangerous(string text) =>
        RedisText.ScriptLines(text).Any(l => RedisText.SplitArgs(l) is { } a && RedisCommands.IsDangerous(a));
}

internal static class StringSplitExtensions
{
    /// 조건에 맞는 글자로 나눈다 (빈 조각 포함)
    public static string[] Split(this string s, Func<char, bool> isSeparator)
    {
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (!isSeparator(s[i])) continue;
            parts.Add(s[start..i]);
            start = i + 1;
        }
        parts.Add(s[start..]);
        return [.. parts];
    }
}
