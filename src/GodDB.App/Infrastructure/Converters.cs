using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GodDB.App.Services;

namespace GodDB.App.Infrastructure;

/// XAML 에서 쓰는 변환기 모음
public static class Conv
{
    /// 열거형 → 화면 이름 (현재 언어)
    public static readonly IValueConverter Title = new FuncValueConverter<object?, string>(v => v switch
    {
        DatabaseKind k => k.Title(),
        ConnectionType t => t.Title(),
        ConnectionSort cs => cs.Title(),
        RedisMode m => m.Title(),
        ConnectionEnvironment e => e.Title(),
        LogSource s => s.Title(),
        UpdateMode u => UpdateService.Title(u),
        AppLanguage l => L10n.DisplayName(l),
        null => "",
        _ => v.ToString() ?? "",
    });

    /// 저장된 비밀번호 표시 (Mac checkmark.seal.fill / seal)
    public static readonly IValueConverter Seal = new FuncValueConverter<bool, string>(b => b ? "✅" : "○");

    public static readonly IValueConverter Not = new FuncValueConverter<bool, bool>(b => !b);

    /// 선택된 줄 이름은 굵게 (Mac .fontWeight(selected ? .semibold : .regular))
    public static readonly IValueConverter SemiboldIf = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    /// 상태창 수준 아이콘
    public static readonly IValueConverter LevelIcon = new FuncValueConverter<LogLevel, string>(l => l switch
    {
        LogLevel.Success => "✓",
        LogLevel.Warning => "⚠",
        LogLevel.Error => "⛔",
        _ => "ⓘ",
    });

    public static readonly IValueConverter LevelBrush = new FuncValueConverter<LogLevel, IBrush>(l => l switch
    {
        LogLevel.Success => new SolidColorBrush(Color.Parse("#34C759")),
        LogLevel.Warning => new SolidColorBrush(Color.Parse("#FF9500")),
        LogLevel.Error => new SolidColorBrush(Color.Parse("#FF3B30")),
        _ => new SolidColorBrush(Color.Parse("#8A8A8E")),
    });

    /// 오류 메시지는 빨갛게 (Classes.error 에)
    public static readonly IValueConverter IsError = new FuncValueConverter<LogLevel, bool>(l => l == LogLevel.Error);

    public static readonly IValueConverter Time = new FuncValueConverter<DateTime, string>(d => d.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
}
