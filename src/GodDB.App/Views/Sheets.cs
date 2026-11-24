using System.Reflection;
using Avalonia.Controls;
using Avalonia.Layout;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// AppState 의 속성(예: nameof(AppState.ExportRequest))이 값을 가지면(또는 true 가 되면) 메인 창이 이 창을 모달로 띄운다.
/// 창이 닫히면 속성을 null / false 로 되돌린다 (이미 다른 값이면 그대로).
/// 창 클래스에는 (AppState app, object value) 생성자가 있어야 한다. value 는 속성 값(요청 객체 또는 true).
/// 다른 화면 포팅은 MainWindow 를 고치지 않고 자기 창 클래스에 이 표시만 붙이면 된다 (Placeholder 가 아닌 것이 우선).
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class SheetForAttribute(string property) : Attribute
{
    public string Property { get; } = property;
    public bool Placeholder { get; init; }
}

/// AppState.RequestWindow(kind, arg) 로 여는 모달 아닌 창 (예: "diagram"). (AppState app, object? argument) 생성자.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class WindowForAttribute(string kind) : Attribute
{
    public string Kind { get; } = kind;
    public bool Placeholder { get; init; }
}

public static class SheetRegistry
{
    private static Dictionary<string, Type>? _sheets;
    private static Dictionary<string, Type>? _windows;

    private static Dictionary<string, Type> Build<TAttr>(Func<TAttr, string> key, Func<TAttr, bool> placeholder) where TAttr : Attribute
    {
        var map = new Dictionary<string, (Type Type, bool Placeholder)>();
        foreach (var t in typeof(SheetRegistry).Assembly.GetTypes())
            foreach (var a in t.GetCustomAttributes<TAttr>())
                if (!map.TryGetValue(key(a), out var cur) || (cur.Placeholder && !placeholder(a)))
                    map[key(a)] = (t, placeholder(a));
        return map.ToDictionary(kv => kv.Key, kv => kv.Value.Type);
    }

    public static IReadOnlyDictionary<string, Type> Sheets => _sheets ??= Build<SheetForAttribute>(a => a.Property, a => a.Placeholder);
    public static IReadOnlyDictionary<string, Type> Windows => _windows ??= Build<WindowForAttribute>(a => a.Kind, a => a.Placeholder);

    public static Window? CreateSheet(string property, AppState app, object value) =>
        Sheets.TryGetValue(property, out var t) ? Activator.CreateInstance(t, app, value) as Window : null;

    public static Window? CreateWindow(string kind, AppState app, object? argument) =>
        Windows.TryGetValue(kind, out var t) ? Activator.CreateInstance(t, app, argument) as Window : null;
}

/// 아직 포팅하지 않은 창 자리 (각 화면 포팅이 [SheetFor] / [WindowFor] 를 붙인 진짜 창으로 대신한다)
[SheetFor(nameof(AppState.ExportRequest), Placeholder = true)]
[SheetFor(nameof(AppState.EditApply), Placeholder = true)]
[SheetFor(nameof(AppState.TableAction), Placeholder = true)]
[SheetFor(nameof(AppState.ShowQuickOpen), Placeholder = true)]
[SheetFor(nameof(AppState.ShowHistory), Placeholder = true)]
[SheetFor(nameof(AppState.AlteringTable), Placeholder = true)]
[SheetFor(nameof(AppState.ImportingTable), Placeholder = true)]
[SheetFor(nameof(AppState.ShowReverseEngineer), Placeholder = true)]
[WindowFor(WindowKinds.Diagram, Placeholder = true)]
public sealed class PlaceholderSheet : Window
{
    public PlaceholderSheet(AppState app, object? value)
    {
        Title = "GodDB";
        Width = 420;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var close = new Button { Content = Tr("닫기"), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "화면 준비 중 (작업 중)", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = value?.GetType().Name ?? "", Classes = { "secondary" } },
                close,
            },
        };
    }
}
