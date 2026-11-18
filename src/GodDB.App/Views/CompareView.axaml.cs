using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 스키마 비교 화면 (Mac CompareView). DataContext = AppState (메인 창이 넣는다), 안쪽은 CompareViewModel.
public partial class CompareView : UserControl
{
    public CompareViewModel? Vm { get; private set; }

    public CompareView()
    {
        InitializeComponent();
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
