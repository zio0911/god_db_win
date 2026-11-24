using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
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
                Vm.SelectionReset += SyncGridSelection;
                Vm.PropertyChanged += (_, e) =>
                {
                    // 목록이 바뀌면(대상에만 있는 항목 켜기 · 끄기, 새 비교) 표의 고른 것을 다시 맞춘다
                    if (e.PropertyName == nameof(CompareViewModel.Visible)) Avalonia.Threading.Dispatcher.UIThread.Post(SyncGridSelection);
                };
            }
        };
        Items.SelectionChanged += (_, _) =>
        {
            if (_syncing || Vm == null) return;
            Vm.SetSelection(Items.SelectedItems.OfType<SchemaDiffItem>().ToList());
        };
        // Mac: 결과 목록을 고르면 ⌘C · ⌘A 가 목록에 간다 → Ctrl+C 는 고른 항목들의 보정 SQL, Ctrl+A 는 보이는 항목 모두
        Items.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (Vm == null || (e.KeyModifiers & ~KeyModifiers.Shift) is not (KeyModifiers.Control or KeyModifiers.Meta)) return;
            if (e.Key == Key.C)
            {
                _ = Vm.CopySelectedCommand.ExecuteAsync(null);
                e.Handled = true;
            }
            else if (e.Key == Key.A)
            {
                Vm.SelectAll();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    private bool _syncing;

    /// 뷰모델의 고른 것을 표에 맞춘다
    private void SyncGridSelection()
    {
        if (Vm == null) return;
        _syncing = true;
        try
        {
            Items.SelectedItems.Clear();
            foreach (var item in Vm.Visible.Where(i => Vm.SelectedIds.Contains(i.Id))) Items.SelectedItems.Add(item);
        }
        finally { _syncing = false; }
    }

    private void OnSelectAll(object? sender, RoutedEventArgs e) => Vm?.SelectAll();
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
