using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using GodDB.Core;

namespace GodDB.App.Infrastructure;

/// XAML 에서 쓰는 번역 소스. 언어를 바꾸면 모든 `{l:T …}` 가 다시 그려진다 (접속 · 탭 · 입력 중인 SQL 은 그대로).
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;

    private Loc() => L10n.LanguageChanged += () =>
    {
        Revision++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    };

    /// 언어가 바뀔 때마다 1씩 는다 (바인딩이 다시 읽게 하는 값)
    public int Revision { get; private set; }

    public string this[string key] => L10n.Tr(key);

    /// 코드에서 만드는 화면용: key 문장을 현재 언어로 보여 주는 바인딩.
    /// (키에 쉼표·괄호가 있어도 되도록 경로 대신 변환기로 찾는다)
    public static Binding Bind(string key) => new(nameof(Revision))
    {
        Source = Instance,
        Mode = BindingMode.OneWay,
        Converter = new FuncValueConverter<int, string>(_ => L10n.Tr(key)),
    };
}

/// `Text="{l:T 쿼리}"` 또는 `{l:T '스키마 비교'}` — 한국어 문장을 키로 현재 언어 문장을 보여준다.
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Bind(Key);
}
