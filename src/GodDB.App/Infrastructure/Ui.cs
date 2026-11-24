using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GodDB.App.Infrastructure;

/// 코드로 만드는 작은 창(입력창 · 확인창)용 도우미
public static class Ui
{
    public static TextBlock Text(string text, params string[] classes)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        foreach (var c in classes) t.Classes.Add(c);
        return t;
    }

    public static TextBlock Headline(string text) => Text(text, "headline");

    /// 코드 배경(CodeBackground) 상자 (예전 TuningSheet.CodeBorder — 쿼리 튜닝을 빼면서 옮김)
    public static Border CodeBorder(Control child)
    {
        var b = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(6), Child = child };
        b.Bind(Border.BackgroundProperty, b.GetResourceObservable("CodeBackground"));
        return b;
    }
    public static TextBlock Caption(string text) => Text(text, "caption");

    public static Button Button(string text, Action onClick, bool isDefault = false, bool isCancel = false, bool accent = false)
    {
        var b = new Button { Content = text, IsDefault = isDefault, IsCancel = isCancel, MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (accent || isDefault) b.Classes.Add("accent");
        b.Click += (_, _) => onClick();
        return b;
    }

    /// 오른쪽 정렬 버튼 줄 (왼쪽에 extra)
    public static Grid ButtonRow(IEnumerable<Control> right, IEnumerable<Control>? left = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var l = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in left ?? []) l.Children.Add(c);
        var r = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in right) r.Children.Add(c);
        Grid.SetColumn(r, 2);
        g.Children.Add(l);
        g.Children.Add(r);
        return g;
    }

    public static StackPanel Stack(double spacing, params Control[] children)
    {
        var s = new StackPanel { Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel HStack(double spacing, params Control[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static Window Dialog(string title, double width, Control content, double? height = null)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new Border { Padding = new Thickness(20), Child = content },
        };
        if (height is { } h) w.Height = h; else w.SizeToContent = SizeToContent.Height;
        return w;
    }
}
