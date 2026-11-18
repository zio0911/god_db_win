using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Controls;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 스키마 비교 화면 (Mac CompareView). DataContext = AppState (메인 창이 넣는다), 안쪽은 CompareViewModel.
public partial class CompareView : UserControl
{
    public CompareViewModel? Vm { get; private set; }

    public CompareView()
    {
        InitializeComponent();
        foreach (var picker in new[] { SourcePicker, TargetPicker })
        {
            picker.ItemTemplate = ChoiceTemplate;
            // 그룹 이름 줄은 고를 수 없다 (Mac Picker 의 Section 머리)
            picker.ContainerPrepared += (_, e) =>
            {
                if (e.Container is ComboBoxItem item && item.DataContext is CompareViewModel.Choice c) item.IsEnabled = !c.IsHeader;
            };
        }
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AppState app && Vm?.App != app)
            {
                Vm = new CompareViewModel(app);
                Root.DataContext = Vm;
            }
        };
    }
}

public partial class CompareView
{
    /// 접속 고르기 항목: [DB 로고] 이름 · 환경 / 그룹 이름 줄 (Mac profileItem)
    public static readonly IDataTemplate ChoiceTemplate = new FuncDataTemplate<CompareViewModel.Choice>((c, _) =>
    {
        if (c == null) return new TextBlock();
        if (c.IsHeader)
            return new TextBlock { Text = c.Name, FontSize = 11, FontWeight = FontWeight.SemiBold, Classes = { "secondary" }, Margin = new Thickness(0, 4, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (c.Kind is { } kind) row.Children.Add(new KindBadge { Kind = kind, Size = 14 });
        row.Children.Add(new TextBlock { Text = c.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    });
}

/// 스키마 비교 항목 아이콘 · 색
public static class CompareConv
{
    public static readonly IValueConverter Icon = new FuncValueConverter<SchemaDiffSeverity, string>(CompareViewModel.Icon);

    public static readonly IValueConverter Brush = new FuncValueConverter<SchemaDiffSeverity, IBrush?>(s =>
    {
        var key = s switch
        {
            SchemaDiffSeverity.Missing => "RedBrush",
            SchemaDiffSeverity.Different => "OrangeBrush",
            _ => "SecondaryText",
        };
        return Avalonia.Application.Current is { } a && a.TryFindResource(key, a.ActualThemeVariant, out var r) ? r as IBrush : null;
    });
}
