using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace GodDB.Core;

/// 화면 언어
public enum AppLanguage { System, Ko, En }

/// 다국어 처리. 소스의 한국어 문장이 키이고, 영어는 번역표(en.json · en.mac.json)에서 찾는다.
/// 표에 없는 문장은 한국어 그대로 보인다. 사용: `using static GodDB.Core.L10n;` → `Tr("%@행", count)`
public static class L10n
{
    private static readonly Lazy<Dictionary<string, string>> English = new(LoadEnglish);

    /// 설정에서 고른 언어 (설정 저장소가 바꾼다)
    public static AppLanguage Language { get; set; } = AppLanguage.System;

    /// 언어가 바뀌면 (화면 다시 그리기용)
    public static event Action? LanguageChanged;

    public static void SetLanguage(AppLanguage language)
    {
        if (Language == language) return;
        Language = language;
        LanguageChanged?.Invoke();
    }

    /// 실제로 쓰는 언어. 환경변수 GODDB_LANG(ko/en)이 있으면 그것을 우선한다 (테스트용).
    public static AppLanguage Resolved
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("GODDB_LANG");
            if (env == "ko") return AppLanguage.Ko;
            if (env == "en") return AppLanguage.En;
            if (Language != AppLanguage.System) return Language;
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? AppLanguage.Ko : AppLanguage.En;
        }
    }

    public static bool IsEnglish => Resolved == AppLanguage.En;

    public static string DisplayName(AppLanguage l) => l switch
    {
        AppLanguage.System => Tr("시스템 언어 따르기"),
        AppLanguage.Ko => "한국어",
        _ => "English",
    };

    /// 한국어 문장을 현재 언어로 바꾸고 `%@` 자리에 값을 차례로 넣는다.
    public static string Tr(string key, params object?[] args)
    {
        var text = IsEnglish && English.Value.TryGetValue(key, out var en) ? en : key;
        return args.Length == 0 ? text : Format(text, args);
    }

    /// `%@` 를 값으로 차례로 바꾼다. 값이 모자라면 빈 문자열.
    public static string Format(string template, params object?[] args)
    {
        var sb = new StringBuilder(template.Length + 16);
        int argIndex = 0;
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] == '%' && i + 1 < template.Length && template[i + 1] == '@')
            {
                sb.Append(argIndex < args.Length ? Convert.ToString(args[argIndex], CultureInfo.InvariantCulture) : "");
                argIndex++;
                i++;
            }
            else if (template[i] == '%' && i + 1 < template.Length && template[i + 1] == '%')
            {
                sb.Append('%');
                i++;
            }
            else sb.Append(template[i]);
        }
        return sb.ToString();
    }

    /// 번역표 전체 (테스트용)
    internal static IReadOnlyDictionary<string, string> EnglishTable => English.Value;

    private static Dictionary<string, string> LoadEnglish()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        var asm = Assembly.GetExecutingAssembly();
        // Mac 판에서 옮긴 표 → Windows 판에서 추가·수정한 표 순서 (뒤가 우선)
        foreach (var name in new[] { "GodDB.Core.L10n.en.mac.json", "GodDB.Core.L10n.en.json" })
        {
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) continue;
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
            if (d == null) continue;
            foreach (var (k, v) in d) table[k] = v;
        }
        return table;
    }
}
