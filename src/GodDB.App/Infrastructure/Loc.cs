using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using GodDB.Core;

namespace GodDB.App.Infrastructure;

/// XAML 에서 쓰는 번역 소스. 언어를 바꾸면 모든 `{l:T …}` 가 다시 그려진다 (접속 · 탭 · 입력 중인 SQL 은 그대로).
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;

    private Loc() => L10n.LanguageChanged += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

    public string this[string key] => L10n.Tr(key);
}

/// `Text="{l:T 쿼리}"` 또는 `{l:T '스키마 비교'}` — 한국어 문장을 키로 현재 언어 문장을 보여준다.
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
